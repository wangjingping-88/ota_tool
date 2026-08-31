using OtaTool.Core.Execution;

namespace OtaTool.Core.Models;

public enum OtaTestPlanExecutionKind
{
    Forward,
    Reverse,
    Cycle,
}

public enum OtaTargetResolutionMode
{
    FixedIds,
    DynamicMatch,
}

public enum OtaTestPlanItemState
{
    NeedsReview,
    Preflighting,
    Ready,
    Running,
    Verifying,
    Succeeded,
    Failed,
    TimedOut,
    Cancelled,
    Skipped,
}

public enum OtaTestPlanState
{
    Draft,
    Preflighting,
    Ready,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record OtaTestPlanTargetRule
{
    public OtaTargetResolutionMode ResolutionMode { get; init; } = OtaTargetResolutionMode.FixedIds;

    /// <summary>Sync/Async 的固定目标，或动态匹配时允许使用的 Extender 范围。</summary>
    public IReadOnlyList<string> DeviceIds { get; init; } = [];

    /// <summary>Node 固定目标映射。动态匹配时 NodeIds 为空，只使用 ExtenderId 限定范围。</summary>
    public IReadOnlyList<OtaExtenderTarget> ExtenderTargets { get; init; } = [];

    public int? NodeType { get; init; }
}

public sealed record OtaTestPlanPatchReference
{
    public string FilePath { get; init; } = string.Empty;

    public string Md5 { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public byte? ManifestDeviceTypeCode { get; init; }

    public byte? ManifestOldVersion { get; init; }

    public byte? ManifestNewVersion { get; init; }

    /// <summary>Gateway 完整镜像内嵌的目标版本；旧模板缺少该字段时在预检阶段重新读取镜像。</summary>
    public byte? FullImageTargetVersion { get; init; }
}

public sealed record OtaTestPlanItemTemplate
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public int Order { get; init; }

    public OtaMode Mode { get; init; }

    public string GatewayId { get; init; } = string.Empty;

    public DeviceType DeviceType { get; init; }

    public OtaTestPlanExecutionKind ExecutionKind { get; init; }

    public string OldVersion { get; init; } = string.Empty;

    public string NewVersion { get; init; } = string.Empty;

    public OtaTestPlanPatchReference ForwardPatch { get; init; } = new();

    public OtaTestPlanPatchReference? ReversePatch { get; init; }

    public OtaTestPlanTargetRule TargetRule { get; init; } = new();

    public int CycleRounds { get; init; } = 1;

    public OtaCycleIntervalOptions? CycleInterval { get; init; }
}

public sealed record OtaTestPlanTemplate
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "未命名测试计划";

    public OtaMode Mode { get; init; }

    public string GatewayId { get; init; } = string.Empty;

    public bool ContinueOnFailure { get; init; }

    public int InterItemDelaySeconds { get; init; }

    public IReadOnlyList<OtaTestPlanItemTemplate> Items { get; init; } = [];
}

/// <summary>完成实时解析后固化的一项计划任务。</summary>
public sealed record OtaTestPlanPreparedItem(
    OtaTestPlanItemTemplate Template,
    OtaTask PrimaryTask,
    OtaTask? ReverseTask = null);
