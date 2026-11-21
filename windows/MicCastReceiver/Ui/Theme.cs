using System.Drawing.Drawing2D;

namespace MicCastReceiver.Ui;

public enum AppTheme
{
    Dark,
    Light
}

/// <summary>
/// 集中管理设计 token，数值与 docs/design-system.md 一一对应。
/// 改视觉只改这里，不要在控件里写魔法数字。
/// </summary>
public static class Theme
{
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    // ===== 颜色（语义）=====
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
    public static Color LogText = Color.Empty;
    public static Color PillIdleBg = Color.Empty;
    public static Color PillIdleFg = Color.Empty;

    // ===== Hero 渐变 =====
    public static Color HeroFrom = Color.Empty;
    public static Color HeroTo = Color.Empty;
    public static Color OnHero = Color.Empty;
    public static Color OnHeroSoft = Color.Empty;
    public static Color HeroTrack = Color.Empty;

    // ===== 圆角 =====
    public const int RadiusHero = 22;
    public const int RadiusCard = 16;
    public const int RadiusControl = 10;
    public const int RadiusSmall = 6;

    // ===== 间距（4 的倍数）=====
    public const int Space1 = 4;
    public const int Space2 = 8;
    public const int Space3 = 12;
    public const int Space4 = 16;
    public const int Space5 = 20;
    public const int Space6 = 24;
    public const int Space8 = 32;
    public const int PagePadding = 20;
    public const int CardPadding = 16;
    public const int CardGap = 12;

    // ===== 字号（pt，约等于 px * 0.75）=====
    public const float FontHero = 17f;
    public const float FontMetric = 15f;
    public const float FontTitle = 10f;
    public const float FontBody = 9.5f;
    public const float FontLabel = 9f;
    public const float FontCaption = 8.5f;

    // ===== 动效 =====
    public const int DurFast = 120;
    public const int DurBase = 200;

    public const string FontFamily = "Microsoft YaHei UI";
    /// <summary>数字专用：等宽，避免数值跳动时整行左右呼吸。</summary>
    public const string FontNumeric = "Segoe UI";

    static Theme() => Apply(AppTheme.Dark);

    public static Font MakeFont(float size, FontStyle style = FontStyle.Regular) =>
        new(FontFamily, size, style);

    public static Font MakeNumeric(float size, FontStyle style = FontStyle.Bold) =>
        new(FontNumeric, size, style);

    /// <summary>读系统设置决定深浅色（HKCU Themes Personalize AppsUseLightTheme）。</summary>
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
            Surface = Color.FromArgb(22, 32, 58);
            SurfaceAlt = Color.FromArgb(27, 39, 66);
            Border = Color.FromArgb(38, 49, 77);
            TextPrimary = Color.FromArgb(230, 237, 247);
            TextSecondary = Color.FromArgb(138, 155, 184);
            TextFaint = Color.FromArgb(100, 116, 139);
            Accent = Color.FromArgb(79, 140, 255);
            AccentHover = Color.FromArgb(110, 157, 255);
            AccentPressed = Color.FromArgb(61, 122, 238);
            OnAccent = Color.White;
            Ok = Color.FromArgb(61, 220, 151);
            Warn = Color.FromArgb(255, 180, 84);
            Danger = Color.FromArgb(224, 92, 92);
            DangerHover = Color.FromArgb(232, 117, 117);
            Track = Color.FromArgb(34, 48, 82);
            LogBg = Color.FromArgb(16, 26, 46);
            LogText = Color.FromArgb(159, 178, 204);
            PillIdleBg = Color.FromArgb(42, 53, 80);
            PillIdleFg = Color.FromArgb(138, 155, 184);

            HeroFrom = Color.FromArgb(62, 111, 230);
            HeroTo = Color.FromArgb(31, 158, 140);
            OnHero = Color.White;
            OnHeroSoft = Color.FromArgb(217, 255, 255, 255);
            HeroTrack = Color.FromArgb(61, 255, 255, 255);
        }
        else
        {
            WindowBg = Color.FromArgb(243, 245, 250);
            Surface = Color.White;
            SurfaceAlt = Color.FromArgb(241, 245, 251);
            Border = Color.FromArgb(228, 233, 242);
            TextPrimary = Color.FromArgb(22, 35, 58);
            TextSecondary = Color.FromArgb(90, 107, 133);
            TextFaint = Color.FromArgb(138, 155, 171);
            Accent = Color.FromArgb(47, 107, 255);
            AccentHover = Color.FromArgb(31, 91, 240);
            AccentPressed = Color.FromArgb(26, 79, 208);
            OnAccent = Color.White;
            Ok = Color.FromArgb(18, 161, 80);
            Warn = Color.FromArgb(184, 110, 0);
            Danger = Color.FromArgb(196, 43, 43);
            DangerHover = Color.FromArgb(214, 60, 60);
            Track = Color.FromArgb(227, 233, 244);
            LogBg = Color.FromArgb(247, 249, 253);
            LogText = Color.FromArgb(90, 107, 133);
            PillIdleBg = Color.FromArgb(237, 241, 248);
            PillIdleFg = Color.FromArgb(90, 107, 133);

            HeroFrom = Color.FromArgb(47, 107, 255);
            HeroTo = Color.FromArgb(18, 165, 148);
            OnHero = Color.White;
            OnHeroSoft = Color.FromArgb(220, 255, 255, 255);
            HeroTrack = Color.FromArgb(77, 255, 255, 255);
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

    /// <summary>Hero 背景：135 度线性渐变，两端一致。</summary>
    public static void FillHero(Graphics g, RectangleF rect, float radius)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundRect(rect, radius);
        using var brush = new LinearGradientBrush(
            new PointF(rect.Left, rect.Top),
            new PointF(rect.Right, rect.Bottom),
            HeroFrom, HeroTo);
        g.FillPath(brush, path);
    }

    /// <summary>把语义色按透明度铺成底纹，用于状态胶囊。</summary>
    public static Color Tint(Color color, int alpha) => Color.FromArgb(alpha, color);
}