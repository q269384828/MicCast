using System.Runtime.InteropServices;

namespace MicCastReceiver.Ui;

/// <summary>
/// IP 输入框。**直接用标准 TextBox + 系统边框**。
///
/// 自绘容器 + 常驻子 TextBox 的组合在 WinForms 下反复出问题：
/// 子控件的底能画出来、光标能出现、Text 属性也有值，但输入的文字就是不可见
/// （试过 Control / Panel / UserControl 三种基类，UserPaint / Transparent /
/// Region 裁剪 / SetBounds / Dock.Fill / OnLayout / BringToFront / AutoSize=false
/// 全部无效）。同目录的 DropDown 能用是因为它没有常驻子控件。
///
/// 结论：这个控件功能优先，用系统方角边框；圆角视觉由所在卡片承载。
/// 若将来迁移到 WinUI 3，这里可以直接换成原生 TextBox——它自带圆角/Fluent 焦点环。
/// </summary>
public sealed class TextField : TextBox
{
    private const int EmSetCueBanner = 0x1501;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    private string _placeholder = string.Empty;

    public TextField()
    {
        BorderStyle = BorderStyle.FixedSingle;
        Font = Theme.MakeFont(Theme.FontBody);
        BackColor = Theme.Mix(Theme.SurfaceAlt, Theme.Surface, 0.5);
        ForeColor = Theme.TextPrimary;
        Height = 34;
    }

    /// <summary>占位提示用 Win32 的 Cue Banner，右侧对齐由系统处理。</summary>
    public string Placeholder
    {
        get => _placeholder;
        set
        {
            _placeholder = value ?? string.Empty;
            if (IsHandleCreated) ApplyCue();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyCue();
    }

    private void ApplyCue()
    {
        try { SendMessage(Handle, EmSetCueBanner, (IntPtr)1, _placeholder); } catch { }
    }

    /// <summary>保留给旧调用点；标准 TextBox 不需要同步内部布局。</summary>
    public void SyncInnerLayout() { }

    public void ApplyTheme()
    {
        BackColor = Theme.Mix(Theme.SurfaceAlt, Theme.Surface, 0.5);
        ForeColor = Theme.TextPrimary;
        Invalidate();
    }
}