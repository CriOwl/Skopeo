using System.Security.Cryptography;
using System.Text;

namespace Spyware.Core.Security;

public static class HashHelper
{
    public static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    public static string Short(string hex, int length = 8) =>
        string.IsNullOrEmpty(hex) ? string.Empty : hex[..Math.Min(length, hex.Length)];

    public static string ToBase64(byte[] data) => Convert.ToBase64String(data);

    public static byte[] FromBase64(string value) => Convert.FromBase64String(value);
}
