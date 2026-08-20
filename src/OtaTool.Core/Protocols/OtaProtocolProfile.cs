using OtaTool.Core.Models;

namespace OtaTool.Core.Protocols;

public interface IOtaProtocolProfile
{
    OtaMode Mode { get; }

    IReadOnlySet<DeviceType> SupportedDeviceTypes { get; }

    bool SupportsGatewayStatusPolling { get; }

    bool SupportsBroadcastTime { get; }

    bool SupportsLogAnalysis { get; }

    Task<OtaTaskResult> StartAsync(OtaTask task, CancellationToken cancellationToken);
}

public abstract class OtaProtocolProfile : IOtaProtocolProfile
{
    public abstract OtaMode Mode { get; }

    public abstract IReadOnlySet<DeviceType> SupportedDeviceTypes { get; }

    public abstract bool SupportsGatewayStatusPolling { get; }

    public abstract bool SupportsBroadcastTime { get; }

    public abstract bool SupportsLogAnalysis { get; }

    public virtual Task<OtaTaskResult> StartAsync(OtaTask task, CancellationToken cancellationToken)
    {
        var validation = OtaTaskValidator.Validate(task, this);
        if (!validation.IsValid)
        {
            return Task.FromResult(new OtaTaskResult(OtaTaskState.Failed, validation.Message, DateTimeOffset.Now));
        }

        return Task.FromResult(new OtaTaskResult(
            OtaTaskState.Ready,
            "任务参数已验证，等待 MQTT 协议适配器发送升级请求。",
            DateTimeOffset.Now));
    }
}

public sealed class TraditionalProtocolProfile : OtaProtocolProfile
{
    private static readonly IReadOnlySet<DeviceType> DeviceTypes = new HashSet<DeviceType>
    {
        DeviceType.Gateway,
        DeviceType.Sync,
    };

    public override OtaMode Mode => OtaMode.Traditional;

    public override IReadOnlySet<DeviceType> SupportedDeviceTypes => DeviceTypes;

    public override bool SupportsGatewayStatusPolling => false;

    public override bool SupportsBroadcastTime => false;

    public override bool SupportsLogAnalysis => false;
}

public sealed class EcoLinkProtocolProfile : OtaProtocolProfile
{
    private static readonly IReadOnlySet<DeviceType> DeviceTypes = new HashSet<DeviceType>
    {
        DeviceType.Gateway,
        DeviceType.Sync,
        DeviceType.Async,
        DeviceType.Node,
    };

    public override OtaMode Mode => OtaMode.EcoLink;

    public override IReadOnlySet<DeviceType> SupportedDeviceTypes => DeviceTypes;

    public override bool SupportsGatewayStatusPolling => true;

    public override bool SupportsBroadcastTime => true;

    public override bool SupportsLogAnalysis => true;
}

public sealed record OtaTaskValidationResult(bool IsValid, string Message)
{
    public static OtaTaskValidationResult Success() => new(true, "任务参数合法。");

    public static OtaTaskValidationResult Failure(string message) => new(false, message);
}

public static class OtaTaskValidator
{
    public static OtaTaskValidationResult Validate(OtaTask task, IOtaProtocolProfile profile)
    {
        if (task.Mode != profile.Mode)
        {
            return OtaTaskValidationResult.Failure("任务模式与协议适配器不一致。");
        }

        if (!profile.SupportedDeviceTypes.Contains(task.DeviceType))
        {
            return OtaTaskValidationResult.Failure($"{profile.Mode} 模式不支持 {task.DeviceType} 升级。");
        }

        if (!byte.TryParse(task.OldVersion, out var oldVersion) || oldVersion is < 1 or > 254 ||
            !byte.TryParse(task.NewVersion, out var newVersion) || newVersion is < 1 or > 254)
        {
            return OtaTaskValidationResult.Failure("必须填写旧版本和新版本。");
        }

        if (oldVersion == newVersion)
        {
            return OtaTaskValidationResult.Failure("旧版本和新版本不能相同。");
        }

        if (task.Timeout <= TimeSpan.Zero)
        {
            return OtaTaskValidationResult.Failure("超时时间必须大于 0。");
        }

        if (task.Target.Scope == TargetScope.SpecifiedIds && task.Target.DeviceIds.Count == 0)
        {
            return OtaTaskValidationResult.Failure("定向升级必须填写至少一个目标 ID。");
        }

        if (task.Target.Scope == TargetScope.Broadcast && task.Target.DeviceIds.Count != 0)
        {
            return OtaTaskValidationResult.Failure("广播升级不得填写目标 ID。");
        }

        if (task.Mode == OtaMode.EcoLink && task.DeviceType == DeviceType.Async
            && task.Target.Scope != TargetScope.SpecifiedIds)
        {
            return OtaTaskValidationResult.Failure("EcoLink Async 升级必须填写目标 ID，不支持广播。");
        }

        if (task.DeviceType is DeviceType.Sync or DeviceType.Async
            && task.Target.Scope == TargetScope.SpecifiedIds
            && task.Target.DeviceIds.Count > 16)
        {
            return OtaTaskValidationResult.Failure("Sync / Async 定向升级最多允许 16 个 Extender ID。");
        }

        if (task.DeviceType == DeviceType.Node)
        {
            if (task.Mode != OtaMode.EcoLink)
            {
                return OtaTaskValidationResult.Failure("传统模式不支持 Node 升级。");
            }
            if (task.NodeType is not (>= 2 and <= 63))
            {
                return OtaTaskValidationResult.Failure("Node OTA 必须填写 2～63 的 node_type。");
            }
            if (task.ExtenderTargets.Count is < 1 or > 16)
            {
                return OtaTaskValidationResult.Failure("Node OTA 必须配置 1～16 个 Extender 目标。");
            }
            var extenderIds = new HashSet<uint>();
            var totalNodeCount = 0;
            foreach (var target in task.ExtenderTargets)
            {
                if (!uint.TryParse(target.ExtenderId, out var extenderId) ||
                    extenderId == 0 ||
                    !extenderIds.Add(extenderId))
                {
                    return OtaTaskValidationResult.Failure("Node OTA 的 Extender ID 必须为唯一正整数。");
                }
                if (target.NodeIds.Count == 0)
                {
                    return OtaTaskValidationResult.Failure("每个 Node OTA Extender 必须至少配置一个 Node ID。");
                }
                var nodeIds = new HashSet<ushort>();
                foreach (var nodeId in target.NodeIds)
                {
                    if (!ushort.TryParse(nodeId, out var numericNodeId) || numericNodeId == 0 || !nodeIds.Add(numericNodeId))
                    {
                        return OtaTaskValidationResult.Failure("Node ID 必须为 1～65535 的唯一整数。");
                    }
                }
                totalNodeCount += nodeIds.Count;
            }
            if (totalNodeCount > 256)
            {
                return OtaTaskValidationResult.Failure("单个 Node OTA 任务最多允许 256 个 Node。");
            }
        }

        if (string.IsNullOrWhiteSpace(task.PatchPath) || !File.Exists(task.PatchPath))
        {
            return OtaTaskValidationResult.Failure("未选择有效 Patch 文件。");
        }

        var patchLength = new FileInfo(task.PatchPath).Length;
        return PatchCapacityPolicy.Check(task.DeviceType, patchLength) is { IsAllowed: true }
            ? OtaTaskValidationResult.Success()
            : OtaTaskValidationResult.Failure(PatchCapacityPolicy.Check(task.DeviceType, patchLength).Message);
    }
}
