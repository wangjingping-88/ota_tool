using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OtaTool.Core.Models;

namespace OtaTool.Core.Analysis;

public sealed record LogAnalysisRequest(OtaMode Mode, string AnalyzerExecutablePath, string LogDirectory, string OutputDirectory);

public sealed record LogAnalysisResult(
    bool IsSuccess,
    string Message,
    string? JsonResultPath,
    JsonDocument? Data = null,
    string? HumanReadableReport = null) : IDisposable
{
    public void Dispose() => Data?.Dispose();
}

public interface ILogAnalyzer
{
    Task<LogAnalysisResult> AnalyzeAsync(LogAnalysisRequest request, CancellationToken cancellationToken = default);
}

public sealed class ExternalEcoLinkLogAnalyzer : ILogAnalyzer
{
    public async Task<LogAnalysisResult> AnalyzeAsync(LogAnalysisRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Mode != OtaMode.EcoLink)
        {
            return new LogAnalysisResult(false, "传统模式不支持日志解析。", null);
        }
        var analyzerPath = Path.GetFullPath(request.AnalyzerExecutablePath);
        var logDirectory = Path.GetFullPath(request.LogDirectory);
        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        if (!File.Exists(analyzerPath)) return new LogAnalysisResult(false, $"日志分析器不存在：{analyzerPath}", null);
        if (!Directory.Exists(logDirectory)) return new LogAnalysisResult(false, "日志目录不存在。", null);
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, $"ota-log-analysis-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
        var isPythonScript = string.Equals(Path.GetExtension(analyzerPath), ".py", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo(isPythonScript ? "python" : analyzerPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = logDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (isPythonScript)
        {
            startInfo.Environment["PYTHONUTF8"] = "1";
            startInfo.ArgumentList.Add(analyzerPath);
        }
        startInfo.ArgumentList.Add(logDirectory);
        startInfo.ArgumentList.Add("--json-out");
        startInfo.ArgumentList.Add(outputPath);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动日志分析器。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (!File.Exists(outputPath))
        {
            return new LogAnalysisResult(false, $"日志分析失败：{(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}", null);
        }
        await using var stream = File.OpenRead(outputPath);
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var reportText = stdout.Trim();
        var message = process.ExitCode == 0
            ? "日志分析完成。"
            : "日志分析完成，检测到升级未闭环或存在异常。";
        return new LogAnalysisResult(true, message, outputPath, document, reportText);
    }
}
