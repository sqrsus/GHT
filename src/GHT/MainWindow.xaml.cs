using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GHT.Models;
using GHT.Services;
using GHT.Services.Dns;

namespace GHT;

public partial class MainWindow : Window
{
    private static readonly string[] CloseActionValues = { "Ask", "Tray", "Exit" };

    private readonly AppSettings _settings;
    private readonly LogService _log;
    private readonly ObservableCollection<DnsPoolEntry> _pool = new();
    private readonly DispatcherTimer _statsTimer;
    private readonly TrayIconService _tray;

    private ProxyServer? _proxy;
    private CancellationTokenSource? _scanCancellation;
    private bool _busy;
    private bool _loading;
    private bool _reallyExit;

    public MainWindow()
    {
        InitializeComponent();

        var app = (App)Application.Current;
        _settings = app.Settings;
        _log = app.Log;

        PoolList.ItemsSource = _pool;

        StartStopButton.Click += OnStartStopClick;
        HideButton.Click += OnHideClick;
        SaveWhitelistButton.Click += OnSaveWhitelistClick;
        RestoreDefaultButton.Click += OnRestoreDefaultClick;
        AccelCheck.Checked += OnAccelChanged;
        AccelCheck.Unchecked += OnAccelChanged;
        ScanButton.Click += OnScanClick;
        ClearPoolButton.Click += OnClearPoolClick;
        AutoStartCheck.Checked += OnAutoStartChanged;
        AutoStartCheck.Unchecked += OnAutoStartChanged;
        CloseActionBox.SelectionChanged += OnCloseActionChanged;

        foreach (var box in new[] { RaceMaxIpsBox, BudgetBox, RaceTimeoutBox, RetryRoundsBox, ScanConcurrencyBox, ScanTimeoutBox })
        {
            box.LostFocus += OnTuningLostFocus;
        }

        // 托盘要先建好：LoadSettingsToUi → UpdateRunningUi 会更新托盘提示
        _tray = new TrayIconService(ShowFromTray, OnTrayToggle, RequestExit);

        LoadSettingsToUi();

        Closing += OnClosing;

        _log.LineWritten += OnLogLine;

        foreach (var line in _log.Recent())
        {
            AppendLog(line);
        }

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += OnStatsTick;
        _statsTimer.Start();
    }

    /// <summary>
    /// 把配置灌进界面。全程用 _loading 屏蔽控件事件，
    /// 否则 IsChecked / SelectedIndex 的赋值会触发保存，把还没读出来的字段覆盖成默认值。
    /// </summary>
    private void LoadSettingsToUi()
    {
        _loading = true;

        try
        {
            PortBox.Text = _settings.ProxyPort.ToString(CultureInfo.InvariantCulture);
            WhitelistBox.Text = _settings.GlobalDnsWhitelist;
            AccelCheck.IsChecked = _settings.DnsAccelEnabled;

            ScanDomainBox.Text = string.IsNullOrWhiteSpace(_settings.DnsLastScanDomain)
                ? _settings.DnsPoolDomain
                : _settings.DnsLastScanDomain;

            RaceMaxIpsBox.Text = _settings.RaceMaxIps.ToString(CultureInfo.InvariantCulture);
            BudgetBox.Text = _settings.ConnectBudgetMs.ToString(CultureInfo.InvariantCulture);
            RaceTimeoutBox.Text = _settings.RaceConnectTimeoutMs.ToString(CultureInfo.InvariantCulture);
            RetryRoundsBox.Text = _settings.RaceRetryRounds.ToString(CultureInfo.InvariantCulture);
            ScanConcurrencyBox.Text = _settings.DnsScanConcurrency.ToString(CultureInfo.InvariantCulture);
            ScanTimeoutBox.Text = _settings.DnsScanTimeoutMs.ToString(CultureInfo.InvariantCulture);

            // 自启状态以注册表为准：可能被组策略或用户手工改过，配置里的值不一定可信。
            var autoStart = AutoStart.Get();
            _settings.AutoStart = autoStart;
            AutoStartCheck.IsChecked = autoStart;

            CloseActionBox.ItemsSource = CloseActionValues;
            CloseActionBox.SelectedIndex = Array.IndexOf(CloseActionValues, _settings.CloseActionOrDefault);
        }
        finally
        {
            _loading = false;
        }

        FillPool();
        RefreshStats();
        UpdateRunningUi(running: false);
    }

