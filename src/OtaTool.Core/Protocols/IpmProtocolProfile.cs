using OtaTool.Core.Models;

namespace OtaTool.Core.Protocols;

public sealed class IpmProtocolProfile : OtaProtocolProfile
{
    public override OtaMode Mode => OtaMode.IPM;
    public override IReadOnlySet<DeviceType> SupportedDeviceTypes { get; } =
        new HashSet<DeviceType> { DeviceType.Gateway, DeviceType.Sync, DeviceType.Mcu };
    public override bool SupportsGatewayStatusPolling => true;
    public override bool SupportsStartAck => false;
    public override bool SupportsBroadcastTime => true;
    public override bool SupportsLogAnalysis => true;
}

public static class OtaProtocolProfiles
{
    public static IOtaProtocolProfile Create(OtaMode mode) => mode switch
    {
        OtaMode.Traditional => new TraditionalProtocolProfile(),
        OtaMode.EcoLink => new EcoLinkProtocolProfile(),
        OtaMode.IPM => new IpmProtocolProfile(),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static string Id(OtaMode mode) => mode switch
    {
        OtaMode.IPM => "ipm-gateway",
        OtaMode.EcoLink => "ecolink-gateway",
        _ => "traditional",
    };

    public static string? ValidateIpmContract(OtaTask task)
    {
        if (task.Mode != OtaMode.IPM) return null;
        if (task.DeviceType is not (DeviceType.Gateway or DeviceType.Sync or DeviceType.Mcu)) return "IPM 不支持此升级类型。";
        if (!uint.TryParse(task.GatewayId, out var gateway) || gateway == 0) return "网关 ID 必须是正整数。";
        if (task.NodeType is not null || task.NodeRetryCount != 0 || task.ExtenderTargets.Count != 0) return "IPM 任务不允许携带节点参数。";
        var kind = task.DeviceType == DeviceType.Sync ? PackageKind.Patch : PackageKind.FullImage;
        if (task.PackageKind != kind) return "IPM 同步升级必须使用差分包，网关和 MCU 必须使用完整镜像。";
        if (task.DeviceType == DeviceType.Gateway)
            return task.Target.DeviceIds.Count == 0 ? null : "网关升级不能携带终端目标。";
        if (task.DeviceType == DeviceType.Mcu && task.Target.Scope != TargetScope.SpecifiedIds) return "MCU 升级必须显式指定目标。";
        if (task.Target.Scope == TargetScope.Broadcast)
            return task.Target.DeviceIds.Count == 0 ? null : "广播升级不能携带目标列表。";
        if (task.Target.DeviceIds.Count is < 1 or > 16) return "定向升级必须指定 1～16 个 WIoTa ID。";
        var ids = new HashSet<uint>();
        return task.Target.DeviceIds.Any(value => !uint.TryParse(value, out var id) || id == 0 || !ids.Add(id))
            ? "WIoTa ID 必须是唯一的十进制正整数。" : null;
    }
}
