using System.Text.Json;
using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;
using Snook.UI;
using Xunit;

namespace Snook.Application.Tests;

public sealed class ClientConnectionTests
{
    [Fact]
    public async Task SavedProfilesArePrivateAtomicRevisionCheckedAndNeverContainCredentials()
    {
        var root = Directory.CreateTempSubdirectory("snook-client-profile-");
        try
        {
            var profiles = new ClientProfileStore(root.FullName);
            Assert.Null((await profiles.ReadAsync()).Profile);
            var first = new ClientConnectionProfile(1, "daemon", root.FullName, null, Path.Combine(root.FullName, "private.token"));
            var saved = await profiles.SaveAsync(first, null);
            Assert.Equal(saved, await profiles.ReadAsync());
            Assert.False(File.Exists(Path.Combine(root.FullName, "Snook", "workspace.db")));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(profiles.ProfilePath));
            var bytes = await File.ReadAllBytesAsync(profiles.ProfilePath);
            var error = await Assert.ThrowsAsync<SnookException>(() => profiles.SaveAsync(first with { Host = "embedded" }, null));
            Assert.Equal(SnookErrorCode.RevisionConflict, error.Code);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(profiles.ProfilePath));
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => profiles.SaveAsync(first, saved.Stamp, cancellation.Token));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(profiles.ProfilePath));
            var updated = await profiles.SaveAsync(first with { Endpoint = "http://127.0.0.1:43872/" }, saved.Stamp);
            Assert.NotEqual(saved.Stamp, updated.Stamp);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(profiles.ProfilePath)!, "*.tmp"));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(profiles.ProfilePath));
            Assert.False(document.RootElement.TryGetProperty("token", out _));
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"version\":1,\"host\":\"daemon\",\"dataDirectory\":\"/tmp\",\"token\":\"secret\"}")]
    [InlineData("{\"version\":2,\"host\":\"daemon\",\"dataDirectory\":\"/tmp\"}")]
    [InlineData("{\"version\":1,\"host\":\"daemon\",\"host\":\"embedded\",\"dataDirectory\":\"/tmp\"}")]
    [InlineData("{\"version\":1,\"host\":\"daemon\",\"dataDirectory\":\"/tmp\",\"endpoint\":\"https://example.com/\"}")]
    public async Task InvalidSavedProfilesFailClosedForCliAndRemainUntouched(string json)
    {
        var root = Directory.CreateTempSubdirectory("snook-bad-profile-");
        try
        {
            var profiles = new ClientProfileStore(root.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(profiles.ProfilePath)!);
            await File.WriteAllTextAsync(profiles.ProfilePath, json);
            MakePrivate(profiles.ProfilePath);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            Assert.Equal(2, await Cli.Program.RunAsync(["--data-dir", root.FullName, "doctor"], stdout, stderr));
            Assert.False(File.Exists(Path.Combine(root.FullName, "Snook", "workspace.db")));
            Assert.Equal(json, await File.ReadAllTextAsync(profiles.ProfilePath));
            Assert.DoesNotContain("secret", stderr.ToString(), StringComparison.Ordinal);
            Assert.Equal(64, await Cli.Program.RunAsync(["--data-dir", root.FullName, "--no-profile", "doctor"], stdout, stderr));
            Assert.False(File.Exists(Path.Combine(root.FullName, "Snook", "workspace.db")));
            // Bypassing a bad profile and selecting embedded is explicit, not fallback.
            Assert.Equal(0, await Cli.Program.RunAsync(["--data-dir", root.FullName, "--no-profile", "--host", "embedded", "doctor"], stdout, stderr));
            Assert.True(File.Exists(Path.Combine(root.FullName, "Snook", "workspace.db")));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void FlagsOverrideEnvironmentThenSavedProfileAndClearingFieldsRestoresDiscovery()
    {
        var root = Path.GetTempPath();
        var saved = new ClientConnectionProfile(1, "daemon", root, "http://127.0.0.1:43871/", Path.Combine(root, "saved.token"));
        var environment = new Dictionary<string, string> { ["SNOOK_DAEMON_PORT"] = "43872", ["SNOOK_DAEMON_TOKEN_FILE"] = Path.Combine(root, "env.token") };
        string? Env(string key) => environment.GetValueOrDefault(key);
        var inherited = ClientProfileStore.Resolve(saved, root, environment: Env);
        Assert.Equal("http://127.0.0.1:43872/", inherited.Endpoint);
        Assert.Equal(environment["SNOOK_DAEMON_TOKEN_FILE"], inherited.TokenFile);
        var explicitValues = ClientProfileStore.Resolve(saved, root, endpoint: "http://127.0.0.1:43873/", tokenFile: "", environment: Env);
        Assert.Equal("http://127.0.0.1:43873/", explicitValues.Endpoint);
        Assert.Null(explicitValues.TokenFile);
        Assert.Null(ClientProfileStore.Resolve(saved, root, endpoint: "", environment: Env).Endpoint);
        environment["SNOOK_DAEMON_PORT"] = "invalid";
        Assert.Throws<SnookException>(() => ClientProfileStore.Resolve(saved, root, environment: Env));
        Assert.Equal("embedded", ClientProfileStore.Resolve(saved, root, host: "embedded", environment: Env).Host);
    }

    [Fact]
    public async Task ProfileLinksAndOversizeFilesAreRejected()
    {
        var root = Directory.CreateTempSubdirectory("snook-profile-paths-");
        try
        {
            var profiles = new ClientProfileStore(root.FullName);
            var profile = new ClientConnectionProfile(1, "daemon", root.FullName);
            await profiles.SaveAsync(profile, null);
            await File.WriteAllTextAsync(profiles.ProfilePath, new string(' ', 16385));
            await Assert.ThrowsAsync<SnookException>(() => profiles.ReadAsync());
            if (!OperatingSystem.IsWindows())
            {
                File.Delete(profiles.ProfilePath);
                var other = Path.Combine(root.FullName, "other.json");
                await File.WriteAllTextAsync(other, "sentinel");
                File.CreateSymbolicLink(profiles.ProfilePath, other);
                await Assert.ThrowsAsync<SnookException>(() => profiles.ReadAsync());
                await Assert.ThrowsAsync<SnookException>(() => profiles.SaveAsync(profile, null));
                Assert.Equal("sentinel", await File.ReadAllTextAsync(other));
            }
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task ConnectionFailureKeepsDaemonDraftAndSettingsSaveDoesNotOpenAWorkspace()
    {
        var root = Directory.CreateTempSubdirectory("snook-connection-draft-");
        try
        {
            var profiles = new ClientProfileStore(root.FullName);
            var profile = new ClientConnectionProfile(1, "daemon", root.FullName);
            await using var view = new ConnectionViewModel(profiles, new(null, null), profile);
            await view.ConnectAsync();
            Assert.True(view.UseDaemon);
            Assert.Null(view.Backend);
            Assert.Contains("token file", view.Status, StringComparison.Ordinal);
            Assert.False(view.IsBusy);
            Assert.Equal(root.FullName, view.DataDirectory);
            Assert.False(Directory.Exists(Path.Combine(root.FullName, "Snook")));
            await view.SaveAsync();
            Assert.Equal(profile, (await profiles.ReadAsync()).Profile);
            Assert.False(File.Exists(Path.Combine(root.FullName, "Snook", "workspace.db")));
            await view.ConnectAsync();
            Assert.Null(view.Backend);
            Assert.True(view.UseDaemon);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task ClosingWhileConnectingDisposesALateBackendAndDoesNotOpenTheMainWindow()
    {
        var root = Directory.CreateTempSubdirectory("snook-connection-cancel-");
        try
        {
            var database = Path.Combine(root.FullName, "owned.db");
            var backend = new SnookBackend(new SqliteStore(database));
            await backend.InitializeAsync();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var view = new ConnectionViewModel(new ClientProfileStore(root.FullName), new(null, null), new(1, "daemon", root.FullName),
                open: async (_, _) => { await release.Task; return backend; });
            var connected = false;
            view.Connected += (_, _) => connected = true;
            var attempt = view.ConnectAsync();
            Assert.Same(attempt, view.ConnectAsync());
            var closing = view.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            release.SetResult();
            await Task.WhenAll(attempt, closing);
            Assert.False(connected);
            Assert.Null(view.Backend);
            await using var reopened = new SnookBackend(new SqliteStore(database));
            await reopened.InitializeAsync();
        }
        finally { root.Delete(true); }
    }

    internal static async Task ExerciseDaemonAsync(string daemonRoot, Uri endpoint, string tokenPath)
    {
        var root = Directory.CreateTempSubdirectory("snook-saved-daemon-");
        try
        {
            var profiles = new ClientProfileStore(root.FullName);
            // A separate client root proves no SQLite database is opened locally.
            var profile = new ClientConnectionProfile(1, "daemon", root.FullName, endpoint.AbsoluteUri, tokenPath);
            await profiles.SaveAsync(profile, null);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            Assert.Equal(0, await Cli.Program.RunAsync(["--data-dir", root.FullName, "doctor"], stdout, stderr));
            Assert.Contains("daemon-client", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains(endpoint.AbsoluteUri, stdout.ToString(), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(root.FullName, "Snook", "workspace.db")));
            var discovered = new ClientProfileStore(daemonRoot);
            var original = await discovered.ReadAsync();
            await discovered.SaveAsync(new(1, "daemon", daemonRoot), original.Stamp);
            stdout.GetStringBuilder().Clear();
            Assert.Equal(0, await Cli.Program.RunAsync(["--data-dir", daemonRoot, "doctor"], stdout, stderr));
            Assert.Contains(endpoint.AbsoluteUri, stdout.ToString(), StringComparison.Ordinal);
            await using var view = new ConnectionViewModel(profiles, await profiles.ReadAsync(), profile with { TokenFile = Path.Combine(root.FullName, "missing.token") });
            await view.ConnectAsync();
            Assert.Null(view.Backend);
            view.TokenFile = tokenPath;
            await view.ConnectAsync();
            Assert.NotNull(view.Backend);
            Assert.True((await view.Backend.GetBootstrapAsync()).Capabilities.DaemonClient);
            await view.Backend.DisposeAsync();
            Assert.False(File.Exists(Path.Combine(root.FullName, "Snook", "workspace.db")));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task ClientCreatedOnANonPumpingUiContextCanDrainItsListenerDuringSynchronousShutdown()
    {
        await Task.Run(() =>
        {
            using var handler = new WaitingHealthHandler();
            SynchronizationContext.SetSynchronizationContext(new NonPumpingContext());
            try
            {
                var client = new DaemonBackendClient(new Uri("http://127.0.0.1:43871/"), "test-token", handler);
                Assert.True(handler.Started.Wait(TimeSpan.FromSeconds(5)));
                var first = client.DisposeAsync().AsTask();
                var second = client.DisposeAsync().AsTask();
                Assert.Same(first, second);
                first.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            finally { SynchronizationContext.SetSynchronizationContext(null); }
        }).WaitAsync(TimeSpan.FromSeconds(15));
    }

    private sealed class NonPumpingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) { }
    }

    private sealed class WaitingHealthHandler : HttpMessageHandler
    {
        public ManualResetEventSlim Started { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.Set();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Started.Dispose();
            base.Dispose(disposing);
        }
    }

    private static void MakePrivate(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
