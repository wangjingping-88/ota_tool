using System.Diagnostics;
using OtaTool.Core.Models;

namespace OtaTool.Core.Diff;

/// <summary>
/// 调用随桌面工具发布的分区差分命令行程序，生成与设备 Bootloader 对应的 Patch。
/// </summary>
public sealed class NativeBsdiffEngine : IDiffEngine
{
    private const string ExecutableName = "bsdiff_cmd.exe";

    public DiffEngineInfo GetInfo() => new(
        "partition-bsdiff-lzzip",
        "1.0.0",
        string.Empty,
        true,
        IsAvailable
            ? "差分引擎已就绪，可制作正向和反向 Patch。"
            : "差分引擎文件缺失，请重新安装 OTA 测试平台。");

    public async Task<DiffResult> GenerateAsync(DiffRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsAvailable) return new DiffResult(false, GetInfo().StatusMessage);
        if (!File.Exists(request.OldImagePath)) return new DiffResult(false, "A 版本固件不存在。");
        if (!File.Exists(request.NewImagePath)) return new DiffResult(false, "B 版本固件不存在。");
        if (await FirmwareImageHash.AreIdenticalAsync(
                request.OldImagePath,
                request.NewImagePath,
                cancellationToken))
        {
            return new DiffResult(false, "A/B 镜像内容相同，请重新导入不同版本镜像。");
        }

        var outputPath = Path.GetFullPath(request.PatchOutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add(Path.GetFullPath(request.OldImagePath));
        process.StartInfo.ArgumentList.Add(Path.GetFullPath(request.NewImagePath));
        process.StartInfo.ArgumentList.Add(outputPath);
        process.StartInfo.ArgumentList.Add(request.UpdateFirstBlock ? "1" : "0");

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var errorText = (await standardError).Trim();
        var outputText = (await standardOutput).Trim();

        if (process.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            var detail = string.IsNullOrWhiteSpace(errorText) ? outputText : errorText;
            return new DiffResult(false, $"差分制作失败（退出码 {process.ExitCode}）：{detail}");
        }

        var patch = await PatchMetadata.FromFileAsync(outputPath, cancellationToken);
        return new DiffResult(true, "Patch 制作完成。", patch);
    }

    public Task<PatchVerifyResult> VerifyAsync(string oldImagePath, string patchPath, string expectedNewImagePath, CancellationToken cancellationToken = default)
        => Task.FromResult(new PatchVerifyResult(false, "当前差分引擎未集成反向还原校验命令。"));

    private static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, ExecutableName);

    private static bool IsAvailable => File.Exists(ExecutablePath);
}
