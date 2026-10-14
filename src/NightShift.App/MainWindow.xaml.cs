using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NightShift.Core;

namespace NightShift.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _view = new();
    private bool _closing;
    private bool _autoScroll = true;
    private bool _scrollQueued;
    private ScrollViewer? _chatScroll;
    private readonly Stack<string> _pageHistory = new();
    private string _currentPage = "Session";
    public MainWindow()
    {
        _view.ValidateImageAsync = AttachmentImageValidation.ValidateAsync;
        ThemeManager.Apply(_view.IsDarkTheme, _view.Provider);
        InitializeComponent(); DataContext = _view;
        _view.PageRequested += ShowPage; _view.CloseRequested += () => Dispatcher.BeginInvoke(Close);
        MinWidth = Math.Min(760, SystemParameters.WorkArea.Width - 32);
        MinHeight = Math.Min(560, SystemParameters.WorkArea.Height - 32);
        Width = Math.Min(1280, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(820, SystemParameters.WorkArea.Height - 32);
        InitializeAppearance(); InitializeSlim();
        DataObject.AddPastingHandler(Composer, AttachmentPaste);
        Composer.CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, ComposerPasteCommand, ComposerCanPasteCommand));
        _view.ChatUpdated += FollowChatEnd;
        _view.ChatReset += () => { _autoScroll = true; FollowChatEnd(); };
    }
    private void WindowLoaded(object sender, RoutedEventArgs e) { NativeMethods.DarkTitlebar(new WindowInteropHelper(this).Handle, _view.IsDarkTheme); UpdateSidebarLayout(); }
    private void MenuClick(object sender, RoutedEventArgs e) { _sidebarOpen = !_sidebarOpen; UpdateSidebarLayout(); }
    private async void BrowseProjectClick(object sender, RoutedEventArgs e)
    {
        if (!_view.CanChangeProject) { _view.Notice = "Crie uma nova sessão antes de escolher outro projeto."; return; }
        var dialog = new OpenFolderDialog { Title = "Pasta do projeto Unity, Godot ou outro", Multiselect = false };
        if (Directory.Exists(_view.ProjectPath)) dialog.InitialDirectory = _view.ProjectPath;
        if (dialog.ShowDialog(this) == true)
        {
            _view.ProjectPath = dialog.FolderName;
            await _view.RunAsync(_view.RefreshRecentAsync);
        }
    }
    private async void ProjectPathLostFocus(object sender, RoutedEventArgs e)
    {
        if (_view.IsConnected && _view.IsCliPlatform && Directory.Exists(_view.ProjectPath))
            await _view.RunAsync(_view.RefreshRecentAsync);
    }
    private void BrowseCodexClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Selecione o " + _view.ProviderLabel + " instalado", Filter = "Executável|*.exe;*.cmd" };
        if (dialog.ShowDialog(this) == true) _view.AgentPath = dialog.FileName;
    }
    private async void ProviderChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string value } && Enum.TryParse<AgentProvider>(value, out var provider)) await _view.SwitchProviderAsync(provider);
    }
    private async void OpenOriginClick(object sender, RoutedEventArgs e) => await _view.RunAsync(_view.OpenOriginAsync);
    private async void ConnectClick(object sender, RoutedEventArgs e) => await _view.RunAsync(_view.IsConnected ? _view.DisconnectAsync : _view.ConnectAsync);
    private async Task SendOrStopAsync()
    {
        if (!_view.HasAttachments && CodexCommands.Parse(_view.Draft) is { Error: null, Definition.Name: "stop" })
        { await _view.StopAsync(); if (!_view.HasNotice) _view.Draft = ""; }
        else await _view.RunAsync(_view.SendPrimaryAsync);
    }
    private async void SendClick(object sender, RoutedEventArgs e) { await SendOrStopAsync(); Composer.Focus(); }
    private async void ShiftClick(object sender, RoutedEventArgs e) { if (_view.CanStop) await _view.StopAsync(); else await _view.RunAsync(_view.StartShiftAsync); }
    private async void ArmShiftClick(object sender, RoutedEventArgs e) => await _view.RunAsync(() => _view.SetNightShiftAsync(true));
    private async void ContinueClick(object sender, RoutedEventArgs e) => await _view.RunAsync(_view.ConfirmContinuationAsync);
    private async void StopClick(object sender, RoutedEventArgs e) => await _view.StopAsync();
    private async void NewClick(object sender, RoutedEventArgs e) { await _view.RunAsync(_view.NewSessionAsync); ShowPage("Session"); }
    private async void ResumeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SessionRecord record }) return;
        await _view.RunAsync(async () => { if (!_view.IsConnected) await _view.ConnectAsync(); await _view.ResumeAsync(record); ShowPage("Session"); });
    }
    private async void SaveClick(object sender, RoutedEventArgs e) => await _view.RunAsync(() => { _view.Save(); return Task.CompletedTask; });
    private async void LoginClick(object sender, RoutedEventArgs e) => await _view.RunAsync(_view.OpenLoginAsync);
    private async void ResumeLastClick(object sender, RoutedEventArgs e) => await _view.RunAsync(_view.ResumeLastAsync);
    private async void RefreshHistoryClick(object sender, RoutedEventArgs e) => await _view.RunAsync(() => _view.RefreshHistoryAsync());
    private async void MoreHistoryClick(object sender, RoutedEventArgs e) => await _view.RunAsync(() => _view.RefreshHistoryAsync(true));
    private async void CommandClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string command }) return;
        if (CodexCommands.Parse(command) is { Error: null, Definition.Name: "stop" }) { await _view.StopAsync(); return; }
        if (command.EndsWith(' ')) { _view.Draft = command; ShowPage("Session"); Composer.Focus(); Composer.CaretIndex = command.Length; return; }
        await _view.RunAsync(() => _view.RunCommandAsync(command));
    }
    private async void SandboxClick(object sender, RoutedEventArgs e) => await _view.RunAsync(_view.SetupSandboxAsync);
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Salvar relatório", Filter = "Markdown|*.md", FileName = "night-shift-" + DateTime.Now.ToString("yyyy-MM-dd-HHmm") + ".md" };
        if (dialog.ShowDialog(this) == true) { File.WriteAllText(dialog.FileName, _view.Report(), new UTF8Encoding(false)); _view.Notice = "Relatório salvo."; }
    }
    private async void PendingClick(object sender, RoutedEventArgs e)
    {
        var request = _view.PendingRequests.FirstOrDefault(); if (request is null) return;
        var dialog = new RequestWindow(request) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            try { await _view.ResolveAsync(request, dialog.Result); }
            catch (Exception ex) { _view.Notice = ex.Message; }
        }
    }
    private async void NavClick(object sender, RoutedEventArgs e) {
        if (sender is not Button { Tag: string tag }) return; ShowPage(tag);
        if (tag == "History" && _view.CanUseShortcuts) await _view.RunAsync(() => _view.RefreshHistoryAsync());
        if (tag == "Live" && _view.CanRefreshLive) await _view.RunAsync(_view.RefreshLiveAsync);
    }
    private async void LivePageClick(object sender, RoutedEventArgs e) { ShowPage("Live"); if (_view.CanRefreshLive) await _view.RunAsync(_view.RefreshLiveAsync); }
    private async void RefreshLiveClick(object sender, RoutedEventArgs e) => await _view.RunAsync(_view.RefreshLiveAsync);
    private void ShowPage(string name)
    {
        if (name != _currentPage) { _pageHistory.Push(_currentPage); _currentPage = name; }
        RenderPage(name);
    }
    private void BackClick(object sender, RoutedEventArgs e)
    {
        if (QuotaPanel.IsVisible) { CloseLimits(); return; }
        var previous = _pageHistory.Count > 0 ? _pageHistory.Pop() : "Session";
        _currentPage = previous;
        RenderPage(previous);
    }
    private void UpdateBackButton()
    {
        var visibility = _pageHistory.Count > 0 || QuotaPanel.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        if (BackButton.Visibility != visibility) { BackButton.Visibility = visibility; UpdateSidebarLayout(); }
    }
    private void RenderPage(string name)
    {
        CloseLimits();
        if (_wasCompact) { _sidebarOpen = false; UpdateSidebarLayout(); }
        SessionPage.Visibility = name == "Session" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = name == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = name == "History" ? Visibility.Visible : Visibility.Collapsed;
        CommandsPage.Visibility = name == "Commands" ? Visibility.Visible : Visibility.Collapsed;
        IntegrationsPage.Visibility = name == "Integrations" ? Visibility.Visible : Visibility.Collapsed;
        LivePage.Visibility = name == "Live" ? Visibility.Visible : Visibility.Collapsed;
        ActivityPage.Visibility = name == "Activity" ? Visibility.Visible : Visibility.Collapsed;
        SessionNav.FontWeight = name == "Session" ? FontWeights.SemiBold : FontWeights.Normal;
        UpdateBackButton();
    }
    private void UseCliClick(object sender, RoutedEventArgs e) => ShowPage("Session");
    private void InstallHelpClick(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(_view.IsClaudeProvider ? "https://code.claude.com/docs/en/setup" : "https://learn.chatgpt.com/docs/cli") { UseShellExecute = true });
    private void IntegrationHelpClick(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(_view.IsClaudeProvider ? "https://code.claude.com/docs/en/sessions" : "https://learn.chatgpt.com/docs/app-server") { UseShellExecute = true });
    private void DismissClick(object sender, RoutedEventArgs e) => _view.Notice = "";
    private async void ComposerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && _view.CanSend) { e.Handled = true; await SendOrStopAsync(); }
    }
    private async void WindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; OpenActions(); }
        else if (e.Key == Key.Escape && QuotaPanel.IsVisible) { e.Handled = true; CloseLimits(); LimitsButton.Focus(); }
        else if (e.Key == Key.Escape && _view.CanStop) { e.Handled = true; await _view.StopAsync(); }
        else if (e.Key == Key.F1) { e.Handled = true; ShowPage("Commands"); }
    }
    private void ChatScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource != ChatScroll) return;
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0 && e.VerticalChange != 0)
            _autoScroll = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 2;
        JumpToEnd.Visibility = !_autoScroll && e.ExtentHeight > e.ViewportHeight ? Visibility.Visible : Visibility.Collapsed;
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) FollowChatEnd();
    }
    private ScrollViewer? ChatScroll => _chatScroll ??= FindChild<ScrollViewer>(ChatList);
    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private void FollowChatEnd()
    {
        if (!_autoScroll || _scrollQueued) return;
        _scrollQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _scrollQueued = false;
            if (!_autoScroll || _closing) return;
            ChatList.UpdateLayout(); ChatScroll?.ScrollToEnd(); JumpToEnd.Visibility = Visibility.Collapsed;
        }));
    }
    private void ChatMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ChatScroll is not { } scroll || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        _autoScroll = false;
        var distance = SystemParameters.WheelScrollLines < 0 ? scroll.ViewportHeight : Math.Max(1, SystemParameters.WheelScrollLines) * 20;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta / 120.0 * distance); e.Handled = true;
    }
    private void ChatKeyDown(object sender, KeyEventArgs e)
    {
        if (ChatScroll is not { } scroll) return;
        if (e.Key == Key.PageDown) { _autoScroll = false; scroll.PageDown(); }
        else if (e.Key == Key.PageUp) { _autoScroll = false; scroll.PageUp(); }
        else if (e.Key == Key.End && Keyboard.Modifiers == ModifierKeys.Control) { _autoScroll = true; scroll.ScrollToEnd(); }
        else if (e.Key == Key.Home && Keyboard.Modifiers == ModifierKeys.Control) { _autoScroll = false; scroll.ScrollToHome(); }
        else return;
        e.Handled = true;
    }
    private void JumpToEndClick(object sender, RoutedEventArgs e) { _autoScroll = true; FollowChatEnd(); }
    private async void EarlierMessagesClick(object sender, RoutedEventArgs e)
    {
        _autoScroll = false;
        var anchor = ChatList.Items.Count > 0 ? ChatList.Items[0] : null;
        _view.LoadEarlierMessages();
        await Dispatcher.InvokeAsync(() => { ChatList.UpdateLayout(); if (anchor is not null) ChatList.ScrollIntoView(anchor); }, DispatcherPriority.Loaded);
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closing) return; e.Cancel = true; _closing = true; IsEnabled = false;
        // Disposal can finish synchronously. Defer the second Close until this
        // Closing event has returned; WPF rejects a recursive Close here.
        try { CloseSlimForExit(); await _view.DisposeAsync(); } finally { _ = Dispatcher.BeginInvoke(new Action(Close)); }
    }
}
