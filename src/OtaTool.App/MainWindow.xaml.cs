using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OtaTool.App.ViewModels;

namespace OtaTool.App;

public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const double CompactLayoutWidth = 1400;
    private const double ShortLayoutHeight = 820;
    private bool _followGlobalLog = true;
    private bool _followMqttMessages = true;
    private bool _responsiveLayoutReady;
    private bool _showCompactTaskStatus;
    private MainWindowViewModel? _mqttMessageSource;
    private HwndSource? _windowSource;
    private UpdateWindow? _updateWindow;
    private bool _startupCompleted;
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        _responsiveLayoutReady = true;
        ApplyResponsiveLayout();
        var viewModel = new MainWindowViewModel();
        viewModel.ApplicationUpdate.UpdateAvailable += OnUpdateAvailable;
        viewModel.CloseApplicationRequested += OnCloseApplicationRequested;
        DataContext = viewModel;
    }

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (_startupCompleted || DataContext is not MainWindowViewModel viewModel) return;
        _startupCompleted = true;
        ApplyResponsiveLayout();
        try
        {
            await viewModel.Initialization;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
            var hasConfirmationArgument = arguments.Any(argument =>
                string.Equals(argument, "--update-confirm", StringComparison.OrdinalIgnoreCase));
            if (hasConfirmationArgument &&
                !viewModel.ConfirmUpdatedStartup(arguments, out var confirmationError))
            {
                throw new InvalidOperationException($"新版启动确认失败：{confirmationError}");
            }

            await viewModel.ApplicationUpdate.CheckAtStartupAsync();
        }
        catch (Exception exception)
        {
            viewModel.ShowInformationDialog(
                "应用初始化未完全完成",
                exception.Message);
        }
    }

    private void OnUpdateAvailable(object? sender, OtaTool.Update.UpdateReleaseInfo release)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_updateWindow is { IsVisible: true })
            {
                _updateWindow.Activate();
                return;
            }

            if (DataContext is not MainWindowViewModel viewModel) return;
            _updateWindow = new UpdateWindow(viewModel, release)
            {
                Owner = this,
            };
            _updateWindow.Closed += (_, _) => _updateWindow = null;
            _updateWindow.ShowDialog();
        });
    }

    protected override void OnSourceInitialized(EventArgs eventArgs)
    {
        base.OnSourceInitialized(eventArgs);
        var windowHandle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(windowHandle);
        _windowSource?.AddHook(WindowProcedure);
        FitWindowToCurrentWorkArea(windowHandle);
        ApplyResponsiveLayout();
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs eventArgs)
        => ApplyResponsiveLayout();

    private void OnWindowDpiChanged(object sender, DpiChangedEventArgs eventArgs)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var windowHandle = new WindowInteropHelper(this).Handle;
            if (windowHandle != IntPtr.Zero) FitWindowToCurrentWorkArea(windowHandle);
            ApplyResponsiveLayout();
        }, DispatcherPriority.Loaded);
    }

    private void OnCompactTaskConfigurationChecked(object sender, RoutedEventArgs eventArgs)
    {
        if (!_responsiveLayoutReady) return;
        _showCompactTaskStatus = false;
        ApplyResponsiveLayout();
    }

    private void OnCompactTaskStatusChecked(object sender, RoutedEventArgs eventArgs)
    {
        if (!_responsiveLayoutReady) return;
        _showCompactTaskStatus = true;
        ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        if (!_responsiveLayoutReady) return;

        var layoutWidth = ActualWidth > 0 ? ActualWidth : Width;
        var layoutHeight = ActualHeight > 0 ? ActualHeight : Height;
        var isCompact = layoutWidth < CompactLayoutWidth;
        var isShort = layoutHeight < ShortLayoutHeight;

        NavigationColumn.Width = new GridLength(isCompact ? 176 : 220);
        HeaderRow.Height = new GridLength(isCompact ? 84 : 100);
        GlobalLogRow.Height = new GridLength(isShort ? 112 : isCompact ? 156 : 204);
        FooterRow.Height = new GridLength(isCompact ? 30 : 34);
        HeaderContentGrid.Margin = isCompact ? new Thickness(18, 0, 18, 0) : new Thickness(28, 0, 28, 0);
        ContentHostGrid.Margin = isCompact ? new Thickness(16, 14, 16, 14) : new Thickness(28, 24, 28, 24);
        GlobalLogBorder.Padding = isCompact ? new Thickness(18, 7, 18, 5) : new Thickness(28, 8, 28, 6);
        FooterBorder.Padding = isCompact ? new Thickness(18, 0, 18, 0) : new Thickness(28, 0, 28, 0);
        GlobalLogTitleText.Text = isCompact
            ? "全局运行日志（最近 300 行）"
            : "全局运行日志（仅保留本次运行最近 300 行，滚轮可暂停查看，按 Enter 回到最新日志）";
        FooterShortcutText.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        CompactTaskViewSelector.Visibility = isCompact ? Visibility.Visible : Visibility.Collapsed;

        if (isCompact)
        {
            TaskConfigurationColumn.Width = new GridLength(1, GridUnitType.Star);
            TaskLayoutGutterColumn.Width = new GridLength(0);
            TaskStatusColumn.Width = new GridLength(0);
            Grid.SetColumn(TaskConfigurationScrollViewer, 0);
            Grid.SetColumn(TaskStatusScrollViewer, 0);
            TaskConfigurationScrollViewer.Visibility = _showCompactTaskStatus ? Visibility.Collapsed : Visibility.Visible;
            TaskStatusScrollViewer.Visibility = _showCompactTaskStatus ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        TaskConfigurationColumn.Width = new GridLength(1, GridUnitType.Star);
        TaskLayoutGutterColumn.Width = new GridLength(16);
        TaskStatusColumn.Width = new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(TaskConfigurationScrollViewer, 0);
        Grid.SetColumn(TaskStatusScrollViewer, 2);
        TaskConfigurationScrollViewer.Visibility = Visibility.Visible;
        TaskStatusScrollViewer.Visibility = Visibility.Visible;
    }

    private void FitWindowToCurrentWorkArea(IntPtr windowHandle)
    {
        var monitorHandle = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitorHandle == IntPtr.Zero) return;

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>(),
        };
        if (!GetMonitorInfo(monitorHandle, ref monitorInfo)) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var workAreaWidth = (monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left) / dpi.DpiScaleX;
        var workAreaHeight = (monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top) / dpi.DpiScaleY;
        Width = Math.Min(Width, Math.Max(MinWidth, workAreaWidth));
        Height = Math.Min(Height, Math.Max(MinHeight, workAreaHeight));
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs eventArgs)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void OnToggleMaximizeClick(object sender, RoutedEventArgs eventArgs)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
            return;
        }

        SystemCommands.MaximizeWindow(this);
    }

    private void OnCloseClick(object sender, RoutedEventArgs eventArgs)
    {
        SystemCommands.CloseWindow(this);
    }

    protected override void OnClosing(CancelEventArgs eventArgs)
    {
        if (!_closeConfirmed &&
            DataContext is MainWindowViewModel viewModel &&
            viewModel.RequestCloseApplicationConfirmation())
        {
            eventArgs.Cancel = true;
        }
        base.OnClosing(eventArgs);
    }

    private void OnCloseApplicationRequested(object? sender, EventArgs eventArgs)
    {
        _closeConfirmed = true;
        Close();
    }

    private async void OnClosed(object? sender, EventArgs eventArgs)
    {
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WindowProcedure);
            _windowSource = null;
        }

        if (_mqttMessageSource is not null)
        {
            _mqttMessageSource.MqttMessages.CollectionChanged -= OnMqttMessagesCollectionChanged;
            _mqttMessageSource = null;
        }

        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ApplicationUpdate.UpdateAvailable -= OnUpdateAvailable;
            viewModel.CloseApplicationRequested -= OnCloseApplicationRequested;
        }

        if (DataContext is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
    }

    private void OnGlobalLogPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Enter || sender is not TextBox textBox) return;
        _followGlobalLog = true;
        textBox.CaretIndex = textBox.Text.Length;
        textBox.ScrollToEnd();
        eventArgs.Handled = true;
    }

    private void OnGlobalLogPreviewMouseWheel(object sender, MouseWheelEventArgs eventArgs)
    {
        _followGlobalLog = false;
    }

    private void OnGlobalLogTextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        if (_followGlobalLog && sender is TextBox textBox) textBox.ScrollToEnd();
    }

    private void OnMqttMessagesLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not ScrollViewer || DataContext is not MainWindowViewModel viewModel || ReferenceEquals(_mqttMessageSource, viewModel)) return;

        if (_mqttMessageSource is not null)
        {
            _mqttMessageSource.MqttMessages.CollectionChanged -= OnMqttMessagesCollectionChanged;
        }

        _mqttMessageSource = viewModel;
        _mqttMessageSource.MqttMessages.CollectionChanged += OnMqttMessagesCollectionChanged;
    }

    private void OnMqttMessagesPreviewMouseWheel(object sender, MouseWheelEventArgs eventArgs)
    {
        _followMqttMessages = false;
    }

    private void OnMqttMessagesPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Enter) return;
        _followMqttMessages = true;
        MqttMessagesScrollViewer.ScrollToEnd();
        eventArgs.Handled = true;
    }

    private void OnMqttMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (!_followMqttMessages) return;
        Dispatcher.BeginInvoke(MqttMessagesScrollViewer.ScrollToEnd, DispatcherPriority.Background);
    }

    private void OnNodeListPreviewMouseWheel(object sender, MouseWheelEventArgs eventArgs)
    {
        if (sender is not ListBox listBox) return;

        var nodeListScrollViewer = FindVisualChild<ScrollViewer>(listBox);
        var canScrollInDirection = nodeListScrollViewer is not null &&
                                   nodeListScrollViewer.ScrollableHeight > 0 &&
                                   (eventArgs.Delta < 0
                                       ? nodeListScrollViewer.VerticalOffset < nodeListScrollViewer.ScrollableHeight
                                       : nodeListScrollViewer.VerticalOffset > 0);
        if (canScrollInDirection) return;

        TaskConfigurationScrollViewer.ScrollToVerticalOffset(
            TaskConfigurationScrollViewer.VerticalOffset - eventArgs.Delta);
        eventArgs.Handled = true;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } descendant) return descendant;
        }

        return null;
    }

    private static IntPtr WindowProcedure(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        if (message != WmGetMinMaxInfo || longParameter == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var monitorHandle = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitorHandle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>(),
        };
        if (!GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            return IntPtr.Zero;
        }

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(longParameter);
        minMaxInfo.MaxPosition.X = monitorInfo.WorkArea.Left - monitorInfo.MonitorArea.Left;
        minMaxInfo.MaxPosition.Y = monitorInfo.WorkArea.Top - monitorInfo.MonitorArea.Top;
        minMaxInfo.MaxSize.X = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
        minMaxInfo.MaxSize.Y = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
        Marshal.StructureToPtr(minMaxInfo, longParameter, false);
        handled = true;
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRectangle MonitorArea;
        public NativeRectangle WorkArea;
        public uint Flags;
    }

}
