using System.Security.Cryptography;

namespace OtaTool.Core.Diff;

/// <summary>
/// 提供固件镜像哈希计算与内容一致性校验。
/// </summary>
public static class FirmwareImageHash
{
    /// <summary>
    /// 计算固件镜像的 SHA256。
    /// </summary>
    public static async Task<string> CalculateSha256Async(
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var absolutePath = Path.GetFullPath(imagePath);
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException("找不到固件镜像。", absolutePath);
        }

        await using var stream = File.OpenRead(absolutePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 通过文件长度和 SHA256 判断两个固件镜像内容是否相同。
    /// </summary>
    public static async Task<bool> AreIdenticalAsync(
        string firstImagePath,
        string secondImagePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(secondImagePath);

        var firstInfo = new FileInfo(Path.GetFullPath(firstImagePath));
        var secondInfo = new FileInfo(Path.GetFullPath(secondImagePath));
        if (!firstInfo.Exists)
        {
            throw new FileNotFoundException("找不到固件镜像。", firstInfo.FullName);
        }
        if (!secondInfo.Exists)
        {
            throw new FileNotFoundException("找不到固件镜像。", secondInfo.FullName);
        }
        if (firstInfo.Length != secondInfo.Length)
        {
            return false;
        }

        var firstHash = await CalculateSha256Async(
            firstInfo.FullName,
            cancellationToken);
        var secondHash = await CalculateSha256Async(
            secondInfo.FullName,
            cancellationToken);
        return string.Equals(
            firstHash,
            secondHash,
            StringComparison.OrdinalIgnoreCase);
    }
}
