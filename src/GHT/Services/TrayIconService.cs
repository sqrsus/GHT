namespace GHT.Services;

/// <summary>托盘图标与右键菜单（WinForms NotifyIcon，WPF 自身不提供托盘）。</summary>
public sealed class TrayIconService : IDisposable
{
    /// <summary>Windows 对托盘提示文本的限制约 63 字符，超长会抛异常。</summary>
    private const int MaxTooltipLength = 63;

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

    /// <summary>
    /// 更新托盘状态。<paramref name="speed"/> 传实时吞吐（可为空），
    /// 空的时候只显示运行状态，避免每秒都改一次托盘文本（部分系统会闪）。
    /// </summary>
    public void SetState(bool running, string detail, string? speed = null)
    {
        var text = $"GHT - {(running ? "运行中" : "已停止")} {detail}";
        if (!string.IsNullOrEmpty(speed))
        {
            text = $"{text} {speed}";
        }

        text = text.Trim();
        _icon.Text = text.Length <= MaxTooltipLength ? text : text[..MaxTooltipLength];
        _toggleItem.Text = running ? "停止加速" : "启动加速";
    }

    /// <summary>气泡提示：启动失败、代理残留恢复这类「用户不看主窗口也需要知道」的事件。</summary>
    public void Notify(string title, string message)
    {
        try
        {
            _icon.ShowBalloonTip(4000, title, message, System.Windows.Forms.ToolTipIcon.Info);
        }
        catch
        {
            // 气泡失败不影响主流程
        }
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
