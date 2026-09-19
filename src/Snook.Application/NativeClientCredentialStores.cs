using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Snook.Domain;

namespace Snook.Application;

public static class ClientCredentialStores
{
    public static IClientCredentialStore CreateNative()
    {
        if (OperatingSystem.IsWindows()) return new WindowsCredentialManagerStore();
        if (OperatingSystem.IsMacOS()) return new MacOSKeychainCredentialStore();
        if (OperatingSystem.IsLinux()) return new LinuxSecretServiceCredentialStore();
        throw Unavailable("No native protected credential-store adapter is available for this platform.");
    }

    public static IClientCredentialStore ForReference(string dataDirectory, string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (reference.StartsWith("owner-file:", StringComparison.Ordinal)) return new OwnerOnlyFileCredentialStore(dataDirectory);
        if (reference.StartsWith("secret-service:", StringComparison.Ordinal))
            return OperatingSystem.IsLinux() ? new LinuxSecretServiceCredentialStore() : throw WrongPlatform();
        if (reference.StartsWith("windows-credential:", StringComparison.Ordinal))
            return OperatingSystem.IsWindows() ? new WindowsCredentialManagerStore() : throw WrongPlatform();
        if (reference.StartsWith("macos-keychain:", StringComparison.Ordinal))
            return OperatingSystem.IsMacOS() ? new MacOSKeychainCredentialStore() : throw WrongPlatform();
        throw new SnookException(SnookErrorCode.ValidationFailed, "The saved credential-store reference type is unknown.");
    }

    public static string NativeStoreName => OperatingSystem.IsWindows() ? "Windows Credential Manager"
        : OperatingSystem.IsMacOS() ? "macOS Keychain"
        : OperatingSystem.IsLinux() ? "Linux Secret Service"
        : "unavailable native store";

    internal static string NormalizeCredential(string credential)
    {
        var value = credential.Trim('\uFEFF', '\r', '\n', ' ');
        if (value.Length is < 32 or > 512
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new SnookException(SnookErrorCode.ValidationFailed, "The device credential has an invalid format.");
        return value;
    }

    internal static string NormalizeLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new SnookException(SnookErrorCode.ValidationFailed, "A credential label is required.");
        label = label.Trim();
        if (label.Length > 100 || label.Any(char.IsControl))
            throw new SnookException(SnookErrorCode.ValidationFailed, "The credential label is too long or contains control characters.");
        return label;
    }

    internal static Guid ParseReference(string reference, string prefix)
        => reference.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(reference[prefix.Length..], "N", out var id) && id != Guid.Empty
                ? id
                : throw new SnookException(SnookErrorCode.ValidationFailed, "The protected credential reference is invalid.");

    internal static SnookException Unavailable(string message, Exception? inner = null)
    {
        message += " Install/unlock the platform credential service, or explicitly select the owner-only file fallback.";
        return inner is null ? new SnookException(SnookErrorCode.StoreUnavailable, message)
            : new SnookException(SnookErrorCode.StoreUnavailable, message, inner);
    }

    private static SnookException WrongPlatform()
        => new(SnookErrorCode.StoreUnavailable, "The saved credential belongs to a protected store on another platform. Re-pair this device; SQLite was not opened.");
}

internal sealed record SecretToolResult(int ExitCode, string StandardOutput);

internal interface ISecretToolProcess
{
    Task<SecretToolResult> RunAsync(IReadOnlyList<string> arguments, string? standardInput, CancellationToken cancellationToken);
}

internal sealed class SecretToolProcess : ISecretToolProcess
{
    private const int MaximumOutputCharacters = 8192;

