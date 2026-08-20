using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using OtaTool.Update;

namespace OtaTool.App.ViewModels;

public sealed class ApplicationUpdateViewModel : ObservableObject, IDisposable
{
    private readonly UpdateService _service;
    private UpdateReleaseInfo? _availableRelease;
    private bool _isChecking;
    private string _status = "启动后将自动检查正式版本更新。";
    private string _lastCheck = "尚未检查";

    public ApplicationUpdateViewModel()
    {
        BuildInfo = ApplicationBuildInfo.FromAssembly(
            Assembly.GetEntryAssembly() ?? typeof(ApplicationUpdateViewModel).Assembly);
        _service = new UpdateService(
            UpdatePaths.DefaultUpdateRoot,
            BuildInfo.InstallDirectory,
            BuildInfo.Version);
        CheckNowCommand = new AsyncRelayCommand(() => CheckAsync(force: true));
        OpenReleasePageCommand = new RelayCommand(_ => OpenReleasePage());
        ShowUpdateCommand = new RelayCommand(_ => ShowAvailableUpdate());
    }

    public event EventHandler<UpdateReleaseInfo>? UpdateAvailable;

    public ApplicationBuildInfo BuildInfo { get; }

    public IUpdateService Service => _service;

    public UpdateReleaseInfo? AvailableRelease
    {
        get => _availableRelease;
        private set
        {
            if (!SetProperty(ref _availableRelease, value)) return;
            OnPropertyChanged(nameof(LatestVersion));
            OnPropertyChanged(nameof(HasAvailableUpdate));
            OnPropertyChanged(nameof(ActionButtonText));
        }
    }

    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (!SetProperty(ref _isChecking, value)) return;
            OnPropertyChanged(nameof(CheckButtonText));
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string LastCheck
    {
        get => _lastCheck;
        private set => SetProperty(ref _lastCheck, value);
    }

    public string CurrentVersion => BuildInfo.DisplayVersion;

    public string BuildTime => BuildInfo.BuildTimeUtc is { } buildTime
        ? $"{buildTime.UtcDateTime:yyyy-MM-dd HH:mm} UTC"
        : "未写入";

    public string GitCommit => BuildInfo.GitCommit;

    public string InstallDirectory => BuildInfo.InstallDirectory;

    public string UpdateChannel => "GitHub 正式版（latest stable）";

    public string LatestVersion => AvailableRelease is null
        ? "—"
        : $"v{AvailableRelease.Version}";

    public bool HasAvailableUpdate => AvailableRelease is not null;

    public string CheckButtonText => IsChecking ? "正在检查…" : "立即检查更新";

    public string ActionButtonText => HasAvailableUpdate ? "查看更新" : "打开发布页";

    public ICommand CheckNowCommand { get; }

    public ICommand OpenReleasePageCommand { get; }

    public ICommand ShowUpdateCommand { get; }

    public async Task CheckAtStartupAsync(CancellationToken cancellationToken = default)
    {
        var result = await CheckCoreAsync(force: false, cancellationToken);
        if (result?.Release is { } release && _service.ShouldPrompt(release))
        {
            _service.MarkPrompted(release);
            UpdateAvailable?.Invoke(this, release);
        }
    }

    public async Task CheckAsync(
        bool force,
        CancellationToken cancellationToken = default)
    {
        var result = await CheckCoreAsync(force, cancellationToken);
        if (result?.Release is { } release)
        {
            _service.MarkPrompted(release);
            UpdateAvailable?.Invoke(this, release);
        }
    }

    public void ShowAvailableUpdate()
    {
        if (AvailableRelease is { } release)
        {
            UpdateAvailable?.Invoke(this, release);
            return;
        }

        OpenReleasePage();
    }

    public void Dispose() => _service.Dispose();

    private async Task<UpdateCheckResult?> CheckCoreAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        if (IsChecking) return null;
        IsChecking = true;
        try
        {
            var result = await _service.CheckForUpdatesAsync(force, cancellationToken);
            LastCheck = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
            switch (result.Status)
            {
                case UpdateCheckStatus.Skipped:
                    Status = "近期已经检查过更新，本次自动检查已跳过。";
                    break;
                case UpdateCheckStatus.NoUpdate:
                    AvailableRelease = null;
                    Status = $"当前 {CurrentVersion} 已是最新正式版。";
                    break;
                case UpdateCheckStatus.UpdateAvailable:
                    AvailableRelease = result.Release;
                    Status = $"发现 {LatestVersion}，可查看说明并下载安装。";
                    break;
                case UpdateCheckStatus.Failed:
                    Status = $"检查失败：{result.ErrorMessage}";
                    break;
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "已取消检查更新。";
            return null;
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void OpenReleasePage()
    {
        var uri = AvailableRelease?.ReleasePageUri ?? _service.ReleasesPageUri;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
