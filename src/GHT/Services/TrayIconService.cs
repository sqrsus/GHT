namespace GHT.Services;

/// <summary>托盘图标与右键菜单（WinForms NotifyIcon，WPF 自身不提供托盘）。</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _toggleItem;

    public TrayIconService(Action onShow, Action onToggle, Action onExit)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();

        var showItem = new System.Windows.Forms.ToolStripMenuItem("显示主窗口");
        showItem.Click += (_, _) => onShow();

        _toggleItem = new System.Windows.Forms.ToolStripMenuItem("启动加速");
        _toggleItem.Click += (_, _) => onToggle();

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => onExit();

        menu.Items.Add(showItem);
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "GHT - GitHub 加速",
            Visible = true,
            ContextMenuStrip = menu
        };

        _icon.DoubleClick += (_, _) => onShow();
    }

    public void SetState(bool running, string detail)
    {
        var text = $"GHT - {(running ? "运行中" : "已停止")} {detail}".Trim();
        _icon.Text = text.Length <= 63 ? text : text[..63];
        _toggleItem.Text = running ? "停止加速" : "启动加速";
    }

    public void Dispose()
    {
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
