using System.Security.Cryptography;
using System.Text;
using OtaTool.Core.Protocols;

namespace OtaTool.Core.Models;

public enum FirmwareDeviceType : byte
{
    Server = 0,
    Gateway = 1,
    ExtenderA = 2,
    ExtenderS = 3,
    RoomLight = 4,
    Switch = 5,
    Socket = 6,
    Slc = 7,
    Dtu = 8,
    LowPowerDtu = 9,
    // 工具内部的 MCU 分类，不是 ECO 固件类型字节。
    Mcu = 10,
}

public static class FirmwarePatchNaming
{
    public const int MaxUserSuffixUtf8Bytes = 16;

    public static string GetPrefix(FirmwareDeviceType deviceType, OtaMode mode = OtaMode.EcoLink)
        => mode == OtaMode.IPM ? deviceType switch
    {
        FirmwareDeviceType.ExtenderS => "ipm-wiota",
        _ => throw new InvalidOperationException("IPM 仅为 WIoTa 同步端制作差分包。"),
    } : deviceType switch
    {
        FirmwareDeviceType.Server => "server",
        FirmwareDeviceType.ExtenderA => "ext-a",
        FirmwareDeviceType.ExtenderS => "ext-s",
        FirmwareDeviceType.RoomLight => "room-light",
        FirmwareDeviceType.Switch => "switch",
        FirmwareDeviceType.Socket => "socket",
        FirmwareDeviceType.Dtu => "dtu",
        FirmwareDeviceType.Slc => "street-light",
        FirmwareDeviceType.LowPowerDtu => "low-power-dtu",
        FirmwareDeviceType.Gateway => "gateway",
        _ => throw new InvalidOperationException($"设备类型 {(byte)deviceType} 不支持制作 Patch。"),
    };

    public static bool IsCompatiblePrefix(FirmwareDeviceType deviceType, string patchType,
        OtaMode mode = OtaMode.EcoLink)
        => (mode != OtaMode.IPM || deviceType == FirmwareDeviceType.ExtenderS) &&
           string.Equals(patchType, GetPrefix(deviceType, mode), StringComparison.Ordinal);

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
    string Sha256,
    OtaMode Mode = OtaMode.EcoLink,
    byte? ImageDeviceTypeCode = null,
    string? DeviceId = null)
{
    public byte DeviceTypeCode => (byte)DeviceType;

    public bool IsNode => DeviceType is >= FirmwareDeviceType.RoomLight and <= FirmwareDeviceType.LowPowerDtu;

    public string PatchPrefix => FirmwarePatchNaming.GetPrefix(DeviceType, Mode);

    public OtaTool.Core.Models.DeviceType OtaDeviceType => DeviceType switch
    {
        FirmwareDeviceType.ExtenderA => OtaTool.Core.Models.DeviceType.Async,
        FirmwareDeviceType.ExtenderS => OtaTool.Core.Models.DeviceType.Sync,
        FirmwareDeviceType.Gateway => OtaTool.Core.Models.DeviceType.Gateway,
        FirmwareDeviceType.Mcu => OtaTool.Core.Models.DeviceType.Mcu,
        _ when IsNode => OtaTool.Core.Models.DeviceType.Node,
        _ => throw new InvalidOperationException($"设备类型 {(byte)DeviceType} 不支持 OTA。"),
    };

    public string DisplayName => DeviceType switch
    {
        FirmwareDeviceType.Server => "云平台服务器",
        FirmwareDeviceType.ExtenderA => "扩展器-异步",
        FirmwareDeviceType.ExtenderS => Mode == OtaMode.IPM ? "倾角定位-WIoTa（同步端）" : "扩展器-同步",
        FirmwareDeviceType.RoomLight => "室内灯控",
        FirmwareDeviceType.Switch => "开关",
        FirmwareDeviceType.Socket => "电源插座",
        FirmwareDeviceType.Dtu => "DTU",
        FirmwareDeviceType.Slc => "路灯控制器",
        FirmwareDeviceType.LowPowerDtu => "低功耗 DTU",
        FirmwareDeviceType.Gateway => "网关",
        FirmwareDeviceType.Mcu => "倾角定位-MCU",
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
        if (Mode != target.Mode || DeviceType != target.DeviceType || DeviceId != target.DeviceId)
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
        CancellationToken cancellationToken = default,
        OtaMode mode = OtaMode.EcoLink)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var fullPath = Path.GetFullPath(imagePath);
        await using var stream = File.OpenRead(fullPath);
        if (mode == OtaMode.IPM)
        {
            if (!Path.GetExtension(fullPath).Equals(".bin", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("IPM 仅接受 .bin 固件镜像。");
            if (stream.Length < IpmImageIdentity.Offset + IpmImageIdentity.Length)
                throw new InvalidDataException("IPM 镜像长度不足，未覆盖统一标识偏移 0x9F00。");
            stream.Position = IpmImageIdentity.Offset;
            var marker = new byte[IpmImageIdentity.Length];
            await stream.ReadExactlyAsync(marker, cancellationToken);
            var ipm = IpmImageIdentity.ParseMarker(marker);
            var ipmType = ipm.DeviceTypeCode switch
            {
                IpmImageIdentity.Gateway => FirmwareDeviceType.Gateway,
                IpmImageIdentity.Wiota => FirmwareDeviceType.ExtenderS,
                _ => FirmwareDeviceType.Mcu,
            };
            var deviceId = ipmType == FirmwareDeviceType.Mcu ? McuAppImageReader.Read(fullPath).DeviceId : null;
            stream.Position = 0;
            var ipmHash = await SHA256.HashDataAsync(stream, cancellationToken);
            return new FirmwareIdentity(ipmType, ipm.Version, false, fullPath, stream.Length,
                Convert.ToHexString(ipmHash).ToLowerInvariant(), mode, ipm.DeviceTypeCode, deviceId);
        }
        // IPM 即使保留 SDK 内部兼容字段，也不能按其他模式导入。
        if (stream.Length >= IpmImageIdentity.Offset + IpmImageIdentity.Length)
        {
            stream.Position = IpmImageIdentity.Offset;
            var marker = new byte[3];
            await stream.ReadExactlyAsync(marker, cancellationToken);
            if (marker.AsSpan().SequenceEqual("IPM"u8))
                throw new InvalidDataException("当前文件是 IPM 固件，请切换到 IPM 模式。");
        }
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
        var deviceType = (FirmwareDeviceType)identity[4];
        if (identity[4] > (byte)FirmwareDeviceType.LowPowerDtu)
        {
            throw new InvalidDataException($"ECO 固件设备类型 {identity[4]} 未定义。");
        }

        stream.Position = 0;
        var sha256 = await SHA256.HashDataAsync(stream, cancellationToken);
        var version = identity[0] is >= 1 and <= 254 ? identity[0] : (byte?)null;
        return new FirmwareIdentity(
            deviceType,
            version,
            !version.HasValue,
            fullPath,
            stream.Length,
            Convert.ToHexString(sha256).ToLowerInvariant(), mode, identity[4]);
    }
}
