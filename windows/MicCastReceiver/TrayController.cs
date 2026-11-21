using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reactive.Linq;
using System.Windows.Forms;
using MicCastReceiver.Ui;

namespace MicCastReceiver;

/// <summary>
/// 系统托盘常驻：图标随连接状态变色，右键菜单可控制接收、开机自启、退出。
/// 关窗口默认隐藏到这里，不退出进程。
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly Form _form;
    private readonly IBackend _backend;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _showItem;
    private IDisposable? _stateSub;
    private bool _disposed;
    private bool _balloonShown;

    /// <summary>为 true 时表示用户从托盘选了「退出」，主窗口可以真正关闭。</summary>
    public bool ExitRequested { get; private set; }

    public TrayController(Form form, IBackend backend)
    {
        _form = form;
        _backend = backend;

        _showItem = new ToolStripMenuItem("显示主界面", null, (_, _) => ShowWindow())
        {
            Font = new Font(_form.Font, FontStyle.Bold)
        };
        _toggleItem = new ToolStripMenuItem("开始接收", null, async (_, _) => await ToggleAsync());
        _startupItem = new ToolStripMenuItem("开机自启", null, (_, _) => ToggleStartup())
        {
            CheckOnClick = false,
            Checked = Startup.IsEnabled()
        };
        var micItem = new ToolStripMenuItem("设为默认麦克风", null, (_, _) => SetDefaultMic());
        var exitItem = new ToolStripMenuItem("退出", null, (_, _) => ExitApp());

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _showItem,
            new ToolStripSeparator(),
            _toggleItem,
            micItem,
            new ToolStripSeparator(),
            _startupItem,
            new ToolStripSeparator(),
            exitItem
        });

        _icon = new NotifyIcon
        {
            Icon = StatusIcons.Idle,
            Text = "MicCast — 未启动",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _icon.DoubleClick += (_, _) => ShowWindow();

        var uiContext = SynchronizationContext.Current;
        _stateSub = _backend.StateStream
            .ObserveOn(uiContext)
            .Sample(TimeSpan.FromMilliseconds(300))
            .Subscribe(OnState);
    }

    private void OnState(StatusDto s)
    {
        if (_disposed) return;

        StatusIcons.Kind kind;
        string label;
        if (!s.WantReceiving)
        {
            kind = StatusIcons.Kind.Idle;
            label = "未启动";
        }
        else if (s.Connected)
        {
            kind = StatusIcons.Kind.Live;
            label = s.ServerName.Length > 0 ? $"正在接收 — {s.ServerName}" : "正在接收";
        }
        else
        {
            kind = StatusIcons.Kind.Waiting;
            label = "重连中…";
        }

        _icon.Icon = StatusIcons.Get(kind);
        _icon.Text = $"MicCast — {label}";
        _toggleItem.Text = s.WantReceiving ? "停止接收" : "开始接收";
    }

    private void ShowWindow()
    {
        if (_form.Visible && _form.WindowState != FormWindowState.Minimized)
        {
            _form.Activate();
            return;
        }
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.ShowInTaskbar = true;
        _form.Activate();
    }

    /// <summary>窗口被用户关闭时调用：藏到托盘，第一次给个提示。</summary>
    /// <param name="silent">true 时不弹气泡（开机自启直接藏起来用）。</param>
    public void HideToTray(bool silent = false)
    {
        _form.Hide();
        _form.ShowInTaskbar = false;
        if (silent || _balloonShown) return;
        _balloonShown = true;
        _icon.BalloonTipTitle = "MicCast 仍在后台运行";
        _icon.BalloonTipText = "双击托盘图标可重新打开界面；要彻底退出请右键选择「退出」。";
        _icon.BalloonTipIcon = ToolTipIcon.Info;
        _icon.ShowBalloonTip(3000);
    }

    private async Task ToggleAsync()
    {
        try
        {
            if (_backend.WantReceiving)
            {
                await _backend.StopAsync();
                return;
            }
            int device = 0;
            string ip = _backend.Servers.FirstOrDefault()?.Ip ?? string.Empty;
            if (ip.Length == 0) ip = LoadLastIp();
            if (ip.Length == 0)
            {
                ShowWindow();
                MessageBox.Show(_form,
                    "还没发现手机。请先在手机上点「启动麦克风服务」，再回来重试。",
                    "没有可用的手机", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            await _backend.StartAsync(ip, device);
        }
        catch (Exception ex)
        {
            MessageBox.Show(_form, $"操作失败：{ex.Message}", "MicCast",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ToggleStartup()
    {
        if (Startup.IsEnabled())
        {
            var (ok, msg) = Startup.Disable();
            _startupItem.Checked = false;
            if (!ok)
            {
                MessageBox.Show(_form, $"关闭开机自启失败：{msg}", "MicCast",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return;
        }

        var (enabled, detail) = Startup.Enable();
        _startupItem.Checked = enabled;
        if (!enabled)
        {
            MessageBox.Show(_form, $"设置开机自启失败：{detail}", "MicCast",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else if (!detail.Contains("--headless", StringComparison.Ordinal))
        {
            MessageBox.Show(_form, $"已写入启动项，但参数看起来不对：\n{detail}", "MicCast",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SetDefaultMic()
    {
        string? script = FindDefaultMicScript();
        if (script is null)
        {
            MessageBox.Show(_form, "找不到 Set-DefaultMic.ps1，无法自动切换。\n" +
                "请在「声音设置」里手动把默认录音设备设为 CABLE Output。",
                "MicCast", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Name \"CABLE Output\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(6000);
            string err = proc?.StandardError.ReadToEnd() ?? string.Empty;
            if (proc is { ExitCode: not 0 })
            {
                MessageBox.Show(_form, $"切换失败：{err.Trim()}", "MicCast",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(_form, $"切换失败：{ex.Message}", "MicCast",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Set-DefaultMic.ps1 在 windows/ 下，从可执行目录往外找几层。</summary>
    private static string? FindDefaultMicScript()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 6 && dir is not null; i++)
            {
                string candidate = Path.Combine(dir.FullName, "Set-DefaultMic.ps1");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
        }
        catch
        {
            // 找不到就算了，让用户手动切
        }
        return null;
    }

    private static string LoadLastIp()
    {
        try
        {
            string file = Path.Combine(AppContext.BaseDirectory, "last-ip.txt");
            return File.Exists(file) ? File.ReadAllText(file).Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void ExitApp()
    {
        ExitRequested = true;
        _icon.Visible = false;
        _form.Close();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stateSub?.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}

/// <summary>
/// 托盘图标：三个状态各画一个带光环的小圆点，运行时生成，不依赖外部资源。
/// </summary>
internal static class StatusIcons
{
    public enum Kind { Idle, Live, Waiting }

    private static readonly Dictionary<Kind, Icon> Cache = new();
    private static readonly object Gate = new();

    public static Icon Idle => Get(Kind.Idle);

    public static Icon Get(Kind kind)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(kind, out var cached)) return cached;
            Icon made = Build(kind);
            Cache[kind] = made;
            return made;
        }
    }

    private static Icon Build(Kind kind)
    {
        Color fill = kind switch
        {
            Kind.Live => Color.FromArgb(46, 204, 113),      // 绿：已连接
            Kind.Waiting => Color.FromArgb(231, 76, 60),    // 红：重连中
            _ => Color.FromArgb(149, 155, 165)              // 灰：未启动
        };
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // 外圈淡色光环，让图标在深色任务栏上也看得清
            using var halo = new SolidBrush(Color.FromArgb(70, fill));
            g.FillEllipse(halo, 2, 2, size - 4, size - 4);

            using var body = new SolidBrush(fill);
            g.FillEllipse(body, 8, 8, size - 16, size - 16);

            // 已连接时中间画一个小麦克风轮廓
            if (kind == Kind.Live)
            {
                using var pen = new Pen(Color.White, 1.8f);
                g.DrawLine(pen, 16, 11, 16, 17);
                g.DrawArc(pen, 12, 13, 8, 8, 0, 180);
                g.DrawLine(pen, 16, 21, 16, 23);
            }
        }

        IntPtr handle = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(handle);
            // 复制一份，之后才能安全销毁原生句柄
            using var ms = new MemoryStream();
            tmp.Save(ms);
            ms.Position = 0;
            return new Icon(ms);
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr handle);
    }
}
