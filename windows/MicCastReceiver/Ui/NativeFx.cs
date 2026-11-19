using System.Runtime.InteropServices;

namespace MicCastReceiver.Ui;

/// <summary>Windows 11 窗口效果：圆角 + 深色标题栏。</summary>
public static class NativeFx
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Form form, bool dark)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        try
        {
            int corner = DwmwcpRound;
            DwmSetWindowAttribute(form.Handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
            int immersive = dark ? 1 : 0;
            DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkMode, ref immersive, sizeof(int));
        }
        catch { }
    }
}
