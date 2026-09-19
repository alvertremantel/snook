using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Snook.Application;
using Snook.Domain;

namespace Snook.Cli;

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal interface ITailscaleProcess
{
    Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

internal sealed class TailscaleProcess : ITailscaleProcess
{
    private const int MaximumOutputCharacters = 1_048_576;

    public async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("tailscale")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) throw new SnookException(SnookErrorCode.StoreUnavailable, "The Tailscale CLI could not be started.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                "The Tailscale CLI was not found. Install and sign in to Tailscale, then retry; Snook does not install or enroll it.", exception);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            var stderr = ReadBoundedAsync(process.StandardError, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            return new ProcessResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new SnookException(SnookErrorCode.StoreUnavailable, "The Tailscale CLI did not finish within 15 seconds.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[8192];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return result.ToString();
            if (result.Length + read > MaximumOutputCharacters)
                throw new SnookException(SnookErrorCode.StoreUnavailable, "Tailscale CLI output exceeded the safe one-megabyte limit.");
            result.Append(buffer, 0, read);
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { }
    }
}

internal sealed record TailscaleServeStatus(string State, Uri? ServeOrigin, Uri Upstream, bool Running,
    bool MagicDnsEnabled, bool HttpsEnabled, bool FunnelConflict, string? CurrentRootProxy,
    IReadOnlyList<string> PreviewCommand, string Guidance);

internal sealed record TailnetSetupNextSteps(string HostPairingCommand, string ClientPairingCommand);

internal sealed class TailscaleServeManager
{
    private readonly ITailscaleProcess _process;

    public TailscaleServeManager(ITailscaleProcess? process = null) => _process = process ?? new TailscaleProcess();

    public async Task<TailscaleServeStatus> InspectAsync(Uri daemonEndpoint, CancellationToken cancellationToken)
    {
        var upstream = DaemonEndpoint.Parse(daemonEndpoint.OriginalString, DaemonEndpointKind.LocalLoopback).Origin;
        var statusResult = await _process.RunAsync(["status", "--json"], cancellationToken);
        if (statusResult.ExitCode != 0)
        {
            var state = ClassifyFailure(statusResult.StandardError, serveCommand: false);
            return Failure(state, upstream, state switch
            {
                "tailscale-not-running" => "The local Tailscale service is not running. Start tailscaled, then retry.",
                "tailscale-not-logged-in" => "Tailscale is running but this node is signed out. Sign in, then retry.",
                "tailscale-permission-denied" => "Tailscale status was denied. Check the local CLI/socket permissions; no configuration was changed.",
                _ => "Tailscale status is unavailable. Start Tailscale, sign in, and verify CLI compatibility, then retry."
            });
        }
        var node = ParseNodeStatus(statusResult.StandardOutput);
        if (!node.Running) return Failure("tailscale-not-running", upstream,
            "Tailscale is installed but is not in the Running state. Sign in or start its service, then retry.", node);
        if (node.Origin is null || !node.MagicDnsEnabled) return Failure("magicdns-unavailable", upstream,
            "This node did not report a canonical .ts.net name with MagicDNS enabled. Enable MagicDNS/HTTPS in the tailnet, then retry.", node);

        var serveResult = await _process.RunAsync(["serve", "status", "--json"], cancellationToken);
        if (serveResult.ExitCode != 0)
        {
            var state = ClassifyFailure(serveResult.StandardError, serveCommand: true);
            return Failure(state, upstream, state switch
            {
                "tailscale-cli-unsupported" => "This Tailscale CLI does not expose the required Serve JSON status. Upgrade it or use the previewed manual command; no configuration was changed.",
                "tailscale-permission-denied" => "Tailscale Serve status was denied. Check local CLI/socket permissions; no configuration was changed.",
                _ => "Tailscale Serve status could not be inspected. Check CLI version and privileges; no configuration was changed."
            }, node);
        }
        var serve = ParseServeStatus(serveResult.StandardOutput, node.Origin);
        var preview = new[] { "tailscale", "serve", "--bg", upstream.AbsoluteUri.TrimEnd('/') };
        if (serve.Funnel) return new("funnel-conflict", node.Origin, upstream, true, node.MagicDnsEnabled,
            node.HttpsEnabled, true, serve.Proxy, preview,
            "Funnel is public and unsupported. Disable the conflicting Funnel mapping yourself before configuring Snook.");
        if (serve.Proxy is not null && !SameUpstream(serve.Proxy, upstream)) return new("root-conflict", node.Origin,
            upstream, true, node.MagicDnsEnabled, node.HttpsEnabled, false, serve.Proxy, preview,
            "The Serve HTTPS root is already owned by another mapping. Snook will not overwrite it.");
        var correct = serve.Proxy is not null && SameUpstream(serve.Proxy, upstream);
        return new(correct ? "ready" : node.HttpsEnabled ? "ready-to-apply" : "https-consent-required",
            node.Origin, upstream, true, node.MagicDnsEnabled, node.HttpsEnabled, false, serve.Proxy, preview,
            correct ? "The tailnet-only HTTPS root already proxies to this Snook daemon. Funnel remains unsupported."
                : "Review the preview, then explicitly apply it. Snook never enables Funnel or edits tailnet policy.");
    }

