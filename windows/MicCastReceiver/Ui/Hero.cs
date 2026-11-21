using System.Drawing.Drawing2D;

namespace MicCastReceiver.Ui;

/// <summary>
/// 顶部品牌区：135 度渐变底 + 大圆角，承载「品牌 + 状态胶囊 + 电平 + 核心指标」。
/// 和手机端 Hero 是同一个东西，保证两端结构一致（见 docs/design-system.md 第 8 节）。
/// </summary>
public sealed class Hero : Panel
{
    public Image? BrandMark { get; set; }

    public int IconSize { get; set; } = 34;

    public Hero()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Theme.WindowBg;
    }

    public void ApplyTheme()
    {
        BackColor = Theme.WindowBg;
        foreach (Control child in Controls)
        {
            if (child is StatusPill pill)
            {
                pill.InHero = true;
                pill.Invalidate();
            }
            else if (child is LevelMeter meter)
            {
                meter.InHero = true;
                meter.BackColor = Color.Transparent;
                meter.Invalidate();
            }
            else
            {
                // Hero 是渐变底，子控件必须真透明才能透出渐变
                if (child is Label) child.BackColor = Color.Transparent;
                child.Invalidate();
            }
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        using (var bg = new SolidBrush(Theme.WindowBg))
        {
            g.FillRectangle(bg, ClientRectangle);
        }

        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        Theme.FillHero(g, rect, Theme.RadiusHero);

        if (BrandMark is not null)
        {
            int pad = Scale(Theme.CardPadding);
            g.DrawImage(BrandMark, new Rectangle(pad, pad, Scale(IconSize), Scale(IconSize)));
        }
    }

    private int Scale(int logical) => (int)Math.Round(logical * DeviceDpi / 96.0);
}