using System.Text.Json;
using OtaTool.Core.Execution;
using OtaTool.Core.Models;

namespace OtaTool.Core.Reports;

public sealed class OtaReport
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public OtaTask Task { get; init; } = new();

    public OtaTaskState FinalState { get; set; } = OtaTaskState.Draft;

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;

    public DateTimeOffset? FinishedAt { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }

    public List<OtaExecutionUpdate> Timeline { get; init; } = [];

    public string? LogAnalysisConclusion { get; set; }

    public OtaCycleSummary? Cycle { get; set; }

    public bool IsArchived => ArchivedAt.HasValue;

    public void AddUpdate(OtaExecutionUpdate update)
    {
        Timeline.Add(update);
        FinalState = update.State;
        if (update.State is OtaTaskState.Succeeded or OtaTaskState.PartiallySucceeded or OtaTaskState.Failed or OtaTaskState.Cancelled or OtaTaskState.TimedOut)
        {
            FinishedAt = update.OccurredAt;
        }
    }
}

public sealed record OtaCycleSummary(int RequestedRounds, int CompletedSteps, int SuccessfulSteps, TimeSpan Duration, string ResultMessage);

public static class OtaReportExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<string> ExportJsonAsync(OtaReport report, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var absolutePath = EnsureOutputDirectory(outputPath);
        await using var stream = File.Create(absolutePath);
        await JsonSerializer.SerializeAsync(stream, report, JsonOptions, cancellationToken);
        return absolutePath;
    }

    public static async Task<string> ExportHtmlAsync(OtaReport report, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var absolutePath = EnsureOutputDirectory(outputPath);
        var title = "OTA 测试报告";
        var rows = string.Join(Environment.NewLine, report.Timeline.Select(update => $"<tr><td>{Html(update.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss"))}</td><td>{Html(update.State.ToString())}</td><td>{Html(update.Message)}</td></tr>"));
        var logAnalysis = report.Task.Mode == OtaMode.Traditional ? "日志解析不支持" : report.LogAnalysisConclusion ?? "未导入日志";
        var cycle = report.Cycle is null
            ? "非循环任务"
            : $"{report.Cycle.RequestedRounds} 轮，成功步骤 {report.Cycle.SuccessfulSteps}/{report.Cycle.CompletedSteps}，耗时 {report.Cycle.Duration}";
        var finalStatus = report.Timeline.LastOrDefault(update => update.GatewayStatus is not null)?.GatewayStatus;
        var requestAck = report.Timeline.LastOrDefault(update => update.RequestAck is not null)?.RequestAck;
        var startAcceptance = requestAck is not null
            ? $"{requestAck.Result} / accepted={(requestAck.Accepted ? 1 : 0)} / duplicate={(requestAck.Duplicate ? 1 : 0)} / SID {requestAck.SessionId}" +
              (string.IsNullOrWhiteSpace(requestAck.Reason) ? string.Empty : $" / {requestAck.Reason}")
            : report.Timeline.Any(update => update.GatewayStatus?.Result.Equals("OK", StringComparison.OrdinalIgnoreCase) == true)
                ? "未收到 cmd=10，由 cmd=9 确认任务已建立"
                : "未确认";
        var retryPolicy = report.Task.DeviceType == DeviceType.Node
            ? report.Task.NodeRetryCount == 1 ? "失败隔离并重试一次" : "整批失败（基线）"
            : "不适用";
        var retryResult = finalStatus?.RetryRound is { } retryRound ? retryRound.ToString() : "未回显";
        var failedNodes = finalStatus?.FailedNodes.Count > 0
            ? string.Join("；", finalStatus.FailedNodes.Select(node => $"Extender {node.ExtenderId} / 类型 {node.NodeType} / Node {node.NodeId}"))
            : "无或未返回";
        var html = $$"""
<!DOCTYPE html><html lang="zh-CN"><head><meta charset="utf-8"><title>{{title}}</title>
<style>body{font-family:"Microsoft YaHei UI",Arial,sans-serif;margin:32px;color:#172033;background:#f4f6f9}main{max-width:1100px;margin:auto;background:#fff;border:1px solid #dde3ec;border-radius:10px;padding:28px}h1{margin:0 0 20px}dl{display:grid;grid-template-columns:170px 1fr;gap:8px 16px}dt{color:#657287}table{width:100%;border-collapse:collapse;margin-top:22px}th,td{padding:10px;border-bottom:1px solid #dde3ec;text-align:left;font-size:13px}th{background:#f5f8fc}.ok{color:#159e68;font-weight:700}.warn{color:#b87500;font-weight:700}.bad{color:#c53333;font-weight:700}</style>
</head><body><main><h1>{{title}}</h1><dl><dt>报告 ID</dt><dd>{{report.Id}}</dd><dt>模式</dt><dd>{{Html(report.Task.Mode.ToString())}}</dd><dt>协议适配器</dt><dd>{{Html(report.Task.ProtocolProfileId)}} {{Html(report.Task.ProtocolProfileVersion)}}</dd><dt>启动受理</dt><dd>{{Html(startAcceptance)}}</dd><dt>升级类型</dt><dd>{{Html(report.Task.DeviceType.ToString())}}</dd><dt>版本</dt><dd>{{Html(report.Task.OldVersion)}} → {{Html(report.Task.NewVersion)}}</dd><dt>Node 失败处理</dt><dd>{{Html(retryPolicy)}}</dd><dt>实际重试轮次</dt><dd>{{Html(retryResult)}}</dd><dt>最终失败节点</dt><dd>{{Html(failedNodes)}}</dd><dt>Patch MD5</dt><dd>{{Html(report.Task.PatchMd5)}}</dd><dt>Patch SHA256</dt><dd>{{Html(report.Task.PatchSha256)}}</dd><dt>循环测试</dt><dd>{{Html(cycle)}}</dd><dt>最终状态</dt><dd class="{{(report.FinalState == OtaTaskState.Succeeded ? "ok" : report.FinalState == OtaTaskState.PartiallySucceeded ? "warn" : "bad")}}">{{Html(report.FinalState.ToString())}}</dd><dt>日志分析</dt><dd>{{Html(logAnalysis)}}</dd></dl><h2>任务时间线</h2><table><thead><tr><th>时间</th><th>状态</th><th>事件</th></tr></thead><tbody>{{rows}}</tbody></table></main></body></html>
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

    private static string Html(string value) => System.Net.WebUtility.HtmlEncode(value);
}
