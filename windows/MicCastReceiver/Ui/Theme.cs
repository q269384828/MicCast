using System.Drawing.Drawing2D;

namespace MicCastReceiver.Ui;

public enum AppTheme
{
    Dark,
    Light
}

/// <summary>集中管理配色与绘制辅助，深浅色只改这里的字段。</summary>
public static class Theme
{
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static Color WindowBg = Color.Empty;
    public static Color Surface = Color.Empty;
    public static Color SurfaceAlt = Color.Empty;
    public static Color Border = Color.Empty;
    public static Color TextPrimary = Color.Empty;
    public static Color TextSecondary = Color.Empty;
    public static Color TextFaint = Color.Empty;
    public static Color Accent = Color.Empty;
    public static Color AccentHover = Color.Empty;
    public static Color AccentPressed = Color.Empty;
    public static Color OnAccent = Color.Empty;
    public static Color Ok = Color.Empty;
    public static Color Warn = Color.Empty;
    public static Color Danger = Color.Empty;
    public static Color DangerHover = Color.Empty;
    public static Color Track = Color.Empty;
    public static Color LogBg = Color.Empty;

    static Theme() => Apply(AppTheme.Dark);

    /// <summary>读系统设置决定深浅色（HKCU\...\Themes\Personalize\AppsUseLightTheme）。</summary>
    public static AppTheme DetectSystemTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int light)
            {
                return light == 0 ? AppTheme.Dark : AppTheme.Light;
            }
        }
        catch { }
        return AppTheme.Dark;
    }

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        if (theme == AppTheme.Dark)
        {
            WindowBg = Color.FromArgb(11, 18, 32);
            Surface = Color.FromArgb(21, 31, 53);
            SurfaceAlt = Color.FromArgb(27, 39, 64);
            Border = Color.FromArgb(38, 49, 73);
            TextPrimary = Color.FromArgb(232, 238, 249);
            TextSecondary = Color.FromArgb(147, 164, 192);
            TextFaint = Color.FromArgb(100, 116, 139);
            Accent = Color.FromArgb(91, 140, 255);
            AccentHover = Color.FromArgb(110, 155, 255);
            AccentPressed = Color.FromArgb(74, 120, 230);
            OnAccent = Color.White;
            Ok = Color.FromArgb(61, 220, 151);
            Warn = Color.FromArgb(255, 180, 84);
            Danger = Color.FromArgb(229, 72, 77);
            DangerHover = Color.FromArgb(240, 92, 96);
            Track = Color.FromArgb(34, 48, 82);
            LogBg = Color.FromArgb(14, 23, 44);
        }
        else
        {
            WindowBg = Color.FromArgb(243, 245, 250);
            Surface = Color.White;
            SurfaceAlt = Color.FromArgb(247, 249, 253);
            Border = Color.FromArgb(226, 232, 242);
            TextPrimary = Color.FromArgb(16, 26, 46);
            TextSecondary = Color.FromArgb(90, 107, 133);
            TextFaint = Color.FromArgb(138, 151, 171);
            Accent = Color.FromArgb(47, 107, 255);
            AccentHover = Color.FromArgb(31, 91, 240);
            AccentPressed = Color.FromArgb(26, 79, 208);
            OnAccent = Color.White;
            Ok = Color.FromArgb(15, 157, 88);
            Warn = Color.FromArgb(178, 106, 0);
            Danger = Color.FromArgb(217, 58, 62);
            DangerHover = Color.FromArgb(232, 74, 78);
            Track = Color.FromArgb(227, 233, 244);
            LogBg = Color.FromArgb(247, 249, 253);
        }
    }

    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t),
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    public static GraphicsPath RoundRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float r = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2f);
        if (r <= 0.5f)
        {
            path.AddRectangle(rect);
            return path;
        }
        float d = r * 2f;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRound(Graphics g, RectangleF rect, float radius, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundRect(rect, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public static void StrokeRound(Graphics g, RectangleF rect, float radius, Color color, float width = 1f)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var inner = RectangleF.Inflate(rect, -width / 2f, -width / 2f);
        using var path = RoundRect(inner, Math.Max(0, radius - width / 2f));
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }
}
