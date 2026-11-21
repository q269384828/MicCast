using System.Drawing;
using System.Runtime.InteropServices;

namespace MicCastReceiver.Ui;

/// <summary>Windows 11 窗口效果：圆角 + 深色标题栏 + 标题栏配色跟随应用主题。</summary>
public static class NativeFx
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
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

            // 不设的话标题栏用系统强调色（常见是高饱和蓝），和内容背景完全割裂。
            int caption = ToColorRef(Theme.Surface);
            DwmSetWindowAttribute(form.Handle, DwmwaCaptionColor, ref caption, sizeof(int));
            int text = ToColorRef(Theme.TextPrimary);
            DwmSetWindowAttribute(form.Handle, DwmwaTextColor, ref text, sizeof(int));
            int border = ToColorRef(Theme.Border);
            DwmSetWindowAttribute(form.Handle, DwmwaBorderColor, ref border, sizeof(int));
        }
        catch { }
    }

    /// <summary>DWM 要的是 COLORREF(0x00BBGGRR)，和 .NET 的 ARGB 字节顺序相反。</summary>
    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);
}
