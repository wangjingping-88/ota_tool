using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace OtaTool.Update;

public sealed class UpdateService : IUpdateService, IDisposable
{
    public static readonly Uri DefaultReleasesPageUri =
        new("https://github.com/wangjingping-88/ota_tool/releases");

    private static readonly TimeSpan SuccessfulCheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan FailedCheckInterval = TimeSpan.FromHours(2);
    private readonly HttpClient _httpClient;
    private readonly GitHubReleaseClient _releaseClient;
    private readonly UpdateStateStore _stateStore;
    private readonly string _updateRoot;
    private readonly string _applicationDirectory;
    private readonly ReleaseVersion _currentVersion;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly bool _ownsHttpClient;

    public UpdateService(
        string updateRoot,
        string applicationDirectory,
        ReleaseVersion currentVersion,
        HttpClient? httpClient = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _updateRoot = UpdatePaths.Normalize(updateRoot);
        _applicationDirectory = UpdatePaths.Normalize(applicationDirectory);
        _currentVersion = currentVersion;
        _httpClient = httpClient ?? CreateHttpClient();
        _ownsHttpClient = httpClient is null;
        _releaseClient = new GitHubReleaseClient(_httpClient);
        _stateStore = new UpdateStateStore(Path.Combine(_updateRoot, "state.json"));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public Uri ReleasesPageUri => DefaultReleasesPageUri;

    public UpdateState GetState() => _stateStore.Load();

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        bool force,
        CancellationToken cancellationToken = default)
    {
        await _checkLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _stateStore.Load();
            var now = _utcNow();
            if (!force && ShouldSkipCheck(state, now))
            {
                return UpdateCheckResult.Skipped(_currentVersion);
            }

            try
            {
                var latest = await _releaseClient
                    .GetLatestReleaseAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (latest.IsDraft ||
                    latest.IsPrerelease ||
                    !ReleaseVersion.TryParseTag(latest.TagName, out var latestVersion))
                {
                    return RecordFailure(state, now, "GitHub 最新 Release 不是有效的正式版本。");
                }

                if (latestVersion.CompareTo(_currentVersion) <= 0)
                {
                    RecordSuccessfulCheck(state, now);
                    return UpdateCheckResult.NoUpdate(_currentVersion);
                }

                var version = latestVersion.ToString();
                var packageName = $"OtaTool-v{version}-win-x64-portable.zip";
                var checksumName = $"{packageName}.sha256.txt";
                var packages = latest.Assets
                    .Where(asset => string.Equals(asset.Name, packageName, StringComparison.Ordinal))
                    .ToArray();
                var checksums = latest.Assets
                    .Where(asset => string.Equals(asset.Name, checksumName, StringComparison.Ordinal))
                    .ToArray();
                if (packages.Length != 1 ||
                    checksums.Length != 1 ||
                    !TryCreateAsset(packages[0], out var package) ||
                    !TryCreateAsset(checksums[0], out var checksum) ||
                    !Uri.TryCreate(latest.HtmlUrl, UriKind.Absolute, out var releasePageUri))
                {
                    return RecordFailure(state, now, "新版本发布资源缺失、重复或摘要无效。");
                }

                var release = new UpdateReleaseInfo(
                    latestVersion,
                    latest.TagName,
                    latest.Body ?? "本次发布未提供更新说明。",
                    latest.PublishedAt,
                    releasePageUri,
                    package!,
                    checksum!);
                RecordSuccessfulCheck(state, now);
                return UpdateCheckResult.Available(_currentVersion, release);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return RecordFailure(state, now, exception.Message);
            }
        }
        finally
        {
            _checkLock.Release();
        }
    }

    public bool ShouldPrompt(UpdateReleaseInfo release) =>
        !string.Equals(
            _stateStore.Load().LastPromptedVersion,
            release.Version.ToString(),
            StringComparison.Ordinal);

    public void MarkPrompted(UpdateReleaseInfo release)
    {
        var state = _stateStore.Load();
        state.LastPromptedVersion = release.Version.ToString();
        _stateStore.Save(state);
    }

    public bool CanInstallInPlace(string installDirectory, out string reason)
    {
        try
        {
            var target = UpdatePaths.Normalize(installDirectory);
            if (UpdatePaths.IsDangerousInstallDirectory(target))
            {
                reason = "当前安装目录属于受保护目录，不能安全执行自动替换。";
                return false;
            }

            if (!File.Exists(Path.Combine(target, UpdatePaths.ApplicationFileName)))
            {
                reason = "当前目录不是有效的 OTA 测试平台便携目录。";
                return false;
            }

            if (!File.Exists(Path.Combine(_applicationDirectory, UpdatePaths.UpdaterFileName)))
            {
                reason = "当前版本未包含独立更新器，请先从 GitHub Release 手动更新。";
                return false;
            }

            var parent = Directory.GetParent(target)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) ||
                !string.Equals(Path.GetPathRoot(parent), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
            {
                reason = "安装目录没有可用于同卷切换的父目录。";
                return false;
            }

            VerifyDirectoryWritable(parent);
            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return false;
        }
    }

    public async Task<PreparedUpdate> DownloadAndPrepareAsync(
        UpdateReleaseInfo release,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (!CanInstallInPlace(_applicationDirectory, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        var target = _applicationDirectory;
        var targetParent = Directory.GetParent(target)?.FullName
                           ?? throw new InvalidOperationException("安装目录没有父目录。");
        var versionName = UpdatePaths.EnsureSafeFileName(release.Version.ToString());
        var downloadDirectory = Path.Combine(_updateRoot, "downloads", versionName);
        Directory.CreateDirectory(downloadDirectory);
        var packagePath = Path.Combine(downloadDirectory, release.PackageAsset.Name);
        var checksumPath = Path.Combine(downloadDirectory, release.ChecksumAsset.Name);
        var token = Guid.NewGuid().ToString("N");
        var stagingDirectory = Path.Combine(targetParent, $"{UpdatePaths.StagePrefix}{token}");
        var backupDirectory = Path.Combine(targetParent, $"{UpdatePaths.BackupPrefix}{token}");
        var jobDirectory = Path.Combine(_updateRoot, "jobs", token);
        var runtimeDirectory = Path.Combine(_updateRoot, "runtime", token);

        try
        {
            await DownloadFileAsync(
                release.ChecksumAsset,
                checksumPath,
                "下载校验文件",
                progress,
                cancellationToken).ConfigureAwait(false);
            await ValidateDownloadedAssetAsync(
                release.ChecksumAsset,
                checksumPath,
                cancellationToken).ConfigureAwait(false);
            var checksumText = await File.ReadAllTextAsync(checksumPath, cancellationToken).ConfigureAwait(false);
            var expectedHash = UpdatePackageUtilities.ParseSha256(
                checksumText,
                release.PackageAsset.Name);

            await DownloadFileAsync(
                release.PackageAsset,
                packagePath,
                "下载更新包",
                progress,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new UpdateDownloadProgress(
                "校验更新包",
                release.PackageAsset.Size,
                release.PackageAsset.Size,
                0));
            var actualHash = await ValidateDownloadedAssetAsync(
                release.PackageAsset,
                packagePath,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("更新包与 SHA-256 校验文件不一致。");
            }

            progress?.Report(new UpdateDownloadProgress(
                "解压并检查更新包",
                release.PackageAsset.Size,
                release.PackageAsset.Size,
                0));
            await Task.Run(
                () => UpdatePackageUtilities.ExtractVerifiedZip(packagePath, stagingDirectory),
                cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(runtimeDirectory);
            var updaterExecutable = Path.Combine(runtimeDirectory, UpdatePaths.UpdaterFileName);
            File.Copy(
                Path.Combine(_applicationDirectory, UpdatePaths.UpdaterFileName),
                updaterExecutable,
                overwrite: false);

            var confirmationFile = Path.Combine(jobDirectory, "startup-confirmed.txt");
            var jobFile = Path.Combine(jobDirectory, "update-job.json");
            UpdateJobStore.Save(jobFile, new UpdateJob
            {
                CurrentProcessId = Environment.ProcessId,
                InstallDirectory = target,
                StagingDirectory = stagingDirectory,
                BackupDirectory = backupDirectory,
                ApplicationFileName = UpdatePaths.ApplicationFileName,
                TargetVersion = release.Version.ToString(),
                ConfirmationFile = confirmationFile,
                LogFilePath = Path.Combine(_updateRoot, "logs", "updater.log"),
                UpdateStateFilePath = Path.Combine(_updateRoot, "state.json"),
            });

            var state = _stateStore.Load();
            state.PendingUpdate = new PendingUpdateState
            {
                TargetVersion = release.Version.ToString(),
                JobFilePath = jobFile,
                PreparedAtUtc = _utcNow(),
            };
            _stateStore.Save(state);
            progress?.Report(new UpdateDownloadProgress(
                "准备安装",
                release.PackageAsset.Size,
                release.PackageAsset.Size,
                0));
            return new PreparedUpdate(
                stagingDirectory,
                target,
                updaterExecutable,
                jobFile);
        }
        catch
        {
            UpdatePackageUtilities.TryDeleteFile(packagePath);
            UpdatePackageUtilities.TryDeleteFile(checksumPath);
            UpdatePackageUtilities.TryDeleteDirectory(stagingDirectory);
            UpdatePackageUtilities.TryDeleteDirectory(jobDirectory);
            UpdatePackageUtilities.TryDeleteDirectory(runtimeDirectory);
            throw;
        }
    }

    public void Dispose()
    {
        _checkLock.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static bool ShouldSkipCheck(UpdateState state, DateTimeOffset now) =>
        state.LastSuccessfulCheckUtc is { } successful && now - successful < SuccessfulCheckInterval ||
        state.LastFailedCheckUtc is { } failed && now - failed < FailedCheckInterval;

    private UpdateCheckResult RecordFailure(
        UpdateState state,
        DateTimeOffset now,
        string message)
    {
        state.LastFailedCheckUtc = now;
        TrySaveState(state);
        TryLogCheckFailure(now, message);
        return UpdateCheckResult.Failed(_currentVersion, message);
    }

    private void RecordSuccessfulCheck(UpdateState state, DateTimeOffset now)
    {
        state.LastSuccessfulCheckUtc = now;
        state.LastFailedCheckUtc = null;
        TrySaveState(state);
    }

    private void TrySaveState(UpdateState state)
    {
        try
        {
            _stateStore.Save(state);
        }
        catch
        {
        }
    }

    private async Task DownloadFileAsync(
        UpdateAsset asset,
        string destinationPath,
        string stage,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.download";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUri);
            request.Headers.UserAgent.ParseAdd("OtaTool-Updater/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } contentLength &&
                contentLength != asset.Size)
            {
                throw new InvalidDataException($"资源 {asset.Name} 的 HTTP 长度与 Release 元数据不一致。");
            }

            await using (var source = await response.Content
                             .ReadAsStreamAsync(cancellationToken)
                             .ConfigureAwait(false))
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                var stopwatch = Stopwatch.StartNew();
                long received = 0;
                while (true)
                {
                    var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    received += count;
                    progress?.Report(new UpdateDownloadProgress(
                        stage,
                        received,
                        asset.Size,
                        stopwatch.Elapsed.TotalSeconds > 0 ? received / stopwatch.Elapsed.TotalSeconds : 0));
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, destinationPath, true);
        }
        finally
        {
            UpdatePackageUtilities.TryDeleteFile(temporaryPath);
        }
    }

    private static async Task<string> ValidateDownloadedAssetAsync(
        UpdateAsset asset,
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != asset.Size)
        {
            throw new InvalidDataException($"资源 {asset.Name} 的实际大小不匹配。");
        }

        var hash = await UpdatePackageUtilities
            .ComputeSha256Async(path, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(asset.Digest["sha256:".Length..], hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"资源 {asset.Name} 的 GitHub SHA-256 摘要校验失败。");
        }

        return hash;
    }

    private static bool TryCreateAsset(
        GitHubReleaseAsset source,
        out UpdateAsset? asset)
    {
        asset = null;
        if (source.Size <= 0 ||
            !Uri.TryCreate(source.BrowserDownloadUrl, UriKind.Absolute, out var uri) ||
            !HasValidSha256Digest(source.Digest))
        {
            return false;
        }

        asset = new UpdateAsset(source.Name, uri, source.Size, source.Digest!);
        return true;
    }

    private static bool HasValidSha256Digest(string? digest) =>
        digest is { Length: 71 } &&
        digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
        digest["sha256:".Length..].All(Uri.IsHexDigit);

    private void TryLogCheckFailure(DateTimeOffset timestamp, string message)
    {
        try
        {
            var logs = Path.Combine(_updateRoot, "logs");
            Directory.CreateDirectory(logs);
            File.AppendAllText(
                Path.Combine(logs, "update-check.log"),
                $"[{timestamp:O}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static void VerifyDirectoryWritable(string directory)
    {
        var probe = Path.Combine(directory, $".ota-tool-write-{Guid.NewGuid():N}.tmp");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }
        }
        catch (Exception exception)
        {
            throw new UnauthorizedAccessException("安装目录不可写，无法执行自动更新。", exception);
        }
        finally
        {
            UpdatePackageUtilities.TryDeleteFile(probe);
        }
    }
}
