using System.Text.Json;

namespace OtaTool.Core.Models;

/// <summary>独立项目清单，绑定实际文件哈希；MCU 版本以 APP 内嵌信息头为准。</summary>
public sealed record IpmPackageMetadata(
    OtaMode Mode, DeviceType DeviceType, PackageKind PackageKind,
    byte Version, uint LinkAddress, long Length, string Md5, string Sha256)
{
    public const uint FirmwareAddress = 0x08000000;
    public const uint AppAddress = 0x08008000;
    public static string SidecarPath(string path) => path + ".ipm.json";

    public static IpmPackageMetadata? Read(string path) => File.Exists(SidecarPath(path))
        ? JsonSerializer.Deserialize<IpmPackageMetadata>(File.ReadAllText(SidecarPath(path)))
        : null;

    public static IpmPackageMetadata ValidateForCatalog(string path)
    {
        var metadata = Read(path) ?? throw new InvalidDataException("未找到已导入文件的本地记录。");
        return Validate(path, metadata.DeviceType, metadata.PackageKind, metadata.Version);
    }

    public static async Task<IpmPackageMetadata> CreateAsync(string path, DeviceType target,
        PackageKind kind, byte version, uint linkAddress = 0, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(target, kind, version);
        if (target == DeviceType.Mcu) ValidateMcuImage(path, version, linkAddress);
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
        ValidateIdentity(target, kind, version);
        if (metadata.Mode != OtaMode.IPM || metadata.DeviceType != target || metadata.PackageKind != kind || metadata.Version != version)
            throw new InvalidDataException("IPM 包项目、目标、类型或目标版本与任务不一致。");
        ValidateContent(path, metadata);
        if (target == DeviceType.Mcu) ValidateMcuImage(path, version, metadata.LinkAddress);
        return metadata;
    }

    private static void ValidateIdentity(DeviceType target, PackageKind kind, byte version)
    {
        if (version is < 1 or > 254) throw new InvalidDataException("软件版本必须是 1～254 的整数。");
        if (target is not (DeviceType.Gateway or DeviceType.Sync or DeviceType.Mcu))
            throw new InvalidDataException("IPM 包目标不受支持。");
        if (kind != (target == DeviceType.Sync ? PackageKind.Patch : PackageKind.FullImage))
            throw new InvalidDataException("IPM 同步端使用差分包，网关与 MCU 使用完整镜像。");
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

    private static void ValidateMcuImage(string path, byte version, uint linkAddress)
    {
        if (linkAddress != FirmwareAddress) throw new InvalidDataException("MCU 完整固件装载地址必须为 0x08000000。");
        if (McuAppImageReader.Read(path).Version != version)
            throw new InvalidDataException("MCU 内嵌软件版本与包清单或任务目标版本不一致。");
    }

    public static void ValidateMcuVectors(string path, uint linkAddress)
    {
        if (linkAddress != FirmwareAddress) throw new InvalidDataException("MCU 完整固件装载地址必须为 0x08000000。");
        McuAppImageReader.Read(path);
    }
}
