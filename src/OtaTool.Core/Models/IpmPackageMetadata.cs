using System.Buffers.Binary;
using System.Text.Json;

namespace OtaTool.Core.Models;

/// <summary>独立项目清单，绑定实际文件哈希；MCU 版本由构建信息或人工核对提供。</summary>
public sealed record IpmPackageMetadata(
    OtaMode Mode, DeviceType DeviceType, PackageKind PackageKind,
    byte Version, uint LinkAddress, long Length, string Md5, string Sha256,
    bool IsDownloadTest = false)
{
    public const uint AppAddress = 0x08008000;
    public static string SidecarPath(string path) => path + ".ipm.json";

    public static IpmPackageMetadata? Read(string path) => File.Exists(SidecarPath(path))
        ? JsonSerializer.Deserialize<IpmPackageMetadata>(File.ReadAllText(SidecarPath(path)))
        : null;

    public static async Task<IpmPackageMetadata> CreatePendingMcuImageAsync(string path)
    {
        ValidatePendingMcuImage(path);
        var hash = await PatchMetadata.FromFileAsync(path);
        // 0 仅代表本地目录中尚未识别的版本/地址，不能用于升级任务。
        var metadata = new IpmPackageMetadata(OtaMode.IPM, DeviceType.Mcu, PackageKind.FullImage,
            0, 0, hash.Length, hash.Md5, hash.Sha256);
        await File.WriteAllTextAsync(SidecarPath(path), JsonSerializer.Serialize(metadata,
            new JsonSerializerOptions { WriteIndented = true }));
        return metadata;
    }

    public static void ValidatePendingMcuImage(string path)
    {
        if (!Path.GetExtension(path).Equals(".bin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("MCU 仅接受完整 APP .bin 镜像。");
        if (new FileInfo(path).Length is < 8 or > PatchCapacityPolicy.McuImageLimit)
            throw new InvalidDataException("MCU APP 长度必须为 8～229376 字节（224 KiB）。");
    }

    public static async Task<IpmPackageMetadata> SetDownloadTestVersionAsync(string path, byte version)
    {
        if (version is < 1 or > 254) throw new InvalidDataException("联调版本必须是 1～254 的整数。");
        var metadata = ValidateForCatalog(path);
        if (metadata.DeviceType != DeviceType.Mcu || metadata.PackageKind != PackageKind.FullImage ||
            (metadata.Version != 0 && !metadata.IsDownloadTest))
            throw new InvalidDataException("已识别固件版本不能被联调输入覆盖。");
        ValidateMcuDownloadTestVectors(path);
        metadata = metadata with { Version = version, IsDownloadTest = true };
        await File.WriteAllTextAsync(SidecarPath(path), JsonSerializer.Serialize(metadata,
            new JsonSerializerOptions { WriteIndented = true }));
        return metadata;
    }

    private static void ValidateMcuDownloadTestVectors(string path)
    {
        try { ValidateMcuVectorsAtAddress(path, AppAddress); }
        catch (InvalidDataException) { ValidateMcuVectorsAtAddress(path, 0x08000000); }
    }

    public static IpmPackageMetadata ValidateForCatalog(string path)
    {
        var metadata = Read(path) ?? throw new InvalidDataException("未找到已导入文件的本地记录。");
        if (metadata is { Mode: OtaMode.IPM, DeviceType: DeviceType.Mcu,
            PackageKind: PackageKind.FullImage, Version: 0, LinkAddress: 0 })
        {
            ValidatePendingMcuImage(path);
            ValidateContent(path, metadata);
            return metadata;
        }
        return Validate(path, metadata.DeviceType, metadata.PackageKind, metadata.Version);
    }

    public static async Task<IpmPackageMetadata> CreateAsync(string path, DeviceType target,
        PackageKind kind, byte version, uint linkAddress = 0, CancellationToken cancellationToken = default)
    {
        if (version is < 1 or > 254) throw new InvalidDataException("软件版本必须是 1～254 的整数。");
        if (target is not (DeviceType.Gateway or DeviceType.Sync or DeviceType.Mcu))
            throw new InvalidDataException("IPM 包目标不受支持。");
        if (kind != (target == DeviceType.Sync ? PackageKind.Patch : PackageKind.FullImage))
            throw new InvalidDataException("IPM 同步端使用差分包，网关与 MCU 使用完整镜像。");
        if (target == DeviceType.Mcu) ValidateMcuVectors(path, linkAddress);
        var hash = await PatchMetadata.FromFileAsync(path, cancellationToken);
        var metadata = new IpmPackageMetadata(OtaMode.IPM, target, kind, version, linkAddress,
            hash.Length, hash.Md5, hash.Sha256);
        await File.WriteAllTextAsync(SidecarPath(path), JsonSerializer.Serialize(metadata,
            new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        return metadata;
    }

    public static IpmPackageMetadata Validate(string path, DeviceType target, PackageKind kind, byte version)
    {
        var metadata = Read(path) ?? throw new InvalidDataException("缺少 IPM 项目包清单，请在 IPM 模式重新导入并核对项目归属。");
        if (metadata.Version is < 1 or > 254)
            throw new InvalidDataException("软件版本必须是 1～254 的整数。");
        if (metadata.Mode != OtaMode.IPM || metadata.DeviceType != target || metadata.PackageKind != kind || metadata.Version != version)
            throw new InvalidDataException("IPM 包项目、目标、类型或目标版本与任务不一致。");
        ValidateContent(path, metadata);
        if (metadata.IsDownloadTest)
        {
            if (target != DeviceType.Mcu || kind != PackageKind.FullImage)
                throw new InvalidDataException("下载联调仅支持 MCU 完整镜像。");
            ValidateMcuDownloadTestVectors(path);
        }
        else if (target == DeviceType.Mcu) ValidateMcuVectors(path, metadata.LinkAddress);
        return metadata;
    }

    private static void ValidateContent(string path, IpmPackageMetadata metadata)
    {
        using var stream = File.OpenRead(path);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        stream.Position = 0;
        var md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(stream));
        if (stream.Length != metadata.Length || !sha.Equals(metadata.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !md5.Equals(metadata.Md5, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("IPM 包内容与导入清单不一致，请重新导入。");
    }

    public static void ValidateMcuVectors(string path, uint linkAddress)
    {
        if (linkAddress != AppAddress) throw new InvalidDataException("MCU APP 链接地址必须为 0x08008000。");
        ValidateMcuVectorsAtAddress(path, linkAddress);
    }

    private static void ValidateMcuVectorsAtAddress(string path, uint linkAddress)
    {
        if (!Path.GetExtension(path).Equals(".bin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("MCU 仅接受完整 APP .bin 镜像。");
        using var stream = File.OpenRead(path);
        if (stream.Length is < 8 or > PatchCapacityPolicy.McuImageLimit)
            throw new InvalidDataException("MCU APP 长度必须为 8～229376 字节（224 KiB）。");
        Span<byte> vectors = stackalloc byte[8];
        stream.ReadExactly(vectors);
        var stack = BinaryPrimitives.ReadUInt32LittleEndian(vectors);
        var reset = BinaryPrimitives.ReadUInt32LittleEndian(vectors[4..]);
        if (stack <= 0x20000000 || stack > 0x20008000 || (stack & 7) != 0 || (reset & 1) == 0 ||
            (reset & ~1U) < linkAddress || (reset & ~1U) >= linkAddress + stream.Length)
            throw new InvalidDataException($"MCU APP 向量无效：请核对 SRAM 栈地址、Thumb 入口和 0x{linkAddress:X8} APP 链接范围。");
    }
}
