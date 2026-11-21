using System.Drawing.Drawing2D;

namespace MicCastReceiver.Ui;

/// <summary>圆角音频电平条：带平滑跟随与渐变填充。</summary>
public sealed class LevelMeter : Control
{
    private float _target;
    private float _display;
    private float _peak;
    private int _peakHold;

    public int BarHeight { get; set; } = 8;

    /// <summary>
    /// 放在 Hero 渐变底上时置 true：绿色渐变在蓝底上分辨不出来，
    /// 改成半透明白轨道 + 纯白填充（和手机端一致）。
    /// </summary>
    public bool InHero { get; set; }

    public LevelMeter()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Height = 10;
    }

    /// <summary>传入 0..1 的目标电平（建议已做对数缩放）。</summary>
    public void SetLevel(float value)
    {
        _target = Math.Clamp(value, 0f, 1f);
        _display += (_target - _display) * (_target > _display ? 0.55f : 0.18f);
        if (_display >= _peak)
        {
            _peak = _display;
            _peakHold = 20;
        }
        else if (_peakHold > 0)
        {
            _peakHold--;
        }
        else
        {
            _peak -= 0.02f;
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float h = Math.Min(BarHeight, Height);
        float y = (Height - h) / 2f;
        var track = new RectangleF(0, y, Width, h);
        Theme.FillRound(g, track, h / 2f, InHero ? Theme.HeroTrack : Theme.Track);

        float width = Math.Max(0, _display) * Width;
        if (width > h)
        {
            var fill = new RectangleF(0, y, width, h);
            using var path = Theme.RoundRect(fill, h / 2f);
            if (InHero)
            {
                using var brush = new SolidBrush(Theme.OnHero);
                g.FillPath(brush, path);
            }
            else
            {
                using var brush = new LinearGradientBrush(
                    new RectangleF(0, y, Math.Max(Width * 0.55f, width), h),
                    Theme.Ok, Theme.Accent, LinearGradientMode.Horizontal);
                g.FillPath(brush, path);
            }
        }

        if (_peak * Width > h)
        {
            float px = Math.Min(Width - 1.5f, _peak * Width);
            Color peakColor = InHero ? Theme.OnHero : Theme.Mix(Theme.Ok, Color.White, 0.35);
            using var pen = new Pen(peakColor, Math.Max(1.5f, h / 4f));
            g.DrawLine(pen, px, y, px, y + h);
        }
    }
}