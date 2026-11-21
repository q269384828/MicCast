using System.Drawing.Drawing2D;

namespace MicCastReceiver.Ui;

public enum ButtonKind
{
    Primary,
    Ghost,
    Danger,
    Subtle
}

/// <summary>自绘圆角按钮，带 hover / press / disabled 三态。</summary>
public sealed class ModernButton : Control
{
    private bool _hover;
    private bool _pressed;

    public ButtonKind Kind { get; set; } = ButtonKind.Primary;
    public int CornerRadius { get; set; } = Theme.RadiusControl;
    public int LogicalPadding { get; set; } = 18;

    public ModernButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);
        Cursor = Cursors.Hand;
        Height = 38;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        Color fill;
        Color text;
        Color border = Color.Empty;

        switch (Kind)
        {
            case ButtonKind.Primary:
                fill = !Enabled ? Theme.Mix(Theme.SurfaceAlt, Theme.Surface, 0.5)
                    : _pressed ? Theme.AccentPressed
                    : _hover ? Theme.AccentHover
                    : Theme.Accent;
                text = Enabled ? Theme.OnAccent : Theme.TextFaint;
                break;
            case ButtonKind.Danger:
                fill = _pressed ? Theme.Mix(Theme.Danger, Color.Black, 0.15)
                    : _hover ? Theme.DangerHover
                    : Theme.Danger;
                text = Color.White;
                break;
            case ButtonKind.Ghost:
                fill = _hover ? Theme.SurfaceAlt : Color.Transparent;
                text = Enabled ? Theme.TextPrimary : Theme.TextFaint;
                // 纯 Border 在浅色背景上淡到像禁用，往文字色借一点才有存在感
                border = Theme.Mix(Theme.Border, Theme.TextFaint, 0.5);
                break;
            default:
                fill = _pressed || _hover ? Theme.Mix(Theme.SurfaceAlt, Theme.Border, 0.35) : Theme.SurfaceAlt;
                text = Enabled ? Theme.TextPrimary : Theme.TextFaint;
                border = Theme.Border;
                break;
        }

        if (fill.A > 0)
        {
            Theme.FillRound(g, rect, CornerRadius, fill);
        }
        if (border.A > 0)
        {
            Theme.StrokeRound(g, rect, CornerRadius, border);
        }

        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
