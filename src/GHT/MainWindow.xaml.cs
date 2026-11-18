using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GHT.Models;
using GHT.Services;
using GHT.Services.Diagnostics;
using GHT.Services.Dns;
using GHT.ViewModels;

namespace GHT;

public partial class MainWindow : Window
{
    private static readonly string[] CloseActionValues = { "Ask", "Tray", "Exit" };

    private static readonly string[] LogFilters = { "全部", "常规（非 DEBUG）", "警告以上", "仅错误" };

    /// <summary>界面保留的日志行数上限。日志文件本身另有轮转策略。</summary>
    private const int MaxLogLines = 2000;

    /// <summary>单次扫描最多应用多少台 DNS。多了收益递减，反而拉长首连聚合窗口。</summary>
    private const int MaxAppliedServers = 32;

    private readonly AppSettings _settings;
    private readonly LogService _log;
    private readonly ObservableCollection<DnsPoolRow> _poolRows = new();
    private readonly List<string> _logLines = new();
    private readonly DispatcherTimer _statsTimer;
    private readonly TrayIconService _tray;

    private ProxyServer? _proxy;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _diagnoseCancellation;
    private List<DnsPoolEntry>? _scanCandidates;
    private ProxyStatsSnapshot? _lastSnapshot;
    private DateTime _lastStatsAt = DateTime.UtcNow;
    private DateTime _lastTrayUpdate = DateTime.MinValue;
    private int _logFilterIndex;
    private bool _busy;
    private bool _loading;
    private bool _reallyExit;

    public MainWindow()
    {
        InitializeComponent();

        var app = (App)Application.Current;
        _settings = app.Settings;
        _log = app.Log;

        PoolList.ItemsSource = _poolRows;

        StartStopButton.Click += OnStartStopClick;
        HideButton.Click += OnHideClick;
        SaveWhitelistButton.Click += OnSaveWhitelistClick;
        RestoreDefaultButton.Click += OnRestoreDefaultClick;
        AccelCheck.Checked += OnAccelChanged;
        AccelCheck.Unchecked += OnAccelChanged;
        ScanButton.Click += OnScanClick;
        ApplyScanButton.Click += OnApplyScanClick;
        CancelScanButton.Click += OnCancelScanClick;
        RemovePoolButton.Click += OnRemovePoolClick;
        ClearPoolButton.Click += OnClearPoolClick;
        DiagnoseButton.Click += OnDiagnoseClick;
        CopyReportButton.Click += OnCopyReportClick;
        OpenLogDirButton.Click += OnOpenLogDirClick;
        RestoreTuningButton.Click += OnRestoreTuningClick;
        LogFilterBox.SelectionChanged += OnLogFilterChanged;
        AutoStartCheck.Checked += OnAutoStartChanged;
        AutoStartCheck.Unchecked += OnAutoStartChanged;
        FallbackCheck.Checked += OnFallbackChanged;
        FallbackCheck.Unchecked += OnFallbackChanged;
        CloseActionBox.SelectionChanged += OnCloseActionChanged;

        foreach (var box in new[] { RaceMaxIpsBox, BudgetBox, RaceTimeoutBox, RetryRoundsBox, ScanConcurrencyBox, ScanTimeoutBox })
        {
            box.LostFocus += OnTuningLostFocus;
        }

        // 托盘要先建好：LoadSettingsToUi → UpdateRunningUi 会更新托盘提示
        _tray = new TrayIconService(ShowFromTray, OnTrayToggle, RequestExit);

        LogFilterBox.ItemsSource = LogFilters;
        LogFilterBox.SelectedIndex = 0;

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

            DiagDomainBox.Text = string.IsNullOrWhiteSpace(_settings.DnsPoolDomain)
                ? "github.com"
                : _settings.DnsPoolDomain;

            RaceMaxIpsBox.Text = _settings.RaceMaxIps.ToString(CultureInfo.InvariantCulture);
            BudgetBox.Text = _settings.ConnectBudgetMs.ToString(CultureInfo.InvariantCulture);
            RaceTimeoutBox.Text = _settings.RaceConnectTimeoutMs.ToString(CultureInfo.InvariantCulture);
            RetryRoundsBox.Text = _settings.RaceRetryRounds.ToString(CultureInfo.InvariantCulture);
            ScanConcurrencyBox.Text = _settings.DnsScanConcurrency.ToString(CultureInfo.InvariantCulture);
            ScanTimeoutBox.Text = _settings.DnsScanTimeoutMs.ToString(CultureInfo.InvariantCulture);
            FallbackCheck.IsChecked = _settings.FallbackOnCandidatesFailed;

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
                _lastSnapshot = null;
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
            _lastSnapshot = null;
            _lastStatsAt = DateTime.UtcNow;
            SystemProxy.Apply(port);
            SettingsStore.Save(_settings, _log);
            UpdateRunningUi(true);
            _log.Info($"已启动加速，系统代理指向 127.0.0.1:{port}");

            if (_settings.DnsPool.Count == 0)
            {
                _tray.Notify("加速池为空", "当前所有域名都会直接走系统 DNS。请先扫描并应用加速池。");
            }
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

        var saved = SettingsStore.Save(_settings, _log);
        _proxy?.UpdateSettings(_settings);

        var parsed = DomainWildcardList.Parse(_settings.GlobalDnsWhitelist);

        if (parsed.IsEmpty)
        {
            _log.Warn("白名单为空：所有域名都会直接走系统 DNS");
        }

        if (showFeedback)
        {
            if (!saved)
            {
                ShowWarning("配置保存失败：请检查磁盘空间或目录权限，详细原因见日志。");
                return;
            }

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
        CancelScanButton.IsEnabled = true;
        ApplyScanButton.IsEnabled = false;
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

            // 扫描结果先进表格（候选态），由用户点「应用结果」再落配置 ——
            // 旧实现在这里弹一次模态二选一，既打断操作，也让「看一眼结果」变成「必须做决定」。
            _scanCandidates = result;
            ShowPoolRows(result.Take(64));
            ApplyScanButton.IsEnabled = true;
            PoolStatusText.Text = $"扫描完成：{result.Count} 个可用 · 未应用（点「应用结果」写入加速池）";
            _log.Info($"扫描完成：{result.Count} 个 DNS 可解析 {domain}，等待用户应用");
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
            CancelScanButton.IsEnabled = false;
            ScanProgress.Value = 0;
        }
    }

