using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using MicCastReceiver.Ui;
using System.Reactive.Linq;

namespace MicCastReceiver;

public sealed class MainForm : Form
{
    // 界面只依赖 IBackend：可能是进程内的 ReceiverHost，也可能是连到别的进程的 PipeBackend。
    // 具体是哪一种由 Program 决定，界面不关心 —— 这就是逻辑与 UX 的分离点。
    private readonly IBackend _backend;
    private readonly StartOptions _options;

    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly StatusPill _pill = new();

    private readonly Card _banner = new();
    private readonly Label _bannerTitle = new();
    private readonly Label _bannerText = new();
    private readonly ModernButton _installButton = new();
    private readonly ModernButton _openPageButton = new();
    private readonly ModernButton _recheckButton = new();

    private readonly Card _deviceCard = new();
    private readonly Label _serverCaption = new();
    private readonly Label _deviceCaption = new();
    private readonly Label _ipCaption = new();
    private readonly DropDown _serverBox = new();
    private readonly DropDown _deviceBox = new();
    private readonly TextField _ipBox = new();
    private readonly ModernButton _refreshButton = new();

    private readonly Card _statusCard = new();
    private readonly Label _hint = new();
    private readonly Label _fpsValue = new();
    private readonly Label _kbpsValue = new();
    private readonly Label _bufValue = new();
    private readonly Label _fpsCaption = new();
    private readonly Label _kbpsCaption = new();
    private readonly Label _bufCaption = new();
    private readonly LevelMeter _level = new();
    private readonly Label _serverLine = new();

    private readonly ModernButton _startButton = new();
    private readonly Label _gainLabel = new();
    private readonly Slider _gainSlider = new();

    private readonly Card _logCard = new();
    private readonly Label _logTitle = new();
    private readonly ModernButton _clearLogButton = new();
    private readonly TextBox _logBox = new();

    private readonly System.Windows.Forms.Timer _uiTimer = new();

    private List<Protocol.DiscoveredServer> _serverItems = new();
    private string _selectedIp = string.Empty;
    private bool _syncingServerBox;
    private bool _installing;
    private readonly List<string> _earlyLogs = new();
    private readonly Queue<(long StartedAt, double Total)> _underrunHistory = new();
    private long _lastUnderrunCalcAt;
    private double _lastUnderrunRate = -1;
    private IDisposable? _stateSubscription;
    private IDisposable? _logSubscription;
    private TrayController? _tray;

    public MainForm() : this(new StartOptions()) { }

    public MainForm(StartOptions options) : this(options, new ReceiverHost()) { }

    public MainForm(StartOptions options, IBackend backend)
    {
        _options = options;
        _backend = backend;

        Theme.Apply(_options.Theme.ToLowerInvariant() switch
        {
            "dark" => AppTheme.Dark,
            "light" => AppTheme.Light,
            _ => Theme.DetectSystemTheme()
        });
        _backend.Profile = _options.Latency;

        Text = "MicCast — 手机麦克风接收端";
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(S(900), S(724));
        MinimumSize = new Size(S(820), S(650));
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9.5f);
        DoubleBuffered = true;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.TextPrimary;
        ApplyWindowIcon();

        BuildControls();
        ApplyTheme();
        LoadDevices();

        _ipBox.Text = _options.Ip.Length > 0 ? _options.Ip : LoadLastIp();
        if (_options.Device >= 0 && _options.Device < _deviceBox.Items.Length)
        {
            _deviceBox.SelectedIndex = _options.Device;
        }
        _pill.Set("未启动", Theme.TextFaint);

        // 用 Rx 订阅：ObserveOn 保证回到 UI 线程，BehaviorSubject/ReplaySubject
        // 保证「晚订阅也能拿到当前状态和历史日志」，Sample 限制刷新频率。
        var uiContext = System.Threading.SynchronizationContext.Current;
        _logSubscription = _backend.LogStream
            .ObserveOn(uiContext)
            .Subscribe(OnLog);

