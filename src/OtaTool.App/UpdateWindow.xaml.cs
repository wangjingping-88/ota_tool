using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using OtaTool.App.ViewModels;
using OtaTool.Update;

namespace OtaTool.App;

public partial class UpdateWindow : Window, INotifyPropertyChanged
{
    private readonly MainWindowViewModel _mainViewModel;
    private readonly UpdateReleaseInfo _release;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _isWorking;
    private double _progressPercent;
    private string _status;

    public UpdateWindow(
        MainWindowViewModel mainViewModel,
        UpdateReleaseInfo release)
    {
        _mainViewModel = mainViewModel;
        _release = release;
        _status = mainViewModel.CanPrepareForApplicationUpdate(out var installReason)
            ? $"准备下载 {release.PackageAsset.Name}（{FormatBytes(release.PackageAsset.Size)}）。"
            : installReason;
        InitializeComponent();
        DataContext = this;
        Closed += (_, _) => _cancellation.Cancel();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected override void OnSourceInitialized(EventArgs eventArgs)
    {
        base.OnSourceInitialized(eventArgs);
        NativeWindowShadow.Apply(this);
    }

    public string VersionTitle => $"发现 v{_release.Version}";

    public string VersionSummary => $"当前 {_mainViewModel.ApplicationUpdate.CurrentVersion} · 发布于 {(_release.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未知时间")}";

    public string ReleaseNotes => string.IsNullOrWhiteSpace(_release.ReleaseNotes)
        ? "本次发布未提供更新说明。"
        : _release.ReleaseNotes;

    public bool IsIdle => !_isWorking;

    public bool CanInstall => !_isWorking && _mainViewModel.CanPrepareForApplicationUpdate(out _);

    public string InstallButtonText => _isWorking ? "正在准备…" : "下载并安装";

    public Visibility ProgressVisibility => _isWorking ? Visibility.Visible : Visibility.Collapsed;

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    private void OnLaterClick(object sender, RoutedEventArgs eventArgs) => Close();

    private void OnOpenReleaseClick(object sender, RoutedEventArgs eventArgs)
    {
        Process.Start(new ProcessStartInfo(_release.ReleasePageUri.AbsoluteUri)
        {
            UseShellExecute = true,
        });
    }

    private async void OnInstallClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!_mainViewModel.CanPrepareForApplicationUpdate(out var reason))
        {
            Status = reason;
            OnPropertyChanged(nameof(CanInstall));
            return;
        }

        IsWorking = true;
        Process? updaterProcess = null;
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                ProgressPercent = value.Percent ?? 0;
                var speed = value.BytesPerSecond > 0 ? $" · {FormatBytes((long)value.BytesPerSecond)}/s" : string.Empty;
                Status = $"{value.Stage}：{FormatBytes(value.BytesReceived)} / {FormatBytes(value.TotalBytes ?? 0)}{speed}";
            });
            var prepared = await _mainViewModel.ApplicationUpdate.Service.DownloadAndPrepareAsync(
                _release,
                progress,
                _cancellation.Token);
            Status = "校验完成，正在安全关闭服务并启动独立更新器。";

            var processInfo = new ProcessStartInfo(prepared.UpdaterExecutablePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(prepared.UpdaterExecutablePath)!,
            };
            processInfo.ArgumentList.Add("--job");
            processInfo.ArgumentList.Add(prepared.JobFilePath);
            updaterProcess = Process.Start(processInfo)
                ?? throw new InvalidOperationException("独立更新器启动失败。");

            var preparation = await _mainViewModel.PrepareForApplicationUpdateAsync();
            if (!preparation.Success)
            {
                throw new InvalidOperationException(preparation.Reason);
            }

            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            StopUpdaterIfRunning(updaterProcess);
            Status = "已取消下载，临时更新文件已清理。";
            IsWorking = false;
        }
        catch (Exception exception)
        {
            StopUpdaterIfRunning(updaterProcess);
            Status = $"更新未启动：{exception.Message}";
            IsWorking = false;
        }
    }

    private static void StopUpdaterIfRunning(Process? process)
    {
        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
        finally
        {
            process?.Dispose();
        }
    }

    private bool IsWorking
    {
        get => _isWorking;
        set
        {
            if (!SetProperty(ref _isWorking, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(InstallButtonText));
            OnPropertyChanged(nameof(ProgressVisibility));
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }

    private bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
