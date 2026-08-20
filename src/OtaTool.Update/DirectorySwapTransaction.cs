namespace OtaTool.Update;

public sealed class DirectorySwapTransaction
{
    private const int MoveRetryCount = 10;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(500);
    private readonly string _installDirectory;
    private readonly string _stagingDirectory;
    private readonly string _backupDirectory;
    private bool _applied;

    public DirectorySwapTransaction(
        string installDirectory,
        string stagingDirectory,
        string backupDirectory)
    {
        _installDirectory = UpdatePaths.Normalize(installDirectory);
        _stagingDirectory = UpdatePaths.Normalize(stagingDirectory);
        _backupDirectory = UpdatePaths.Normalize(backupDirectory);
        ValidatePaths();
    }

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_installDirectory))
        {
            throw new DirectoryNotFoundException($"安装目录不存在：{_installDirectory}");
        }

        if (!Directory.Exists(_stagingDirectory))
        {
            throw new DirectoryNotFoundException($"更新暂存目录不存在：{_stagingDirectory}");
        }

        if (Directory.Exists(_backupDirectory))
        {
            throw new IOException($"更新备份目录已经存在：{_backupDirectory}");
        }

        await MoveWithRetryAsync(
            _installDirectory,
            _backupDirectory,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await MoveWithRetryAsync(
                _stagingDirectory,
                _installDirectory,
                cancellationToken).ConfigureAwait(false);
            _applied = true;
        }
        catch
        {
            await MoveWithRetryAsync(
                _backupDirectory,
                _installDirectory,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (!_applied || !Directory.Exists(_backupDirectory))
        {
            return;
        }

        var failedDirectory = $"{_stagingDirectory}-failed-{Guid.NewGuid():N}";
        if (Directory.Exists(_installDirectory))
        {
            await MoveWithRetryAsync(
                _installDirectory,
                failedDirectory,
                cancellationToken).ConfigureAwait(false);
        }

        await MoveWithRetryAsync(
            _backupDirectory,
            _installDirectory,
            cancellationToken).ConfigureAwait(false);
        UpdatePackageUtilities.TryDeleteDirectory(failedDirectory);
        _applied = false;
    }

    public void Commit()
    {
        if (_applied)
        {
            UpdatePackageUtilities.TryDeleteDirectory(_backupDirectory);
            _applied = false;
        }
    }

    private void ValidatePaths()
    {
        var installParent = Directory.GetParent(_installDirectory)?.FullName;
        var stagingParent = Directory.GetParent(_stagingDirectory)?.FullName;
        var backupParent = Directory.GetParent(_backupDirectory)?.FullName;
        if (string.IsNullOrWhiteSpace(installParent) ||
            !string.Equals(stagingParent, installParent, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(backupParent, installParent, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetPathRoot(_installDirectory), Path.GetPathRoot(_stagingDirectory), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(_stagingDirectory).StartsWith(UpdatePaths.StagePrefix, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(_backupDirectory).StartsWith(UpdatePaths.BackupPrefix, StringComparison.OrdinalIgnoreCase) ||
            UpdatePaths.IsDangerousInstallDirectory(_installDirectory))
        {
            throw new InvalidOperationException("更新暂存、备份和安装目录不符合安全切换规则。");
        }
    }

    private static async Task MoveWithRetryAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < MoveRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastException = exception;
            }

            await Task.Delay(MoveRetryDelay, cancellationToken).ConfigureAwait(false);
        }

        throw new IOException($"无法将目录从 {source} 移动到 {destination}。", lastException);
    }
}
