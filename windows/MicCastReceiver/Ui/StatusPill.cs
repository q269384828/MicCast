using System.Drawing.Drawing2D;

namespace MicCastReceiver.Ui;

/// <summary>圆角状态胶囊：小圆点 + 文字，宽度随文字自适应。</summary>
public sealed class StatusPill : Control
{
    private string _caption = "未启动";
    private Color _accent = Color.Gray;

    public StatusPill()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
        Height = 26;
    }

    public void Set(string caption, Color accent)
    {
        _caption = caption;
        _accent = accent;
        int textWidth = TextRenderer.MeasureText(_caption, Font).Width;
        Width = textWidth + (int)Math.Round(30 * DeviceDpi / 96.0);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new RectangleF(0, 0, Width - 1, Height - 1);
        Theme.FillRound(g, rect, Height / 2f, Theme.Mix(_accent, Theme.Current == AppTheme.Dark ? Color.Black : Color.White, 0.72));

        float dot = Math.Max(5f, Height / 4.6f);
        var dotRect = new RectangleF(10, (Height - dot) / 2f, dot, dot);
        using (var brush = new SolidBrush(_accent))
        {
            g.FillEllipse(brush, dotRect);
        }

        TextRenderer.DrawText(g, _caption, Font,
            new Rectangle((int)(10 + dot + 6), 0, Width - (int)(10 + dot + 6) - 8, Height),
            Theme.Mix(_accent, Theme.TextPrimary, 0.35),
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}
