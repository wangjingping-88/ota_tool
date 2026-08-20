using System.Security.Cryptography;

namespace OtaTool.Core.Models;

public sealed record PatchMetadata(
    string FilePath,
    long Length,
    string Md5,
    string Sha256,
    DateTimeOffset ImportedAt)
{
    public static async Task<PatchMetadata> FromFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var absolutePath = Path.GetFullPath(filePath);
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException("找不到 Patch 文件。", absolutePath);
        }

        await using var stream = File.OpenRead(absolutePath);
        var length = stream.Length;
        var md5 = await MD5.HashDataAsync(stream, cancellationToken);
        stream.Position = 0;
        var sha256 = await SHA256.HashDataAsync(stream, cancellationToken);
        return new PatchMetadata(
            absolutePath,
            length,
            Convert.ToHexString(md5).ToLowerInvariant(),
            Convert.ToHexString(sha256).ToLowerInvariant(),
            DateTimeOffset.Now);
    }
}

public sealed record PatchCapacityCheckResult(bool IsAllowed, long Limit, string Message);

public sealed record PatchCapacityLimits(
    long Node = PatchCapacityPolicy.NodePatchLimit,
    long Async = PatchCapacityPolicy.AsyncPatchLimit,
    long Sync = PatchCapacityPolicy.SyncPatchLimit,
    long Gateway = PatchCapacityPolicy.GatewayPatchLimit)
{
    public long For(DeviceType deviceType) => deviceType switch
    {
        DeviceType.Node => Node,
        DeviceType.Async => Async,
        DeviceType.Sync => Sync,
        DeviceType.Gateway => Gateway,
        _ => throw new ArgumentOutOfRangeException(nameof(deviceType)),
    };
}

public static class PatchCapacityPolicy
{
    public const long NodePatchLimit = 0xD000;
    public const long AsyncPatchLimit = 0x2F000;
    public const long SyncPatchLimit = 0xD000;
    public const long GatewayPatchLimit = 0x200000;

    public static PatchCapacityCheckResult Check(
        DeviceType deviceType,
        long patchLength,
        PatchCapacityLimits? limits = null)
    {
        limits ??= new PatchCapacityLimits();
        var limit = limits.For(deviceType);

        if (patchLength < 0)
        {
            return new PatchCapacityCheckResult(false, limit, "Patch 长度非法。");
        }

        if (patchLength > limit)
        {
            return new PatchCapacityCheckResult(
                false,
                limit,
                $"Patch 大小 {patchLength / 1024d:F1} KiB，超过当前门限 {limit / 1024d:F1} KiB。请减小版本跨度或采用分段升级。");
        }

        return new PatchCapacityCheckResult(true, limit, "Patch 容量合法。");
    }
}
