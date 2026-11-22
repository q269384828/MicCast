using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Runtime.InteropServices;
using System.Reactive.Linq;
using Windows.UI;

namespace MicCastReceiver.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly StartOptions _options;
    private readonly IBackend _backend;

    private readonly List<Protocol.DiscoveredServer> _serverItems = new();
    private readonly List<string> _earlyLogs = new();
    private readonly Queue<(long StartedAt, double Total)> _underrunHistory = new();
    private long _lastUnderrunCalcAt;
    private double _lastUnderrunRate = -1;

    private string _selectedIp = string.Empty;
    private bool _syncingServerBox;
    private bool _syncingDeviceBox;
    private bool _installing;
    private bool _loaded;
    private IDisposable? _stateSubscription;
    private IDisposable? _logSubscription;
    private DispatcherTimer? _uiTimer;
    private TrayController? _tray;

    public MainWindow(StartOptions options)
    {
        _options = options;
        _sizeWndProc = SizeWndProc;

        var (backend, host) = CreateBackend();
        _backend = backend;
        _backend.Profile = options.Latency;

        InitializeComponent();

        Title = "MicCast — 手机麦克风接收端";
        ApplyTitleBar();
        ApplyWindowIcon();

        IpBox.Text = _options.Ip.Length > 0 ? _options.Ip : LoadLastIp();
        RefreshBanner();
        LoadDevices();
        UpdateState();

        // Rx 订阅：ObserveOn 保证回到 UI 线程，晚订阅也能拿到当前状态与历史日志
        // 必须 ObserveOn 回 UI 线程：日志是由后台线程推过来的，
        // 直接改 LogText.Text 会抛跨线程异常，表现为日志区一直是空的。
        var logContext = new DispatcherQueueSynchronizationContext(DispatcherQueue);
        _logSubscription = _backend.LogStream
            .ObserveOn(logContext)
            .Subscribe(OnLog);
        _stateSubscription = _backend.StateStream
            .Sample(TimeSpan.FromMilliseconds(60))
            .ObserveOn(new DispatcherQueueSynchronizationContext(DispatcherQueue))
            .Subscribe(_ =>
            {
                UpdateState();
                RefreshServerList();
            });

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _uiTimer.Tick += (_, _) => UpdateStats();
        _uiTimer.Start();

        _loaded = true;
        FlushEarlyLogs();

        // DPI 只有渲染后才知道，所以尺寸要在 Loaded 里定
        RootGrid.Loaded += (_, _) => ApplyWindowSize();

        if (_options.AutoStart && !_backend.WantReceiving)
        {
            // 仅在后台还没开始接收时才自动启动；已经在跑时必须保持原状，
            // 否则打开界面就会把正在进行的接收停掉。
            _ = AutoStartAsync();
        }

        // 托盘常驻：关窗口时藏到这里
        _tray = new TrayController(this, _backend);

        Closed += (_, _) =>
        {
            _uiTimer?.Stop();
            _stateSubscription?.Dispose();
            _logSubscription?.Dispose();
            _tray?.Dispose();
            _backend.Dispose();
            host?.Dispose();
        };
    }

    /// <summary>标记为逻辑单例宿主；拿不到互斥量时连已有后台。</summary>
    private static (IBackend, IDisposable?) CreateBackend()
    {
        var host = new ReceiverHost { Profile = LatencyProfile.Balanced };
        if (BackendServer.TryCreate(host, out BackendServer? server) && server is not null)
        {
            return (host, server);
        }
        var pipe = PipeBackend.ConnectAsync().GetAwaiter().GetResult();
        host.Dispose();
        if (pipe is null)
        {
            throw new InvalidOperationException(
                "MicCast 的后台正在运行，但界面连不上它的通信通道。请先在任务管理器里结束 MicCastReceiver 进程，再重新打开。");
        }
        return (pipe, null);
    }

    /// <summary>开机自启：不弹窗口，直接进托盘。</summary>
    public void HideAtStartup()
    {
        _tray?.HideToTray(silent: true);
    }

    // ===== 窗口外观 =====

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// 设成设计尺寸 900x724（逻辑像素，和 WinForms 版一致）。
    /// AppWindow.Resize 要物理像素，所以按窗口 DPI 换算。
    ///
    /// **必须在 Loaded 之后调用**：窗口还没渲染出来时 GetDpiForWindow 返回 96，
    /// 于是 900 被当成物理像素，在 150% 缩放屏上只有 600 逻辑宽，内容会溢出被裁。
    /// </summary>
    public void ApplyWindowSize()
    {
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            // 先装尺寸约束，再设初始尺寸 —— 否则这次 Resize 会绕过限制
            ApplySizeConstraints();
            uint dpi = GetDpiForWindow(hwnd);
            double scale = dpi == 0 ? 1.0 : dpi / 96.0;

            const int logicalWidth = 900;
            const int logicalHeight = 724;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(
                (int)Math.Round(logicalWidth * scale),
                (int)Math.Round(logicalHeight * scale)));

            // 居中：取工作区尺寸算偏移
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            var work = area.WorkArea;
            AppWindow.Move(new Windows.Graphics.PointInt32(
                work.X + Math.Max(0, (work.Width - AppWindow.Size.Width) / 2),
                work.Y + Math.Max(0, (work.Height - AppWindow.Size.Height) / 2)));
        }
        catch (Exception ex)
        {
            App.Log($"ApplyWindowSize FAILED {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ===== 窗口尺寸上下限 =====

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int GWLP_WNDPROC = -4;

    /// <summary>逻辑尺寸上限：内容 MaxWidth 860 + 左右 page padding 40 + 滚动条 ≈ 940，取 960 留余量。</summary>
    private const int MaxLogicalWidth = 960;
    private const int MaxLogicalHeight = 1000;
    private const int MinLogicalWidth = 720;
    private const int MinLogicalHeight = 560;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // LOAD BEARING: 必须存字段，委托被 GC 后 WndProc 会崩（同托盘那套）
    private readonly WndProcDelegate _sizeWndProc;
    private IntPtr _originalWndProc;
    private IntPtr _windowHwnd;

    /// <summary>
    /// 限制窗口能被拉到多大 / 多小。
    ///
    /// 为什么不用 OverlappedPresenter.PreferredMaximumWidth：
    /// 实测它只对**用户拖拽边框**生效，程序化的 SetWindowPos 会绕过去。
    /// 处理 WM_GETMINMAXINFO 才是 Win32 的正规做法 —— 拖拽、最大化、
    /// SetWindowPos 全部都会走这里，一处生效。
    ///
    /// 窗口再宽也只是两侧留白（内容 MaxWidth 860），所以给上限比让它无限拉宽合理；
    /// 下限防止把内容压扁。
    /// </summary>
    private void ApplySizeConstraints()
    {
        try
        {
            _windowHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (_windowHwnd == IntPtr.Zero || _originalWndProc != IntPtr.Zero) return;

            IntPtr procPtr = Marshal.GetFunctionPointerForDelegate(_sizeWndProc);
            _originalWndProc = SetWindowLongPtr(_windowHwnd, GWLP_WNDPROC, procPtr);
        }
        catch (Exception ex)
        {
            App.Log($"ApplySizeConstraints FAILED {ex.GetType().Name}: {ex.Message}");
        }
    }

    private IntPtr SizeWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            uint dpi = GetDpiForWindow(hwnd);
            double scale = dpi == 0 ? 1.0 : dpi / 96.0;

            var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            info.ptMinTrackSize.X = (int)Math.Round(MinLogicalWidth * scale);
            info.ptMinTrackSize.Y = (int)Math.Round(MinLogicalHeight * scale);
            info.ptMaxTrackSize.X = (int)Math.Round(MaxLogicalWidth * scale);
            info.ptMaxTrackSize.Y = (int)Math.Round(MaxLogicalHeight * scale);
            Marshal.StructureToPtr(info, lParam, false);
        }
        return CallWindowProc(_originalWndProc, hwnd, msg, wParam, lParam);
    }

    private void ApplyTitleBar()
    {
        // 自定义标题栏：ExtendsContentIntoTitleBar + 指定可拖动区域，
        // 这样品牌标识和状态都能画进标题栏（WinForms 版只能改 DWM 配色）。
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var tb = AppWindow.TitleBar;
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            tb.ButtonBackgroundColor = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonForegroundColor = GetColor("TextMutedBrush");
            tb.ButtonHoverBackgroundColor = GetColor("SurfaceAltBrush");
            tb.ButtonHoverForegroundColor = GetColor("TextBrush");
            tb.ButtonPressedBackgroundColor = GetColor("SurfaceAltBrush");
            tb.ButtonPressedForegroundColor = GetColor("TextBrush");
        }
    }

    private void ApplyWindowIcon()
    {
        try
        {
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        }
        catch
        {
            // 图标设置失败不影响功能
        }
    }

    /// <summary>
    /// 取资源。样式和画笔都定义在 Application.Resources（Themes/*.xaml），
    /// 不在 RootGrid.Resources —— 直接索引会 KeyNotFoundException 崩在 XAML 层。
    /// </summary>
    private T Res<T>(string key) where T : class
    {
        if (RootGrid.Resources.TryGetValue(key, out object? local) && local is T typed) return typed;
        if (Application.Current.Resources.TryGetValue(key, out object? app) && app is T typedApp) return typedApp;
        throw new KeyNotFoundException($"找不到资源 {key}");
    }

    private Color GetColor(string key) => Res<SolidColorBrush>(key).Color;

    // ===== 状态 → 视觉 =====

    private void UpdateState()
    {
        if (!_loaded) return;

        bool running = _backend.Connected;
        bool reconnecting = _backend.WantReceiving && !running;

        StartButton.Content = running ? "停止接收" : (reconnecting ? "取消重连" : "开始接收");
        StartButton.Style = Res<Style>(running || reconnecting ? "OutlineButtonStyle" : "PrimaryButtonStyle");

        // 增益可能被托盘或另一个前端改过，回填滑块（防重入）
        int gainInt = (int)Math.Round(_backend.GainDb);
        if (Math.Abs(GainSlider.Value - gainInt) > 0.4)
        {
            _syncingGain = true;
            GainSlider.Value = Math.Clamp(gainInt, (int)GainSlider.Minimum, (int)GainSlider.Maximum);
            _syncingGain = false;
        }
        GainLabel.Text = gainInt == 0 ? "增益 0 dB" : $"增益 {(gainInt > 0 ? "+" : "")}{gainInt} dB";

        if (running)
        {
            SetPill("接收中", "OkBrush");
        }
        else if (reconnecting)
        {
            SetPill("重连中…", "WarnBrush");
        }
        else if (_backend.LastError.Length > 0)
        {
            SetPill("连接失败", "DangerBrush");
        }
        else if (_serverItems.Count > 0)
        {
            SetPill("已发现手机", "AccentBrush");
        }
        else
        {
            SetPill("未启动", "OnHeroBrush");
        }

        StreamingBlock.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        if (reconnecting)
        {
            HintText.Text = "手机端已断开，正在自动重连…（点「取消重连」放弃）";
            HintText.Visibility = Visibility.Visible;
            LevelScale.ScaleX = 0;
        }
        else if (!running)
        {
            HintText.Text = _backend.LastError.Length > 0
                ? _backend.LastError
                : (_serverItems.Count > 0
                    ? "已发现手机，确认输出设备是 CABLE Input 后点「开始接收」。"
                    : "先在手机上点「启动麦克风服务」，手机会自动出现在上面的列表里。");
            HintText.Visibility = Visibility.Visible;
            LevelScale.ScaleX = 0;
        }
        else
        {
            HintText.Visibility = Visibility.Collapsed;
            ServerLine.Text = BuildServerLine();
        }
    }

    private void SetPill(string caption, string brushKey)
    {
        StatusText.Text = caption;
        StatusDot.Fill = Res<SolidColorBrush>(brushKey);
    }

    private string BuildServerLine() =>
        $"缓冲 {_backend.BufferedMs}ms / 目标 {_backend.TargetMs}ms　抖动 {_backend.JitterMs:F1}ms　" +
        $"丢包 {_backend.LossPercent:F2}%　速率 {_backend.AveragePlaybackRate:F3}x　" +
        $"丢弃 {_backend.DroppedMs:F0}ms　欠载 {UnderrunRate(_backend.UnderrunMs):F1}ms/s";

    /// <summary>
    /// 欠载累计量只增不减，直接显示会让人以为一直在坏；换算成 5 秒窗口内的
    /// 速率才反映当下健康度（稳态应低于 2 ms/s）。
    /// </summary>
    private double UnderrunRate(double cumulativeMs)
    {
        long now = Environment.TickCount64;
        if (_lastUnderrunRate >= 0 && now - _lastUnderrunCalcAt < 250) return _lastUnderrunRate;

        _underrunHistory.Enqueue((now, cumulativeMs));
        while (_underrunHistory.Count > 2 && now - _underrunHistory.Peek().StartedAt > 5000)
        {
            _underrunHistory.Dequeue();
        }
        var oldest = _underrunHistory.Peek();
        double seconds = (now - oldest.StartedAt) / 1000.0;
        double rate = seconds < 0.5 ? 0 : Math.Max(0, (cumulativeMs - oldest.Total) / seconds);
        _lastUnderrunCalcAt = now;
        _lastUnderrunRate = rate;
        return rate;
    }

    private void UpdateStats()
    {
        if (!_loaded) return;

        if (_backend.Connected)
        {
            FpsValue.Text = $"{_backend.Fps:F0}";
            KbpsValue.Text = $"{_backend.Kbps:F0}";
            BufValue.Text = $"{_backend.BufferedMs}";
            ServerLine.Text = BuildServerLine();

            double level = Math.Clamp(_backend.Level, 0, 1);
            double scaled = level <= 0.0001 ? 0 : Math.Clamp((20 * Math.Log10(level) + 60) / 60, 0, 1);
            LevelScale.ScaleX = scaled;
            LevelLabel.Text = level <= 0.0005 ? "无输入" : $"{Math.Round(20 * Math.Log10(level))} dB";

            if (_options.LogPath.Length > 0 && _backend.Frames > 0)
            {
                WriteTrace($"STATS frames={_backend.Frames} fps={_backend.Fps:F0} kbps={_backend.Kbps:F0} " +
                    $"loss={_backend.LossPercent:F3}% buffered={_backend.BufferedMs}ms target={_backend.TargetMs}ms " +
                    $"jitter={_backend.JitterMs:F2}ms rate={_backend.AveragePlaybackRate:F4} " +
                    $"dropped={_backend.DroppedMs:F0}ms underrun={_backend.UnderrunMs:F0}ms " +
                    $"level={_backend.Level:F4} device={_backend.DeviceName}");
            }
        }
        else
        {
            LevelScale.ScaleX = 0;
            FpsValue.Text = "0";
            KbpsValue.Text = "0";
            BufValue.Text = "0";
            _underrunHistory.Clear();
            _lastUnderrunRate = -1;
            ServerLine.Text = _backend.WantReceiving ? "后台正在重连…" : string.Empty;
        }
    }

    private async Task AutoStartAsync()
    {
        string ip = IpBox.Text.Trim();
        if (!System.Net.IPAddress.TryParse(ip, out _))
        {
            var deadline = DateTime.Now.AddSeconds(_options.DiscoverTimeoutSeconds);
            while (DateTime.Now < deadline && _serverItems.Count == 0)
            {
                await Task.Delay(300);
            }
            if (_serverItems.Count == 0) return;
            ip = _serverItems[0].Ip;
            IpBox.Text = ip;
        }
        await _backend.StartAsync(ip, DeviceBox.SelectedIndex);
        UpdateState();
    }

    // ===== 设备 / 服务器列表 =====

    private void LoadDevices()
    {
        var devices = _backend.GetDevices();
        _syncingDeviceBox = true;
        DeviceBox.Items.Clear();
        foreach (var d in devices) DeviceBox.Items.Add($"[{d.Index}] {d.Name}");

        int preferred = -1;
        for (int i = 0; i < devices.Count; i++)
        {
            if (devices[i].Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
            {
                preferred = i;
                break;
            }
        }
        DeviceBox.SelectedIndex = devices.Count > 0 ? (preferred >= 0 ? preferred : 0) : -1;
        _syncingDeviceBox = false;
    }

    private void RefreshServerList()
    {
        if (!_loaded) return;

        var servers = _backend.Servers;
        bool sameSet = servers.Count == _serverItems.Count
                       && servers.Zip(_serverItems).All(p => p.First.Ip == p.Second.Ip && p.First.Name == p.Second.Name);
        if (sameSet)
        {
            UpdateHint();
            return;
        }

        _serverItems.Clear();
        _serverItems.AddRange(servers);

        int previous = ServerBox.SelectedIndex;
        _syncingServerBox = true;
        try
        {
            ServerBox.Items.Clear();
            foreach (var s in _serverItems) ServerBox.Items.Add($"{s.Name} · {s.Ip}");

            // 优先按已记录的 IP 匹配（含 last-ip.txt），这样重开程序也能自动选中
            string wanted = _selectedIp.Length > 0 ? _selectedIp : IpBox.Text.Trim();
            int index = _serverItems.FindIndex(s => s.Ip == wanted);
            if (index < 0 && previous >= 0 && previous < _serverItems.Count) index = previous;
            if (index < 0 && _serverItems.Count > 0) index = 0;

            ServerBox.SelectedIndex = index;
            if (index >= 0)
            {
                _selectedIp = _serverItems[index].Ip;
                IpBox.Text = _selectedIp;
            }
        }
        finally
        {
            _syncingServerBox = false;
        }

        if (!_backend.Connected)
        {
            SetPill(_serverItems.Count > 0 ? "已发现手机" : "未启动",
                _serverItems.Count > 0 ? "AccentBrush" : "OnHeroBrush");
        }
        UpdateHint();
    }

    private void UpdateHint()
    {
        if (_backend.Connected || !_loaded) return;
        HintText.Text = _backend.LastError.Length > 0
            ? _backend.LastError
            : (_serverItems.Count > 0
                ? "已发现手机，确认输出设备是 CABLE Input 后点「开始接收」。"
                : "先在手机上点「启动麦克风服务」，手机会自动出现在上面的列表里。");
    }

    private void RefreshBanner()
    {
        bool installed = VbCable.IsInstalled();
        Banner.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        InstallButton.IsEnabled = !_installing;
    }

    // ===== 事件处理 =====

    private void OnServerSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingServerBox) return;
        int i = ServerBox.SelectedIndex;
        if (i >= 0 && i < _serverItems.Count)
        {
            _selectedIp = _serverItems[i].Ip;
            IpBox.Text = _selectedIp;
        }
    }

    private void OnDeviceSelected(object sender, SelectionChangedEventArgs e) { }

    private void OnRefreshDevices(object sender, RoutedEventArgs e)
    {
        LoadDevices();
        RefreshServerList();
        RefreshBanner();
    }

    private bool _syncingGain;

    private void OnGainChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loaded || _syncingGain) return;
        int db = (int)Math.Round(e.NewValue);
        _backend.GainDb = db;
        GainLabel.Text = db == 0 ? "增益 0 dB" : $"增益 {(db > 0 ? "+" : "")}{db} dB";
    }

    private async void OnToggleReceiving(object sender, RoutedEventArgs e)
    {
        if (_backend.WantReceiving)
        {
            await _backend.StopAsync();
            UpdateState();
            return;
        }

        string ip = IpBox.Text.Trim();
        if (!System.Net.IPAddress.TryParse(ip, out _))
        {
            await ShowMessageAsync("还没找到手机",
                "请在手机上点「启动麦克风服务」，列表会自动出现；也可以手动填写手机上显示的 IP。");
            return;
        }
        if (DeviceBox.SelectedIndex < 0)
        {
            await ShowMessageAsync("未选设备", "请先选择输出设备（建议 CABLE Input）");
            return;
        }

        SaveLastIp(ip);
        StartButton.IsEnabled = false;
        try
        {
            await _backend.StartAsync(ip, DeviceBox.SelectedIndex);
        }
        finally
        {
            StartButton.IsEnabled = true;
            UpdateState();
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "知道了",
            XamlRoot = RootGrid.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async void OnInstallVbCable(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        _installing = true;
        InstallButton.IsEnabled = false;
        InstallButton.Content = "安装中…";
        try
        {
            var (ok, message) = await VbCable.InstallAsync(OnLog, CancellationToken.None);
            OnLog(message);
            if (ok)
            {
                LoadDevices();
                UpdateState();
            }
        }
        finally
        {
            _installing = false;
            InstallButton.Content = "自动下载并安装";
            RefreshBanner();
        }
    }

    private void OnOpenVbCablePage(object sender, RoutedEventArgs e) => VbCable.OpenHomePage();

    private void OnRecheckVbCable(object sender, RoutedEventArgs e)
    {
        LoadDevices();
        RefreshBanner();
    }

    private readonly List<string> _logLines = new();

    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        _logLines.Clear();
        LogText.Text = string.Empty;
    }

    private void OnLog(string line)
    {
        if (!_loaded)
        {
            lock (_earlyLogs)
            {
                _earlyLogs.Add(line);
                if (_earlyLogs.Count > 200) _earlyLogs.RemoveAt(0);
            }
            return;
        }
        _logLines.Add(line);
        if (_logLines.Count > 500) _logLines.RemoveAt(0);
        LogText.Text = string.Join(Environment.NewLine, _logLines);
        LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null);
        WriteTrace(line);
    }

    private void FlushEarlyLogs()
    {
        List<string> pending;
        lock (_earlyLogs)
        {
            if (_earlyLogs.Count == 0) return;
            pending = _earlyLogs.ToList();
            _earlyLogs.Clear();
        }
        foreach (var line in pending) OnLog(line);
    }

    private void WriteTrace(string line)
    {
        if (_options.LogPath.Length == 0) return;
        try { File.AppendAllText(_options.LogPath, line + Environment.NewLine); } catch { }
    }

    // ===== 持久化 =====

    private static string LastIpFile => Path.Combine(AppContext.BaseDirectory, "last-ip.txt");

    private static string LoadLastIp()
    {
        try { return File.Exists(LastIpFile) ? File.ReadAllText(LastIpFile).Trim() : string.Empty; }
        catch { return string.Empty; }
    }

    private static void SaveLastIp(string ip)
    {
        try { File.WriteAllText(LastIpFile, ip); } catch { }
    }
}