    private void OnCancelScanClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _scanCancellation?.Cancel();
        }
        catch
        {
            // ignore
        }
    }

    private void OnApplyScanClick(object sender, RoutedEventArgs e)
    {
        if (_scanCandidates is not { Count: > 0 } candidates)
        {
            return;
        }

        var take = Math.Min(candidates.Count, MaxAppliedServers);
        var domain = (_settings.DnsLastScanDomain = (ScanDomainBox.Text ?? string.Empty).Trim());

        _settings.DnsPool = candidates.Take(take).ToList();
        _settings.DnsPoolDomain = domain;
        _settings.DnsPoolUpdatedAt = DateTimeOffset.Now;

        if (!SettingsStore.Save(_settings, _log))
        {
            ShowWarning("加速池保存失败：请检查磁盘空间或目录权限，详细原因见日志。");
        }

        _proxy?.UpdatePool(_settings.DnsPool);
        _scanCandidates = null;
        ApplyScanButton.IsEnabled = false;

        FillPool();
        _log.Info($"已应用 {take} 个 DNS 到加速池（目标域名 {domain}）");
    }

    private void OnRemovePoolClick(object sender, RoutedEventArgs e)
    {
        if (PoolList.SelectedItem is not DnsPoolRow row)
        {
            ShowWarning("请先在列表里选中一行。");
            return;
        }

        if (_scanCandidates is { } candidates)
        {
            candidates.RemoveAll(c => string.Equals(c.Ip, row.Ip, StringComparison.OrdinalIgnoreCase));
            ShowPoolRows(candidates.Take(64));
            ApplyScanButton.IsEnabled = candidates.Count > 0;
            PoolStatusText.Text = $"扫描结果剩余 {candidates.Count} 个 · 未应用";
            return;
        }

        var removed = _settings.DnsPool.RemoveAll(entry => string.Equals(entry.Ip, row.Ip, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            return;
        }

        SettingsStore.Save(_settings, _log);
        _proxy?.UpdatePool(_settings.DnsPool);
        FillPool();
        _log.Info($"已从加速池移除 {row.Ip}");
    }

    private void OnClearPoolClick(object sender, RoutedEventArgs e)
    {
        if (_scanCandidates is { } candidates)
        {
            candidates.Clear();
            _scanCandidates = null;
            ApplyScanButton.IsEnabled = false;
        }

        if (_settings.DnsPool.Count == 0)
        {
            FillPool();
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
        ShowPoolRows(_settings.DnsPool);

        if (_settings.DnsPool.Count == 0)
        {
            PoolStatusText.Text = "加速池为空，请先扫描";
            return;
        }

        var updated = _settings.DnsPoolUpdatedAt?.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "未知";
        PoolStatusText.Text = $"已应用 {_settings.DnsPool.Count} 个 DNS · 目标 {_settings.DnsPoolDomain} · 更新于 {updated}";
    }

    private void ShowPoolRows(IEnumerable<DnsPoolEntry> entries)
    {
        _poolRows.Clear();

        foreach (var entry in entries)
        {
            _poolRows.Add(new DnsPoolRow(entry));
        }

        RefreshPoolHealth();
    }

    /// <summary>把运行期健康度刷到表格上。只在代理运行时才有意义，未运行时统一显示「待测」。</summary>
    private void RefreshPoolHealth()
    {
        if (_proxy is not { } proxy)
        {
            return;
        }

        foreach (var row in _poolRows)
        {
            var (ok, fail) = proxy.Resolver.GetServerHealth(row.Ip);
            row.UpdateHealth(ok, fail);
        }
    }

    // ── 域名诊断 ────────────────────────────────────────────────────────

    private async void OnDiagnoseClick(object sender, RoutedEventArgs e)
    {
        var domain = (DiagDomainBox.Text ?? string.Empty).Trim();
        if (domain.Length == 0)
        {
            ShowWarning("请填写要诊断的域名。");
            return;
        }

        if (!int.TryParse((DiagPortBox.Text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || port < 1 || port > 65535)
        {
            ShowWarning("端口必须是 1-65535 之间的整数。");
            return;
        }

        DiagnoseButton.IsEnabled = false;
        DiagReportBox.Text = $"正在诊断 {domain}:{port} …";
        _diagnoseCancellation = new CancellationTokenSource();

        try
        {
            var service = new DiagnosticsService(_log);
            var report = await service
                .RunAsync(domain, port, _settings, _diagnoseCancellation.Token)
                .ConfigureAwait(true);

            DiagReportBox.Text = report.ToText();
            _log.Info($"诊断完成：{domain} → {report.Verdict}");
        }
        catch (OperationCanceledException)
        {
            DiagReportBox.Text = "诊断已取消。";
        }
        catch (Exception ex)
        {
            _log.Error("域名诊断失败", ex);
            DiagReportBox.Text = $"诊断失败：{ex.Message}";
        }
        finally
        {
            _diagnoseCancellation?.Dispose();
            _diagnoseCancellation = null;
            DiagnoseButton.IsEnabled = true;
        }
    }

    private void OnCopyReportClick(object sender, RoutedEventArgs e)
    {
        var text = DiagReportBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
            ShowInfo("诊断报告已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            ShowWarning($"复制失败：{ex.Message}");
        }
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

        RefreshTuningBoxes();

        SettingsStore.Save(_settings, _log);
        _proxy?.UpdateSettings(_settings);
    }

    private void OnRestoreTuningClick(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();

        _settings.RaceMaxIps = defaults.RaceMaxIps;
        _settings.ConnectBudgetMs = defaults.ConnectBudgetMs;
        _settings.RaceConnectTimeoutMs = defaults.RaceConnectTimeoutMs;
        _settings.RaceRetryRounds = defaults.RaceRetryRounds;
        _settings.RaceRetryDelayMs = defaults.RaceRetryDelayMs;
        _settings.DnsScanConcurrency = defaults.DnsScanConcurrency;
        _settings.DnsScanTimeoutMs = defaults.DnsScanTimeoutMs;
        _settings.PoolResolveConcurrency = defaults.PoolResolveConcurrency;
        _settings.FirstBatchCollectMs = defaults.FirstBatchCollectMs;
        _settings.FirstBatchMinIps = defaults.FirstBatchMinIps;
        _settings.DnsCacheMinTtlSec = defaults.DnsCacheMinTtlSec;
        _settings.DnsCacheMaxTtlSec = defaults.DnsCacheMaxTtlSec;
        _settings.RaceBatchBudgetMs = defaults.RaceBatchBudgetMs;
        _settings.PreferIpv4 = defaults.PreferIpv4;
        _settings.FallbackOnCandidatesFailed = defaults.FallbackOnCandidatesFailed;

        SettingsStore.Normalize(_settings);
        RefreshTuningBoxes();
        SettingsStore.Save(_settings, _log);
        _proxy?.UpdateSettings(_settings);
        ShowInfo("参数已恢复为默认值。");
    }

    private void RefreshTuningBoxes()
    {
        RaceMaxIpsBox.Text = _settings.RaceMaxIps.ToString(CultureInfo.InvariantCulture);
        BudgetBox.Text = _settings.ConnectBudgetMs.ToString(CultureInfo.InvariantCulture);
        RaceTimeoutBox.Text = _settings.RaceConnectTimeoutMs.ToString(CultureInfo.InvariantCulture);
        RetryRoundsBox.Text = _settings.RaceRetryRounds.ToString(CultureInfo.InvariantCulture);
        ScanConcurrencyBox.Text = _settings.DnsScanConcurrency.ToString(CultureInfo.InvariantCulture);
        ScanTimeoutBox.Text = _settings.DnsScanTimeoutMs.ToString(CultureInfo.InvariantCulture);
        FallbackCheck.IsChecked = _settings.FallbackOnCandidatesFailed;
    }

    private void OnFallbackChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.FallbackOnCandidatesFailed = FallbackCheck.IsChecked == true;
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
            _diagnoseCancellation?.Cancel();
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

        // 窗口不可见时没必要每秒算统计、拼字符串、刷表格
        _statsTimer.Stop();
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

        _lastSnapshot = null;
        _statsTimer.Start();
        RefreshStats();
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
            StatsText.Text = "未启动";
            _lastSnapshot = null;
            return;
        }

        var now = DateTime.UtcNow;
        double upRate = 0;
        double downRate = 0;

        if (_lastSnapshot is { } previous)
        {
            var seconds = (now - _lastStatsAt).TotalSeconds;
            if (seconds > 0.1)
            {
                upRate = Math.Max(0, (stats.BytesUp - previous.BytesUp) / seconds);
                downRate = Math.Max(0, (stats.BytesDown - previous.BytesDown) / seconds);
            }
        }

        _lastSnapshot = stats;
        _lastStatsAt = now;

        var cacheHits = _proxy?.Resolver.CacheHits ?? 0;

        var text =
            $"请求 {stats.Requests} · 池胜 {stats.PoolWins} · 池败 {stats.PoolLosses} · 直连 {stats.DirectConnects}" +
            $" · 缓存 {cacheHits} · 隧道 {stats.Tunnels}（活跃 {stats.ActiveTunnels}） · 失败 {stats.Errors}" +
            $"　|　↑ {FormatRate(upRate)}　↓ {FormatRate(downRate)}" +
            $" · 首连 P50 {stats.ConnectP50Ms}ms / P95 {stats.ConnectP95Ms}ms";

        text += Environment.NewLine + BuildFallbackLine(stats);

        if (!string.IsNullOrEmpty(stats.LastWinner))
        {
            text += $"　|　最近胜出：{stats.LastWinner}";
        }

        if (!string.IsNullOrEmpty(stats.LastFailure))
        {
            text += $"　|　最近失败：{stats.LastFailure}";
        }

        StatsText.Text = text;

        RefreshPoolHealth();
        UpdateTraySpeed(upRate, downRate);
    }

    /// <summary>
    /// 回落要按原因拆开展示：只显示一个总数的话，「预算太紧」和「池子对该域名没答案」
    /// 看起来一模一样，而两者的修法完全相反（前者调预算，后者换池子）。
    /// 后面跟的「系统 DNS 成功 N/M」决定该不该继续保留回落策略。
    /// </summary>
    private static string BuildFallbackLine(ProxyStatsSnapshot stats)
    {
        var fallbacks = stats.Fallbacks;

        if (fallbacks == 0)
        {
            return "回落 0（尚未发生系统 DNS 回落）";
        }

        var attempted = stats.FallbackDirectOk + stats.FallbackDirectFail;
        var rate = stats.FallbackSuccessRate * 100;

        return $"回落 {fallbacks} = 预算 {stats.FallbackBudget} / 空池 {stats.FallbackPoolEmpty}" +
               $" / 无答案 {stats.FallbackNoAnswer} / 候选耗尽 {stats.FallbackCandidates}" +
               $"　→ 系统 DNS 成功 {stats.FallbackDirectOk}/{attempted}（{rate:0}%）均 {stats.FallbackDirectAvgMs}ms";
    }

    private void UpdateTraySpeed(double upRate, double downRate)
    {
        // 托盘文本更新太频繁在部分系统上会闪；5 秒一次足够表达「有没有在跑流量」
        if ((DateTime.UtcNow - _lastTrayUpdate).TotalSeconds < 5)
        {
            return;
        }

        _lastTrayUpdate = DateTime.UtcNow;
        _tray.SetState(true, $"127.0.0.1:{_settings.ProxyPort}", $"↑{FormatRate(upRate)} ↓{FormatRate(downRate)}");
    }

    private static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond < 1024)
        {
            return $"{bytesPerSecond:0} B/s";
        }

        if (bytesPerSecond < 1024 * 1024)
        {
            return $"{bytesPerSecond / 1024:0.0} KB/s";
        }

        return $"{bytesPerSecond / (1024 * 1024):0.0} MB/s";
    }

    private void UpdateRunningUi(bool running)
    {
        StatusDot.Fill = running ? Brushes.ForestGreen : Brushes.Gray;
        StatusText.Text = running ? $"运行中 · 127.0.0.1:{_settings.ProxyPort}" : "未启动";
        StartStopButton.Content = running ? "停止加速" : "启动加速";
        PortBox.IsEnabled = !running;
        _lastTrayUpdate = DateTime.MinValue;
        _tray.SetState(running, running ? $"127.0.0.1:{_settings.ProxyPort}" : string.Empty);
    }

    // ── 日志 ────────────────────────────────────────────────────────────

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
        _logLines.Add(line);

        if (_logLines.Count > MaxLogLines)
        {
            _logLines.RemoveRange(0, MaxLogLines / 4);
        }

        if (!MatchesFilter(line))
        {
            return;
        }

        LogBox.AppendText(line + Environment.NewLine);

        if (LogBox.Text.Length > 80_000)
        {
            LogBox.Text = LogBox.Text[^40_000..];
        }

        if (AutoScrollCheck.IsChecked == true)
        {
            LogBox.ScrollToEnd();
        }
    }

    private void OnLogFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        _logFilterIndex = Math.Clamp(LogFilterBox.SelectedIndex, 0, LogFilters.Length - 1);
        RenderLog();
    }

    private void RenderLog()
    {
        var visible = _logLines.Where(MatchesFilter).ToArray();

        LogBox.Text = visible.Length == 0
            ? string.Empty
            : string.Join(Environment.NewLine, visible) + Environment.NewLine;

        if (AutoScrollCheck.IsChecked == true)
        {
            LogBox.ScrollToEnd();
        }
    }

    private bool MatchesFilter(string line)
    {
        switch (_logFilterIndex)
        {
            case 1:
                return !line.Contains("[DEBUG]", StringComparison.Ordinal);
            case 2:
                return line.Contains("[WARN]", StringComparison.Ordinal)
                       || line.Contains("[ERROR]", StringComparison.Ordinal);
            case 3:
                return line.Contains("[ERROR]", StringComparison.Ordinal);
            default:
                return true;
        }
    }

    private void OnOpenLogDirClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.LogDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowWarning($"打开日志目录失败：{ex.Message}");
        }
    }

    private void ShowInfo(string message) =>
        MessageBox.Show(this, message, "GHT", MessageBoxButton.OK, MessageBoxImage.Information);

    private void ShowWarning(string message) =>
        MessageBox.Show(this, message, "GHT", MessageBoxButton.OK, MessageBoxImage.Warning);
}
