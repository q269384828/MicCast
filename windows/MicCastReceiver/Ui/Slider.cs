using System.Drawing.Drawing2D;

namespace MicCastReceiver.Ui;

/// <summary>
/// 自绘双向滑块：以中点为零位向两侧填充，拇指尺寸带缓动，
/// 支持拖动 / 点击跳转 / 方向键 / 滚轮。替代系统 TrackBar，
/// 让增益控件和其余自绘圆角控件保持同一套视觉语言。
/// </summary>
public sealed class Slider : Control
{
    private int _min = -12;
    private int _max = 12;
    private int _value;
    private bool _hover;
    private bool _dragging;
    private float _thumbScale = 1f;
    private float _thumbTarget = 1f;

    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 16 };

    public event EventHandler? ValueChanged;

    public Slider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        TabStop = true;
        Height = 44;
        _anim.Tick += OnAnimTick;
    }

    public int Minimum
    {
        get => _min;
        set { _min = value; if (_value < _min) Value = _min; else Invalidate(); }
    }

    public int Maximum
    {
        get => _max;
        set { _max = value; if (_value > _max) Value = _max; else Invalidate(); }
    }

    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, _min, _max);
            if (v == _value) return;
            _value = v;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>零位在区间中点时向两侧填充（增益这类双向量）；否则从左端填充。</summary>
    public bool Bidirectional { get; set; } = true;

    private float ThumbRadius => Math.Min(Height / 2f, 8f * DeviceDpi / 96f) * _thumbScale;
    private float TrackThickness => 4f * DeviceDpi / 96f;
    private float Pad => 9f * DeviceDpi / 96f;

    private RectangleF TrackRect
    {
        get
        {
            float h = TrackThickness;
            return new RectangleF(Pad, (Height - h) / 2f, Math.Max(1f, Width - Pad * 2f), h);
        }
    }

    private float ValueToX(float value)
    {
        var t = TrackRect;
        float ratio = _max == _min ? 0f : (value - _min) / (_max - _min);
        return t.X + t.Width * Math.Clamp(ratio, 0f, 1f);
    }

    private int XToValue(float x)
    {
        var t = TrackRect;
        if (t.Width <= 0) return _min;
        double ratio = Math.Clamp((x - t.X) / t.Width, 0.0, 1.0);
        return _min + (int)Math.Round(ratio * (_max - _min));
    }

    private void SetThumbTarget(float target)
    {
        if (Math.Abs(_thumbTarget - target) < 0.001f) return;
        _thumbTarget = target;
        if (!_anim.Enabled) _anim.Start();
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        float delta = _thumbTarget - _thumbScale;
        if (Math.Abs(delta) < 0.005f)
        {
            _thumbScale = _thumbTarget;
            _anim.Stop();
        }
        else
        {
            _thumbScale += delta * 0.4f;
        }
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        SetThumbTarget(_dragging ? 0.88f : 1.2f);
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _dragging = false;
        SetThumbTarget(1f);
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            Focus();
            _dragging = true;
            SetThumbTarget(0.88f);
            Value = XToValue(e.X);
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) Value = XToValue(e.X);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            SetThumbTarget(_hover ? 1.2f : 1f);
        }
        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Value = _value + (e.Delta > 0 ? 1 : -1);
        base.OnMouseWheel(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int step = e.Shift ? 3 : 1;
        switch (e.KeyCode)
        {
            case Keys.Left:
            case Keys.Down:
                Value = _value - step;
                e.Handled = true;
                break;
            case Keys.Right:
            case Keys.Up:
                Value = _value + step;
                e.Handled = true;
                break;
            case Keys.Home:
                Value = _min;
                e.Handled = true;
                break;
            case Keys.End:
                Value = _max;
                e.Handled = true;
                break;
        }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var track = TrackRect;
        float radius = track.Height / 2f;
        Theme.FillRound(g, track, radius, Theme.Track);

        float x = ValueToX(_value);
        float origin = Bidirectional ? ValueToX((_min + _max) / 2f) : track.X;

        // 已选段：从零位到当前值
        float left = Math.Min(origin, x);
        float right = Math.Max(origin, x);
        if (right - left > 0.5f)
        {
            var selected = new RectangleF(left, track.Y, right - left, track.Height);
            Theme.FillRound(g, selected, radius, Theme.Accent);
        }

        // 零位刻度：双向滑块需要一个"回到这里"的视觉锚点
        if (Bidirectional)
        {
            float zero = ValueToX((_min + _max) / 2f);
            using var tick = new Pen(Theme.Mix(Theme.Track, Theme.TextFaint, 0.55f), 1.5f);
            g.DrawLine(tick, zero, track.Y - 5f, zero, track.Bottom + 5f);
        }

        // 拇指：浅色圆盘 + 强调色描边 + 落影
        float r = ThumbRadius;
        var thumbRect = new RectangleF(x - r, Height / 2f - r, r * 2f, r * 2f);

        using (var shadow = new SolidBrush(Color.FromArgb(28, 0, 0, 0)))
        {
            g.FillEllipse(shadow, thumbRect.X, thumbRect.Y + 1.5f, thumbRect.Width, thumbRect.Height);
        }
        using (var fill = new SolidBrush(Theme.Surface))
        {
            g.FillEllipse(fill, thumbRect);
        }
        float ringWidth = Focused ? 2.4f : 2f;
        using (var ring = new Pen(Theme.Accent, ringWidth))
        {
            var inset = RectangleF.Inflate(thumbRect, -ringWidth / 2f, -ringWidth / 2f);
            g.DrawEllipse(ring, inset);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _anim.Tick -= OnAnimTick;
            _anim.Dispose();
        }
        base.Dispose(disposing);
    }
}
