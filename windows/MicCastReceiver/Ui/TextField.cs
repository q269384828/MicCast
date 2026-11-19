using System.Drawing.Drawing2D;

namespace MicCastReceiver.Ui;

/// <summary>圆角输入框：外框自绘，内部放一个无边框 TextBox。</summary>
public sealed class TextField : Control
{
    private readonly TextBox _inner = new();
    private bool _hover;

    public TextField()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Font = new Font("Microsoft YaHei UI", 9.5f);
        Height = 38;

        _inner.BorderStyle = BorderStyle.None;
        _inner.Font = Font;
        Controls.Add(_inner);
    }

    public string Placeholder { get; set; } = string.Empty;

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text
    {
        get => _inner.Text;
        set => _inner.Text = value ?? string.Empty;
    }

    public void ApplyTheme()
    {
        BackColor = Color.Transparent;
        _inner.BackColor = Theme.Surface;
        _inner.ForeColor = Theme.TextPrimary;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        int pad = (int)Math.Round(12 * DeviceDpi / 96.0);
        int height = _inner.PreferredHeight;
        _inner.SetBounds(pad, (Height - height) / 2, Math.Max(10, Width - pad * 2), height);
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
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _inner.Focus();
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        Theme.FillRound(g, rect, 10, Theme.Mix(Theme.SurfaceAlt, Theme.Surface, 0.5));
        Theme.StrokeRound(g, rect, 10, _inner.Focused ? Theme.Accent : (_hover ? Theme.Mix(Theme.Border, Theme.Accent, 0.35) : Theme.Border));

        if (_inner.Text.Length == 0 && Placeholder.Length > 0)
        {
            int pad = (int)Math.Round(12 * DeviceDpi / 96.0);
            TextRenderer.DrawText(g, Placeholder, Font,
                new Rectangle(pad, 0, Width - pad * 2, Height), Theme.TextFaint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }
    }
}