        _stateSubscription = _backend.StateStream
            .ObserveOn(uiContext)
            .Sample(TimeSpan.FromMilliseconds(60))
            .Subscribe(_ => { UpdateState(); RefreshServerList(); });

        _uiTimer.Interval = 60;
        _uiTimer.Tick += (_, _) => UpdateStats();
        _uiTimer.Start();

        // 托盘常驻：关窗口时藏到这里，图标随连接状态变色
        _tray = new TrayController(this, _backend);

        Shown += async (_, _) =>
        {
            NativeFx.Apply(this, Theme.Current == AppTheme.Dark);
            RefreshBanner();
            DoLayout();
            FlushEarlyLogs();

            // 开机自启：不弹窗口，直接藏到托盘（静默，不弹气泡）
            if (_options.Minimized && _tray is not null)
            {
                _tray.HideToTray(silent: true);
            }

            if (_options.AutoStart && !_backend.WantReceiving)
            {
                // 仅在后台还没开始接收时才自动启动。
                // 后台（可能是 CLI 起的）已经在跑时必须保持原状 ——
                // 否则打开界面就会把正在进行的接收停掉。
                if (await EnsureTargetAsync()) await ToggleAsync();
                if (_options.LoopbackVerifyMs > 0)
                {
                    await Task.Delay(3000);
                    double rms = await MeasureLoopbackAsync(_options.LoopbackVerifyMs);
                    WriteTrace($"LOOPBACK rms={rms:F5} ({(rms > 0.0005 ? "有声" : "静音")})");
                }
            }
        };
    }

    private int S(double value) => (int)Math.Round(value * DeviceDpi / 96.0);

    private void BuildControls()
    {
        _title.Text = "MicCast";
        _title.Font = new Font("Microsoft YaHei UI", 17f, FontStyle.Bold);
        _title.AutoSize = true;

        _subtitle.Text = "声渡 · 把手机麦克风变成一个 Windows 麦克风";
        _subtitle.Font = new Font("Microsoft YaHei UI", 9f);
        _subtitle.AutoSize = true;

        _bannerTitle.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
        _bannerText.Font = new Font("Microsoft YaHei UI", 9f);

        _installButton.Text = "自动下载并安装";
        _installButton.Kind = ButtonKind.Primary;
        _installButton.Click += async (_, _) => await InstallVbCableAsync();
        _openPageButton.Text = "打开下载页面";
        _openPageButton.Kind = ButtonKind.Ghost;
        _openPageButton.Click += (_, _) => VbCable.OpenHomePage();
        _recheckButton.Text = "我已装好，刷新";
        _recheckButton.Kind = ButtonKind.Ghost;
        _recheckButton.Click += (_, _) =>
        {
            LoadDevices();
            RefreshBanner();
        };

        _serverCaption.Text = "手机";
        _deviceCaption.Text = "输出设备";
        _ipCaption.Text = "或手动 IP";

        _serverBox.Placeholder = "正在扫描局域网…";
        _serverBox.SelectionChanged += (_, _) =>
        {
            if (_syncingServerBox) return;
            if (_serverBox.SelectedIndex >= 0 && _serverBox.SelectedIndex < _serverItems.Count)
            {
                _selectedIp = _serverItems[_serverBox.SelectedIndex].Ip;
                _ipBox.Text = _selectedIp;
            }
        };

        _ipBox.Placeholder = "192.168.x.x";

        _refreshButton.Text = "刷新";
        _refreshButton.Kind = ButtonKind.Subtle;
        _refreshButton.Click += (_, _) =>
        {
            LoadDevices();
            RefreshServerList();
            RefreshBanner();
        };

        _hint.Font = new Font("Microsoft YaHei UI", 9.5f);

        _fpsValue.Text = "0";
        _kbpsValue.Text = "0";
        _bufValue.Text = "0";
        foreach (var v in new[] { _fpsValue, _kbpsValue, _bufValue })
        {
            // Segoe UI 的数字是等宽的：数值跳动时整行不会左右呼吸
            v.Font = new Font("Segoe UI", 15f, FontStyle.Bold);
            v.TextAlign = ContentAlignment.BottomLeft;
        }

        _fpsCaption.Text = "帧/秒";
        _kbpsCaption.Text = "kbps";
        _bufCaption.Text = "毫秒缓冲";
        foreach (var c in new[] { _fpsCaption, _kbpsCaption, _bufCaption })
        {
            c.Font = new Font("Microsoft YaHei UI", 8.5f);
            c.TextAlign = ContentAlignment.TopLeft;
        }

        _serverLine.Font = new Font("Microsoft YaHei UI", 8.5f);

        _startButton.Text = "开始接收";
        _startButton.Kind = ButtonKind.Primary;
        _startButton.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
        _startButton.Click += async (_, _) => await ToggleAsync();

        _gainLabel.Text = "增益 0 dB";
        _gainLabel.Font = new Font("Microsoft YaHei UI", 9f);
        // 右对齐贴住滑块：文字宽度随数值变化，靠右才不会被推着左右跳
        _gainLabel.TextAlign = ContentAlignment.MiddleRight;

        // 增益只在前端做一次乘法，改动立刻作用于后续帧，不需要重连。
        _gainSlider.Minimum = -12;
        _gainSlider.Maximum = 12;
        _gainSlider.Value = 0;
        _gainSlider.ValueChanged += (_, _) =>
        {
            double db = _gainSlider.Value;
            _backend.GainDb = db;
            _gainLabel.Text = db == 0 ? "增益 0 dB" : $"增益 {(db > 0 ? "+" : "")}{db:0} dB";
        };

        _logTitle.Text = "日志";
        _logTitle.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
        _clearLogButton.Text = "清空";
        _clearLogButton.Kind = ButtonKind.Ghost;
        _clearLogButton.Click += (_, _) => _logBox.Clear();

        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        // 不显示系统滚动条（暗色下是白底很扎眼），滚轮/键盘照样能滚
        _logBox.ScrollBars = ScrollBars.None;
        _logBox.BorderStyle = BorderStyle.None;
        _logBox.Font = new Font("Consolas", 8.75f);
        _logBox.WordWrap = true;

        _banner.Controls.AddRange(new Control[] { _bannerTitle, _bannerText, _installButton, _openPageButton, _recheckButton });
        _deviceCard.Controls.AddRange(new Control[] { _serverCaption, _serverBox, _deviceCaption, _deviceBox, _ipCaption, _ipBox, _refreshButton });
        _statusCard.Controls.AddRange(new Control[] { _hint, _fpsValue, _fpsCaption, _kbpsValue, _kbpsCaption, _bufValue, _bufCaption, _level, _serverLine });
        _logCard.Controls.AddRange(new Control[] { _logTitle, _clearLogButton, _logBox });

        Controls.AddRange(new Control[] { _title, _subtitle, _pill, _banner, _deviceCard, _statusCard, _startButton, _gainLabel, _gainSlider, _logCard });
    }

    private void ApplyTheme()
    {
        BackColor = Theme.WindowBg;
        ForeColor = Theme.TextPrimary;

        _title.ForeColor = Theme.TextPrimary;
        _subtitle.ForeColor = Theme.TextSecondary;

        foreach (var card in new[] { _banner, _deviceCard, _statusCard, _logCard })
        {
            card.ApplyTheme();
        }

        foreach (var label in new[]
                 {
                     _bannerText, _serverCaption, _deviceCaption, _ipCaption, _fpsCaption, _kbpsCaption,
                     _bufCaption, _serverLine, _logTitle
                 })
        {
            label.BackColor = Theme.Surface;
            label.ForeColor = Theme.TextSecondary;
        }

        foreach (var label in new[] { _bannerTitle, _fpsValue, _kbpsValue, _bufValue, _hint })
        {
            label.BackColor = Theme.Surface;
            label.ForeColor = Theme.TextPrimary;
        }

        _serverBox.BackColor = Theme.Surface;
        _deviceBox.BackColor = Theme.Surface;
        _ipBox.ApplyTheme();
        _level.BackColor = Theme.Surface;

        _logBox.BackColor = Theme.LogBg;
        _logBox.ForeColor = Theme.TextSecondary;

        _pill.BackColor = Theme.WindowBg;

        Invalidate(true);
    }

    private void DoLayout()
    {
        int pad = S(22);
        int width = ClientSize.Width - pad * 2;

        int y = S(16);
        _title.Location = new Point(pad + S(44), y);
        _subtitle.Location = new Point(pad + S(45), y + S(28));
        _pill.Location = new Point(ClientSize.Width - pad - _pill.Width, y + S(14));
        y += S(58);

        if (_banner.Visible)
        {
            _banner.SetBounds(pad, y, width, S(104));
            LayoutBanner();
            y += S(104) + S(14);
        }

        _deviceCard.SetBounds(pad, y, width, S(150));
        LayoutDeviceCard();
        y += S(150) + S(14);

        int statusHeight = _backend.Connected ? S(132) : S(74);
        _statusCard.SetBounds(pad, y, width, statusHeight);
        LayoutStatusCard();
        y += statusHeight + S(14);

        _startButton.SetBounds(pad, y, S(150), S(44));

        // 增益是微调项：右对齐成一組，和左侧主操作形成「主-次」平衡，
        // 而不是让滑块横贯整行、把左侧按钮衬得像半截。
        int sliderWidth = S(280);
        int sliderX = pad + width - sliderWidth;
        int gainLabelWidth = S(88);
        _gainSlider.SetBounds(sliderX, y, sliderWidth, S(44));
        _gainLabel.SetBounds(sliderX - gainLabelWidth - S(10), y + S(12), gainLabelWidth, S(20));
        y += S(44) + S(14);

        int logHeight = Math.Max(S(120), ClientSize.Height - y - pad);
        _logCard.SetBounds(pad, y, width, logHeight);
        LayoutLogCard();
    }

    private void LayoutBanner()
    {
        int w = _banner.Width;
        _bannerTitle.SetBounds(S(18), S(14), w - S(36), S(22));
        _bannerText.SetBounds(S(18), S(38), w - S(36), S(20));

        int buttonY = S(64);
        int x = S(18);
        _installButton.SetBounds(x, buttonY, S(146), S(32));
        x += S(146) + S(10);
        _openPageButton.SetBounds(x, buttonY, S(120), S(32));
        x += S(120) + S(10);
        _recheckButton.SetBounds(x, buttonY, S(130), S(32));
    }

    private void LayoutDeviceCard()
    {
        int w = _deviceCard.Width;
        int labelWidth = S(66);
        int left = S(18);
        int x = left + labelWidth;

        _serverCaption.SetBounds(left, S(18), labelWidth, S(24));
        _serverBox.SetBounds(x, S(12), w - x - S(18), S(38));

        _deviceCaption.SetBounds(left, S(66), labelWidth, S(24));
        int refreshWidth = S(64);
        _deviceBox.SetBounds(x, S(60), w - x - S(18) - refreshWidth - S(8), S(38));
        _refreshButton.SetBounds(w - S(18) - refreshWidth, S(60), refreshWidth, S(38));

        _ipCaption.SetBounds(left, S(112), labelWidth + S(8), S(22));
        _ipBox.SetBounds(x, S(105), S(230), S(34));
    }

    private void LayoutStatusCard()
    {
        int w = _statusCard.Width;
        int left = S(18);
        bool running = _backend.Connected;

        _hint.Visible = !running;
        _hint.SetBounds(left, S(4), w - S(36), S(64));

        foreach (var c in new Control[] { _fpsValue, _fpsCaption, _kbpsValue, _kbpsCaption, _bufValue, _bufCaption, _level, _serverLine })
        {
            c.Visible = running;
        }

        if (!running) return;

        int columnWidth = (w - left * 2) / 3;
        // 垂直方向上下留白取接近对称，卡片不再显得内容压在上半截
        _fpsValue.SetBounds(left, S(20), columnWidth, S(26));
        _fpsCaption.SetBounds(left, S(46), columnWidth, S(16));
        _kbpsValue.SetBounds(left + columnWidth, S(20), columnWidth, S(26));
        _kbpsCaption.SetBounds(left + columnWidth, S(46), columnWidth, S(16));
        _bufValue.SetBounds(left + columnWidth * 2, S(20), columnWidth, S(26));
        _bufCaption.SetBounds(left + columnWidth * 2, S(46), columnWidth, S(16));

        _level.SetBounds(left, S(72), w - left * 2, S(10));
        _serverLine.SetBounds(left, S(90), w - left * 2, S(18));
    }

    private void LayoutLogCard()
    {
        int w = _logCard.Width;
        _logTitle.SetBounds(S(18), S(12), S(60), S(20));
        _clearLogButton.SetBounds(w - S(18) - S(60), S(9), S(60), S(26));
        _logBox.SetBounds(S(18), S(42), w - S(36), Math.Max(S(40), _logCard.Height - S(58)));
    }

    private void RefreshBanner()
    {
        bool installed = VbCable.IsInstalled();
        _banner.Visible = !installed;
        _installButton.Enabled = !_installing;
        if (!installed)
        {
            _bannerTitle.Text = "还需要一个虚拟声卡";
            _bannerText.Text = "Windows 没有内置虚拟麦克风。装好 VB-CABLE 后会出现 CABLE Input / CABLE Output 两个设备。";
            _banner.Invalidate();
        }
        DoLayout();
        RefreshServerList();
    }

    private void LoadDevices()
    {
        var devices = _backend.GetDevices();
        _deviceBox.Items = devices.Select(d => $"[{d.Index}] {d.Name}").ToArray();

        int preferred = -1;
        for (int i = 0; i < devices.Count; i++)
        {
            if (devices[i].Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
            {
                preferred = i;
                break;
            }
        }
        _deviceBox.SelectedIndex = devices.Count > 0 ? (preferred >= 0 ? preferred : 0) : -1;
        _deviceCaption.Text = preferred >= 0 ? "输出设备" : "输出设备";
    }

    private void RefreshServerList()
    {
        var servers = _backend.Servers;
        bool sameSet = servers.Count == _serverItems.Count
                       && servers.Zip(_serverItems).All(p => p.First.Ip == p.Second.Ip && p.First.Name == p.Second.Name);
        if (sameSet)
        {
            UpdateHint();
            return;
        }

        _serverItems = servers.ToList();
        int previous = _serverBox.SelectedIndex;
        _syncingServerBox = true;
        try
        {
            _serverBox.Items = _serverItems.Select(s => $"{s.Name} · {s.Ip}").ToArray();

            // 优先按已记录的 IP 匹配（包括 last-ip.txt 里带过来的那个），这样重开程序也能自动选中
            string wanted = _selectedIp.Length > 0 ? _selectedIp : _ipBox.Text.Trim();
            int index = _serverItems.FindIndex(s => s.Ip == wanted);
            if (index < 0 && previous >= 0 && previous < _serverItems.Count) index = previous;
            if (index < 0 && _serverItems.Count > 0) index = 0;

            _serverBox.SelectedIndex = index;
            if (index >= 0)
            {
                _selectedIp = _serverItems[index].Ip;
                _ipBox.Text = _selectedIp;
            }
        }
        finally
        {
            _syncingServerBox = false;
        }

        if (!_backend.Connected)
        {
            _pill.Set(_serverItems.Count > 0 ? "已发现手机" : "未启动",
                _serverItems.Count > 0 ? Theme.Accent : Theme.TextFaint);
        }
        UpdateHint();
        DoLayout();
    }

    private void UpdateHint()
    {
        if (_backend.Connected) return;
        _hint.Text = _backend.LastError.Length > 0
            ? _backend.LastError
            : (_serverItems.Count > 0
                ? "已发现手机，确认输出设备是 CABLE Input 后点「开始接收」。"
                : "先在手机上点「启动麦克风服务」，手机会自动出现在上面的列表里。");
    }

    private async Task<bool> EnsureTargetAsync()
    {
        if (_ipBox.Text.Trim().Length > 0) return true;
        var deadline = DateTime.Now.AddSeconds(20);
        while (DateTime.Now < deadline)
        {
            RefreshServerList();
            if (_serverItems.Count > 0)
            {
                _selectedIp = _serverItems[0].Ip;
                _ipBox.Text = _selectedIp;
                WriteTrace($"DISCOVERED {_serverItems[0].Name} @ {_selectedIp}");
                return true;
            }
            await Task.Delay(300);
        }
        WriteTrace("DISCOVERED none (timeout)");
        return false;
    }

    private static async Task<double> MeasureLoopbackAsync(int durationMs)
    {
        try
        {
            using var capture = new NAudio.Wave.WasapiLoopbackCapture();
            double sum = 0;
            long count = 0;
            capture.DataAvailable += (_, e) =>
            {
                for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                {
                    short v = BitConverter.ToInt16(e.Buffer, i);
                    double d = v / 32768.0;
                    sum += d * d;
                    count++;
                }
            };
            capture.StartRecording();
            await Task.Delay(durationMs);
            capture.StopRecording();
            return count > 0 ? Math.Sqrt(sum / count) : 0;
        }
        catch
        {
            return -1;
        }
    }

    private async Task InstallVbCableAsync()
    {
        if (_installing) return;
        _installing = true;
        _installButton.Enabled = false;
        _installButton.Text = "安装中…";
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
            _installButton.Text = "自动下载并安装";
            RefreshBanner();
        }
    }

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

    private async Task ToggleAsync()
    {
        // 重连逻辑已经上移到 ReceiverHost，这里只表达用户意图
        if (_backend.WantReceiving)
        {
            await _backend.StopAsync();
            UpdateState();
            return;
        }

        string ip = _ipBox.Text.Trim();
        if (!System.Net.IPAddress.TryParse(ip, out _))
        {
            MessageBox.Show(this,
                "还没找到手机。请在手机上点「启动麦克风服务」，列表会自动出现；也可以手动填写手机上显示的 IP。",
                "缺少手机地址", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_deviceBox.SelectedIndex < 0)
        {
            MessageBox.Show(this, "请先选择输出设备（建议 CABLE Input）", "未选设备",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SaveLastIp(ip);
        _startButton.Enabled = false;
        try
        {
            await _backend.StartAsync(ip, _deviceBox.SelectedIndex);
        }
        finally
        {
            _startButton.Enabled = true;
            UpdateState();
        }
    }

    private void OnLog(string line)
    {
        // 走 Rx 订阅后这里已经保证在 UI 线程；句柄未就绪的情况由 _earlyLogs 兜底
        if (!IsHandleCreated || IsDisposed)
        {
            lock (_earlyLogs)
            {
                _earlyLogs.Add(line);
                if (_earlyLogs.Count > 200) _earlyLogs.RemoveAt(0);
            }
            return;
        }
        _logBox.AppendText(line + Environment.NewLine);
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
        foreach (var line in pending)
        {
            _logBox.AppendText(line + Environment.NewLine);
        }
    }

    private void WriteTrace(string line)
    {
        if (_options.LogPath.Length == 0) return;
        try { File.AppendAllText(_options.LogPath, line + Environment.NewLine); } catch { }
    }

    private void UpdateState()
    {
        bool running = _backend.Connected;
        bool reconnecting = _backend.WantReceiving && !running;
        _startButton.Text = running ? "停止接收" : (reconnecting ? "取消重连" : "开始接收");
        // 停止是可逆的次要动作：描边样式，在窗口背景上轮廓清楚又不抢主色
        _startButton.Kind = running || reconnecting ? ButtonKind.Ghost : ButtonKind.Primary;

        // 增益可能被托盘或另一个前端改过，这里回填滑块（防重入）
        int gainInt = (int)Math.Round(_backend.GainDb);
        if (_gainSlider.Value != gainInt)
        {
            _gainSlider.Value = Math.Clamp(gainInt, _gainSlider.Minimum, _gainSlider.Maximum);
        }

        if (running)
        {
            _pill.Set("接收中", Theme.Ok);
        }
        else if (reconnecting)
        {
            _pill.Set("重连中…", Theme.Warn);
        }
        else if (_backend.LastError.Length > 0)
        {
            _pill.Set("连接失败", Theme.Danger);
        }
        else if (_serverItems.Count > 0)
        {
            _pill.Set("已发现手机", Theme.Accent);
        }
        else
        {
            _pill.Set("未启动", Theme.TextFaint);
        }

        if (reconnecting)
        {
            _hint.Text = "手机端已断开，正在自动重连…（点「取消重连」放弃）";
            _level.SetLevel(0);
        }
        else if (!running)
        {
            _hint.Text = _backend.LastError.Length > 0
                ? _backend.LastError
                : (_serverItems.Count > 0
                    ? "已发现手机，选好输出设备后点「开始接收」。"
                    : "先在手机上点「启动麦克风服务」，手机会自动出现在上面的列表里。");
            _level.SetLevel(0);
        }
        else
        {
            _serverLine.Text = $"缓冲 {_backend.BufferedMs}ms / 目标 {_backend.TargetMs}ms　抖动 {_backend.JitterMs:F1}ms　丢包 {_backend.LossPercent:F2}%　速率 {_backend.AveragePlaybackRate:F3}x　丢弃 {_backend.DroppedMs:F0}ms　欠载 {UnderrunRate(_backend.UnderrunMs):F1}ms/s";
        }

        DoLayout();
        Invalidate(true);
    }

    /// <summary>
    /// 欠载的累计量只增不减，直接显示会让人以为一直在坏；换算成 5 秒窗口内的
    /// 速率才反映当下健康度（稳态应该 &lt; 2 ms/s）。
    /// </summary>
    private double UnderrunRate(double cumulativeMs)
    {
        long now = Environment.TickCount64;
        // UpdateState 和 UpdateStats 都会调这里，节流一下避免同一时刻重复入队
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
        if (_backend.Connected)
        {
            _fpsValue.Text = $"{_backend.Fps:F0}";
            _kbpsValue.Text = $"{_backend.Kbps:F0}";
            _bufValue.Text = $"{_backend.BufferedMs}";
            _serverLine.Text = $"缓冲 {_backend.BufferedMs}ms / 目标 {_backend.TargetMs}ms　抖动 {_backend.JitterMs:F1}ms　丢包 {_backend.LossPercent:F2}%　速率 {_backend.AveragePlaybackRate:F3}x　丢弃 {_backend.DroppedMs:F0}ms　欠载 {UnderrunRate(_backend.UnderrunMs):F1}ms/s";

            double level = Math.Clamp(_backend.Level, 0, 1);
            double scaled = level <= 0.0001 ? 0 : Math.Clamp((20 * Math.Log10(level) + 60) / 60, 0, 1);
            _level.SetLevel((float)scaled);

            if (_options.LogPath.Length > 0 && _backend.Frames > 0)
            {
                WriteTrace($"STATS frames={_backend.Frames} fps={_backend.Fps:F0} kbps={_backend.Kbps:F0} loss={_backend.LossPercent:F3}% lostFrames={_backend.LostFrames} outOfOrder={_backend.OutOfOrderCount} buffered={_backend.BufferedMs}ms target={_backend.TargetMs}ms jitter={_backend.JitterMs:F2}ms rate={_backend.AveragePlaybackRate:F4} dropped={_backend.DroppedMs:F0}ms overflow={_backend.CatchUpCount} underrun={_backend.UnderrunMs:F0}ms underrunCount={_backend.UnderrunCount} level={_backend.Level:F4} device={_backend.DeviceName}");
            }
        }
        else
        {
            _level.SetLevel(0);
            // 断开后别留着上一次的数字，否则看起来像还在收
            _fpsValue.Text = "0";
            _kbpsValue.Text = "0";
            _bufValue.Text = "0";
            _underrunHistory.Clear();
            _lastUnderrunRate = -1;
            _serverLine.Text = _backend.WantReceiving ? "后台正在重连…" : "";
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        int pad = S(22);
        var mark = new Rectangle(pad, S(16), S(38), S(38));

        var art = LoadBrandMark(mark.Width);
        if (art is not null)
        {
            g.DrawImage(art, mark);
        }
    }

    private static Bitmap? _brandMark;
    private static int _brandMarkSize;

    /// <summary>从嵌入的 app.ico 设置窗体图标，绕开 Windows 图标缓存里的旧版本。</summary>
    private void ApplyWindowIcon()
    {
        try
        {
            using var stream = typeof(MainForm).Assembly
                .GetManifestResourceStream("MicCastReceiver.app.ico");
            if (stream is null) return;
            using var icon = new Icon(stream);
            Icon = (Icon)icon.Clone();
        }
        catch
        {
            // 失败就沿用 exe 关联图标，不影响功能
        }
    }

    /// <summary>
    /// 界面左上角的品牌标记用透明底素材叠加在窗口背景上，
    /// 而不是贴完整图标 —— 带渐变底的图标放在深色界面上会显出亮色方块。
    /// </summary>
    private static Bitmap? LoadBrandMark(int size)
    {
        if (_brandMark is not null && _brandMarkSize == size) return _brandMark;
        try
        {
            using var stream = typeof(MainForm).Assembly
                .GetManifestResourceStream("MicCastReceiver.brand_mark.png");
            if (stream is null) return null;
            using var src = new Bitmap(stream);
            int target = Math.Max(8, size);
            var scaled = new Bitmap(target, target, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var sg = Graphics.FromImage(scaled))
            {
                sg.Clear(Color.Transparent);
                sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                sg.SmoothingMode = SmoothingMode.AntiAlias;
                sg.DrawImage(src, new Rectangle(0, 0, target, target));
            }
            _brandMark?.Dispose();
            _brandMark = scaled;
            _brandMarkSize = size;
            return _brandMark;
        }
        catch
        {
            return null;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (IsHandleCreated) DoLayout();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 用户点右上角 X：藏到托盘继续后台接收，不退出进程。
        // UserClosing = 点 X / Alt+F4；None = 直接收到 WM_CLOSE（自动化、第三方工具发来的）。
        // 关机（WindowsShutDown）和任务管理器结束（TaskManagerClosing）必须放行，
        // 否则会拖住系统关机。
        bool userInitiated = e.CloseReason is CloseReason.UserClosing or CloseReason.None;
        if (_tray is not null && !_tray.ExitRequested && userInitiated)
        {
            e.Cancel = true;
            _tray.HideToTray();
            return;
        }

        // 走到这里是真要退出了。留一条记录，方便排查「为什么点 X 就退了」这类问题
        // （比如某些远程控制工具发的 WM_CLOSE 会被判定成 TaskManagerClosing）。
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "miccast-close.log"),
                $"{DateTime.Now:HH:mm:ss} EXIT CloseReason={e.CloseReason} tray={(_tray is null ? "NULL" : "ok")} exit={_tray?.ExitRequested}{Environment.NewLine}");
        }
        catch { }

        _uiTimer.Stop();
        try { _tray?.Dispose(); } catch { }
        try { _stateSubscription?.Dispose(); } catch { }
        try { _logSubscription?.Dispose(); } catch { }
        try { _backend.Dispose(); } catch { }
        base.OnFormClosing(e);
    }
}
