using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScreenGuide.DesktopClient;

/// <summary>A view of the coordinator-owned state. This window never starts capture or executes a request.</summary>
public partial class AssistantOrbWindow : Window
{
    private bool _positioned;
    private bool _allowClose;
    private bool _exitRequested;
    private bool _canPauseVoice;

    public event Action? ToggleVoiceRequested;
    public event Action? StopRequestRequested;
    public event Action? DashboardRequested;
    public event Action? MemoryRequested;
    public event Action? ExitRequested;

    public AssistantOrbWindow()
    {
        InitializeComponent();
    }

    public void Render(string status, string detail, bool canStart, bool canStop, bool isSpeaking, bool needsAttention, bool canStopRequest = false)
    {
        Dispatcher.VerifyAccess();
        OrbStatus.Text = string.IsNullOrWhiteSpace(status) ? "语音已暂停" : status.Trim();
        OrbStatus.ToolTip = OrbStatus.Text;
        OrbDetail.Text = string.IsNullOrWhiteSpace(detail) ? "点击小球开始语音，点击管理查看对话和设置。" : detail.Trim();
        OrbDetail.ToolTip = OrbDetail.Text;
        _canPauseVoice = !canStart && canStop;
        var toggleLabel = canStart ? "开始语音" : canStop ? "暂停语音" : "请稍候";
        VoiceToggleLabel.Text = toggleLabel;
        OrbVoiceToggle.IsEnabled = canStart || canStop;
        AutomationProperties.SetName(OrbVoiceToggle, toggleLabel);
        OrbVoiceToggle.ToolTip = canStart
            ? "开始语音：本次打开期间使用麦克风；再次点击暂停"
            : canStop ? "暂停语音，立即关闭麦克风并停止播报（Esc）" : "正在切换语音状态，请稍候";
        OrbStopRequest.IsEnabled = canStopRequest || isSpeaking;
        OrbDashboard.Content = needsAttention ? "查看确认" : "管理";
        AutomationProperties.SetName(OrbDashboard, needsAttention ? "打开管理窗口查看待处理事项" : "打开管理窗口");
        var stateColor = needsAttention ? "#865008" : isSpeaking ? "#185EC3" : _canPauseVoice ? "#087268" : "#234761";
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(stateColor)!;
        OrbStatus.Foreground = brush;
        OrbVoiceToggle.BorderBrush = brush;
        // State is communicated by text and controls. There is deliberately no perpetual animation.
    }

    public void ShowSurface()
    {
        Dispatcher.VerifyAccess();
        if (_allowClose)
        {
            return;
        }

        if (!_positioned)
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 20;
            Top = workArea.Bottom - Height - 20;
            _positioned = true;
        }

        ClampToWorkArea();
        if (!IsVisible)
        {
            // ShowActivated=false keeps the app the user was working in in the foreground.
            Show();
        }
    }

    public void CloseForExit()
    {
        Dispatcher.VerifyAccess();
        _allowClose = true;
        Close();
    }

    private void OrbVoiceToggle_Click(object sender, RoutedEventArgs e) => ToggleVoiceRequested?.Invoke();
    private void OrbStopRequest_Click(object sender, RoutedEventArgs e) => StopRequestRequested?.Invoke();
    private void OrbDashboard_Click(object sender, RoutedEventArgs e) => DashboardRequested?.Invoke();
    private void OrbMemories_Click(object sender, RoutedEventArgs e) => MemoryRequested?.Invoke();
    private void OrbExit_Click(object sender, RoutedEventArgs e) => RequestExit();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (_canPauseVoice)
        {
            ToggleVoiceRequested?.Invoke();
        }
        else if (OrbStopRequest.IsEnabled)
        {
            StopRequestRequested?.Invoke();
        }

        e.Handled = true;
    }

    private void OrbDragHandle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Left += e.HorizontalChange;
        Top += e.VerticalChange;
        ClampToWorkArea();
    }

    private void OrbDragHandle_KeyDown(object sender, KeyEventArgs e)
    {
        var distance = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 20 : 4;
        switch (e.Key)
        {
            case Key.Left: Left -= distance; break;
            case Key.Right: Left += distance; break;
            case Key.Up: Top -= distance; break;
            case Key.Down: Top += distance; break;
            default: return;
        }

        ClampToWorkArea();
        e.Handled = true;
    }

    private void ClampToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Clamp(Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        Top = Math.Clamp(Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        // Dispatch after this Closing event so the owner may safely close us during shutdown.
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(RequestExit));
    }

    private void RequestExit()
    {
        if (_exitRequested)
        {
            return;
        }

        _exitRequested = true;
        ExitRequested?.Invoke();
    }
}
