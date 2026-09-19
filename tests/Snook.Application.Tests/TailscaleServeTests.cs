using System.Text.Json;
using Snook.Cli;
using Xunit;

namespace Snook.Application.Tests;

public sealed class TailscaleServeTests
{
    private static readonly string[] ExpectedApply = ["serve", "--bg", "http://127.0.0.1:43871"];
    private static readonly string[] ExpectedDisable = ["serve", "--https=443", "--set-path=/", "off"];
    private const string NodeStatus = """
        {
          "BackendState": "Running",
          "Self": { "DNSName": "snook-host.example-tailnet.ts.net." },
          "CurrentTailnet": { "MagicDNSEnabled": true },
          "CertDomains": ["snook-host.example-tailnet.ts.net"]
        }
        """;

    [Fact]
    public void CurrentStatusShapesProduceCanonicalOriginAndExactRootMapping()
    {
        var node = TailscaleServeManager.ParseNodeStatus(NodeStatus);
        Assert.True(node.Running);
        Assert.True(node.MagicDnsEnabled);
        Assert.True(node.HttpsEnabled);
        Assert.Equal("https://snook-host.example-tailnet.ts.net/", node.Origin?.AbsoluteUri);
        var origin = Assert.IsType<Uri>(node.Origin);
        var serve = TailscaleServeManager.ParseServeStatus("""
            {
              "TCP": { "443": { "HTTPS": true } },
              "Web": {
                "snook-host.example-tailnet.ts.net:443": {
                  "Handlers": { "/": { "Proxy": "http://127.0.0.1:43871" } }
                }
              },
              "AllowFunnel": {}
            }
            """, origin);
        Assert.Equal("http://127.0.0.1:43871", serve.Proxy);
        Assert.False(serve.Funnel);

        var status = new TailscaleServeStatus("ready", origin, new Uri("http://127.0.0.1:43871/"),
            true, true, true, false, serve.Proxy, ExpectedApply, "ready");
        var next = Cli.Program.BuildTailnetNextSteps(status);
        Assert.Contains(origin.AbsoluteUri, next.HostPairingCommand, StringComparison.Ordinal);
        Assert.Contains("pair create DEVICE_NAME", next.HostPairingCommand, StringComparison.Ordinal);
        Assert.Contains(origin.AbsoluteUri, next.ClientPairingCommand, StringComparison.Ordinal);
        Assert.Contains("PRIVATE_CODE_FILE", next.ClientPairingCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("daemon.token", next.HostPairingCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetupIsIdempotentAndRefusesConflictOrFunnelWithoutMutation()
    {
        var correct = Serve("http://127.0.0.1:43871");
        var idempotentProcess = new FakeProcess(
            new(0, NodeStatus, ""), new(0, correct, ""));
        var manager = new TailscaleServeManager(idempotentProcess);
        var ready = await manager.ApplyAsync(new Uri("http://127.0.0.1:43871/"), CancellationToken.None);
        Assert.Equal("ready", ready.State);
        Assert.Equal(2, idempotentProcess.Calls.Count);

        var conflictingProcess = new FakeProcess(
            new(0, NodeStatus, ""), new(0, Serve("http://127.0.0.1:9999"), ""));
        var conflict = await new TailscaleServeManager(conflictingProcess)
            .InspectAsync(new Uri("http://127.0.0.1:43871/"), CancellationToken.None);
        Assert.Equal("root-conflict", conflict.State);
        Assert.Equal(2, conflictingProcess.Calls.Count);

        var wrongLoopbackProcess = new FakeProcess(
            new(0, NodeStatus, ""), new(0, Serve("http://[::1]:43871"), ""));
        var wrongLoopback = await new TailscaleServeManager(wrongLoopbackProcess)
            .InspectAsync(new Uri("http://127.0.0.1:43871/"), CancellationToken.None);
        Assert.Equal("root-conflict", wrongLoopback.State);

        var shorthandProcess = new FakeProcess(
            new(0, NodeStatus, ""), new(0, Serve("http://127.1:43871"), ""));
        var shorthand = await new TailscaleServeManager(shorthandProcess)
            .InspectAsync(new Uri("http://127.0.0.1:43871/"), CancellationToken.None);
        Assert.Equal("root-conflict", shorthand.State);

        var funnelProcess = new FakeProcess(new(0, NodeStatus, ""), new(0, """
            { "AllowFunnel": { "snook-host.example-tailnet.ts.net:443": true } }
            """, ""));
        var funnel = await new TailscaleServeManager(funnelProcess)
            .InspectAsync(new Uri("http://127.0.0.1:43871/"), CancellationToken.None);
        Assert.Equal("funnel-conflict", funnel.State);
        Assert.Equal(2, funnelProcess.Calls.Count);
    }

    [Fact]
    public async Task ExplicitApplyUsesArgumentListAndVerifiesExactResult()
    {
        var process = new FakeProcess(
            new(0, NodeStatus, ""), new(0, "{}", ""),
            new(0, "", ""),
            new(0, NodeStatus, ""), new(0, Serve("http://127.0.0.1:43871"), ""));
        var status = await new TailscaleServeManager(process)
            .ApplyAsync(new Uri("http://127.0.0.1:43871/"), CancellationToken.None);
        Assert.Equal("ready", status.State);
        Assert.Equal(ExpectedApply, process.Calls[2]);
        Assert.DoesNotContain(process.Calls.SelectMany(call => call), argument => argument.Contains("funnel", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(process.Calls.SelectMany(call => call), argument => argument == "reset");
    }

    [Fact]
    public async Task SetupPreconditionCompletesBeforeMutationAndFailurePreventsApply()
    {
        var process = new FakeProcess(
            new(0, NodeStatus, ""), new(0, "{}", ""), new(0, "", ""),
            new(0, NodeStatus, ""), new(0, Serve("http://127.0.0.1:43871"), ""));
        var callbackCompleted = false;
        var status = await new TailscaleServeManager(process).ApplyAsync(
            new Uri("http://127.0.0.1:43871/"), (planned, _) =>
            {
                Assert.Equal("ready-to-apply", planned.State);
                Assert.Equal(2, process.Calls.Count);
                callbackCompleted = true;
                return Task.CompletedTask;
            }, CancellationToken.None);
        Assert.True(callbackCompleted);
        Assert.Equal("ready", status.State);
        Assert.Equal(ExpectedApply, process.Calls[2]);

        var rejectedProcess = new FakeProcess(new(0, NodeStatus, ""), new(0, "{}", ""));
        await Assert.ThrowsAsync<IOException>(() => new TailscaleServeManager(rejectedProcess).ApplyAsync(
            new Uri("http://127.0.0.1:43871/"), (_, _) => throw new IOException("receipt unavailable"),
            CancellationToken.None));
        Assert.Equal(2, rejectedProcess.Calls.Count);
    }

    [Fact]
    public async Task HttpsConsentAndFailedOrUnverifiedApplyNeverClaimReady()
    {
        var withoutCertificate = NodeStatus.Replace(
            "\"CertDomains\": [\"snook-host.example-tailnet.ts.net\"]", "\"CertDomains\": []", StringComparison.Ordinal);
        var consentProcess = new FakeProcess(new(0, withoutCertificate, ""), new(0, "{}", ""));
        var consentError = await Assert.ThrowsAsync<Snook.Domain.SnookException>(() => new TailscaleServeManager(consentProcess)
            .ApplyAsync(new Uri("http://127.0.0.1:43871/"), CancellationToken.None));
        Assert.Contains("consent", consentError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, consentProcess.Calls.Count);

        var failedProcess = new FakeProcess(
            new(0, NodeStatus, ""), new(0, "{}", ""), new(1, "", "permission denied"));
        await Assert.ThrowsAsync<Snook.Domain.SnookException>(() => new TailscaleServeManager(failedProcess)
            .ApplyAsync(new Uri("http://127.0.0.1:43871/"), CancellationToken.None));
        Assert.Equal(ExpectedApply, failedProcess.Calls[2]);

        var unverifiedProcess = new FakeProcess(
            new(0, NodeStatus, ""), new(0, "{}", ""), new(0, "", ""),
            new(0, NodeStatus, ""), new(0, "{}", ""));
        await Assert.ThrowsAsync<Snook.Domain.SnookException>(() => new TailscaleServeManager(unverifiedProcess)
            .ApplyAsync(new Uri("http://127.0.0.1:43871/"), CancellationToken.None));
        Assert.Equal(5, unverifiedProcess.Calls.Count);
    }

    [Fact]
    public void StatusParsingIsBoundedAndRejectsMalformedOrIncompatibleJson()
    {
        Assert.Throws<Snook.Domain.SnookException>(() => TailscaleServeManager.ParseNodeStatus("{"));
        Assert.Throws<Snook.Domain.SnookException>(() => TailscaleServeManager.ParseNodeStatus(new string(' ', 1_048_577)));
        foreach (var invalid in new[]
        {
            "[]",
            "{\"BackendState\":1}",
            "{\"CurrentTailnet\":[],\"Self\":{}}",
            "{\"Self\":{\"DNSName\":false}}",
            "{\"CertDomains\":[{}]}",
            "{\"BackendState\":\"Running\",\"BackendState\":\"Stopped\"}"
        })
            Assert.Throws<Snook.Domain.SnookException>(() => TailscaleServeManager.ParseNodeStatus(invalid));
        var node = TailscaleServeManager.ParseNodeStatus(NodeStatus);
        foreach (var invalid in new[]
        {
            "[]",
            "{\"AllowFunnel\":true}",
            "{\"Web\":[]}",
            "{\"Web\":{\"snook-host.example-tailnet.ts.net:443\":{\"Handlers\":1}}}",
            "{\"Web\":{\"snook-host.example-tailnet.ts.net:443\":{\"Handlers\":{\"/\":{\"Proxy\":1}}}}}",
            "{\"Web\":{},\"Web\":{}}"
        })
            Assert.Throws<Snook.Domain.SnookException>(() => TailscaleServeManager.ParseServeStatus(invalid, node.Origin!));
        var unrelated = TailscaleServeManager.ParseServeStatus(Serve("http://127.0.0.1:43871")
            .Replace("snook-host.example-tailnet.ts.net:443", "other.example-tailnet.ts.net:443", StringComparison.Ordinal), node.Origin!);
        Assert.Null(unrelated.Proxy);
        Assert.False(unrelated.Funnel);
    }

    [Theory]
    [InlineData("failed to connect to local tailscaled; it doesn't appear to be running", "tailscale-not-running")]
    [InlineData("not logged in", "tailscale-not-logged-in")]
    [InlineData("permission denied", "tailscale-permission-denied")]
    public async Task NodeStatusFailuresAreActionablyClassified(string stderr, string expected)
    {
        var status = await new TailscaleServeManager(new FakeProcess(new ProcessResult(1, "", stderr)))
            .InspectAsync(new Uri("http://127.0.0.1:43871/"), CancellationToken.None);
        Assert.Equal(expected, status.State);
    }

    [Theory]
    [InlineData("unknown flag: --json", "tailscale-cli-unsupported")]
    [InlineData("permission denied", "tailscale-permission-denied")]
    public async Task ServeStatusFailuresAreActionablyClassified(string stderr, string expected)
    {
        var status = await new TailscaleServeManager(new FakeProcess(
            new ProcessResult(0, NodeStatus, ""), new ProcessResult(1, "", stderr)))
            .InspectAsync(new Uri("http://127.0.0.1:43871/"), CancellationToken.None);
        Assert.Equal(expected, status.State);
    }

    [Fact]
    public async Task DisableRequiresRecordedExactMappingAndRemovesOnlyRoot()
    {
        var process = new FakeProcess(
            new(0, NodeStatus, ""), new(0, Serve("http://127.0.0.1:43871"), ""),
            new(0, "", ""),
            new(0, NodeStatus, ""), new(0, "{}", ""));
        var status = await new TailscaleServeManager(process).DisableExactAsync(
            new Uri("http://127.0.0.1:43871/"), new Uri("https://snook-host.example-tailnet.ts.net/"),
            new Uri("http://127.0.0.1:43871/"), CancellationToken.None);
        Assert.Equal("disabled", status.State);
        Assert.Equal(ExpectedDisable, process.Calls[2]);

        var mismatch = new TailscaleServeManager(new FakeProcess());
        await Assert.ThrowsAsync<Snook.Domain.SnookException>(() => mismatch.DisableExactAsync(
            new Uri("http://127.0.0.1:43871/"), new Uri("https://snook-host.example-tailnet.ts.net/"),
            new Uri("http://127.0.0.1:43872/"), CancellationToken.None));
    }

    [Fact]
    public async Task ServeReceiptIsPrivateAtomicStrictAndNonLinkFollowing()
    {
        var root = Directory.CreateTempSubdirectory("snook-serve-receipt-");
        try
        {
            var path = Path.Combine(root.FullName, "Snook", "tailscale-serve.json");
            var origin = new Uri("https://snook-host.example-tailnet.ts.net/");
            var upstream = new Uri("http://127.0.0.1:43871/");
            await Cli.Program.WriteServeReceiptAsync(path, origin, upstream, CancellationToken.None);
            var read = await Cli.Program.ReadServeReceiptAsync(path, CancellationToken.None);
            Assert.Equal(origin, read.Origin);
            Assert.Equal(upstream, read.Upstream);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

            await Cli.Program.EnsureServeReceiptAsync(path, origin, upstream, CancellationToken.None);
            var beforeConflict = await File.ReadAllBytesAsync(path);
            var conflict = await Assert.ThrowsAsync<Snook.Domain.SnookException>(() =>
                Cli.Program.EnsureServeReceiptAsync(path,
                    new Uri("https://other.example-tailnet.ts.net/"), upstream, CancellationToken.None));
            Assert.Contains("different", conflict.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(beforeConflict, await File.ReadAllBytesAsync(path));

            var original = await File.ReadAllBytesAsync(path);
            using (var cancelled = new CancellationTokenSource())
            {
                await cancelled.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Cli.Program.WriteServeReceiptAsync(
                    path, origin, upstream, cancelled.Token));
            }
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));

            foreach (var invalid in new[]
            {
                "{\"version\":2,\"origin\":\"https://snook-host.example-tailnet.ts.net/\",\"upstream\":\"http://127.0.0.1:43871/\"}",
                "{\"version\":1,\"origin\":\"https://snook-host.example-tailnet.ts.net/\",\"upstream\":\"http://127.0.0.1:43871/\",\"future\":true}",
                "{\"version\":1,\"version\":1,\"origin\":\"https://snook-host.example-tailnet.ts.net/\",\"upstream\":\"http://127.0.0.1:43871/\"}",
                "{\"version\":1,\"origin\":\"https://example.com/\",\"upstream\":\"http://127.0.0.1:43871/\"}"
            })
            {
                await File.WriteAllTextAsync(path, invalid);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await Assert.ThrowsAsync<Snook.Domain.SnookException>(() => Cli.Program.ReadServeReceiptAsync(path, CancellationToken.None));
            }

            if (!OperatingSystem.IsWindows())
            {
                File.Delete(path);
                var target = Path.Combine(root.FullName, "target.json");
                await File.WriteAllTextAsync(target, "sentinel");
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.CreateSymbolicLink(path, target);
                await Assert.ThrowsAsync<Snook.Domain.SnookException>(() => Cli.Program.ReadServeReceiptAsync(path, CancellationToken.None));
                Assert.Equal("sentinel", await File.ReadAllTextAsync(target));
            }
        }
        finally { root.Delete(true); }
    }

    private static string Serve(string proxy) => JsonSerializer.Serialize(new
    {
        Web = new Dictionary<string, object>
        {
            ["snook-host.example-tailnet.ts.net:443"] = new { Handlers = new Dictionary<string, object> { ["/"] = new { Proxy = proxy } } }
        },
        AllowFunnel = new Dictionary<string, bool>()
    });

    private sealed class FakeProcess(params ProcessResult[] results) : ITailscaleProcess
    {
        private readonly Queue<ProcessResult> _results = new(results);
        public List<string[]> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(arguments.ToArray());
            return Task.FromResult(_results.Dequeue());
        }
    }
}
