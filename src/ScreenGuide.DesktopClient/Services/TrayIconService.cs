using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace ScreenGuide.DesktopClient.Services;

public sealed partial class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly NotificationActivationRouter _activationRouter = new();
    private readonly Forms.ToolStripItem _assistantItem;
    private readonly Forms.ToolStripItem _islandSettingsItem;
    private Icon? _currentIcon;

    public TrayIconService()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开元枢管理", null, (_, _) => ManagementRequested?.Invoke());
        _assistantItem = menu.Items.Add("打开语音助手", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("说出或输入新指令", null, (_, _) => NewTaskRequested?.Invoke());
        menu.Items.Add("当前编程任务", null, (_, _) => CurrentTaskRequested?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("停止当前任务", null, (_, _) => CancelCurrentTaskRequested?.Invoke());
        menu.Items.Add("设置", null, (_, _) => SettingsRequested?.Invoke());
        _islandSettingsItem = menu.Items.Add("灵动岛设置", null, (_, _) => IslandSettingsRequested?.Invoke());
        _islandSettingsItem.Visible = false;
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "元枢",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _notifyIcon.BalloonTipClicked += (_, _) =>
        {
            if (_activationRouter.TryActivate(out var taskId))
            {
                NotificationClicked?.Invoke(taskId);
            }
        };
        UpdateState(TrayVisualState.Idle);
    }

    public event Action? OpenRequested;

    public event Action? ManagementRequested;

    public event Action? IslandSettingsRequested;

    public event Action? NewTaskRequested;

    public event Action? CurrentTaskRequested;

    public event Action? CancelCurrentTaskRequested;

    public event Action? SettingsRequested;

    public event Action? ExitRequested;

    public event Action<Guid>? NotificationClicked;

    public void SetIslandAvailable(bool available)
    {
        _assistantItem.Text = available ? "打开灵动岛" : "打开语音助手";
        _islandSettingsItem.Visible = available;
    }

    public void UpdateState(TrayVisualState state)
    {
        var color = state switch
        {
            TrayVisualState.Working => Color.FromArgb(40, 104, 216),
            TrayVisualState.WaitingForUser => Color.FromArgb(196, 122, 18),
            TrayVisualState.Error => Color.FromArgb(201, 76, 76),
            _ => Color.FromArgb(31, 138, 106)
        };
        var icon = CreateIcon(color);
        _notifyIcon.Icon = icon;
        _currentIcon?.Dispose();
        _currentIcon = icon;
        _notifyIcon.Text = state switch
        {
            TrayVisualState.Working => "元枢 · 编程任务执行中",
            TrayVisualState.WaitingForUser => "元枢 · 等待你的决定",
            TrayVisualState.Error => "元枢 · 需要检查",
            _ => "元枢 · 空闲"
        };
    }

    public void ShowNotification(NotificationCandidate notification)
    {
        _activationRouter.MarkShown(notification.TaskId);
        _notifyIcon.BalloonTipTitle = notification.Title;
        _notifyIcon.BalloonTipText = notification.Message.Length <= 240
            ? notification.Message
            : notification.Message[..240];
        _notifyIcon.BalloonTipIcon = notification.Kind == "Failed"
            ? Forms.ToolTipIcon.Error
            : notification.Kind == "WaitingForUser"
                ? Forms.ToolTipIcon.Warning
                : Forms.ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(6000);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
    }

    private static Icon CreateIcon(Color color)
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/yuanshu-icon.ico", UriKind.Absolute));
        if (resource?.Stream is { } stream)
        {
            using (stream)
            using (var icon = new Icon(stream))
            {
                return (Icon)icon.Clone();
            }
        }

        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            graphics.FillEllipse(brush, 2, 2, 28, 28);
            using var font = new Font("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            graphics.DrawString("Y", font, textBrush, new RectangleF(2, 1, 28, 28), format);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial int DestroyIcon(nint handle);
}
