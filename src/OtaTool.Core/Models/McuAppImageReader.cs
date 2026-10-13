using System.Buffers.Binary;
using System.Text;

namespace OtaTool.Core.Models;

public sealed record McuAppImage(byte Version, string DeviceId, ushort HardwareRevision,
    uint LinkAddress, uint ImageCrc32, uint PackageCrc32, byte[] AppBytes);

/// <summary>读取含 UBoot 的完整固件；版本来自偏移 0x8000 的 APP 信息，OTA 原样下发整包。</summary>
public static class McuAppImageReader
{
    public const int UbootSize = 0x8000;
    public const int HeaderSize = 64;
    public const int VectorOffset = 0x200;
    public const string DeviceId = "IPM_GD32L233";

    public static McuAppImage Read(string path)
    {
        if (!Path.GetExtension(path).Equals(".bin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("MCU 仅接受 .bin 镜像。");
        if (new FileInfo(path).Length is < 0xA000 or > PatchCapacityPolicy.McuImageLimit)
            throw new InvalidDataException("MCU 完整固件长度必须为 40960～262144 字节。");
        return Parse(File.ReadAllBytes(path));
    }

    public static McuAppImage Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length is < 0xA000 or > (int)PatchCapacityPolicy.McuImageLimit)
            throw new InvalidDataException("MCU 完整固件长度必须为 40960～262144 字节。");
        var identity = IpmImageIdentity.Parse(source);
        if (identity.DeviceTypeCode != IpmImageIdentity.Mcu)
            throw new InvalidDataException("MCU 完整固件的 IPM 类型必须为 3。");
        if (source.Length is < UbootSize + VectorOffset + 8 or > (int)PatchCapacityPolicy.McuImageLimit ||
            !source[UbootSize..].StartsWith("IPMAPP01"u8))
            throw new InvalidDataException("MCU 需要包含 UBoot 的完整固件，APP 信息头必须位于文件偏移 0x8000。");
        ValidateVectors(source, IpmPackageMetadata.FirmwareAddress, 0, UbootSize);
        var app = source[UbootSize..];
        if (app.Length is < VectorOffset + 8 or > (int)PatchCapacityPolicy.McuAppImageLimit)
            throw new InvalidDataException("MCU APP 长度必须为 520～229376 字节（224 KiB）。");
        if (U16(app, 8) != 1 || U16(app, 10) != HeaderSize)
            throw new InvalidDataException("MCU 信息头格式或长度不受支持。");
        if (Crc32(app[..60]) != U32(app, 60))
            throw new InvalidDataException("MCU 信息头 CRC32 校验失败。");
        var version = U32(app, 12);
        if (version is < 1 or > 254)
            throw new InvalidDataException("MCU 内嵌软件版本必须为 1～254。");
        if (version != identity.Version)
            throw new InvalidDataException("MCU 统一标识版本与 APP 信息头版本不一致。");
        Span<byte> expectedId = stackalloc byte[16];
        expectedId.Clear();
        Encoding.ASCII.GetBytes(DeviceId, expectedId);
        if (!app.Slice(16, 16).SequenceEqual(expectedId) || U16(app, 48) != 1)
            throw new InvalidDataException("MCU 设备标识或硬件版本不匹配，要求 IPM_GD32L233 / 硬件版本 1。");
        if (U32(app, 32) != IpmPackageMetadata.AppAddress || U32(app, 36) != VectorOffset)
            throw new InvalidDataException("MCU APP 地址必须为 0x08008000，向量偏移必须为 0x200。");
        if (U32(app, 40) != app.Length)
            throw new InvalidDataException("MCU 信息头中的镜像长度与 APP 文件长度不一致。");
        if (U16(app, 50) is < 1 or > 2 || U32(app, 52) != 0 || U32(app, 56) != 0)
            throw new InvalidDataException("MCU 引导接口版本或保留字段不受支持。");
        if (app.Slice(HeaderSize, VectorOffset - HeaderSize).IndexOfAnyExcept((byte)0xFF) >= 0)
            throw new InvalidDataException("MCU 信息头后的预留区域必须填充 0xFF。");
        var crc = U32(app, 44);
        if (Crc32(app[HeaderSize..]) != crc)
            throw new InvalidDataException("MCU APP CRC32 校验失败。");
        ValidateVectors(app, IpmPackageMetadata.AppAddress, VectorOffset, app.Length);
        return new McuAppImage((byte)version, DeviceId, 1, IpmPackageMetadata.FirmwareAddress,
            crc, Crc32(source), app.ToArray());
    }

    private static void ValidateVectors(ReadOnlySpan<byte> image, uint address, int vectorOffset, int length)
    {
        var stack = U32(image, vectorOffset);
        var reset = U32(image, vectorOffset + 4);
        if (stack <= 0x20000000 || stack > 0x20008000 || (stack & 7) != 0 || (reset & 1) == 0 ||
            (reset & ~1U) < address + vectorOffset || (reset & ~1U) >= address + length)
            throw new InvalidDataException("MCU 镜像向量无效，请核对 SRAM 栈地址、Thumb 入口与链接范围。");
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320U);
        }
        return crc ^ 0xFFFFFFFF;
    }
}
