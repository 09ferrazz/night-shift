using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using NightShift.Core;

namespace NightShift.App;

public partial class MainWindow
{
    private bool _appearanceReady;
    private bool _sidebarOpen = true;
    private bool _wasCompact;
    private bool _hadAttachments;
    private bool _appliedDarkTheme;
    private AgentProvider? _appliedProvider;
    private CodexPlatform? _recentPlatform;
    private ListCollectionView? _recentView;
    private string _recentFilter = "";

    private void InitializeAppearance()
    {
        _appearanceReady = true; _appliedDarkTheme = _view.IsDarkTheme;
        _view.PropertyChanged += AppearanceChanged;
        PreviewMouseDown += (_, e) =>
        {
            if (QuotaPanel.IsVisible && e.OriginalSource is System.Windows.Media.Visual source
                && !QuotaPanel.IsAncestorOf(source) && !LimitsButton.IsAncestorOf(source)) CloseLimits();
        };
        Closed += (_, _) => _view.PropertyChanged -= AppearanceChanged;
        AppearanceChanged(this, new PropertyChangedEventArgs(null));
        UpdateSidebarLayout();
    }
    private void AppearanceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_hadAttachments != _view.HasAttachments) { _hadAttachments = _view.HasAttachments; UpdateSidebarLayout(); }
        if (_appliedDarkTheme != _view.IsDarkTheme || _appliedProvider != _view.Provider)
        {
            _appliedDarkTheme = _view.IsDarkTheme; _appliedProvider = _view.Provider; ThemeManager.Apply(_appliedDarkTheme, _view.Provider);
            if (IsLoaded) NativeMethods.DarkTitlebar(new WindowInteropHelper(this).Handle, _appliedDarkTheme);
        }
        if (_recentPlatform != _view.Platform)
        {
            _recentPlatform = _view.Platform;
            _recentView = new ListCollectionView((IList)(_view.IsSharedPlatform ? _view.LiveSessions : _view.RecentSessions));
            _recentView.Filter = RecentMatches; RecentList.ItemsSource = _recentView;
        }
        var filter = _view.Provider + "|" + _view.ProjectPath + "|" + _view.AllProjects + "|" + SidebarSearch.Text;
        if (_recentFilter != filter) { _recentFilter = filter; _recentView?.Refresh(); }
        UpdateRecentEmpty();
    }
    private bool RecentMatches(object item)
    {
        if (item is not SessionRecord record || record.Provider != _view.Provider) return false;
        if (!_view.AllProjects && !string.IsNullOrWhiteSpace(_view.ProjectPath) && !SessionCatalog.SameProject(record.ProjectPath, _view.ProjectPath)) return false;
        var query = SidebarSearch.Text.Trim();
        return query.Length == 0 || record.DisplayTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase) || record.ProjectPath.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }
    private void SidebarSearchChanged(object sender, TextChangedEventArgs e) { _recentView?.Refresh(); UpdateRecentEmpty(); }
    private void UpdateRecentEmpty()
    {
        if (!_appearanceReady || _recentView is null) return;
        RecentEmpty.Visibility = _recentView.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentEmpty.Text = SidebarSearch.Text.Length > 0 ? "Nenhuma conversa encontrada" :
            _view.IsSharedPlatform ? _view.IsConnected ? "Nenhuma sessão ao vivo neste projeto" : "Conecte ao servidor compartilhado" :
            _view.IsConnected ? "Nenhuma conversa neste projeto" : "Conecte para carregar conversas";
    }
    private void WindowSizeChanged(object sender, SizeChangedEventArgs e) { if (_appearanceReady) UpdateSidebarLayout(); }
    private void RootLayoutSizeChanged(object sender, SizeChangedEventArgs e) { if (_appearanceReady) UpdateSidebarLayout(); }
    private void UpdateSidebarLayout()
    {
        if (!_appearanceReady) return;
        var width = RootLayout.ActualWidth > 0 ? RootLayout.ActualWidth : Width;
        var compact = width < 1060;
        if (compact != _wasCompact) { _sidebarOpen = !compact; _wasCompact = compact; }
        SidebarColumn.Width = new GridLength(_sidebarOpen && !compact ? 250 : 0);
        Grid.SetColumnSpan(NavigationPanel, compact ? 2 : 1);
        NavigationPanel.Visibility = _sidebarOpen ? Visibility.Visible : Visibility.Collapsed;
        PageHost.Margin = compact ? new Thickness(18,12,18,10) : new Thickness(28,18,28,14);
        // The star-sized header column truncates the project title without wrapping controls.
        ProjectButton.Width = double.NaN;
        EmptyLogo.Visibility = RootLayout.ActualHeight < 650 ? Visibility.Collapsed : Visibility.Visible;
        Composer.Height = RootLayout.ActualHeight < 650 ? (_view.HasAttachments ? 40 : 60) : 80;
        NoticeScroll.MaxHeight = RootLayout.ActualHeight < 650 ? 28 : 80;
        RequestNotices.MaxHeight = RootLayout.ActualHeight < 650 ? (_view.HasAttachments ? 62 : 92) : 128;
    }
    private async void HeaderConnectionClick(object sender, RoutedEventArgs e)
    {
        if (_view.IsConnected) ShowPage("Integrations");
        else if (_view.CanConnect) await _view.RunAsync(_view.ConnectAsync);
    }
    private async void PrimaryActionClick(object sender, RoutedEventArgs e)
    {
        await SendOrStopAsync(); Composer.Focus();
    }
    private async void NightToggleClick(object sender, RoutedEventArgs e)
    {
        var enabled = NightToggle.IsChecked == true;
        if (!enabled) await _view.SetNightShiftAsync(false);
        else await _view.RunAsync(() => _view.SetNightShiftAsync(true));
    }
    private async void HistoryClick(object sender, RoutedEventArgs e)
    {
        if (_view.IsSharedPlatform) { ShowPage("Live"); if (_view.CanRefreshLive) await _view.RunAsync(_view.RefreshLiveAsync); }
        else { ShowPage("History"); if (_view.CanUseShortcuts) await _view.RunAsync(() => _view.RefreshHistoryAsync()); }
    }
    private void CopyMessageClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string text }) return;
        try { Clipboard.SetText(text); }
        catch (Exception ex) { _view.Notice = "Não foi possível copiar: " + ex.Message; }
    }
    private void ActionsClick(object sender, RoutedEventArgs e) => OpenActions();
    private async void LimitsClick(object sender, RoutedEventArgs e)
    {
        if (QuotaPanel.IsVisible) { CloseLimits(); return; }
        QuotaPanel.Visibility = Visibility.Visible;
        UpdateBackButton();
        await _view.RefreshLimitsAsync();
    }
    private void CloseLimits() { QuotaPanel.Visibility = Visibility.Collapsed; UpdateBackButton(); }
    private void CloseLimitsClick(object sender, RoutedEventArgs e) { CloseLimits(); LimitsButton.Focus(); }
    private async void RefreshLimitsClick(object sender, RoutedEventArgs e) => await _view.RefreshLimitsAsync();
    private void OpenActions()
    {
        CloseLimits();
        var menu = new ContextMenu { PlacementTarget = ActionsButton, Placement = PlacementMode.Bottom };
        void Add(string label, Action action, bool enabled = true, string? shortcut = null)
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled, InputGestureText = shortcut ?? "" };
            item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        Add("Voltar à conversa", () => ShowPage("Session"));
        Add("Nova conversa", () => NewClick(this, new RoutedEventArgs()), _view.CanUseShortcuts);
        Add(_view.IsSharedPlatform ? "Sessões ao vivo" : "Todas as conversas", () => HistoryClick(this, new RoutedEventArgs()));
        Add("Comandos", () => ShowPage("Commands"), shortcut:"F1");
        menu.Items.Add(new Separator());
        Add("Conexão e plataformas", () => ShowPage("Integrations"));
        Add("Atividade e limites", () => ShowPage("Activity"));
        Add("Ajustes", () => ShowPage("Settings"));
        Add("Exportar relatório", () => ExportClick(this, new RoutedEventArgs()));
        Add("Parar", () => StopClick(this, new RoutedEventArgs()), _view.CanStop, "Esc");
        ActionsButton.ContextMenu = menu; menu.IsOpen = true;
    }
}
