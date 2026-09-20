using System.Security.Cryptography;
using Java.Security;
using Javax.Crypto;

namespace Snook.Android;

internal static class KeystoreSpike
{
    private const string ProbeAlias = "org.snook.spike.secret.v1";
    private const string RemovedAlias = "org.snook.spike.removed-key-test.v1";

    public static async Task<string> RunAsync(string privateDirectory)
    {
        var protector = new KeystoreProtector(ProbeAlias);
        var file = Path.Combine(privateDirectory, "keystore-probe.bin");
        byte[] envelope;
        if (File.Exists(file))
        {
            // Bound before allocating and again during read; do not trust a restored file.
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != 61) throw new InvalidOperationException("Invalid probe envelope size.");
            envelope = new byte[61];
            await stream.ReadExactlyAsync(envelope);
            if (stream.ReadByte() != -1) throw new InvalidOperationException("Probe envelope grew during read.");
        }
        else
        {
            var initial = RandomNumberGenerator.GetBytes(32);
            try { envelope = protector.Protect(initial, "paired-profile-probe"); }
            finally { CryptographicOperations.ZeroMemory(initial); }
            var staging = file + ".staging";
            try
            {
                await using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await stream.WriteAsync(envelope);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(staging, file);
            }
            finally { File.Delete(staging); }
        }

        var secret = protector.Unprotect(envelope, "paired-profile-probe");
        try
        {
            if (secret.Length != 32) throw new InvalidOperationException("Unexpected probe secret size.");
            var second = protector.Protect(secret, "paired-profile-probe");
            if (second.AsSpan(1, 12).SequenceEqual(envelope.AsSpan(1, 12)))
                throw new InvalidOperationException("Encryption reused a nonce.");
            var restored = protector.Unprotect(second, "paired-profile-probe");
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(secret, restored))
                    throw new InvalidOperationException("Keystore round trip changed the secret.");
            }
            finally { CryptographicOperations.ZeroMemory(restored); }

            var tampered = (byte[])envelope.Clone();
            tampered[^1] ^= 1;
            RequireAuthenticationFailure(protector, tampered, "paired-profile-probe");
            RequireAuthenticationFailure(protector, envelope, "different-profile");

            // Invalidate only this isolated throwaway alias, never the persisted probe key.
            var removed = new KeystoreProtector(RemovedAlias);
            var invalidatedEnvelope = removed.Protect(secret, "invalidation-probe");
            using var keyStore = KeyStore.GetInstance("AndroidKeyStore")
                ?? throw new InvalidOperationException("Android Keystore is unavailable.");
            keyStore.Load(null);
            keyStore.DeleteEntry(RemovedAlias);
            var rejected = false;
            try
            {
                var unexpected = removed.Unprotect(invalidatedEnvelope, "invalidation-probe");
                CryptographicOperations.ZeroMemory(unexpected);
            }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected || keyStore.ContainsAlias(RemovedAlias))
                throw new InvalidOperationException("Reading regenerated a removed key.");
            return Convert.ToHexString(SHA256.HashData(secret));
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    private static void RequireAuthenticationFailure(KeystoreProtector protector, byte[] envelope, string purpose)
    {
        try
        {
            var unexpected = protector.Unprotect(envelope, purpose);
            CryptographicOperations.ZeroMemory(unexpected);
        }
        catch (AEADBadTagException) { return; }
        throw new InvalidOperationException("An unauthenticated secret was accepted.");
    }
}