    // ── 启动/停止 ───────────────────────────────────────────────────────

    private async void OnStartStopClick(object sender, RoutedEventArgs e) => await ToggleProxyAsync();

    private void OnTrayToggle()
    {
        if (Dispatcher.CheckAccess())
        {
            _ = ToggleProxyAsync();
        }
        else
        {
            Dispatcher.BeginInvoke(new Action(() => { _ = ToggleProxyAsync(); }));
        }
    }

    private async Task ToggleProxyAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        StartStopButton.IsEnabled = false;

        try
        {
            if (_proxy is { IsRunning: true })
            {
                var running = _proxy;
                _proxy = null;
                await running.StopAsync().ConfigureAwait(true);
                SystemProxy.Clear();
                UpdateRunningUi(false);
                _log.Info("已停止加速并关闭系统代理");
                return;
            }

            if (!int.TryParse(PortBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                || port < 1024 || port > 65535)
            {
                ShowWarning("端口必须是 1024-65535 之间的整数。");
                return;
            }

            _settings.ProxyPort = port;

            var server = new ProxyServer(_settings, _log);
            try
            {
                await server.StartAsync(port).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _log.Error($"启动代理失败（端口 {port}）", ex);
                ShowWarning($"端口 {port} 无法监听，可能已被占用：{ex.Message}");
                return;
            }

            _proxy = server;
            SystemProxy.Apply(port);
            SettingsStore.Save(_settings, _log);
            UpdateRunningUi(true);
            _log.Info($"已启动加速，系统代理指向 127.0.0.1:{port}");
        }
        catch (Exception ex)
        {
            _log.Error("切换加速状态失败", ex);
            ShowWarning(ex.Message);
        }
        finally
        {
            _busy = false;
            StartStopButton.IsEnabled = true;
        }
    }

    // ── 白名单 ──────────────────────────────────────────────────────────

    private void OnSaveWhitelistClick(object sender, RoutedEventArgs e) => SaveWhitelist(showFeedback: true);

