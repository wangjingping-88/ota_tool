using System.Diagnostics;
using OtaTool.Core.Models;

namespace OtaTool.Core.Diff;

/// <summary>
/// 调用随桌面工具发布的分区差分命令行程序，生成与设备 Bootloader 对应的 Patch。
/// </summary>
public sealed class NativeBsdiffEngine : IDiffEngine
{
    private const string GeneratorExecutableName = "bsdiff_cmd.exe";
    private const string VerifierExecutableName = "partition_patch_verify.exe";

    public DiffEngineInfo GetInfo() => new(
        "partition-bsdiff-lzzip",
        "1.1.0-native-verify",
        string.Empty,
        true,
        IsAvailable
            ? "差分引擎和原生还原验证器已就绪。"
            : MissingComponentMessage);

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
                FileName = GeneratorExecutablePath,
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

    public async Task<PatchVerifyResult> VerifyAsync(
        string oldImagePath,
        string patchPath,
        string expectedNewImagePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(VerifierExecutablePath))
        {
            return new PatchVerifyResult(false, "缺少原生 Patch 还原验证器，请重新安装 OTA 测试平台。");
        }
        if (!File.Exists(oldImagePath)) return new PatchVerifyResult(false, "还原源镜像不存在。");
        if (!File.Exists(patchPath)) return new PatchVerifyResult(false, "待验证 Patch 不存在。");
        if (!File.Exists(expectedNewImagePath)) return new PatchVerifyResult(false, "还原目标镜像不存在。");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = VerifierExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add(Path.GetFullPath(oldImagePath));
        process.StartInfo.ArgumentList.Add(Path.GetFullPath(patchPath));
        process.StartInfo.ArgumentList.Add(Path.GetFullPath(expectedNewImagePath));

        try
        {
            process.Start();
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }

            var outputText = (await standardOutput).Trim();
            var errorText = (await standardError).Trim();
            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(errorText) ? outputText : errorText;
                if (string.IsNullOrWhiteSpace(detail)) detail = "验证器未返回具体原因。";
                return new PatchVerifyResult(
                    false,
                    $"原生还原验证失败（退出码 {process.ExitCode}）：{detail}");
            }

            return new PatchVerifyResult(
                true,
                string.IsNullOrWhiteSpace(outputText)
                    ? "原生 Patch 还原验证通过。"
                    : $"原生 Patch 还原验证通过：{outputText}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new PatchVerifyResult(false, $"原生还原验证器启动失败：{exception.Message}");
        }
    }

    private static string GeneratorExecutablePath => Path.Combine(AppContext.BaseDirectory, GeneratorExecutableName);

    private static string VerifierExecutablePath => Path.Combine(AppContext.BaseDirectory, VerifierExecutableName);

    private static bool IsAvailable => File.Exists(GeneratorExecutablePath) && File.Exists(VerifierExecutablePath);

    private static string MissingComponentMessage => !File.Exists(GeneratorExecutablePath)
        ? "差分制作引擎缺失，请重新安装 OTA 测试平台。"
        : "原生 Patch 还原验证器缺失，请重新安装 OTA 测试平台。";
}
