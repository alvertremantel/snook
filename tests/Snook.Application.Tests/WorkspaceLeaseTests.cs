using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Snook.Domain;
using Snook.Persistence.Sqlite;
using Xunit;

namespace Snook.Application.Tests;

public sealed class WorkspaceLeaseTests
{
    [Fact]
    public async Task CancelledInitializationReleasesAcquiredOwnership()
    {
        var directory = Directory.CreateTempSubdirectory("snook-owner-cancel-");
        try
        {
            var database = Path.Combine(directory.FullName, "workspace.db");
            await using var cancelled = new SqliteStore(database);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.InitializeAsync(cancellation.Token));
            Assert.False(File.Exists(database));
            Assert.False(File.Exists(database + ".owner"));
            await using var recovered = new SqliteStore(database);
            await recovered.InitializeAsync();
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task SymlinkMarkerCannotTruncateItsTarget()
    {
        if (OperatingSystem.IsWindows()) return; // Creating symlinks may require an elevated test process.
        var directory = Directory.CreateTempSubdirectory("snook-owner-link-");
        try
        {
            var database = Path.Combine(directory.FullName, "workspace.db");
            var target = Path.Combine(directory.FullName, "retained.txt");
            await File.WriteAllTextAsync(target, "retain this content");
            File.CreateSymbolicLink(database + ".owner", target);
            await using var contender = new SqliteStore(database);
            var error = await Assert.ThrowsAsync<SnookException>(() => contender.InitializeAsync());
            Assert.Equal(SnookErrorCode.StoreUnavailable, error.Code);
            Assert.Equal("retain this content", await File.ReadAllTextAsync(target));
            Assert.False(File.Exists(database));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task KilledDaemonReleasesKernelOwnershipWithoutLosingWorkspace()
    {
        var directory = Directory.CreateTempSubdirectory("snook-owner-process-");
        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        using var process = StartProcess("snookd.dll", ["--data-dir", directory.FullName, "--port", port.ToString(CultureInfo.InvariantCulture)]);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var ready = await process.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.NotNull(ready);
            using var readiness = JsonDocument.Parse(ready);
            Assert.True(readiness.RootElement.GetProperty("ready").GetBoolean());
            await using var client = await DaemonConnection.ConnectAsync(directory.FullName,
                endpoint: $"http://127.0.0.1:{port}/", cancellationToken: timeout.Token);
            var before = await client.GetBootstrapAsync(timeout.Token);
            var board = await client.CreateBoardAsync("Retained across owner death", cancellationToken: timeout.Token);
            var database = Path.Combine(directory.FullName, "Snook", "workspace.db");
            await using (var contender = new SqliteStore(database))
            {
                var error = await Assert.ThrowsAsync<SnookException>(() => contender.InitializeAsync(timeout.Token));
                Assert.Equal(SnookErrorCode.StoreUnavailable, error.Code);
            }
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(File.Exists(database + ".owner"));
            await using var recovered = new SnookBackend(new SqliteStore(database));
            await recovered.InitializeAsync(timeout.Token);
            var after = await recovered.GetBootstrapAsync(timeout.Token);
            Assert.Equal(before.Workspace.Id, after.Workspace.Id);
            Assert.Contains(after.Boards, item => item == board);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task DisabledRuntimeLocksFailBeforeTouchingExistingMarkerOrDatabase()
    {
        if (OperatingSystem.IsWindows()) return; // Windows sharing is enforced by the kernel independently of this switch.
        var directory = Directory.CreateTempSubdirectory("snook-disabled-locking-");
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(directory.FullName, "Snook"));
            var database = Path.Combine(workspace.FullName, "workspace.db");
            const string marker = "untouched legacy owner marker";
            await File.WriteAllTextAsync(database + ".owner", marker);
            using var process = StartProcess("snook.dll", ["--no-profile", "--host", "embedded", "--data-dir", directory.FullName, "doctor"], disableLocks: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("StoreUnavailable", await errors, StringComparison.Ordinal);
            await output;
            Assert.False(File.Exists(database));
            Assert.Equal(marker, await File.ReadAllTextAsync(database + ".owner"));
            Assert.Single(Directory.GetFiles(workspace.FullName));
        }
        finally { directory.Delete(true); }
    }

    private static Process StartProcess(string assembly, string[] arguments, bool disableLocks = false)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, assembly));
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (disableLocks) info.Environment["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = "1";
        return Process.Start(info) ?? throw new InvalidOperationException("Test process did not start.");
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("live-reused-pid")]
    [InlineData("version-two")]
    public async Task AbandonedMarkerDoesNotRequirePidToDisappear(string marker)
    {
        var directory = Directory.CreateTempSubdirectory("snook-owner-recovery-");
        try
        {
            var database = Path.Combine(directory.FullName, "workspace.db");
            await File.WriteAllTextAsync(database + ".owner", marker switch
            {
                "empty" => "",
                "live-reused-pid" => $"pid={Environment.ProcessId}\nstarted=2020-01-01T00:00:00Z",
                _ => "lease-version=2\nowner-pid=1"
            });
            await using (var owner = new SqliteStore(database))
            {
                await owner.InitializeAsync();
                await using var contender = new SqliteStore(database);
                var error = await Assert.ThrowsAsync<SnookException>(() => contender.InitializeAsync());
                Assert.Equal(SnookErrorCode.StoreUnavailable, error.Code);
            }
            Assert.False(File.Exists(database + ".owner"));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task LiveLegacyOwnerRemainsExcludedBeforeDatabaseCreation()
    {
        var directory = Directory.CreateTempSubdirectory("snook-legacy-owner-");
        try
        {
            var database = Path.Combine(directory.FullName, "workspace.db");
            using (var legacy = new FileStream(database + ".owner", FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, 128, FileOptions.DeleteOnClose))
            {
                await using var contender = new SqliteStore(database);
                var error = await Assert.ThrowsAsync<SnookException>(() => contender.InitializeAsync());
                Assert.Equal(SnookErrorCode.StoreUnavailable, error.Code);
                Assert.False(File.Exists(database));
                Assert.Equal(0, legacy.Length);
            }
            await using var recovered = new SqliteStore(database);
            await recovered.InitializeAsync();
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task SimultaneousContendersHaveExactlyOneOwner()
    {
        var directory = Directory.CreateTempSubdirectory("snook-owner-race-");
        var database = Path.Combine(directory.FullName, "workspace.db");
        var contenders = Enumerable.Range(0, 12).Select(_ => new SqliteStore(database)).ToArray();
        try
        {
            await File.WriteAllTextAsync(database + ".owner", "abandoned partial marker");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = contenders.Select(owner => Task.Run(async () =>
            {
                await ready.Task;
                try { await owner.InitializeAsync(); return true; }
                catch (SnookException error) when (error.Code == SnookErrorCode.StoreUnavailable) { return false; }
            })).ToArray();
            ready.SetResult();
            Assert.Single((await Task.WhenAll(attempts)).Where(success => success));
        }
        finally
        {
            foreach (var owner in contenders) await owner.DisposeAsync();
            directory.Delete(true);
        }
    }
}