    private void OnAccelChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SaveWhitelist(showFeedback: false);
    }

    private void SaveWhitelist(bool showFeedback)
    {
        _settings.GlobalDnsWhitelist = WhitelistBox.Text ?? string.Empty;
        _settings.DnsAccelEnabled = AccelCheck.IsChecked == true;
        SettingsStore.Save(_settings, _log);
        _proxy?.UpdateSettings(_settings);

        var parsed = DomainWildcardList.Parse(_settings.GlobalDnsWhitelist);

        if (parsed.IsEmpty)
        {
            _log.Warn("白名单为空：所有域名都会直接走系统 DNS");
        }

        if (showFeedback)
        {
            ShowInfo(parsed.IsEmpty
                ? "已保存。白名单为空，全部域名将直接使用系统 DNS。"
                : $"已保存并生效，共 {parsed.Entries.Count} 条规则。");
        }
    }

    private void OnRestoreDefaultClick(object sender, RoutedEventArgs e)
    {
        WhitelistBox.Text = AppSettings.DefaultWhitelist;
        AccelCheck.IsChecked = true;
        SaveWhitelist(showFeedback: true);
    }

    // ── 加速池 ──────────────────────────────────────────────────────────

    private async void OnScanClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var domain = (ScanDomainBox.Text ?? string.Empty).Trim();
        if (domain.Length == 0)
        {
            ShowWarning("请填写用于探测的域名，例如 github.com。");
            return;
        }

        _busy = true;
        ScanButton.IsEnabled = false;
        ScanProgress.Value = 0;
        _scanCancellation = new CancellationTokenSource();

        try
        {
            var scanner = new DnsScanner(_log);
            var progress = new Progress<DnsScanProgress>(value =>
            {
                ScanProgress.Value = value.Total == 0 ? 0 : value.Completed * 100.0 / value.Total;
                PoolStatusText.Text = $"扫描中 {value.Completed}/{value.Total} · 可用 {value.Succeeded}";
            });

            var result = await scanner
                .ScanAsync(domain, _settings.DnsScanConcurrency, _settings.DnsScanTimeoutMs, progress, _scanCancellation.Token)
                .ConfigureAwait(true);

            if (result.Count == 0)
            {
                PoolStatusText.Text = "扫描完成，但没有可用 DNS";
                ShowWarning("没有扫描到能解析该域名的公共 DNS，请换个域名或稍后重试。");
                return;
            }

            var take = Math.Min(result.Count, 32);
            var answer = MessageBox.Show(
                this,
                $"共 {result.Count} 个公共 DNS 可正常解析 {domain}。\n是否把延迟最低的 {take} 个应用到加速池？",
                "GHT",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                PoolStatusText.Text = $"扫描完成，未应用（可用 {result.Count} 个）";
                return;
            }

            _settings.DnsPool = result.Take(take).ToList();
            _settings.DnsPoolDomain = domain;
            _settings.DnsPoolUpdatedAt = DateTimeOffset.Now;
            _settings.DnsLastScanDomain = domain;

            SettingsStore.Save(_settings, _log);
            _proxy?.UpdatePool(_settings.DnsPool);

            FillPool();
            _log.Info($"已应用 {take} 个 DNS 到加速池（目标域名 {domain}）");
        }
        catch (OperationCanceledException)
        {
            PoolStatusText.Text = "扫描已取消";
        }
        catch (Exception ex)
        {
            _log.Error("扫描 DNS 失败", ex);
            ShowWarning(ex.Message);
        }
        finally
        {
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            _busy = false;
            ScanButton.IsEnabled = true;
            ScanProgress.Value = 0;
        }
    }

    private void OnClearPoolClick(object sender, RoutedEventArgs e)
    {
        if (_settings.DnsPool.Count == 0)
        {
            return;
        }

        _settings.DnsPool.Clear();
        _settings.DnsPoolUpdatedAt = null;
        SettingsStore.Save(_settings, _log);
        _proxy?.UpdatePool(_settings.DnsPool);
        FillPool();
        _log.Info("加速池已清空");
    }

    private void FillPool()
    {
        _pool.Clear();

        foreach (var entry in _settings.DnsPool)
        {
            _pool.Add(entry);
        }

        if (_settings.DnsPool.Count == 0)
        {
            PoolStatusText.Text = "加速池为空，请先扫描";
            return;
        }

        var updated = _settings.DnsPoolUpdatedAt?.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "未知";
        PoolStatusText.Text = $"已应用 {_settings.DnsPool.Count} 个 DNS · 目标 {_settings.DnsPoolDomain} · 更新于 {updated}";
    }

    // ── 参数 ────────────────────────────────────────────────────────────

    private void OnTuningLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.RaceMaxIps = ParseInt(RaceMaxIpsBox, _settings.RaceMaxIps);
        _settings.ConnectBudgetMs = ParseInt(BudgetBox, _settings.ConnectBudgetMs);
        _settings.RaceConnectTimeoutMs = ParseInt(RaceTimeoutBox, _settings.RaceConnectTimeoutMs);
        _settings.RaceRetryRounds = ParseInt(RetryRoundsBox, _settings.RaceRetryRounds);
        _settings.DnsScanConcurrency = ParseInt(ScanConcurrencyBox, _settings.DnsScanConcurrency);
        _settings.DnsScanTimeoutMs = ParseInt(ScanTimeoutBox, _settings.DnsScanTimeoutMs);

        SettingsStore.Normalize(_settings);

        RaceMaxIpsBox.Text = _settings.RaceMaxIps.ToString(CultureInfo.InvariantCulture);
        BudgetBox.Text = _settings.ConnectBudgetMs.ToString(CultureInfo.InvariantCulture);
        RaceTimeoutBox.Text = _settings.RaceConnectTimeoutMs.ToString(CultureInfo.InvariantCulture);
        RetryRoundsBox.Text = _settings.RaceRetryRounds.ToString(CultureInfo.InvariantCulture);
        ScanConcurrencyBox.Text = _settings.DnsScanConcurrency.ToString(CultureInfo.InvariantCulture);
        ScanTimeoutBox.Text = _settings.DnsScanTimeoutMs.ToString(CultureInfo.InvariantCulture);

        SettingsStore.Save(_settings, _log);
        _proxy?.UpdateSettings(_settings);
    }

    private static int ParseInt(TextBox box, int fallback) =>
        int.TryParse((box.Text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var enabled = AutoStartCheck.IsChecked == true;
        AutoStart.Set(enabled);
        _settings.AutoStart = enabled;
        SettingsStore.Save(_settings, _log);

        // 读回真实状态（可能被组策略拦掉）
        var actual = AutoStart.Get();
        _loading = true;
        AutoStartCheck.IsChecked = actual;
        _loading = false;
    }

    private void OnCloseActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var index = CloseActionBox.SelectedIndex;
        if (index < 0 || index >= CloseActionValues.Length)
        {
            return;
        }

        _settings.CloseAction = CloseActionValues[index];
        SettingsStore.Save(_settings, _log);
    }

    // ── 生命周期 ────────────────────────────────────────────────────────

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_reallyExit)
        {
            Cleanup();
            Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
            return;
        }

        switch (_settings.CloseActionOrDefault)
        {
            case "Exit":
                _reallyExit = true;
                Cleanup();
                Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
                return;

            case "Tray":
                e.Cancel = true;
                HideToTray();
                return;
        }

        var answer = MessageBox.Show(
            this,
            "关闭窗口后是否继续在托盘中运行？\n\n是：最小化到托盘，代理继续工作\n否：退出程序并关闭系统代理",
            "GHT",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (answer == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (answer == MessageBoxResult.Yes)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _reallyExit = true;
        Cleanup();
        Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
    }

    private void Cleanup()
    {
        _statsTimer.Stop();
        _log.LineWritten -= OnLogLine;

        try
        {
            _scanCancellation?.Cancel();
        }
        catch
        {
            // ignore
        }

        if (_proxy is not null)
        {
            try
            {
                _proxy.StopAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // ignore
            }

            _proxy = null;
        }

        try
        {
            SystemProxy.Clear();
        }
        catch
        {
            // ignore
        }

        _tray.Dispose();
    }

    private void OnHideClick(object sender, RoutedEventArgs e) => HideToTray();

    private void HideToTray()
    {
        Hide();
        _log.Info("窗口已最小化到托盘");
    }

    private void ShowFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ShowFromTray));
            return;
        }

        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void RequestExit()
    {
        void Execute()
        {
            _reallyExit = true;
            Close();
        }

        if (Dispatcher.CheckAccess())
        {
            Execute();
        }
        else
        {
            Dispatcher.BeginInvoke(new Action(Execute));
        }
    }

    // ── 状态刷新 ────────────────────────────────────────────────────────

    private void OnStatsTick(object? sender, EventArgs e) => RefreshStats();

    private void RefreshStats()
    {
        var snapshot = _proxy?.Statistics.Snapshot();

        if (snapshot is not { } stats)
        {
            StatsText.Text = "请求 0 · 缓存命中 0 · 竞速 0 · 隧道 0 · 失败 0";
            return;
        }

        var cacheHits = _proxy?.Resolver.CacheHits ?? 0;

        StatsText.Text =
            $"请求 {stats.Requests} · 缓存命中 {cacheHits} · 竞速 {stats.Races} · 隧道 {stats.Tunnels} · 失败 {stats.Errors}" +
            (string.IsNullOrEmpty(stats.LastWinner) ? string.Empty : $"　|　最近胜出：{stats.LastWinner}");
    }

    private void UpdateRunningUi(bool running)
    {
        StatusDot.Fill = running ? Brushes.ForestGreen : Brushes.Gray;
        StatusText.Text = running ? $"运行中 · 127.0.0.1:{_settings.ProxyPort}" : "未启动";
        StartStopButton.Content = running ? "停止加速" : "启动加速";
        PortBox.IsEnabled = !running;
        _tray.SetState(running, running ? $"127.0.0.1:{_settings.ProxyPort}" : string.Empty);
    }

    private void OnLogLine(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<string>(OnLogLine), line);
            return;
        }

        AppendLog(line);
    }

    private void AppendLog(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);

        if (LogBox.Text.Length > 80_000)
        {
            LogBox.Text = LogBox.Text[^40_000..];
        }

        LogBox.ScrollToEnd();
    }

    private void ShowInfo(string message) =>
        MessageBox.Show(this, message, "GHT", MessageBoxButton.OK, MessageBoxImage.Information);

    private void ShowWarning(string message) =>
        MessageBox.Show(this, message, "GHT", MessageBoxButton.OK, MessageBoxImage.Warning);
}
