using System.Drawing.Drawing2D;

namespace MicCastReceiver.Ui;

/// <summary>圆角卡片背景。子控件请放在圆角以内，避免方角溢出。</summary>
public sealed class Card : Panel
{
    public int CornerRadius { get; set; } = 14;
    public bool ShowBorder { get; set; } = true;
    public Color? FillOverride { get; set; }

    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.WindowBg;
    }

    public void ApplyTheme()
    {
        BackColor = Theme.WindowBg;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(Theme.WindowBg))
        {
            g.FillRectangle(bg, ClientRectangle);
        }

        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        Theme.FillRound(g, rect, CornerRadius, FillOverride ?? Theme.Surface);
        if (ShowBorder)
        {
            Theme.StrokeRound(g, rect, CornerRadius, Theme.Border);
        }
    }
}
