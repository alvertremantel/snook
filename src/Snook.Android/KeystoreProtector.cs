using System.Text;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace Snook.Android;

// Native primitive for the Phase 0 spike. Credential/profile orchestration and
// app-lock policy are separate gates; no workspace secrets are enrolled yet.
internal sealed class KeystoreProtector
{
    private const string Provider = "AndroidKeyStore";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int DefaultMaximumSecretSize = 4096;
    private const int AbsoluteMaximumSecretSize = 64 * 1024;
    private static readonly object KeyGate = new();
    private readonly string _alias;
    private readonly int _maximumSecretSize;

    public KeystoreProtector(string alias, int maximumSecretSize = DefaultMaximumSecretSize)
    {
        if (string.IsNullOrEmpty(alias) || alias.Length > 96
            || alias.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-'))
            throw new ArgumentException("Invalid key alias.", nameof(alias));
        if (maximumSecretSize is < 1 or > AbsoluteMaximumSecretSize)
            throw new ArgumentOutOfRangeException(nameof(maximumSecretSize));
        _alias = alias;
        _maximumSecretSize = maximumSecretSize;
    }

    public byte[] Protect(byte[] plaintext, string purpose)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (plaintext.Length < 1 || plaintext.Length > _maximumSecretSize)
            throw new ArgumentException("Secret size is outside the supported bounds.", nameof(plaintext));
        var associatedData = AssociatedData(purpose);
        using var key = GetKey(create: true);
        using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")
            ?? throw new InvalidOperationException("The Android cipher is unavailable.");
        cipher.Init(CipherMode.EncryptMode, key);
        cipher.UpdateAAD(associatedData);
        var nonce = cipher.GetIV() ?? throw new InvalidOperationException("No nonce was generated.");
        if (nonce.Length != NonceSize) throw new InvalidOperationException("Unexpected nonce size.");
        var encrypted = cipher.DoFinal(plaintext) ?? throw new InvalidOperationException("Encryption failed.");
        var envelope = new byte[1 + NonceSize + encrypted.Length];
        envelope[0] = 1;
        nonce.CopyTo(envelope, 1);
        encrypted.CopyTo(envelope, 1 + NonceSize);
        return envelope;
    }

    public byte[] Unprotect(byte[] envelope, string purpose)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Length < 1 + NonceSize + TagSize + 1
            || envelope.Length > 1 + NonceSize + TagSize + _maximumSecretSize
            || envelope[0] != 1)
            throw new ArgumentException("Invalid encrypted secret envelope.", nameof(envelope));
        var associatedData = AssociatedData(purpose);
        // Reading must never regenerate a missing/invalidated key or fall back to plaintext.
        using var key = GetKey(create: false);
        using var parameters = new GCMParameterSpec(128, envelope.AsSpan(1, NonceSize).ToArray());
        using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")
            ?? throw new InvalidOperationException("The Android cipher is unavailable.");
        cipher.Init(CipherMode.DecryptMode, key, parameters);
        cipher.UpdateAAD(associatedData);
        return cipher.DoFinal(envelope.AsSpan(1 + NonceSize).ToArray())
            ?? throw new InvalidOperationException("Decryption failed.");
    }

    private byte[] AssociatedData(string purpose)
    {
        if (string.IsNullOrEmpty(purpose) || purpose.Length > 128
            || purpose.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-'))
            throw new ArgumentException("Invalid secret purpose.", nameof(purpose));
        return Encoding.UTF8.GetBytes($"Snook.Android.secret.v1:{_alias}:{purpose}");
    }

    private IKey GetKey(bool create)
    {
        lock (KeyGate)
        {
            using var store = KeyStore.GetInstance(Provider)
                ?? throw new InvalidOperationException("Android Keystore is unavailable.");
            store.Load(null);
            if (store.GetKey(_alias, null) is { } existing) return existing;
            if (!create) throw new InvalidOperationException("The secret key is missing. Re-pairing is required.");
            using var builder = new KeyGenParameterSpec.Builder(_alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt);
            builder.SetKeySize(256);
            builder.SetBlockModes(KeyProperties.BlockModeGcm);
            builder.SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone);
            builder.SetRandomizedEncryptionRequired(true);
            using var parameters = builder.Build();
            using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Provider)
                ?? throw new InvalidOperationException("The Android key generator is unavailable.");
            generator.Init(parameters);
            return generator.GenerateKey() ?? throw new InvalidOperationException("Key generation failed.");
        }
    }
}
