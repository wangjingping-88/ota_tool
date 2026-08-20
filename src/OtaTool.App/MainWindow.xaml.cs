using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using OtaTool.App.ViewModels;

namespace OtaTool.App;

public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private bool _followGlobalLog = true;
    private bool _followMqttMessages = true;
    private MainWindowViewModel? _mqttMessageSource;
    private HwndSource? _windowSource;
    private UpdateWindow? _updateWindow;
    private bool _startupCompleted;

    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainWindowViewModel();
        viewModel.ApplicationUpdate.UpdateAvailable += OnUpdateAvailable;
        DataContext = viewModel;
    }

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (_startupCompleted || DataContext is not MainWindowViewModel viewModel) return;
        _startupCompleted = true;
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
            MessageBox.Show(
                $"应用初始化未完全完成：{exception.Message}",
                "OTA 测试平台",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
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
