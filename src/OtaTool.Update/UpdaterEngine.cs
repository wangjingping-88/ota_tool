using System.Diagnostics;

namespace OtaTool.Update;

public interface IUpdaterProcessRuntime
{
    Task<bool> WaitForExitAsync(
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    int Start(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory);

    bool HasExited(int processId);

    void Kill(int processId);
}

public sealed class SystemUpdaterProcessRuntime : IUpdaterProcessRuntime
{
    public async Task<bool> WaitForExitAsync(
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return process.HasExited;
            }
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    public int Start(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)?.Id
               ?? throw new InvalidOperationException($"无法启动程序：{executablePath}");
    }

    public bool HasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    public void Kill(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (ArgumentException)
        {
        }
    }
}

public sealed class UpdaterEngine
{
    private readonly IUpdaterProcessRuntime _processRuntime;
    private readonly string _updateRoot;
    private readonly TimeSpan _applicationExitTimeout;
    private readonly TimeSpan _startupConfirmationTimeout;
    private readonly TimeSpan _confirmationPollInterval;

    public UpdaterEngine(
        IUpdaterProcessRuntime? processRuntime = null,
        string? updateRoot = null,
        TimeSpan? applicationExitTimeout = null,
        TimeSpan? startupConfirmationTimeout = null,
        TimeSpan? confirmationPollInterval = null)
    {
        _processRuntime = processRuntime ?? new SystemUpdaterProcessRuntime();
        _updateRoot = UpdatePaths.Normalize(updateRoot ?? UpdatePaths.DefaultUpdateRoot);
        _applicationExitTimeout = applicationExitTimeout ?? TimeSpan.FromSeconds(30);
        _startupConfirmationTimeout = startupConfirmationTimeout ?? TimeSpan.FromSeconds(60);
        _confirmationPollInterval = confirmationPollInterval ?? TimeSpan.FromMilliseconds(250);
    }

    public async Task<int> RunAsync(
        string jobFilePath,
        CancellationToken cancellationToken = default)
    {
        UpdateJob? job = null;
        DirectorySwapTransaction? transaction = null;
        var newProcessId = 0;
        var validated = false;
        var originalApplicationExited = false;
        try
        {
            job = UpdateJobStore.Load(jobFilePath);
            ValidateJob(job, jobFilePath);
            validated = true;
            Log(job, $"开始安装 OTA 测试平台 v{job.TargetVersion}。");

            if (!await _processRuntime.WaitForExitAsync(
                    job.CurrentProcessId,
                    _applicationExitTimeout,
                    cancellationToken).ConfigureAwait(false))
            {
                Log(job, "主程序在 30 秒内没有正常退出，取消更新。");
                ClearPendingUpdate(job);
                return 2;
            }
            originalApplicationExited = true;

            transaction = new DirectorySwapTransaction(
                job.InstallDirectory,
                job.StagingDirectory,
                job.BackupDirectory);
            await transaction.ApplyAsync(cancellationToken).ConfigureAwait(false);
            UpdatePackageUtilities.TryDeleteFile(job.ConfirmationFile);
            var applicationPath = Path.Combine(job.InstallDirectory, job.ApplicationFileName);
            newProcessId = _processRuntime.Start(
                applicationPath,
                [UpdateStartupConfirmation.ArgumentName, job.ConfirmationFile],
                job.InstallDirectory);

            if (!await WaitForConfirmationAsync(
                    job.ConfirmationFile,
                    newProcessId,
                    cancellationToken).ConfigureAwait(false))
            {
                Log(job, "新版未完成启动确认，准备回滚。");
                if (!_processRuntime.HasExited(newProcessId))
                {
                    _processRuntime.Kill(newProcessId);
                }

                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                ClearPendingUpdate(job);
                StartRestoredApplication(job);
                Log(job, "已经恢复并启动旧版本。");
                return 3;
            }

            transaction.Commit();
            ClearPendingUpdate(job);
            Log(job, "更新完成，新版本启动确认成功。");
            return 0;
        }
        catch (Exception exception)
        {
            if (job is not null && validated)
            {
                Log(job, $"更新失败：{exception}");
                try
                {
                    if (newProcessId != 0 && !_processRuntime.HasExited(newProcessId))
                    {
                        _processRuntime.Kill(newProcessId);
                    }

                    if (transaction is not null)
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }

                    ClearPendingUpdate(job);
                    if (originalApplicationExited)
                    {
                        StartRestoredApplication(job);
                    }
                }
                catch (Exception rollbackException)
                {
                    Log(job, $"自动回滚失败：{rollbackException}");
                }
            }

            return 1;
        }
    }

    private async Task<bool> WaitForConfirmationAsync(
        string confirmationFile,
        int processId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _startupConfirmationTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(confirmationFile))
            {
                return true;
            }

            if (_processRuntime.HasExited(processId))
            {
                return false;
            }

            await Task.Delay(_confirmationPollInterval, cancellationToken).ConfigureAwait(false);
        }

        return File.Exists(confirmationFile);
    }

    private void ValidateJob(UpdateJob job, string jobFilePath)
    {
        var jobsRoot = Path.Combine(_updateRoot, "jobs");
        if (!UpdatePaths.IsPathWithin(jobFilePath, jobsRoot) ||
            !UpdatePaths.IsPathWithin(job.ConfirmationFile, jobsRoot) ||
            !UpdatePaths.IsPathWithin(job.LogFilePath, _updateRoot) ||
            !UpdatePaths.IsPathWithin(job.UpdateStateFilePath, _updateRoot) ||
            UpdatePaths.IsDangerousInstallDirectory(job.InstallDirectory) ||
            !string.Equals(job.ApplicationFileName, UpdatePaths.ApplicationFileName, StringComparison.Ordinal) ||
            !File.Exists(Path.Combine(job.InstallDirectory, job.ApplicationFileName)) ||
            !File.Exists(Path.Combine(job.StagingDirectory, job.ApplicationFileName)) ||
            !File.Exists(Path.Combine(job.StagingDirectory, UpdatePaths.UpdaterFileName)))
        {
            throw new InvalidDataException("更新任务路径或必要文件无效。");
        }

        _ = new DirectorySwapTransaction(
            job.InstallDirectory,
            job.StagingDirectory,
            job.BackupDirectory);
    }

    private void StartRestoredApplication(UpdateJob job)
    {
        var applicationPath = Path.Combine(job.InstallDirectory, job.ApplicationFileName);
        if (File.Exists(applicationPath))
        {
            _processRuntime.Start(applicationPath, [], job.InstallDirectory);
        }
    }

    private static void ClearPendingUpdate(UpdateJob job)
    {
        try
        {
            new UpdateStateStore(job.UpdateStateFilePath).ClearPendingUpdate();
        }
        catch
        {
        }
    }

    private static void Log(UpdateJob job, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(job.LogFilePath)!);
            File.AppendAllText(
                job.LogFilePath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
