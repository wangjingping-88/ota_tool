using System.Configuration;
using System.Data;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace OtaTool.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static readonly object CrashLogLock = new();
    private static int _fatalDialogShown;

    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(eventArgs);
    }

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        base.OnExit(eventArgs);
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        WriteCrashLog("DispatcherUnhandledException", eventArgs.Exception);

        if (Interlocked.Exchange(ref _fatalDialogShown, 1) != 0)
        {
            return;
        }

        MessageBox.Show(
            $"工具发生无法恢复的界面异常：{eventArgs.Exception.Message}\n\n详细信息已写入 crash.log。",
            "OTA 测试平台",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Current.Shutdown(-1);
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs)
    {
        WriteCrashLog(
            "AppDomain.UnhandledException",
            eventArgs.ExceptionObject as Exception ?? new InvalidOperationException(eventArgs.ExceptionObject.ToString()));
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs eventArgs)
    {
        WriteCrashLog("TaskScheduler.UnobservedTaskException", eventArgs.Exception);
        eventArgs.SetObserved();
    }

    private static void WriteCrashLog(string source, Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OtaTool");
            Directory.CreateDirectory(directory);
            var content = new StringBuilder()
                .AppendLine($"[{DateTimeOffset.Now:O}] {source}")
                .AppendLine(exception.ToString())
                .AppendLine()
                .ToString();
            lock (CrashLogLock)
            {
                File.AppendAllText(Path.Combine(directory, "crash.log"), content, Encoding.UTF8);
            }
        }
        catch
        {
            // 崩溃记录失败时不得再次触发未处理异常。
        }
    }
}
