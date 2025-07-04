using System.Security.Cryptography;
using System.Text;

namespace Stratify.S3.Services;

public class ETagService
{
    public static string ComputeETag(string key, long size, DateTime lastModified)
    {
        var etagContent = $"{key}:{size}:{lastModified.Ticks}";
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(etagContent))).ToLower();
    }

    public static string ComputeETag(FileInfo fileInfo, string key)
    {
        return ComputeETag(key, fileInfo.Length, fileInfo.LastWriteTimeUtc);
    }

    public static string ComputeContentETag(byte[] content)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(content);
        return Convert.ToHexString(hash).ToLower();
    }

    public static string ComputeContentETag(Stream stream)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLower();
    }

    public static async Task<string> ComputeContentETagAsync(Stream stream)
    {
        using var md5 = MD5.Create();
        var hash = await md5.ComputeHashAsync(stream);
        return Convert.ToHexString(hash).ToLower();
    }
}