    public Task<TailscaleServeStatus> ApplyAsync(Uri daemonEndpoint, CancellationToken cancellationToken) =>
        ApplyAsync(daemonEndpoint, beforeMutation: null, cancellationToken);

    internal async Task<TailscaleServeStatus> ApplyAsync(Uri daemonEndpoint,
        Func<TailscaleServeStatus, CancellationToken, Task>? beforeMutation, CancellationToken cancellationToken)
    {
        var status = await InspectAsync(daemonEndpoint, cancellationToken);
        if (status.State == "ready")
        {
            if (beforeMutation is not null) await beforeMutation(status, cancellationToken);
            return status;
        }
        if (status.State == "https-consent-required")
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                "Tailscale HTTPS consent is not enabled. Enable HTTPS for the tailnet explicitly, inspect again, then apply the preview; Snook did not change tailnet-wide HTTPS state.");
        if (status.State != "ready-to-apply")
            throw new SnookException(SnookErrorCode.StoreUnavailable, status.Guidance);
        if (beforeMutation is not null) await beforeMutation(status, cancellationToken);
        var result = await _process.RunAsync(status.PreviewCommand.Skip(1).ToArray(), cancellationToken);
        if (result.ExitCode != 0)
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                "Tailscale Serve did not accept the root mapping. HTTPS consent, privileges, or CLI compatibility may require the previewed command to be run manually.");
        var verified = await InspectAsync(daemonEndpoint, cancellationToken);
        if (verified.State != "ready" || verified.ServeOrigin != status.ServeOrigin
            || verified.Upstream != status.Upstream)
            throw new SnookException(SnookErrorCode.StoreUnavailable, "Tailscale Serve returned success but the exact Snook root mapping could not be verified.");
        return verified;
    }

    public async Task<TailscaleServeStatus> DisableExactAsync(Uri daemonEndpoint, Uri recordedOrigin,
        Uri recordedUpstream, CancellationToken cancellationToken)
    {
        var expected = DaemonEndpoint.Parse(daemonEndpoint.OriginalString, DaemonEndpointKind.LocalLoopback).Origin;
        if (expected != recordedUpstream)
            throw new SnookException(SnookErrorCode.StoreUnavailable, "The recorded Serve upstream does not match this daemon; no mapping was changed.");
        var current = await InspectAsync(expected, cancellationToken);
        if (current.ServeOrigin != recordedOrigin || current.State != "ready" || current.FunnelConflict)
            throw new SnookException(SnookErrorCode.StoreUnavailable, "The current Serve root is not the exact Snook mapping recorded during setup; no mapping was changed.");
        var result = await _process.RunAsync(["serve", "--https=443", "--set-path=/", "off"], cancellationToken);
        if (result.ExitCode != 0)
            throw new SnookException(SnookErrorCode.StoreUnavailable, "Tailscale refused to disable the exact Snook Serve root mapping.");
        var verified = await InspectAsync(expected, cancellationToken);
        if (verified.CurrentRootProxy is not null)
            throw new SnookException(SnookErrorCode.StoreUnavailable, "The Serve disable command returned success but the root mapping is still present.");
        return verified with { State = "disabled", Guidance = "The recorded Snook Serve root mapping is disabled. No other Serve configuration was changed." };
    }

    internal static (bool Running, bool MagicDnsEnabled, bool HttpsEnabled, Uri? Origin) ParseNodeStatus(string json)
    {
        using var document = ParseBounded(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw InvalidCliJson();
        var running = false;
        if (root.TryGetProperty("BackendState", out var state))
        {
            if (state.ValueKind != JsonValueKind.String) throw InvalidCliJson();
            running = state.GetString() == "Running";
        }
        var magicDns = false;
        if (root.TryGetProperty("CurrentTailnet", out var tailnet))
        {
            if (tailnet.ValueKind != JsonValueKind.Object) throw InvalidCliJson();
            if (tailnet.TryGetProperty("MagicDNSEnabled", out var enabled))
            {
                if (enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw InvalidCliJson();
                magicDns = enabled.GetBoolean();
            }
        }
        Uri? origin = null;
        if (root.TryGetProperty("Self", out var self))
        {
            if (self.ValueKind != JsonValueKind.Object) throw InvalidCliJson();
            if (self.TryGetProperty("DNSName", out var name))
            {
                if (name.ValueKind != JsonValueKind.String) throw InvalidCliJson();
                var host = name.GetString()?.TrimEnd('.').ToLowerInvariant();
                if (host is not null && Uri.TryCreate($"https://{host}/", UriKind.Absolute, out var candidate))
                    try { origin = DaemonEndpoint.Parse(candidate.AbsoluteUri, DaemonEndpointKind.TailscaleServe).Origin; }
                    catch (SnookException) { }
            }
        }
        var https = false;
        if (root.TryGetProperty("CertDomains", out var certs))
        {
            if (certs.ValueKind != JsonValueKind.Array || certs.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                throw InvalidCliJson();
            https = origin is not null && certs.EnumerateArray().Any(item =>
                string.Equals(item.GetString(), origin.Host, StringComparison.OrdinalIgnoreCase));
        }
        return (running, magicDns, https, origin);
    }

    internal static (string? Proxy, bool Funnel) ParseServeStatus(string json, Uri origin)
    {
        using var document = ParseBounded(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw InvalidCliJson();
        var funnel = false;
        if (root.TryGetProperty("AllowFunnel", out var allowFunnel))
        {
            if (allowFunnel.ValueKind != JsonValueKind.Object) throw InvalidCliJson();
            funnel = allowFunnel.EnumerateObject().Any(property =>
                property.Value.ValueKind != JsonValueKind.False && property.Value.ValueKind != JsonValueKind.Null);
        }
        string? proxy = null;
        if (root.TryGetProperty("Web", out var web))
        {
            if (web.ValueKind != JsonValueKind.Object) throw InvalidCliJson();
            foreach (var site in web.EnumerateObject())
            {
                if (!site.Name.Equals(origin.Host + ":443", StringComparison.OrdinalIgnoreCase)) continue;
                if (site.Value.ValueKind != JsonValueKind.Object) throw InvalidCliJson();
                if (!site.Value.TryGetProperty("Handlers", out var handlers)) continue;
                if (handlers.ValueKind != JsonValueKind.Object) throw InvalidCliJson();
                if (!handlers.TryGetProperty("/", out var rootHandler)) continue;
                if (rootHandler.ValueKind != JsonValueKind.Object) throw InvalidCliJson();
                if (!rootHandler.TryGetProperty("Proxy", out var value)) continue;
                if (value.ValueKind != JsonValueKind.String) throw InvalidCliJson();
                proxy = value.GetString();
            }
        }
        return (proxy, funnel);
    }

    private static JsonDocument ParseBounded(string json)
    {
        if (json.Length > 1_048_576) throw new SnookException(SnookErrorCode.StoreUnavailable, "Tailscale CLI JSON exceeded the safe one-megabyte limit.");
        try
        {
            var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Disallow });
            try { RejectDuplicateProperties(document.RootElement); return document; }
            catch { document.Dispose(); throw; }
        }
        catch (JsonException exception) { throw new SnookException(SnookErrorCode.StoreUnavailable, "Tailscale CLI returned malformed or incompatible JSON.", exception); }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static SnookException InvalidCliJson()
        => new(SnookErrorCode.StoreUnavailable, "Tailscale CLI returned malformed or incompatible JSON.");

    private static bool SameUpstream(string value, Uri expected)
    {
        try { return DaemonEndpoint.Parse(value, DaemonEndpointKind.LocalLoopback).Origin == expected; }
        catch (SnookException) { return false; }
    }

    private static string ClassifyFailure(string stderr, bool serveCommand)
    {
        if (stderr.Contains("failed to connect to local tailscaled", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("doesn't appear to be running", StringComparison.OrdinalIgnoreCase))
            return "tailscale-not-running";
        if (stderr.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("logged out", StringComparison.OrdinalIgnoreCase))
            return "tailscale-not-logged-in";
        if (stderr.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("access denied", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("requires root", StringComparison.OrdinalIgnoreCase))
            return "tailscale-permission-denied";
        if (serveCommand && (stderr.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("unknown flag", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("usage:", StringComparison.OrdinalIgnoreCase)))
            return "tailscale-cli-unsupported";
        return serveCommand ? "serve-status-unavailable" : "tailscale-unavailable";
    }

    private static TailscaleServeStatus Failure(string state, Uri upstream, string guidance,
        (bool Running, bool MagicDnsEnabled, bool HttpsEnabled, Uri? Origin)? node = null)
        => new(state, node?.Origin, upstream, node?.Running ?? false, node?.MagicDnsEnabled ?? false,
            node?.HttpsEnabled ?? false, false, null,
            ["tailscale", "serve", "--bg", upstream.AbsoluteUri.TrimEnd('/')], guidance);
}
