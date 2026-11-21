using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace MicCastReceiver.WinUI;

/// <summary>
/// 运行时绘制托盘状态图标（灰 / 绿 / 黄圆点 + 麦形）。
/// 比维护三份 .ico 简单，改色只改一个数值。
/// </summary>
internal static class StatusIcons
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static IntPtr Create(int r, int g, int b)
    {
        using var bmp = new Bitmap(32, 32);
        using (var gfx = Graphics.FromImage(bmp))
        {
            gfx.SmoothingMode = SmoothingMode.AntiAlias;
            gfx.Clear(Color.Transparent);

            using var dot = new SolidBrush(Color.FromArgb(r, g, b));
            gfx.FillEllipse(dot, 8, 8, 16, 16);

            // 中间留一个浅色小圆，视觉上像"指示灯"
            using var inner = new SolidBrush(Color.FromArgb(230, Color.White));
            gfx.FillEllipse(inner, 12, 12, 8, 8);
        }
        return bmp.GetHicon();
    }

    public static void Destroy(IntPtr icon)
    {
        if (icon != IntPtr.Zero) DestroyIcon(icon);
    }
}