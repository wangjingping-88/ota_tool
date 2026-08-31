using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OtaTool.App;

internal static class NativeWindowShadow
{
    private const int DwmwaNcRenderingPolicy = 2;
    private const int DwmncrpEnabled = 2;

    public static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6)) return;

        try
        {
            if (DwmIsCompositionEnabled(out var compositionEnabled) != 0 || !compositionEnabled)
            {
                return;
            }

            var windowHandle = new WindowInteropHelper(window).Handle;
            if (windowHandle == IntPtr.Zero) return;

            var policy = DwmncrpEnabled;
            _ = DwmSetWindowAttribute(
                windowHandle,
                DwmwaNcRenderingPolicy,
                ref policy,
                Marshal.SizeOf<int>());
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
