using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace McGo.Core.Utilities;

public static class FileUtilities
{
    public static string SanitizeFilename(string? value, string fallback = "ImportedPack")
    {
        var sanitized = Regex.Replace(value ?? "", @"[<>:""/\\|?*\x00-\x1F]+", "-").Trim('.', '-', '_', ' ');
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    public static string Sha1File(string path)
    {
        using var sha1 = SHA1.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha1.ComputeHash(stream)).ToLowerInvariant();
    }

    public static string Sha1Text(string value)
    {
        using var sha1 = SHA1.Create();
        return Convert.ToHexString(sha1.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    public static string Sha256File(string path)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
    }
}
