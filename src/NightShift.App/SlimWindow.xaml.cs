using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NightShift.Core;

namespace NightShift.App;

public partial class SlimWindow : Window
{
    private readonly MainViewModel _view;
    private HwndSource? _source;
    private bool _placing;
    private bool _dragged;
    private Point _dragStart;
    private double _dragLeft;
    private double _dragTop;
    private bool _exiting;
    private SlimPoint? _collapsedPosition;
    public bool IsPanelOpen { get; private set; }

    public SlimWindow(MainViewModel view)
    {
        _view = view; InitializeComponent(); DataContext = view;
        var area = SlimDesktopBounds.InitialArea(this, view.SlimLeft, view.SlimTop);
        var position = SlimPlacement.Clamp(view.SlimLeft, view.SlimTop, Width, Height, area);
        Left = position.Left; Top = position.Top;
        SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source?.AddHook(WindowMessage);
        };
        Loaded += (_, _) => Reposition();
        Closed += (_, _) => { _source?.RemoveHook(WindowMessage); _source = null; };
    }
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x21 && !IsPanelOpen) { handled = true; return 3; } // WM_MOUSEACTIVATE / MA_NOACTIVATE
        if (message is 0x7E or 0x1A or 0x2E0) // display/work-area/DPI changed; let WPF apply DPI first
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Reposition));
        return 0;
    }
    private void Reposition()
    {
        if (_placing || _exiting) return;
        _placing = true;
        try
        {
            var area = SlimDesktopBounds.CurrentArea(this);
            Width = IsPanelOpen ? Math.Min(SlimPlacement.PanelWidth, area.Width) : SlimPlacement.CatSize;
            Height = IsPanelOpen ? Math.Min(SlimPlacement.ExpandedHeight, area.Height) : SlimPlacement.CatSize;
            var position = SlimPlacement.Clamp(Left, Top, Width, Height, area);
            Left = position.Left; Top = position.Top;
        }
        finally { _placing = false; }
    }
    private void SavePosition()
    {
        var anchor = IsPanelOpen && _collapsedPosition is { } saved ? saved : new SlimPoint(Left, Top);
        var position = SlimPlacement.Clamp(anchor.Left, anchor.Top, SlimPlacement.CatSize, SlimPlacement.CatSize, SlimDesktopBounds.CurrentArea(this));
        _view.SaveSlimPosition(position.Left, position.Top);
    }
    private void CatClick(object sender, RoutedEventArgs e)
    {
        if (_dragged) return;
        if (IsPanelOpen) CollapsePanel(); else OpenPanel();
    }
    public void OpenPanel()
    {
        if (IsPanelOpen) return;
        _collapsedPosition = new(Left, Top);
        IsPanelOpen = true; CompactPanel.Visibility = Visibility.Visible; Reposition();
        // Focus changes only after the user explicitly opens the panel.
        Activate(); CodexProvider.Focus();
    }
    public void CollapsePanel()
    {
        if (!IsPanelOpen) return;
        IsPanelOpen = false; Width = SlimPlacement.CatSize; Height = SlimPlacement.CatSize;
        if (_collapsedPosition is { } saved) { Left = saved.Left; Top = saved.Top; _collapsedPosition = null; }
        CompactPanel.Visibility = Visibility.Collapsed;
        Reposition(); SavePosition(); Keyboard.ClearFocus();
    }
    private void CollapseClick(object sender, RoutedEventArgs e) => CollapsePanel();
    private void SlimKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsPanelOpen) { e.Handled = true; CollapsePanel(); }
    }
    private void CatMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragged = false; _dragStart = PointToScreen(e.GetPosition(this));
        _dragLeft = Left; _dragTop = Top;
    }
    private void CatMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var point = PointToScreen(e.GetPosition(this)); var dpi = VisualTreeHelper.GetDpi(this);
        var x = (point.X - _dragStart.X) / dpi.DpiScaleX;
        var y = (point.Y - _dragStart.Y) / dpi.DpiScaleY;
        if (!_dragged && Math.Abs(x) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragged = true; CatButton.CaptureMouse();
        Left = _dragLeft + x; Top = _dragTop + y; Reposition();
        if (IsPanelOpen) _collapsedPosition = new(Left, Top);
        e.Handled = true;
    }
    private void CatMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragged) return;
        CatButton.ReleaseMouseCapture(); Reposition(); SavePosition(); e.Handled = true;
    }
    private async void ProviderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value } || !Enum.TryParse<AgentProvider>(value, out var provider)) return;
        await _view.SwitchProviderAsync(provider);
    }
    private async void ConnectClick(object sender, RoutedEventArgs e) => await _view.RunAsync(_view.IsConnected ? _view.DisconnectAsync : _view.ConnectAsync);
    private async void RefreshClick(object sender, RoutedEventArgs e) => await _view.RunAsync(_view.RefreshLiveAsync);
    private async void LinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SessionRecord record } || !_view.CanRefreshLive) return;
        await _view.RunAsync(async () => { if (!_view.IsConnected) await _view.ConnectAsync(); await _view.ResumeAsync(record); });
    }
    private async void NightClick(object sender, RoutedEventArgs e)
    {
        if (!_view.SlimCanSetNightShift || sender is not CheckBox toggle) return;
        var enabled = toggle.IsChecked == true;
        await _view.RunAsync(() => _view.SetSlimNightShiftAsync(enabled));
    }
    private async void ContinueClick(object sender, RoutedEventArgs e)
    {
        if (!_view.IsReadOnlySession) await _view.RunAsync(_view.ConfirmContinuationAsync);
    }
    private async void StopClick(object sender, RoutedEventArgs e)
    {
        if (!_view.IsReadOnlySession) await _view.StopAsync();
    }
    private async void OpenOriginClick(object sender, RoutedEventArgs e) => await _view.RunAsync(_view.OpenOriginAsync);
    private async void PendingClick(object sender, RoutedEventArgs e)
    {
        var request = _view.PendingRequests.FirstOrDefault(); if (request is null) return;
        var dialog = new RequestWindow(request) { Owner = this };
        if (dialog.ShowDialog() == true) await _view.RunAsync(() => _view.ResolveAsync(request, dialog.Result));
    }
    private void ReturnClick(object sender, RoutedEventArgs e) => Close();
    private void SlimClosing(object? sender, CancelEventArgs e)
    {
        if (!_exiting) SavePosition(); // Full-window close owns disposal; slim close only returns.
    }
    internal void CloseForExit() { _exiting = true; SavePosition(); Close(); }
}
