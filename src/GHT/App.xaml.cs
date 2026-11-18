using System.Windows;
using System.Windows.Threading;
using GHT.Models;
using GHT.Services;

namespace GHT;

public partial class App : Application
{
    private SingleInstance? _singleInstance;

    public AppSettings Settings { get; private set; } = new();

    public LogService Log { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppPaths.EnsureCreated();

        Log = new LogService(AppPaths.LogDirectory);
        Log.Info($"GHT 启动，版本 {typeof(App).Assembly.GetName().Version}");
        Log.Info($"运行环境：{RuntimeGuard.Describe()}");

        // 环境不满足时，任何后续逻辑都可能崩在别处，且症状与真正原因无关。
        // 在创建任何窗口之前说清楚，是唯一能让用户看懂的时刻。
        if (!RuntimeGuard.IsSupported(out var reason))
        {
            Log.Warn($"不支持的运行环境：{reason}");

            MessageBox.Show(
                $"{reason}。\n\n当前环境：\n{RuntimeGuard.Describe()}",
                "GHT 无法在此系统上运行",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
            return;
        }

        _singleInstance = new SingleInstance();

        // 单实例闸门必须早于 base.OnStartup（后者会按 StartupUri 创建窗口）
        if (!_singleInstance.TryAcquire())
        {
            Log.Info("已有实例在运行，唤出已有窗口后退出");
            _singleInstance.SignalExisting();
            Shutdown(0);
            return;
        }

        Settings = SettingsStore.Load(Log);
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnUnhandledException;

        // 上次被强杀会导致系统代理残留指向已关闭的端口 → 这里是唯一的自动修复时机
        if (SystemProxy.Recover())
        {
            Log.Info("检测到上次异常退出残留的系统代理，已还原为用户原设置");
        }

        base.OnStartup(e);

        _singleInstance.StartListening(() => Dispatcher.BeginInvoke(new Action(BringToFront)));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            SystemProxy.Clear();
        }
        catch
        {
            // 退出清理失败不阻断进程退出
        }

        _singleInstance?.Dispose();
        Log?.Info("GHT 退出");

        // 日志改为异步批量落盘，这里必须等队列写完，否则退出原因那一行会丢
        Log?.Dispose();

        base.OnExit(e);
    }

    private void BringToFront()
    {
        if (MainWindow is not MainWindow window)
        {
            return;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("未处理异常", e.Exception);

        MessageBox.Show(
            e.Exception.Message,
            "GHT 发生异常",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }
}