    public async Task<SecretToolResult> RunAsync(IReadOnlyList<string> arguments, string? standardInput,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("secret-tool")
            {
                UseShellExecute = false,
                RedirectStandardInput = standardInput is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) throw ClientCredentialStores.Unavailable("Linux Secret Service could not be started.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw ClientCredentialStores.Unavailable("The `secret-tool` Secret Service client was not found.", exception);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            var stderr = ReadBoundedAsync(process.StandardError, deadline.Token);
            if (standardInput is not null)
            {
                await process.StandardInput.WriteLineAsync(standardInput.AsMemory(), deadline.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(deadline.Token);
            _ = await stderr; // Drain, but never surface possibly sensitive external text.
            return new SecretToolResult(process.ExitCode, await stdout);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw ClientCredentialStores.Unavailable("Linux Secret Service did not respond within 15 seconds.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var value = new StringBuilder();
        var buffer = new char[1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return value.ToString();
            if (value.Length + read > MaximumOutputCharacters)
                throw ClientCredentialStores.Unavailable("Linux Secret Service returned oversized output.");
            value.Append(buffer, 0, read);
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { }
    }
}

internal sealed class LinuxSecretServiceCredentialStore(ISecretToolProcess? process = null) : IClientCredentialStore
{
    private const string Prefix = "secret-service:";
    private readonly ISecretToolProcess _process = process ?? new SecretToolProcess();

    public async Task<string> StoreAsync(string credential, string label, CancellationToken cancellationToken = default)
    {
        EnsurePlatform(process is not null);
        credential = ClientCredentialStores.NormalizeCredential(credential);
        label = ClientCredentialStores.NormalizeLabel(label);
        var id = Guid.NewGuid();
        var result = await _process.RunAsync(["store", $"--label=Snook — {label}", "service", "snook", "credential", id.ToString("N")],
            credential, cancellationToken);
        if (result.ExitCode != 0) throw ClientCredentialStores.Unavailable("Linux Secret Service refused to store the credential.");
        return Prefix + id.ToString("N");
    }

    public async Task<string> RetrieveAsync(string reference, CancellationToken cancellationToken = default)
    {
        EnsurePlatform(process is not null);
        var id = ClientCredentialStores.ParseReference(reference, Prefix);
        var result = await _process.RunAsync(["lookup", "service", "snook", "credential", id.ToString("N")], null, cancellationToken);
        if (result.ExitCode != 0) throw ClientCredentialStores.Unavailable("The Linux Secret Service credential is unavailable.");
        return ClientCredentialStores.NormalizeCredential(result.StandardOutput);
    }

    public async Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        EnsurePlatform(process is not null);
        var id = ClientCredentialStores.ParseReference(reference, Prefix);
        var result = await _process.RunAsync(["clear", "service", "snook", "credential", id.ToString("N")], null, cancellationToken);
        if (result.ExitCode != 0) throw ClientCredentialStores.Unavailable("Linux Secret Service could not remove the credential.");
    }

    private static void EnsurePlatform(bool injected)
    {
        if (!injected && !OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
    }
}

internal sealed class WindowsCredentialManagerStore : IClientCredentialStore
{
    private const string Prefix = "windows-credential:";
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public Task<string> StoreAsync(string credential, string label, CancellationToken cancellationToken = default)
    {
        EnsurePlatform();
        cancellationToken.ThrowIfCancellationRequested();
        credential = ClientCredentialStores.NormalizeCredential(credential);
        label = ClientCredentialStores.NormalizeLabel(label);
        var id = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes(credential);
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var native = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = Target(id),
                Comment = "Snook paired device: " + label,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = pinned.AddrOfPinnedObject(),
                Persist = CredentialPersistLocalMachine,
                UserName = "Snook"
            };
            if (!CredWrite(ref native, 0)) throw NativeFailure("Windows Credential Manager refused to store the credential.");
            return Task.FromResult(Prefix + id.ToString("N"));
        }
        finally { pinned.Free(); CryptographicOperations.ZeroMemory(bytes); }
    }

    public Task<string> RetrieveAsync(string reference, CancellationToken cancellationToken = default)
    {
        EnsurePlatform();
        cancellationToken.ThrowIfCancellationRequested();
        var id = ClientCredentialStores.ParseReference(reference, Prefix);
        if (!CredRead(Target(id), CredentialTypeGeneric, 0, out var pointer))
            throw NativeFailure("The Windows Credential Manager credential is unavailable.");
        try
        {
            var native = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (native.CredentialBlobSize is 0 or > 1024) throw ClientCredentialStores.Unavailable("The Windows credential has invalid contents.");
            var bytes = new byte[native.CredentialBlobSize];
            Marshal.Copy(native.CredentialBlob, bytes, 0, bytes.Length);
            try { return Task.FromResult(ClientCredentialStores.NormalizeCredential(Encoding.UTF8.GetString(bytes))); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        EnsurePlatform();
        cancellationToken.ThrowIfCancellationRequested();
        var id = ClientCredentialStores.ParseReference(reference, Prefix);
        if (!CredDelete(Target(id), CredentialTypeGeneric, 0) && Marshal.GetLastWin32Error() != ErrorNotFound)
            throw NativeFailure("Windows Credential Manager could not remove the credential.");
        return Task.CompletedTask;
    }

    private static string Target(Guid id) => "Snook/paired-device/" + id.ToString("N");
    private static void EnsurePlatform() { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(); }
    private static SnookException NativeFailure(string message)
        => ClientCredentialStores.Unavailable(message, new Win32Exception(Marshal.GetLastWin32Error()));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}

internal sealed class MacOSKeychainCredentialStore : IClientCredentialStore
{
    private const string Prefix = "macos-keychain:";
    private const int ItemNotFound = -25300;
    private static readonly byte[] Service = "Snook"u8.ToArray();

    public Task<string> StoreAsync(string credential, string label, CancellationToken cancellationToken = default)
    {
        EnsurePlatform();
        cancellationToken.ThrowIfCancellationRequested();
        credential = ClientCredentialStores.NormalizeCredential(credential);
        _ = ClientCredentialStores.NormalizeLabel(label);
        var id = Guid.NewGuid();
        var account = Encoding.UTF8.GetBytes(id.ToString("N"));
        var secret = Encoding.UTF8.GetBytes(credential);
        try
        {
            var status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)Service.Length, Service,
                (uint)account.Length, account, (uint)secret.Length, secret, out var item);
            if (item != IntPtr.Zero) CFRelease(item);
            if (status != 0) throw NativeFailure("macOS Keychain refused to store the credential.", status);
            return Task.FromResult(Prefix + id.ToString("N"));
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public Task<string> RetrieveAsync(string reference, CancellationToken cancellationToken = default)
    {
        EnsurePlatform();
        cancellationToken.ThrowIfCancellationRequested();
        var id = ClientCredentialStores.ParseReference(reference, Prefix);
        var account = Encoding.UTF8.GetBytes(id.ToString("N"));
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service,
            (uint)account.Length, account, out var length, out var data, out var item);
        if (status != 0) throw NativeFailure("The macOS Keychain credential is unavailable.", status);
        try
        {
            if (length is 0 or > 1024) throw ClientCredentialStores.Unavailable("The macOS Keychain credential has invalid contents.");
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            try { return Task.FromResult(ClientCredentialStores.NormalizeCredential(Encoding.UTF8.GetString(bytes))); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally
        {
            _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        EnsurePlatform();
        cancellationToken.ThrowIfCancellationRequested();
        var id = ClientCredentialStores.ParseReference(reference, Prefix);
        var account = Encoding.UTF8.GetBytes(id.ToString("N"));
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service,
            (uint)account.Length, account, out _, out var data, out var item);
        if (status == ItemNotFound) return Task.CompletedTask;
        if (status != 0) throw NativeFailure("macOS Keychain could not find the credential to remove.", status);
        try
        {
            _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
            status = SecKeychainItemDelete(item);
            if (status != 0) throw NativeFailure("macOS Keychain could not remove the credential.", status);
            return Task.CompletedTask;
        }
        finally { if (item != IntPtr.Zero) CFRelease(item); }
    }

    private static void EnsurePlatform() { if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(); }
    private static SnookException NativeFailure(string message, int status)
        => ClientCredentialStores.Unavailable($"{message} OSStatus {status}.");

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, uint passwordLength, byte[] password, out IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr value);
}
