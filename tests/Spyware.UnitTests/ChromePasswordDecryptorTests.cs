using System.Security.Cryptography;
using System.Text;
using Spyware.Collectors.Browsers;
using Spyware.Core.Contracts;
using Spyware.Core.Logging;
using Xunit;

namespace Spyware.UnitTests;

public class ChromePasswordDecryptorTests
{
    private static readonly ILog NullLog = new SilentLog();

    [Fact]
    public void V10Blob_WithKey_IsDecrypted()
    {
        byte[] key = new byte[32];
        RandomNumberGenerator.Fill(key);

        string secret = "S3cr3to-Prueba-ñ";
        byte[] blob = BuildV10Blob(key, Encoding.UTF8.GetBytes(secret));

        CredentialOutcome outcome = ChromePasswordDecryptor.Decrypt(blob, key, NullLog);

        Assert.Equal(CredentialStatus.Decrypted, outcome.Status);
        Assert.Equal(secret, outcome.Password);
    }

    [Fact]
    public void V10Blob_WithoutKey_IsEncryptedOnly()
    {
        byte[] key = new byte[32];
        RandomNumberGenerator.Fill(key);

        byte[] blob = BuildV10Blob(key, Encoding.UTF8.GetBytes("secreto"));
        CredentialOutcome outcome = ChromePasswordDecryptor.Decrypt(blob, aesKey: null, NullLog);

        // Acceso de lectura OK, descifrado no: exactamente el estado encrypted_only.
        Assert.Equal(CredentialStatus.EncryptedOnly, outcome.Status);
        Assert.Null(outcome.Password);
    }

    [Fact]
    public void V10Blob_WithWrongKey_IsEncryptedOnly()
    {
        byte[] key = new byte[32];
        RandomNumberGenerator.Fill(key);

        byte[] other = new byte[32];
        RandomNumberGenerator.Fill(other);

        byte[] blob = BuildV10Blob(key, Encoding.UTF8.GetBytes("secreto"));

        Assert.Equal(CredentialStatus.EncryptedOnly, ChromePasswordDecryptor.Decrypt(blob, other, NullLog).Status);
    }

    [Fact]
    public void V20Blob_IsUnsupported() =>
        Assert.Equal(CredentialStatus.Unsupported,
            ChromePasswordDecryptor.Decrypt(Encoding.ASCII.GetBytes("v20" + new string('A', 64)), new byte[32], NullLog).Status);

    [Fact]
    public void EmptyBlob_IsCorrupted() =>
        Assert.Equal(CredentialStatus.Corrupted, ChromePasswordDecryptor.Decrypt(Array.Empty<byte>(), new byte[32], NullLog).Status);

    private static byte[] BuildV10Blob(byte[] key, byte[] plain)
    {
        byte[] nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);

        byte[] tag = new byte[16];
        byte[] cipher = new byte[plain.Length];

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);

        byte[] blob = new byte[3 + nonce.Length + cipher.Length + tag.Length];

        Encoding.ASCII.GetBytes("v10").AsSpan().CopyTo(blob.AsSpan(0));
        nonce.AsSpan().CopyTo(blob.AsSpan(3));
        cipher.AsSpan().CopyTo(blob.AsSpan(3 + nonce.Length));
        tag.AsSpan().CopyTo(blob.AsSpan(3 + nonce.Length + cipher.Length));

        return blob;
    }

    private sealed class SilentLog : ILog
    {
        public LogLevel MinimumLevel { get; set; }
        public bool MirrorToConsole { get; set; }

        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
