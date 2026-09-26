using System.Security.Cryptography;
using System.Text;

namespace Goulash.Api.Providers;

public sealed class AiApiKeyProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const byte PayloadVersion = 1;
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("Goulash.Api:ai-api-key:v1");
    private readonly byte[] _key;

    public AiApiKeyProtector(string? base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
            throw new InvalidOperationException("Ai:EncryptionKey must be configured as a base64-encoded 32-byte key.");

        try
        {
            _key = Convert.FromBase64String(base64Key);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Ai:EncryptionKey must be configured as a base64-encoded 32-byte key.");
        }

        if (_key.Length != 32)
            throw new InvalidOperationException("Ai:EncryptionKey must be configured as a base64-encoded 32-byte key.");
    }

    public byte[] Protect(string apiKey)
    {
        var plaintext = Encoding.UTF8.GetBytes(apiKey);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);

            var payload = new byte[1 + NonceSize + TagSize + ciphertext.Length];
            payload[0] = PayloadVersion;
            nonce.CopyTo(payload, 1);
            tag.CopyTo(payload, 1 + NonceSize);
            ciphertext.CopyTo(payload, 1 + NonceSize + TagSize);
            return payload;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public string Unprotect(byte[] payload)
    {
        if (payload.Length < 1 + NonceSize + TagSize || payload[0] != PayloadVersion)
            throw new CryptographicException("The encrypted API key has an unsupported format.");

        var nonce = payload.AsSpan(1, NonceSize);
        var tag = payload.AsSpan(1 + NonceSize, TagSize);
        var ciphertext = payload.AsSpan(1 + NonceSize + TagSize);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
