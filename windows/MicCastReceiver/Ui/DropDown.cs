using System.Drawing.Drawing2D;

namespace MicCastReceiver.Ui;

/// <summary>自绘下拉选择器：圆角胶囊 + 自绘弹出列表（不用系统 ComboBox，避免在暗色界面里出现白底）。</summary>
public sealed class DropDown : Control
{
    private bool _hover;
    private bool _open;
    private string[] _items = Array.Empty<string>();
    private int _selectedIndex = -1;

    public string Placeholder { get; set; } = "未选择";
    public int CornerRadius { get; set; } = 10;
    public event EventHandler? SelectionChanged;

    public DropDown()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);
        Cursor = Cursors.Hand;
        Height = 38;
    }

    public string[] Items
    {
        get => _items;
        set
        {
            _items = value ?? Array.Empty<string>();
            if (_selectedIndex >= _items.Length) _selectedIndex = -1;
            Invalidate();
        }
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex == value) return;
            _selectedIndex = value;
            Invalidate();
        }
    }

    public string SelectedText =>
        _selectedIndex >= 0 && _selectedIndex < _items.Length ? _items[_selectedIndex] : string.Empty;

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
        if (e.Button == MouseButtons.Left && _items.Length > 0)
        {
            ShowPopup();
        }
        base.OnMouseDown(e);
    }

    private void ShowPopup()
    {
        if (_open) return;
        _open = true;
        Invalidate();

        var popup = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            BackColor = Theme.Surface,
            TopMost = true
        };
        popup.Paint += (_, pe) =>
        {
            using var pen = new Pen(Theme.Border);
            pe.Graphics.DrawRectangle(pen, 0, 0, popup.Width - 1, popup.Height - 1);
        };

        int rowHeight = (int)Math.Round(32 * DeviceDpi / 96.0);
        var list = new ListBox
        {
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = rowHeight,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextPrimary,
            Font = Font,
            IntegralHeight = false,
            Left = 1,
            Top = 1
        };
        list.Items.AddRange(_items);
        if (_selectedIndex >= 0) list.SelectedIndex = _selectedIndex;

        list.DrawItem += (_, de) =>
        {
            if (de.Index < 0) return;
            bool selected = (de.State & DrawItemState.Selected) != 0;
            var g = de.Graphics;
            var bounds = new RectangleF(3, de.Bounds.Top + 2, list.ClientSize.Width - 6, de.Bounds.Height - 4);
            if (selected)
            {
                Theme.FillRound(g, bounds, 7, Theme.Mix(Theme.Accent, Theme.Surface, 0.65));
            }
            TextRenderer.DrawText(g, _items[de.Index], Font,
                new Rectangle(de.Bounds.Left + 12, de.Bounds.Top, de.Bounds.Width - 20, de.Bounds.Height),
                Theme.TextPrimary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        };

        popup.Controls.Add(list);
        popup.ClientSize = new Size(Width, Math.Min(_items.Length * rowHeight + 2, rowHeight * 10 + 2));
        // ListBox 必须显式撑满弹窗，否则只会是默认的 120x96，右侧留白且点不到
        list.SetBounds(1, 1, popup.ClientSize.Width - 2, popup.ClientSize.Height - 2);

        var screen = Screen.FromControl(this).WorkingArea;
        var below = PointToScreen(new Point(0, Height + 3));
        var above = PointToScreen(new Point(0, -popup.Height - 3));
        int x = Math.Max(screen.Left, Math.Min(below.X, screen.Right - popup.Width));
        int y = below.Y + popup.Height <= screen.Bottom ? below.Y : Math.Max(screen.Top, above.Y);
        popup.Location = new Point(x, y);

        popup.KeyPreview = true;
        popup.KeyDown += (_, ke) =>
        {
            if (ke.KeyCode == Keys.Escape) popup.Close();
        };
        list.KeyDown += (_, ke) =>
        {
            if (ke.KeyCode == Keys.Enter) popup.Close();
        };
        list.Click += (_, _) =>
        {
            int index = list.SelectedIndex;
            popup.Close();
            if (index >= 0 && index != _selectedIndex)
            {
                _selectedIndex = index;
                Invalidate();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        };

        popup.FormClosed += (_, _) =>
        {
            _open = false;
            Invalidate();
        };
        popup.Show(this);
        list.Focus();

        // 本次点击的 mouse-up 会立刻把焦点还给主窗体，导致 Deactivate 马上触发把弹窗关掉。
        // 等这一轮消息处理完再订阅，才不会被自己的点击误关。
        popup.BeginInvoke(new Action(() =>
        {
            if (!popup.IsDisposed)
            {
                popup.Deactivate += (_, _) => popup.Close();
            }
        }));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        Theme.FillRound(g, rect, CornerRadius, _hover || _open ? Theme.SurfaceAlt : Theme.Mix(Theme.SurfaceAlt, Theme.Surface, 0.5));
        Theme.StrokeRound(g, rect, CornerRadius, _open ? Theme.Accent : Theme.Border);

        int chevronSize = (int)Math.Round(9 * DeviceDpi / 96.0);
        var textRect = new RectangleF(14, 0, Width - 14 - chevronSize - 18, Height);
        bool empty = SelectedText.Length == 0;
        TextRenderer.DrawText(g, empty ? Placeholder : SelectedText, Font,
            Rectangle.Round(textRect), empty ? Theme.TextFaint : Theme.TextPrimary,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        float cx = Width - 14 - chevronSize / 2f;
        float cy = Height / 2f;
        using var pen = new Pen(Theme.TextSecondary, Math.Max(1.4f, DeviceDpi / 96f * 1.4f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        g.DrawLines(pen, new[]
        {
            new PointF(cx - chevronSize / 2f, cy - chevronSize / 4f),
            new PointF(cx, cy + chevronSize / 4f),
            new PointF(cx + chevronSize / 2f, cy - chevronSize / 4f)
        });
    }
}
