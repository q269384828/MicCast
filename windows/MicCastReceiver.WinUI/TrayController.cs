using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace MicCastReceiver.WinUI;

/// <summary>
/// 托盘常驻。WinUI 3 没有原生托盘，这里照 PowerToys 的 TrayIconService 做：
///
/// 1. 建一个**专用且永不激活**的 WinUI Window 只为拿 HWND（不占用主窗口，
///    主窗口关掉重开也不影响托盘图标）
/// 2. 用 SetWindowLongPtr 子类化它的 WndProc（不是自建消息窗口 ——
///    自建窗口要管窗口类注册、消息泵归属，容易出坑）
/// 3. 委托必须存在字段里（PowerToys 原文注释：否则 `WndProc will explode`）
/// 4. 监听 RegisterWindowMessage("TaskbarCreated") 处理 explorer.exe 重启
/// 5. NIM_ADD 在开机自启时可能因 explorer 还没就绪而失败，用
///    WM_WINDOWPOSCHANGING 作重试钩子（那时收不到 TaskbarCreated）
/// </summary>
public sealed class TrayController : IDisposable
{
    private const uint MyNotifyId = 1001;
    private const uint WmTrayIcon = 0x0400 + 1;   // WM_USER + 1
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_LBUTTONUP = 0x0202;

    private const int GWLP_WNDPROC = -4;

    private const int NIM_ADD = 0;
    private const int NIM_MODIFY = 1;
    private const int NIM_DELETE = 2;
    private const int NIM_SETVERSION = 4;

    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;
    private const int NIF_INFO = 0x10;

    private const int MF_STRING = 0x0;
    private const int MF_SEPARATOR = 0x800;
    private const int MF_CHECKED = 0x8;

    private const int TPM_LEFTALIGN = 0x0;
    private const int TPM_BOTTOMALIGN = 0x20;

    // 菜单项 ID 用 WM_USER + n，和 WndProc 里的 WM_COMMAND 分支对应
    private const int IdmShow = 0x0400 + 1;
    private const int IdmToggle = 0x0400 + 2;
    private const int IdmDefaultMic = 0x0400 + 3;
    private const int IdmStartup = 0x0400 + 4;
    private const int IdmQuit = 0x0400 + 5;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public int uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", EntryPoint = "CreatePopupMenu")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, int uFlags, int uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, int uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly Window _mainWindow;
    private readonly IBackend _backend;
    private readonly IDisposable _stateSubscription;
    private readonly StatusDto _lastStatus = new();

    // LOAD BEARING: 必须存字段。委托被 GC 回收后，WndProc 会崩。
    private readonly WndProcDelegate _trayWndProc;
    private readonly uint _wmTaskbarRestart;

    private Window? _trayWindow;      // 专用隐形窗口，只为拿 HWND
    private IntPtr _hwnd;
    private IntPtr _originalWndProc;
    private NOTIFYICONDATA? _trayIconData;

    private IntPtr _popupMenu;
    private IntPtr _hIconIdle;
    private IntPtr _hIconLive;
    private IntPtr _hIconWaiting;
    private IntPtr _currentIcon;
    private bool _isConnected;
    private bool _wantReceiving;

    public TrayController(Window mainWindow, IBackend backend)
    {
        _mainWindow = mainWindow;
        _backend = backend;
        _trayWndProc = WndProc;
        _wmTaskbarRestart = RegisterWindowMessage("TaskbarCreated");

        _hIconIdle = StatusIcons.Create(110, 116, 139);
        _hIconLive = StatusIcons.Create(61, 220, 151);
        _hIconWaiting = StatusIcons.Create(255, 180, 84);
        _currentIcon = _hIconIdle;

        SetupTrayIcon();
        _stateSubscription = _backend.StateStream.Subscribe(UpdateIcon);
    }

