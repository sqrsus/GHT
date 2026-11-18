namespace GHT.Services;

/// <summary>托盘图标与右键菜单（WinForms NotifyIcon，WPF 自身不提供托盘）。</summary>
public sealed class TrayIconService : IDisposable
{
    /// <summary>Windows 对托盘提示文本的限制约 63 字符，超长会抛异常。</summary>
    private const int MaxTooltipLength = 63;

    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _toggleItem;

    /// <summary>图标本体与它依赖的流（<see cref="System.Drawing.Icon(Stream, System.Drawing.Size)"/> 要求流存活到图标释放）。</summary>
    private readonly System.Drawing.Icon _appIcon;
    private readonly Stream? _iconStream;

    /// <summary>
    /// 图标是否由本类创建。<c>SystemIcons.Application</c> 是进程级共享实例，
    /// 释放它会让同进程其它地方拿到的同名图标一起失效，所以只有自己创建的才收。
    /// </summary>
    private readonly bool _ownsAppIcon;

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

        _appIcon = LoadAppIcon(out _iconStream, out _ownsAppIcon);

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _appIcon,
            Text = "GHT - GitHub 加速",
            Visible = true,
            ContextMenuStrip = menu
        };

        _icon.DoubleClick += (_, _) => onShow();
    }

    /// <summary>
    /// 托盘图标：优先取 exe 内嵌的 Win32 图标，这样任务栏、资源管理器、托盘三处一致。
    /// 开发期用 <c>dotnet run</c> 时 apphost 可能还没有图标，再退回程序集里的 <c>Resources/app.ico</c>。
    /// 两档都失败才用系统默认图标 —— 托盘图标缺失不该影响代理本身。
    /// </summary>
    private static System.Drawing.Icon LoadAppIcon(out Stream? keepAlive, out bool owned)
    {
        keepAlive = null;
        owned = false;

        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var fromExe = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (fromExe is not null)
                {
                    owned = true;
                    return fromExe;
                }
            }
        }
        catch
        {
            // 读不到 exe 图标属正常（未发布、被占用、路径异常），继续走下一档
        }

        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("Resources/app.ico", UriKind.Relative));

            if (resource?.Stream is not null)
            {
                var buffer = new MemoryStream();
                resource.Stream.CopyTo(buffer);
                buffer.Position = 0;

                keepAlive = buffer;
                owned = true;
                return new System.Drawing.Icon(buffer, new System.Drawing.Size(32, 32));
            }
        }
        catch
        {
            // 同上：图标问题不阻断托盘
        }

        return System.Drawing.SystemIcons.Application;
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

        // NotifyIcon 不会释放被赋值的 Icon，这里自己收：否则每次重建托盘都会漏一个图标句柄
        try
        {
            if (_ownsAppIcon)
            {
                _appIcon.Dispose();
            }

            _iconStream?.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
