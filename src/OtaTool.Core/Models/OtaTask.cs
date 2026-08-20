namespace OtaTool.Core.Models;

public enum OtaMode
{
    Traditional,
    EcoLink,
}

public enum DeviceType
{
    Gateway,
    Sync,
    Async,
    Node,
}

public enum TargetScope
{
    SpecifiedIds,
    Broadcast,
}

public enum OtaTaskState
{
    Draft,
    Ready,
    Running,
    Succeeded,
    Failed,
    TimedOut,
    Cancelled,
}

public sealed record OtaTaskTarget(
    TargetScope Scope,
    IReadOnlyList<string> DeviceIds)
{
    public static OtaTaskTarget Broadcast() => new(TargetScope.Broadcast, []);

    public static OtaTaskTarget Specified(params string[] deviceIds) => new(TargetScope.SpecifiedIds, deviceIds);
}

/// <summary>Node OTA 中一个 Extender 与其下属 Node 目标的显式映射。</summary>
public sealed record OtaExtenderTarget(string ExtenderId, IReadOnlyList<string> NodeIds);

public sealed class OtaTask
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public OtaMode Mode { get; init; }

    public DeviceType DeviceType { get; init; }

    public OtaTaskTarget Target { get; init; } = OtaTaskTarget.Broadcast();

    /// <summary>仅 EcoLink Node 任务使用；每项必须包含一个 Extender 及其 Node 列表。</summary>
    public IReadOnlyList<OtaExtenderTarget> ExtenderTargets { get; init; } = [];

    /// <summary>仅 EcoLink Node 任务使用，范围 2～63。</summary>
    public int? NodeType { get; init; }

    public string GatewayId { get; init; } = string.Empty;

    public string OldVersion { get; init; } = string.Empty;

    public string NewVersion { get; init; } = string.Empty;

    public string PatchPath { get; init; } = string.Empty;

    public string PatchUrl { get; init; } = string.Empty;

    public string PatchMd5 { get; init; } = string.Empty;

    public string PatchSha256 { get; init; } = string.Empty;

    /// <summary>任务创建时固化的协议适配器标识，运行中不得切换。</summary>
    public string ProtocolProfileId { get; init; } = string.Empty;

    public string ProtocolProfileVersion { get; init; } = string.Empty;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed record OtaTaskResult(
    OtaTaskState State,
    string Message,
    DateTimeOffset OccurredAt,
    int? SuccessCount = null,
    int? FailedCount = null);
