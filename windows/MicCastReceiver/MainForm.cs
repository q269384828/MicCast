using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using MicCastReceiver.Ui;

namespace MicCastReceiver;

public sealed class MainForm : Form
{
    private readonly Receiver _receiver = new();
    private readonly Discovery _discovery = new();
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

    private readonly Card _logCard = new();
    private readonly Label _logTitle = new();
    private readonly ModernButton _clearLogButton = new();
    private readonly TextBox _logBox = new();

    private readonly System.Windows.Forms.Timer _uiTimer = new();

    private List<Protocol.DiscoveredServer> _serverItems = new();
    private string _selectedIp = string.Empty;
    private bool _syncingServerBox;
    private bool _installing;

    public MainForm() : this(new StartOptions()) { }

    public MainForm(StartOptions options)
    {
        _options = options;

        Theme.Apply(_options.Theme.ToLowerInvariant() switch
        {
            "dark" => AppTheme.Dark,
            "light" => AppTheme.Light,
            _ => Theme.DetectSystemTheme()
        });

        Text = "MicCast — 手机麦克风接收端";
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(S(900), S(724));
        MinimumSize = new Size(S(820), S(650));
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9.5f);
        DoubleBuffered = true;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.TextPrimary;

        BuildControls();
        ApplyTheme();
        LoadDevices();

        _ipBox.Text = _options.Ip.Length > 0 ? _options.Ip : LoadLastIp();
        if (_options.Device >= 0 && _options.Device < _deviceBox.Items.Length)
        {
            _deviceBox.SelectedIndex = _options.Device;
        }
        _pill.Set("未启动", Theme.TextFaint);

        _receiver.Log += OnLog;
        _receiver.StateChanged += () => BeginInvoke(new Action(UpdateState));

        _discovery.Log += OnLog;
        _discovery.Changed += () => BeginInvoke(new Action(RefreshServerList));
        _discovery.Start();

        _uiTimer.Interval = 60;
        _uiTimer.Tick += (_, _) => UpdateStats();
        _uiTimer.Start();

