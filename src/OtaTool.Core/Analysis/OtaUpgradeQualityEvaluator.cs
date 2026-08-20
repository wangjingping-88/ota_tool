using System.Text;
using System.Text.Json;

namespace OtaTool.Core.Analysis;

public sealed record OtaUpgradeQualityAssessment(
    int Score,
    string Grade,
    string Color,
    string Summary,
    string Details,
    int ClosedLoopScore,
    int CompletionScore,
    int ReliabilityScore,
    int PerformanceScore);

public static class OtaUpgradeQualityEvaluator
{
    public static OtaUpgradeQualityAssessment Evaluate(JsonElement root)
    {
        var conclusions = GetObject(root, "conclusions");
        var counts = GetObject(root, "counts");
        var maintenance = GetObject(root, "maintenance");
        var latency = GetObject(maintenance, "latency_ms");
        var retries = GetObject(root, "retries");
        var syncTiming = GetObject(root, "sync_frame_timing");
        var nodeLink = GetObject(root, "node_link_summary");

        var overallSuccess = GetBoolean(conclusions, "overall_success");
        var deviceSuccess = GetBoolean(conclusions, "device_upgrade_success");
        var parentSuccess = GetBoolean(conclusions, "parent_task_success");
        var target = Math.Max(0, GetInt32(counts, "target"));

        var closedLoopScore =
            (deviceSuccess ? 15 : 0) +
            (parentSuccess ? 15 : 0) +
            (overallSuccess ? 20 : 0);

        var completionScore = target == 0
            ? 0
            : RatioPoints(GetInt32(counts, "ready"), target, 5) +
              RatioPoints(GetInt32(counts, "boot_report"), target, 5) +
              RatioPoints(GetInt32(counts, "node_finished"), target, 5) +
              RatioPoints(GetInt32(counts, "aggregated_finished"), target, 5);

        var txFailures = Math.Max(0, GetInt32(syncTiming, "tx_failure_events"));
        var inferredMissedFrames = Math.Max(0, GetInt32(syncTiming, "inferred_missed_frames"));
        var maintenanceRepeats = Math.Max(0, GetInt32(retries, "maintenance_repeat"));
        var maintenanceCompleted = Math.Max(0, GetInt32(maintenance, "completed_count"));
        var weakNodeCount = GetArrayLength(nodeLink, "weak_link_node_ids");

        var txPenalty = Math.Min(4, txFailures * 2);
        var missedPenalty = target == 0
            ? Math.Min(5, inferredMissedFrames)
            : Math.Min(5, (int)Math.Ceiling(inferredMissedFrames / (double)target));
        var weakLinkPenalty = target == 0
            ? Math.Min(5, weakNodeCount)
            : Math.Min(5, (int)Math.Ceiling(weakNodeCount * 5.0 / target));
        var repeatPenalty = maintenanceCompleted == 0
            ? 0
            : Math.Min(6, (int)Math.Round(maintenanceRepeats * 6.0 / maintenanceCompleted));
        var reliabilityScore = Math.Clamp(20 - txPenalty - missedPenalty - weakLinkPenalty - repeatPenalty, 0, 20);

        var p95 = GetNullableInt32(latency, "p95");
        var performanceScore = p95 switch
        {
            null => 0,
            <= 300 => 10,
            <= 500 => 9,
            <= 800 => 7,
            <= 1200 => 5,
            _ => 2,
        };

        var score = Math.Clamp(closedLoopScore + completionScore + reliabilityScore + performanceScore, 0, 100);
        var (grade, color) = score switch
        {
            >= 90 => ("优秀", "#159E68"),
            >= 80 => ("良好", "#2570E8"),
            >= 70 => ("中等", "#B7791F"),
            >= 60 => ("需改进", "#C56A1A"),
            _ => ("不合格", "#C53333"),
        };

        var observations = new List<string>();
        if (overallSuccess) observations.Add("升级任务与设备结果均已闭环。" );
        else observations.Add("升级未形成完整闭环，需优先排查阻断原因。" );
        if (target > 0) observations.Add($"目标完成度：Node FINISHED {GetInt32(counts, "node_finished")}/{target}，聚合 FINISHED {GetInt32(counts, "aggregated_finished")}/{target}。" );
        if (txFailures > 0 || inferredMissedFrames > 0)
            observations.Add($"同步链路存在 {txFailures} 次发送失败、推断漏帧 {inferredMissedFrames} 帧。" );
        if (maintenanceRepeats > 0)
            observations.Add($"维护阶段发生 {maintenanceRepeats} 次重试/重复，传输冗余偏高。" );
        if (weakNodeCount > 0)
            observations.Add($"检测到 {weakNodeCount} 个弱链路 Node。" );
        if (p95.HasValue)
            observations.Add($"维护响应时延 P95 为 {p95.Value} ms。" );

        var suggestions = new List<string>();
        if (!overallSuccess) suggestions.Add("先解决阻断原因，再进行连续多轮闭环验证。" );
        if (txFailures > 0 || inferredMissedFrames > 0) suggestions.Add("检查 Sync 发送节拍、空口占用和帧头同步稳定性。" );
        if (maintenanceRepeats > 0) suggestions.Add("重点分析维护重试触发条件，降低重复分片和等待时延。" );
        if (weakNodeCount > 0) suggestions.Add("复测弱链路 Node，并核对 RSSI、天线和现场干扰。" );
        if (suggestions.Count == 0) suggestions.Add("当前指标稳定，建议继续执行循环升级验证长期可靠性。" );

        var summary = score >= 90
            ? "升级闭环与链路质量整体稳定。"
            : score >= 80
                ? "升级已闭环，但链路重试或时延仍有优化空间。"
                : score >= 60
                    ? "升级结果可用，但可靠性指标需要重点改进。"
                    : "升级质量未达到测试要求，应先处理阻断项。";

        var details = new StringBuilder()
            .AppendLine("评分明细")
            .AppendLine($"闭环完整性  {closedLoopScore}/50")
            .AppendLine($"目标完成度  {completionScore}/20")
            .AppendLine($"传输可靠性  {reliabilityScore}/20")
            .AppendLine($"时延表现    {performanceScore}/10")
            .AppendLine()
            .AppendLine("主要观察")
            .AppendLine(string.Join(Environment.NewLine, observations.Select(item => $"• {item}")))
            .AppendLine()
            .AppendLine("改进建议")
            .Append(string.Join(Environment.NewLine, suggestions.Select(item => $"• {item}")))
            .ToString();

        return new OtaUpgradeQualityAssessment(
            score,
            grade,
            color,
            summary,
            details,
            closedLoopScore,
            completionScore,
            reliabilityScore,
            performanceScore);
    }

    private static int RatioPoints(int value, int total, int maximum)
        => total <= 0 ? 0 : Math.Clamp((int)Math.Round(Math.Clamp(value, 0, total) * maximum / (double)total), 0, maximum);

    private static JsonElement GetObject(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static bool GetBoolean(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
           value.GetBoolean();

    private static int GetInt32(JsonElement element, string name)
        => GetNullableInt32(element, name) ?? 0;

    private static int? GetNullableInt32(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var result)
            ? result
            : null;

    private static int GetArrayLength(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;
}
