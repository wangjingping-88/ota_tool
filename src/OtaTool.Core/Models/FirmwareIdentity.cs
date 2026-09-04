using System.Security.Cryptography;
using System.Text;
using OtaTool.Core.Protocols;

namespace OtaTool.Core.Models;

public enum FirmwareDeviceType : byte
{
    Server = 0,
    ExtenderA = 1,
    ExtenderS = 2,
    RoomLight = 3,
    Switch = 4,
    Socket = 5,
    Dtu = 6,
    StreetLight = 7,
    Gateway = 8,
}

public static class FirmwarePatchNaming
{
    public const int MaxUserSuffixUtf8Bytes = 16;

    public static string GetPrefix(FirmwareDeviceType deviceType) => deviceType switch
    {
        FirmwareDeviceType.Server => "server",
        FirmwareDeviceType.ExtenderA => "ext-a",
        FirmwareDeviceType.ExtenderS => "ext-s",
        FirmwareDeviceType.RoomLight => "room-light",
        FirmwareDeviceType.Switch => "switch",
        FirmwareDeviceType.Socket => "socket",
        FirmwareDeviceType.Dtu => "dtu",
        FirmwareDeviceType.StreetLight => "street-light",
        FirmwareDeviceType.Gateway => "gateway",
        _ => throw new InvalidOperationException($"设备类型 {(byte)deviceType} 不支持制作 Patch。"),
    };

    public static bool IsCompatiblePrefix(FirmwareDeviceType deviceType, string patchType)
        => string.Equals(patchType, GetPrefix(deviceType), StringComparison.Ordinal) ||
           deviceType is >= FirmwareDeviceType.RoomLight and <= FirmwareDeviceType.StreetLight &&
           string.Equals(patchType, "node", StringComparison.Ordinal);

    public static string CreateName(
        string patchPrefix,
        byte oldVersion,
        byte newVersion,
        string userSuffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patchPrefix);
        if (oldVersion is < 1 or > 254 || newVersion is < 1 or > 254 || oldVersion == newVersion)
        {
            throw new InvalidOperationException("OTA 版本号必须是 1～254 且新旧版本不能相同。");
        }

        var normalizedSuffix = NormalizeUserSuffix(userSuffix);
        return $"{patchPrefix}-v{oldVersion}-to-v{newVersion}-{normalizedSuffix}.patch";
    }

    public static bool IsValidUserSuffix(string? userSuffix)
    {
        try
        {
            _ = NormalizeUserSuffix(userSuffix);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static string NormalizeUserSuffix(string? userSuffix)
    {
        var normalized = userSuffix?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new ArgumentException("请填写 Patch 用户后缀。", nameof(userSuffix));
        }
        if (Encoding.UTF8.GetByteCount(normalized) > MaxUserSuffixUtf8Bytes)
        {
            throw new ArgumentException($"Patch 用户后缀最多 {MaxUserSuffixUtf8Bytes} 个 UTF-8 字节。", nameof(userSuffix));
        }
        if (normalized.Any(character => character is not (>= 'a' and <= 'z') and not (>= 'A' and <= 'Z') and not (>= '0' and <= '9')))
        {
            throw new ArgumentException("Patch 用户后缀只能包含 ASCII 字母和数字。", nameof(userSuffix));
        }
        return normalized;
    }
}

