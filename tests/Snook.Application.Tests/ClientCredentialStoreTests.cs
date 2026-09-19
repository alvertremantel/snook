using Snook.Application;
using Snook.Domain;
using Xunit;

namespace Snook.Application.Tests;

public sealed class ClientCredentialStoreTests
{
    [Fact]
    public async Task SecretServiceUsesStandardInputAndOpaqueReference()
    {
        const string credential = "abcdefghijklmnopqrstuvwxyzABCDEFGH12345678_-";
        var process = new FakeSecretToolProcess(
            new(0, ""), new(0, credential + "\n"), new(0, ""));
        var store = new LinuxSecretServiceCredentialStore(process);
        var reference = await store.StoreAsync(credential, "Travel laptop");
        Assert.StartsWith("secret-service:", reference, StringComparison.Ordinal);
        Assert.Equal(credential, process.Calls[0].StandardInput);
        Assert.DoesNotContain(process.Calls[0].Arguments, argument => argument.Contains(credential, StringComparison.Ordinal));
        Assert.Contains(process.Calls[0].Arguments, argument => argument == "--label=Snook — Travel laptop");

        Assert.Equal(credential, await store.RetrieveAsync(reference));
        await store.DeleteAsync(reference);
        Assert.Equal("store", process.Calls[0].Arguments[0]);
        Assert.Equal("lookup", process.Calls[1].Arguments[0]);
        Assert.Equal("clear", process.Calls[2].Arguments[0]);
        Assert.Equal(process.Calls[0].Arguments[^1], process.Calls[1].Arguments[^1]);
        Assert.Equal(process.Calls[0].Arguments[^1], process.Calls[2].Arguments[^1]);
    }

    [Fact]
    public async Task SecretServiceFailureIsActionableAndOwnerFileFallbackIsExplicit()
    {
        var failed = new LinuxSecretServiceCredentialStore(new FakeSecretToolProcess(new SecretToolResult(1, "")));
        var error = await Assert.ThrowsAsync<SnookException>(() => failed.StoreAsync(
            "abcdefghijklmnopqrstuvwxyzABCDEFGH12345678_-", "Laptop"));
        Assert.Equal(SnookErrorCode.StoreUnavailable, error.Code);
        Assert.Contains("owner-only file fallback", error.Message, StringComparison.Ordinal);

        var root = Directory.CreateTempSubdirectory("snook-credential-routing-");
        try
        {
            Assert.IsType<OwnerOnlyFileCredentialStore>(ClientCredentialStores.ForReference(root.FullName,
                "owner-file:" + Guid.NewGuid().ToString("N")));
            Assert.Throws<SnookException>(() => ClientCredentialStores.ForReference(root.FullName,
                "unknown:" + Guid.NewGuid().ToString("N")));
            var ownerFile = new OwnerOnlyFileCredentialStore(root.FullName);
            await Assert.ThrowsAsync<SnookException>(() => ownerFile.RetrieveAsync("owner-file:" + Guid.Empty.ToString("N")));
        }
        finally { root.Delete(true); }
    }

    private sealed class FakeSecretToolProcess(params SecretToolResult[] results) : ISecretToolProcess
    {
        private readonly Queue<SecretToolResult> _results = new(results);
        public List<Call> Calls { get; } = [];

        public Task<SecretToolResult> RunAsync(IReadOnlyList<string> arguments, string? standardInput,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new(arguments.ToArray(), standardInput));
            return Task.FromResult(_results.Dequeue());
        }
    }

    [Fact]
    public async Task CancelledOwnerFileStoreLeavesNoCredentialArtifact()
    {
        var root = Directory.CreateTempSubdirectory("snook-credential-cancel-");
        try
        {
            var store = new OwnerOnlyFileCredentialStore(root.FullName);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.StoreAsync(
                "abcdefghijklmnopqrstuvwxyzABCDEFGH12345678_-", "Laptop", cancellation.Token));
            var directory = Path.Combine(root.FullName, "Snook", "client-credentials");
            Assert.Empty(Directory.Exists(directory) ? Directory.GetFiles(directory) : []);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task OwnerFileStoreIsPrivateAndRejectsPermissionOrLinkSubstitution()
    {
        var root = Directory.CreateTempSubdirectory("snook-owner-credential-");
        try
        {
            const string credential = "abcdefghijklmnopqrstuvwxyzABCDEFGH12345678_-";
            var store = new OwnerOnlyFileCredentialStore(root.FullName);
            var reference = await store.StoreAsync(credential, "Laptop");
            Assert.Equal(credential, await store.RetrieveAsync(reference));
            var directory = Path.Combine(root.FullName, "Snook", "client-credentials");
            var path = Assert.Single(Directory.GetFiles(directory));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(directory));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                await Assert.ThrowsAsync<SnookException>(() => store.RetrieveAsync(reference));

                File.Delete(path);
                var target = Path.Combine(root.FullName, "target.token");
                await File.WriteAllTextAsync(target, "sentinel");
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.CreateSymbolicLink(path, target);
                await Assert.ThrowsAsync<SnookException>(() => store.RetrieveAsync(reference));
                await Assert.ThrowsAsync<SnookException>(() => store.DeleteAsync(reference));
                Assert.Equal("sentinel", await File.ReadAllTextAsync(target));
            }
            else
            {
                await store.DeleteAsync(reference);
                Assert.False(File.Exists(path));
            }
        }
        finally { root.Delete(true); }
    }

    private sealed record Call(string[] Arguments, string? StandardInput);
}