        Shown += async (_, _) =>
        {
            NativeFx.Apply(this, Theme.Current == AppTheme.Dark);
            RefreshBanner();
            DoLayout();
            if (_options.AutoStart)
            {
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
            v.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
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

        Controls.AddRange(new Control[] { _title, _subtitle, _pill, _banner, _deviceCard, _statusCard, _startButton, _logCard });
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

        int statusHeight = _receiver.Connected ? S(132) : S(74);
        _statusCard.SetBounds(pad, y, width, statusHeight);
        LayoutStatusCard();
        y += statusHeight + S(14);

        _startButton.SetBounds(pad, y, S(180), S(44));
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
        _ipBox.SetBounds(x, S(105), S(170), S(34));
    }

    private void LayoutStatusCard()
    {
        int w = _statusCard.Width;
        int left = S(18);
        bool running = _receiver.Connected;

        _hint.Visible = !running;
        _hint.SetBounds(left, S(4), w - S(36), S(64));

        foreach (var c in new Control[] { _fpsValue, _fpsCaption, _kbpsValue, _kbpsCaption, _bufValue, _bufCaption, _level, _serverLine })
        {
            c.Visible = running;
        }

        if (!running) return;

        int columnWidth = (w - left * 2) / 3;
        _fpsValue.SetBounds(left, S(14), columnWidth, S(26));
        _fpsCaption.SetBounds(left, S(40), columnWidth, S(16));
        _kbpsValue.SetBounds(left + columnWidth, S(14), columnWidth, S(26));
        _kbpsCaption.SetBounds(left + columnWidth, S(40), columnWidth, S(16));
        _bufValue.SetBounds(left + columnWidth * 2, S(14), columnWidth, S(26));
        _bufCaption.SetBounds(left + columnWidth * 2, S(40), columnWidth, S(16));

        _level.SetBounds(left, S(62), w - left * 2, S(10));
        _serverLine.SetBounds(left, S(78), w - left * 2, S(18));
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
        var devices = _receiver.GetDevices();
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
        var servers = _discovery.Servers;
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

        if (!_receiver.Connected)
        {
            _pill.Set(_serverItems.Count > 0 ? "已发现手机" : "未启动",
                _serverItems.Count > 0 ? Theme.Accent : Theme.TextFaint);
        }
        UpdateHint();
        DoLayout();
    }

    private void UpdateHint()
    {
        if (_receiver.Connected) return;
        _hint.Text = _receiver.LastError.Length > 0
            ? _receiver.LastError
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
        if (_receiver.Connected)
        {
            await _receiver.StopAsync();
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
            await _receiver.StartAsync(ip, _deviceBox.SelectedIndex);
        }
        finally
        {
            _startButton.Enabled = true;
            UpdateState();
        }
    }

    private void OnLog(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(OnLog), line);
            return;
        }
        _logBox.AppendText(line + Environment.NewLine);
        WriteTrace(line);
    }

    private void WriteTrace(string line)
    {
        if (_options.LogPath.Length == 0) return;
        try { File.AppendAllText(_options.LogPath, line + Environment.NewLine); } catch { }
    }

    private void UpdateState()
    {
        bool running = _receiver.Connected;
        _startButton.Text = running ? "停止接收" : "开始接收";
        _startButton.Kind = running ? ButtonKind.Danger : ButtonKind.Primary;

        if (running)
        {
            _pill.Set("接收中", Theme.Ok);
        }
        else if (_receiver.LastError.Length > 0)
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

        if (!running)
        {
            _hint.Text = _receiver.LastError.Length > 0
                ? _receiver.LastError
                : (_serverItems.Count > 0
                    ? "已发现手机，选好输出设备后点「开始接收」。"
                    : "先在手机上点「启动麦克风服务」，手机会自动出现在上面的列表里。");
            _level.SetLevel(0);
        }
        else
        {
            _serverLine.Text = $"手机 {_receiver.ServerName} v{_receiver.ServerVersion} → 输出 {_receiver.DeviceName}";
        }

        DoLayout();
        Invalidate(true);
    }

    private void UpdateStats()
    {
        if (_receiver.Connected)
        {
            _fpsValue.Text = $"{_receiver.Fps:F0}";
            _kbpsValue.Text = $"{_receiver.Kbps:F0}";
            _bufValue.Text = $"{_receiver.BufferedMs}";

            double level = Math.Clamp(_receiver.Level, 0, 1);
            double scaled = level <= 0.0001 ? 0 : Math.Clamp((20 * Math.Log10(level) + 60) / 60, 0, 1);
            _level.SetLevel((float)scaled);

            if (_options.LogPath.Length > 0 && _receiver.Frames > 0)
            {
                WriteTrace($"STATS frames={_receiver.Frames} fps={_receiver.Fps:F0} kbps={_receiver.Kbps:F0} buffered={_receiver.BufferedMs}ms level={_receiver.Level:F4} device={_receiver.DeviceName}");
            }
        }
        else
        {
            _level.SetLevel(0);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int pad = S(22);
        var mark = new RectangleF(pad, S(18), S(34), S(34));
        using (var path = Theme.RoundRect(mark, S(10)))
        using (var brush = new LinearGradientBrush(
                   new RectangleF(mark.X, mark.Y, mark.Width, mark.Height),
                   Theme.Accent, Theme.Mix(Theme.Accent, Theme.Ok, 0.45), LinearGradientMode.ForwardDiagonal))
        {
            g.FillPath(brush, path);
        }

        float cx = mark.X + mark.Width / 2f;
        float top = mark.Y + mark.Height * 0.22f;
        float bodyW = mark.Width * 0.24f;
        float bodyH = mark.Height * 0.40f;
        using (var white = new SolidBrush(Color.White))
        {
            g.FillEllipse(white, cx - bodyW / 2f, top, bodyW, bodyH);
            using var pen = new Pen(Color.White, S(2))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawArc(pen, cx - bodyW * 1.15f, top + bodyH - bodyH * 0.55f, bodyW * 2.3f, bodyH * 1.1f, 20, 140);
            g.DrawLine(pen, cx, top + bodyH * 1.55f, cx, top + bodyH * 1.95f);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (IsHandleCreated) DoLayout();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _uiTimer.Stop();
        try { _discovery.Dispose(); } catch { }
        try { _receiver.Dispose(); } catch { }
        base.OnFormClosing(e);
    }
}