    /// <summary>
    /// 建托盘图标。可重复调用：NIM_DELETE / NIM_ADD 都是幂等的，
    /// explorer 重启后必须重新走一遍。
    /// </summary>
    private void SetupTrayIcon()
    {
        if (_hwnd == IntPtr.Zero)
        {
            // 专用隐形窗口：不 Activate，所以不会出现在任务栏。
            // 不子类化主窗口是为了让"关窗口→托盘"这条路不受主窗口生命周期影响。
            _trayWindow = new Window();
            _hwnd = WindowNative.GetWindowHandle(_trayWindow);

            IntPtr procPtr = Marshal.GetFunctionPointerForDelegate(_trayWndProc);
            _originalWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, procPtr);
        }

        if (_popupMenu == IntPtr.Zero)
        {
            _popupMenu = CreatePopupMenu();
        }

        _trayIconData ??= new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = MyNotifyId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WmTrayIcon,
            hIcon = _currentIcon,
            szTip = "MicCast — 未启动",
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };

        var data = _trayIconData.Value;
        if (!Shell_NotifyIcon(NIM_ADD, ref data))
        {
            // explorer.exe 可能还没就绪（开机自启场景）。
            // 置空让 WM_WINDOWPOSCHANGING 那条重试路径重新来一次。
            _trayIconData = null;
            return;
        }

        // 4.0 版本行为：气泡通知走 szInfo / szInfoTitle
        var ver = data;
        ver.uVersion = 4;
        Shell_NotifyIcon(NIM_SETVERSION, ref ver);
    }

    private void UpdateIcon(StatusDto s)
    {
        _isConnected = s.Connected;
        _wantReceiving = s.WantReceiving;
        _currentIcon = s.Connected ? _hIconLive : (s.WantReceiving ? _hIconWaiting : _hIconIdle);

        if (_trayIconData is null) return;

        var data = _trayIconData.Value;
        data.uFlags = NIF_ICON | NIF_TIP;
        data.hIcon = _currentIcon;
        data.szTip = s.Connected ? "MicCast — 接收中"
            : (s.WantReceiving ? "MicCast — 重连中" : "MicCast — 未启动");
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_COMMAND:
                HandleMenuCommand((int)(wParam.ToInt64() & 0xFFFF));
                break;

            // 开机自启时 explorer 还没就绪 → NIM_ADD 失败 → 也收不到 TaskbarCreated。
            // WM_WINDOWPOSCHANGING 在 explorer 启动序列里一定会来，用它做重试钩子。
            case 0x0047: // WM_WINDOWPOSCHANGING
                if (_trayIconData is null) SetupTrayIcon();
                break;

            case WmTrayIcon:
                HandleTrayMessage(lParam);
                break;

            default:
                if (msg == _wmTaskbarRestart)
                {
                    // explorer.exe 重启了，通知区域图标被系统清掉，重新加一次
                    _trayIconData = null;
                    SetupTrayIcon();
                }
                break;
        }

        return CallWindowProc(_originalWndProc, hwnd, msg, wParam, lParam);
    }

    private void HandleTrayMessage(IntPtr lParam)
    {
        switch ((uint)(lParam.ToInt64() & 0xFFFF))
        {
            case WM_RBUTTONUP:
                ShowMenu();
                break;
            case WM_LBUTTONUP:
                ShowWindow();
                break;
        }
    }

    private void ShowMenu()
    {
        if (_popupMenu == IntPtr.Zero) return;

        // 菜单内容每次现建：勾选状态和"开始/停止"文案会变，
        // PowerToys 的做法是建一次复用，我们这里项数少，重建更直观。
        DestroyMenuIfNeeded();
        _popupMenu = CreatePopupMenu();

        AppendMenu(_popupMenu, MF_STRING, IdmShow, "显示主界面");
        AppendMenu(_popupMenu, MF_SEPARATOR, 0, null);
        AppendMenu(_popupMenu, MF_STRING, IdmToggle, _wantReceiving ? "停止接收" : "开始接收");
        AppendMenu(_popupMenu, MF_STRING, IdmDefaultMic, "设为默认麦克风");
        AppendMenu(_popupMenu, MF_SEPARATOR, 0, null);
        AppendMenu(_popupMenu, MF_STRING | (Startup.IsEnabled() ? MF_CHECKED : 0), IdmStartup, "开机自启");
        AppendMenu(_popupMenu, MF_SEPARATOR, 0, null);
        AppendMenu(_popupMenu, MF_STRING, IdmQuit, "退出");

        GetCursorPos(out var pt);
        // 弹出菜单前必须把窗口设成前台，否则点菜单外面收不到关闭消息
        SetForegroundWindow(_hwnd);
        TrackPopupMenuEx(_popupMenu, TPM_LEFTALIGN | TPM_BOTTOMALIGN, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        PostMessage(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);
    }

    private void DestroyMenuIfNeeded()
    {
        if (_popupMenu != IntPtr.Zero)
        {
            DestroyMenu(_popupMenu);
            _popupMenu = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    private void HandleMenuCommand(int cmd)
    {
        switch (cmd)
        {
            case IdmShow:
                ShowWindow();
                break;
            case IdmToggle:
                _ = ToggleAsync();
                break;
            case IdmDefaultMic:
                SetDefaultMicrophone();
                break;
            case IdmStartup:
                if (Startup.IsEnabled()) Startup.Disable();
                else Startup.Enable();
                break;
            case IdmQuit:
                RemoveIcon();
                Application.Current.Exit();
                break;
        }
    }

    /// <summary>
    /// 调 windows/Set-DefaultMic.ps1 把系统默认录音设备切成 CABLE Output。
    /// 脚本从可执行目录往外找几层 —— 开发时在 windows/ 下，发布后要用户自备。
    /// </summary>
    private void SetDefaultMicrophone()
    {
        string? script = FindDefaultMicScript();
        if (script is null) return;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Name \"CABLE Output\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(6000);
        }
        catch
        {
            // 切不了就让用户手动来，不打断托盘交互
        }
    }

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
        catch { }
        return null;
    }

    private async Task ToggleAsync()
    {
        if (_backend.WantReceiving)
        {
            await _backend.StopAsync();
            return;
        }
        string ip = ReadLastIp();
        if (!System.Net.IPAddress.TryParse(ip, out _)) return;
        await _backend.StartAsync(ip, -1);
    }

    private static string ReadLastIp()
    {
        try
        {
            string file = Path.Combine(AppContext.BaseDirectory, "last-ip.txt");
            return File.Exists(file) ? File.ReadAllText(file).Trim() : string.Empty;
        }
        catch { return string.Empty; }
    }

    public void ShowWindow()
    {
        _mainWindow.AppWindow.Show();
        _mainWindow.Activate();
    }

    public void HideToTray(bool silent)
    {
        // WinUI 3 的 Window 没有 Hide()，要通过 AppWindow 隐藏
        _mainWindow.AppWindow.Hide();

        if (silent || _trayIconData is null) return;

        var data = _trayIconData.Value;
        data.uFlags = NIF_INFO;
        data.szInfoTitle = "MicCast";
        data.szInfo = "还在后台运行，点托盘图标可以打开主界面。";
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void RemoveIcon()
    {
        if (_trayIconData is null) return;
        var data = _trayIconData.Value;
        if (Shell_NotifyIcon(NIM_DELETE, ref data)) _trayIconData = null;
    }

    public void Dispose()
    {
        _stateSubscription.Dispose();
        RemoveIcon();
        DestroyMenuIfNeeded();

        // 把原 WndProc 还回去，再销毁托盘窗口
        if (_hwnd != IntPtr.Zero && _originalWndProc != IntPtr.Zero)
        {
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _originalWndProc);
            _originalWndProc = IntPtr.Zero;
        }
        _trayWindow?.Close();
        _trayWindow = null;
        _hwnd = IntPtr.Zero;

        DestroyIcon(_hIconIdle);
        DestroyIcon(_hIconLive);
        DestroyIcon(_hIconWaiting);
    }
}