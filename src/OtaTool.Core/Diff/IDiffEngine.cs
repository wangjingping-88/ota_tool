using System.Security.Cryptography;
using System.Text.Json.Serialization;
using OtaTool.Core.Models;

namespace OtaTool.Core.Diff;

public sealed record DiffEngineInfo(string Id, string Version, string SourceSha256, bool IsVerified, string StatusMessage);

public sealed record DiffRequest(
    string OldImagePath,
    string NewImagePath,
    string PatchOutputPath,
    DeviceType DeviceType,
    string OldVersion,
    string NewVersion,
    bool UpdateFirstBlock = false,
    int RomIndex = 0);

public sealed record DiffResult(bool IsSuccess, string Message, PatchMetadata? Patch = null);

public sealed record PatchVerifyResult(bool IsSuccess, string Message, string? RecoveredSha256 = null);

public interface IDiffEngine
{
    DiffEngineInfo GetInfo();

    Task<DiffResult> GenerateAsync(DiffRequest request, CancellationToken cancellationToken = default);

    Task<PatchVerifyResult> VerifyAsync(string oldImagePath, string patchPath, string expectedNewImagePath, CancellationToken cancellationToken = default);
}

/// <summary>默认引擎门禁。没有黄金样本和 Bootloader 验证时，禁止生成不可信 Patch。</summary>
public sealed class UnavailableDiffEngine : IDiffEngine
{
    public DiffEngineInfo GetInfo() => new(
        "partition-bsdiff-lzzip",
        "未安装",
        string.Empty,
        false,
        "差分引擎未安装或未认证；仅允许导入已生成的 Patch。");

    public Task<DiffResult> GenerateAsync(DiffRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new DiffResult(false, GetInfo().StatusMessage));

    public Task<PatchVerifyResult> VerifyAsync(string oldImagePath, string patchPath, string expectedNewImagePath, CancellationToken cancellationToken = default)
        => Task.FromResult(new PatchVerifyResult(false, GetInfo().StatusMessage));
}

public sealed record PackageManifest(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("patch_type")] string PatchType,
    [property: JsonPropertyName("device_type")] byte DeviceTypeCode,
    [property: JsonPropertyName("old_version")] byte OldVersion,
    [property: JsonPropertyName("new_version")] byte NewVersion,
    [property: JsonPropertyName("patch_size")] long PatchLength,
    [property: JsonPropertyName("patch_md5")] string PatchMd5,
    [property: JsonPropertyName("patch_sha256")] string PatchSha256,
    [property: JsonPropertyName("old_image_size")] long OldImageLength,
    [property: JsonPropertyName("old_image_sha256")] string OldImageSha256,
    [property: JsonPropertyName("new_image_size")] long NewImageLength,
    [property: JsonPropertyName("new_image_sha256")] string NewImageSha256,
    [property: JsonPropertyName("engine")] string EngineId,
    [property: JsonPropertyName("engine_version")] string EngineVersion,
    [property: JsonPropertyName("restore_verified")] bool PatchVerified,
    [property: JsonPropertyName("created_at")] DateTimeOffset GeneratedAt)
{
    public DeviceType OtaDeviceType => ((FirmwareDeviceType)DeviceTypeCode) switch
    {
        FirmwareDeviceType.ExtenderA => DeviceType.Async,
        FirmwareDeviceType.ExtenderS => DeviceType.Sync,
        FirmwareDeviceType.Gateway => DeviceType.Gateway,
        >= FirmwareDeviceType.RoomLight and <= FirmwareDeviceType.StreetLight => DeviceType.Node,
        _ => throw new InvalidDataException($"Patch 设备类型 {DeviceTypeCode} 不支持 OTA。"),
    };
}

public static class PackageManifestFactory
{
    public static async Task<PackageManifest> CreateAsync(DiffEngineInfo engine, DiffRequest request, PatchMetadata patch, bool patchVerified, CancellationToken cancellationToken = default)
    {
        var oldIdentity = await FirmwareIdentityReader.ReadAsync(request.OldImagePath, cancellationToken);
        var newIdentity = await FirmwareIdentityReader.ReadAsync(request.NewImagePath, cancellationToken);
        oldIdentity.EnsureCompatibleWith(newIdentity);
        if (!byte.TryParse(request.OldVersion, out var oldVersion) ||
            !byte.TryParse(request.NewVersion, out var newVersion) ||
            oldVersion is < 1 or > 254 || newVersion is < 1 or > 254 ||
            oldVersion == newVersion ||
            (oldIdentity.Version.HasValue && oldVersion != oldIdentity.Version.Value) ||
            (newIdentity.Version.HasValue && newVersion != newIdentity.Version.Value))
        {
            throw new InvalidOperationException("Patch 请求版本与 A/B 镜像身份不一致。");
        }
        return new PackageManifest(
            1,
            oldIdentity.PatchPrefix,
            oldIdentity.DeviceTypeCode,
            oldVersion,
            newVersion,
            patch.Length,
            patch.Md5,
            patch.Sha256,
            oldIdentity.Length,
            oldIdentity.Sha256,
            newIdentity.Length,
            newIdentity.Sha256,
            engine.Id,
            engine.Version,
            patchVerified,
            DateTimeOffset.Now);
    }
}

public static class PackageManifestExporter
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<string> ExportAsync(PackageManifest manifest, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using var stream = File.Create(fullPath);
        await System.Text.Json.JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
        return fullPath;
    }
}

public static class PackageManifestImporter
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static async Task<PackageManifest?> TryLoadAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var fullPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullPath)) return null;
        await using var stream = File.OpenRead(fullPath);
        return await System.Text.Json.JsonSerializer.DeserializeAsync<PackageManifest>(stream, JsonOptions, cancellationToken: cancellationToken);
    }

    public static async Task<PackageManifest> LoadAndValidateAsync(
        string patchPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patchPath);
        var fullPatchPath = Path.GetFullPath(patchPath);
        var manifestPath = fullPatchPath + ".json";
        var manifest = await TryLoadAsync(manifestPath, cancellationToken)
            ?? throw new InvalidDataException($"Patch 缺少强制侧车元数据：{Path.GetFileName(manifestPath)}");
        if (manifest.SchemaVersion != 1 ||
            string.IsNullOrWhiteSpace(manifest.PatchType) ||
            !Enum.IsDefined(typeof(FirmwareDeviceType), manifest.DeviceTypeCode) ||
            manifest.OldVersion is < 1 or > 254 ||
            manifest.NewVersion is < 1 or > 254 ||
            manifest.OldVersion == manifest.NewVersion ||
            !manifest.PatchVerified)
        {
            throw new InvalidDataException("Patch 侧车元数据字段非法或还原测试未通过。");
        }
        var metadata = await PatchMetadata.FromFileAsync(fullPatchPath, cancellationToken);
        if (metadata.Length != manifest.PatchLength ||
            !metadata.Md5.Equals(manifest.PatchMd5, StringComparison.OrdinalIgnoreCase) ||
            !metadata.Sha256.Equals(manifest.PatchSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Patch 文件与侧车元数据的大小或哈希不一致。");
        }
        var firmwareDeviceType = (FirmwareDeviceType)manifest.DeviceTypeCode;
        if (!FirmwarePatchNaming.IsCompatiblePrefix(firmwareDeviceType, manifest.PatchType))
        {
            throw new InvalidDataException("Patch 类型与设备类型不一致。");
        }
        return manifest;
    }
}
