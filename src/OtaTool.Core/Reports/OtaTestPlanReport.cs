using System.Text.Json;
using OtaTool.Core.Models;

namespace OtaTool.Core.Reports;

public sealed class OtaTestPlanItemReport
{
    public OtaTestPlanItemTemplate Template { get; init; } = new();

    public OtaTestPlanItemState State { get; set; } = OtaTestPlanItemState.NeedsReview;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public string Message { get; set; } = string.Empty;

    public IReadOnlyList<string> ResolvedTargets { get; set; } = [];

    public IReadOnlyList<Guid> ChildReportIds { get; set; } = [];
}

public sealed class OtaTestPlanReport
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public OtaTestPlanTemplate Plan { get; init; } = new();

    public OtaTestPlanState FinalState { get; set; } = OtaTestPlanState.Draft;

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;

    public DateTimeOffset? FinishedAt { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }

    public List<OtaTestPlanItemReport> Items { get; init; } = [];

    public bool IsArchived => ArchivedAt.HasValue;

    public int SucceededCount => Items.Count(item => item.State == OtaTestPlanItemState.Succeeded);

    public int FailedCount => Items.Count(item => item.State is OtaTestPlanItemState.Failed or OtaTestPlanItemState.TimedOut);

    public int SkippedCount => Items.Count(item => item.State == OtaTestPlanItemState.Skipped);
}

public static class OtaTestPlanReportExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<string> ExportJsonAsync(
        OtaTestPlanReport report,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var absolutePath = EnsureOutputDirectory(outputPath);
        await using var stream = File.Create(absolutePath);
        await JsonSerializer.SerializeAsync(stream, report, JsonOptions, cancellationToken);
        return absolutePath;
    }

    public static async Task<string> ExportHtmlAsync(
        OtaTestPlanReport report,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var absolutePath = EnsureOutputDirectory(outputPath);
        var rows = string.Join(Environment.NewLine, report.Items
            .OrderBy(item => item.Template.Order)
            .Select((item, index) =>
            {
                var duration = item.StartedAt.HasValue && item.FinishedAt.HasValue
                    ? $"{Math.Max(0, (item.FinishedAt.Value - item.StartedAt.Value).TotalSeconds):N1} 秒"
                    : "—";
                var targets = item.ResolvedTargets.Count == 0 ? "—" : string.Join("、", item.ResolvedTargets);
                var children = item.ChildReportIds.Count == 0 ? "—" : string.Join("、", item.ChildReportIds);
                return $"<tr><td>{index + 1}</td><td>{Html(item.Template.Name)}</td><td>{Html(item.Template.DeviceType.ToString())}</td><td>{Html(item.Template.ExecutionKind.ToString())}</td><td>{Html(item.Template.OldVersion)} to {Html(item.Template.NewVersion)}</td><td>{Html(targets)}</td><td>{Html(duration)}</td><td>{Html(children)}</td><td>{Html(item.State.ToString())}</td><td>{Html(item.Message)}</td></tr>";
            }));
        var html = $$"""
<!DOCTYPE html><html lang="zh-CN"><head><meta charset="utf-8"><title>OTA 多任务测试计划报告</title>
<style>body{font-family:"Microsoft YaHei UI",Arial,sans-serif;margin:32px;color:#172033;background:#f4f6f9}main{max-width:1200px;margin:auto;background:#fff;border:1px solid #dde3ec;border-radius:10px;padding:28px}h1{margin:0 0 20px}dl{display:grid;grid-template-columns:150px 1fr;gap:8px 16px}dt{color:#657287}table{width:100%;border-collapse:collapse;margin-top:22px}th,td{padding:10px;border-bottom:1px solid #dde3ec;text-align:left;font-size:13px}th{background:#f5f8fc}</style>
</head><body><main><h1>OTA 多任务测试计划报告</h1><dl><dt>计划名称</dt><dd>{{Html(report.Plan.Name)}}</dd><dt>Gateway</dt><dd>{{Html(report.Plan.GatewayId)}}</dd><dt>模式</dt><dd>{{Html(report.Plan.Mode.ToString())}}</dd><dt>失败策略</dt><dd>{{(report.Plan.ContinueOnFailure ? "失败后继续" : "遇错停止")}}</dd><dt>任务间隔</dt><dd>{{report.Plan.InterItemDelaySeconds}} 秒</dd><dt>最终状态</dt><dd>{{Html(report.FinalState.ToString())}}</dd><dt>统计</dt><dd>成功 {{report.SucceededCount}} / 失败 {{report.FailedCount}} / 跳过 {{report.SkippedCount}}</dd></dl><table><thead><tr><th>#</th><th>任务</th><th>类型</th><th>执行方式</th><th>版本</th><th>实际目标</th><th>耗时</th><th>子报告 ID</th><th>状态</th><th>结果</th></tr></thead><tbody>{{rows}}</tbody></table></main></body></html>
""";
        await File.WriteAllTextAsync(absolutePath, html, cancellationToken);
        return absolutePath;
    }

    private static string EnsureOutputDirectory(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var absolutePath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        return absolutePath;
    }

    private static string Html(string? text) => System.Net.WebUtility.HtmlEncode(text ?? string.Empty);
}