public sealed record FirmwareIdentity(
    FirmwareDeviceType DeviceType,
    byte? Version,
    bool IsLegacyEcoMarker,
    string SourcePath,
    long Length,
    string Sha256)
{
    public byte DeviceTypeCode => (byte)DeviceType;

    public bool IsNode => DeviceType is >= FirmwareDeviceType.RoomLight and <= FirmwareDeviceType.StreetLight;

    public string PatchPrefix => FirmwarePatchNaming.GetPrefix(DeviceType);

    public OtaTool.Core.Models.DeviceType OtaDeviceType => DeviceType switch
    {
        FirmwareDeviceType.ExtenderA => OtaTool.Core.Models.DeviceType.Async,
        FirmwareDeviceType.ExtenderS => OtaTool.Core.Models.DeviceType.Sync,
        FirmwareDeviceType.Gateway => OtaTool.Core.Models.DeviceType.Gateway,
        _ when IsNode => OtaTool.Core.Models.DeviceType.Node,
        _ => throw new InvalidOperationException($"设备类型 {(byte)DeviceType} 不支持 OTA。"),
    };

    public string DisplayName => DeviceType switch
    {
        FirmwareDeviceType.Server => "云平台服务器",
        FirmwareDeviceType.ExtenderA => "扩展器-异步",
        FirmwareDeviceType.ExtenderS => "扩展器-同步",
        FirmwareDeviceType.RoomLight => "室内灯控",
        FirmwareDeviceType.Switch => "开关",
        FirmwareDeviceType.Socket => "电源插座",
        FirmwareDeviceType.Dtu => "DTU",
        FirmwareDeviceType.StreetLight => "路灯控制器",
        FirmwareDeviceType.Gateway => "网关",
        _ => $"未知类型（{DeviceTypeCode}）",
    };

    public string VersionText => Version.HasValue
        ? ProtocolVersionFormatter.FormatWithPrefix(Version.Value)
        : "未知版本";

    public string SuggestedPatchNameTo(FirmwareIdentity target)
    {
        ArgumentNullException.ThrowIfNull(target);
        EnsureCompatibleWith(target);
        if (!Version.HasValue || !target.Version.HasValue)
        {
            throw new InvalidOperationException("旧格式镜像必须补充版本后才能自动命名 Patch。");
        }
        return $"{PatchPrefix}-v{Version.Value}-to-v{target.Version.Value}.patch";
    }

    public void EnsureCompatibleWith(FirmwareIdentity target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (DeviceType != target.DeviceType)
        {
            throw new InvalidOperationException(
                $"A/B 镜像类型不一致：{DisplayName}（{DeviceTypeCode}）与 {target.DisplayName}（{target.DeviceTypeCode}）。");
        }
        if (Version.HasValue && target.Version.HasValue && Version.Value == target.Version.Value)
        {
            throw new InvalidOperationException("A/B 镜像版本相同，不能制作差分包。");
        }
    }
}

public static class FirmwareIdentityReader
{
    public const int BootloaderLength = 28 * 1024;
    public const int IdentityOffset = BootloaderLength - 5;
    public const int EcoMagicOffset = BootloaderLength - 4;
    public const int IdentityLength = 5;

    public static async Task<FirmwareIdentity> ReadAsync(
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var fullPath = Path.GetFullPath(imagePath);
        await using var stream = File.OpenRead(fullPath);
        if (stream.Length < IdentityOffset + IdentityLength)
        {
            throw new InvalidDataException("镜像长度不足，未覆盖 ECO 固件标识地址 0x6FFF。");
        }

        stream.Position = IdentityOffset;
        var identity = new byte[IdentityLength];
        await stream.ReadExactlyAsync(identity, cancellationToken);
        if (identity[1] != (byte)'e' || identity[2] != (byte)'c' || identity[3] != (byte)'o')
        {
            throw new InvalidDataException("未识别到有效 ECO 固件标识。");
        }
        if (!Enum.IsDefined(typeof(FirmwareDeviceType), identity[4]))
        {
            throw new InvalidDataException($"ECO 固件设备类型 {identity[4]} 未定义。");
        }

        stream.Position = 0;
        var sha256 = await SHA256.HashDataAsync(stream, cancellationToken);
        var version = identity[0] is >= 1 and <= 254 ? identity[0] : (byte?)null;
        return new FirmwareIdentity(
            (FirmwareDeviceType)identity[4],
            version,
            !version.HasValue,
            fullPath,
            stream.Length,
            Convert.ToHexString(sha256).ToLowerInvariant());
    }
}
