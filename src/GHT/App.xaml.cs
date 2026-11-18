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
