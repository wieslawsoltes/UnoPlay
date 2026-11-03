using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UnoPlay.Core;
using Windows.Foundation;
using Windows.System;

namespace UnoPlay.ClassicUI;

public sealed partial class ClassicPlayerView
{
    private ClassicWindowFrame? _libraryWindow, _browserWindow;
    private TextBox? _libraryFilter, _address;
    private ListView? _libraryTracks;
    private TextBlock? _libraryInfo, _browserStatus;
    private Grid? _webHost;
#if UNOPLAY_BROWSER
    private Uno.UI.NativeElementHosting.BrowserHtmlElement? _browserFrame;
#else
    private WebView2? _web;
#endif
    private bool _libraryUpdate, _auxActive;
    private readonly List<Uri> _webHistory = [];
    private int _historyIndex = -1;

    private ClassicWindowFrame MakeTool(ClassicWindowStyle style, string title, int width, int height)
    {
        var window = new ClassicWindowFrame(_skin, style, title);
        window.SetSize(Math.Min(width, (int)Math.Max(250, ActualWidth - 16)), Math.Min(height, (int)Math.Max(116, ActualHeight - 16)));
        Canvas.SetLeft(window, Math.Max(0, Math.Round((ActualWidth - window.Width) / 2)));
        Canvas.SetTop(window, Math.Max(0, Math.Round((ActualHeight - window.Height) / 2)));
        window.FocusRequested += (_, _) => FocusTool(window);
        window.CloseRequested += (_, _) =>
        {
            window.Visibility = Visibility.Collapsed;
            ReleaseToolFocus();
            if (ReferenceEquals(window, _spotifyWindow)) _spotify.IsMonitoring = _spotifyActive;
            if (ReferenceEquals(window, _browserWindow))
                StopBrowser();
            Focus(FocusState.Programmatic);
        };
        _desk.Children.Add(window);
        return window;
    }
    private void ReleaseToolFocus()
    {
        if (!_auxActive) return;
        _auxActive = false;
        if (_libraryWindow is not null) { _libraryWindow.SetActive(false); Canvas.SetZIndex(_libraryWindow, 0); }
        if (_browserWindow is not null) { _browserWindow.SetActive(false); Canvas.SetZIndex(_browserWindow, 0); }
        if (_spotifyWindow is not null) { _spotifyWindow.SetActive(false); Canvas.SetZIndex(_spotifyWindow, 0); }
        DrawAll();
    }
    private void FocusTool(ClassicWindowFrame window)
    {
        _auxActive = true;
        if (_videoFocus) SetVideoFocus(false);
        DrawAll();
        if (_libraryWindow is not null)
        {
            _libraryWindow.SetActive(_libraryWindow == window);
            Canvas.SetZIndex(_libraryWindow, _libraryWindow == window ? 9 : 7);
        }
        if (_browserWindow is not null)
        {
            _browserWindow.SetActive(_browserWindow == window);
            Canvas.SetZIndex(_browserWindow, _browserWindow == window ? 9 : 7);
        }
        if (_spotifyWindow is not null)
        {
            _spotifyWindow.SetActive(_spotifyWindow == window);
            Canvas.SetZIndex(_spotifyWindow, _spotifyWindow == window ? 9 : 7);
        }
    }
    private static Button Command(string label, Action invoke)
    {
        var button = new Button { Content = label, MinWidth = 0, MinHeight = 26, FontSize = 12, Padding = new Thickness(7, 2, 7, 2) };
        button.Click += (_, _) => invoke();
        AutomationProperties.SetName(button, label);
        return button;
    }
    private static Grid RowsGrid(params GridLength[] heights)
    {
        var grid = new Grid();
        foreach (var height in heights)
            grid.RowDefinitions.Add(new RowDefinition { Height = height });
        return grid;
    }
    private static void Row(Grid grid, FrameworkElement element, int row)
    {
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }
    private void ShowLibrary()
    {
        if (_libraryWindow is null)
        {
            _libraryWindow = MakeTool(ClassicWindowStyle.General, "Media Library", 430, 330);
            var grid = RowsGrid(GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto);
            grid.Padding = new Thickness(7);
            grid.RowSpacing = 4;
            _libraryFilter = new TextBox { PlaceholderText = "Search titles, artists and locations", MinHeight = 28, FontSize = 13 };
            AutomationProperties.SetName(_libraryFilter, "Search media library");
            _libraryFilter.TextChanged += (_, _) => RefreshLibrary();
            Row(grid, _libraryFilter, 0);
            _libraryTracks = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                IsItemClickEnabled = false,
                DisplayMemberPath = nameof(LibraryEntry.Label),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                FontSize = 12
            };
            _libraryTracks.SelectionChanged += (_, _) => LibrarySelection();
            _libraryTracks.DoubleTapped += (_, _) => _ = Guard(PlayLibraryTrack);
            _libraryTracks.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; _ = Guard(PlayLibraryTrack); } };
            Row(grid, _libraryTracks, 1);
            _libraryInfo = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxLines = 3 };
            Row(grid, _libraryInfo, 2);
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            controls.Children.Add(Command("Play", () => _ = Guard(PlayLibraryTrack)));
            controls.Children.Add(Command("Add files…", () => _ = Guard(() => PickMedia(false))));
            controls.Children.Add(Command("Info", () => _ = Guard(async () =>
            {
                if (_libraryTracks.SelectedItem is LibraryEntry row)
                    await ShowInfo(TitleOf(row.Track), LibraryDetails(row.Track));
            })));
            Row(grid, controls, 3);
            _libraryWindow.Client = grid;
        }
        _libraryWindow.Visibility = Visibility.Visible;
        FocusTool(_libraryWindow);
        RefreshLibrary();
        _libraryFilter!.Focus(FocusState.Programmatic);
    }

    private sealed record LibraryEntry(Track Track, string Label)
    {
        public override string ToString() => Label;
    }
    private void RefreshLibrary()
    {
        if (_libraryWindow?.Visibility != Visibility.Visible || _libraryTracks is null || _libraryFilter is null)
            return;
        string query = _libraryFilter.Text.Trim();
        var selected = (_libraryTracks.SelectedItem as LibraryEntry)?.Track ?? _loaded;
        var entries = Queue.Tracks.Select((t, i) => new LibraryEntry(t, $"{i + 1}. {TitleOf(t)}"))
            .Where(row => row.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Track.Location.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        _libraryUpdate = true;
        try
        {
            _libraryTracks.ItemsSource = entries;
            _libraryTracks.SelectedItem = entries.FirstOrDefault(row => ReferenceEquals(row.Track, selected)) ?? entries.FirstOrDefault();
        }
        finally { _libraryUpdate = false; }
        LibrarySelection();
    }
    private string LibraryDetails(Track t)
    {
        string location = t.Source is UnoPlay.Media.BrowserMediaFile file ? file.Name : t.Location;
        return location + TechnicalInfo(_tags.GetValueOrDefault(t));
    }
    private void LibrarySelection()
    {
        if (_libraryUpdate || _libraryInfo is null)
            return;
        _libraryInfo.Foreground = ClassicSurface.Brush(_skin.Playlist.Normal);
        _libraryInfo.Text = _libraryTracks?.SelectedItem is LibraryEntry row ? LibraryDetails(row.Track) : "No matching tracks";
    }
    private async Task PlayLibraryTrack()
    {
        if (_libraryTracks?.SelectedItem is LibraryEntry row)
            await PlayIndex(IndexOf(row.Track));
    }

    private void ShowMiniBrowser()
    {
        if (_browserWindow is null)
        {
            _browserWindow = MakeTool(ClassicWindowStyle.MiniBrowser, "MiniBrowser", 480, 360);
            _browserWindow.BrowserCommand += (_, command) => BrowserAction(command);
            var grid = RowsGrid(GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto);
            grid.RowSpacing = 4;
            var bar = new Grid { ColumnSpacing = 4, Margin = new Thickness(4, 3, 4, 0) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _address = new TextBox { PlaceholderText = "Enter an http or https address", MinHeight = 26, FontSize = 12 };
            AutomationProperties.SetName(_address, "MiniBrowser address");
            _address.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; _ = Guard(NavigateAddress); } };
            bar.Children.Add(_address);
            var go = Command("Go", () => _ = Guard(NavigateAddress));
            Grid.SetColumn(go, 1);
            bar.Children.Add(go);
            Row(grid, bar, 0);
            _webHost = new Grid { Margin = new Thickness(4, 0, 4, 0) };
            var welcome = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 7 };
            welcome.Children.Add(new TextBlock { Text = "MINIBROWSER", FontSize = 16, Foreground = ClassicSurface.Brush(_skin.Playlist.Current) });
            welcome.Children.Add(new TextBlock { Text = "Open a page by entering its address above.", FontSize = 12, Foreground = ClassicSurface.Brush(_skin.Playlist.Normal) });
            _webHost.Children.Add(welcome);
            Row(grid, _webHost, 1);
            _browserStatus = new TextBlock { Margin = new Thickness(6, 0, 6, 2), Text = "Ready", FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            Row(grid, _browserStatus, 2);
            _browserWindow.Client = grid;
        }
        _browserWindow.Visibility = Visibility.Visible;
        FocusTool(_browserWindow);
        _address!.Focus(FocusState.Programmatic);
    }
    private static bool IsHttp(string? value, out Uri? location)
        => Uri.TryCreate(value, UriKind.Absolute, out location) && location.Scheme is "http" or "https";
    private async Task NavigateAddress()
    {
        if (!IsHttp(_address?.Text.Trim(), out var destination))
        {
            _browserStatus!.Text = "Enter an HTTP or HTTPS address.";
            return;
        }
        var uri = destination!;
        RecordNavigation(uri);
        await NavigateBrowser(uri);
    }
    private void RecordNavigation(Uri uri)
    {
        if (_webHistory.Count == 0 || _webHistory[_historyIndex] != uri)
        {
            if (_historyIndex + 1 < _webHistory.Count)
                _webHistory.RemoveRange(_historyIndex + 1, _webHistory.Count - _historyIndex - 1);
            _webHistory.Add(uri);
            _historyIndex = _webHistory.Count - 1;
        }
        _address!.Text = uri.AbsoluteUri;
    }
    private async Task NavigateBrowser(Uri uri)
    {
        _address!.Text = uri.AbsoluteUri;
        _browserStatus!.Text = "Loading " + uri.Host;
#if UNOPLAY_BROWSER
        if (_browserFrame is null)
        {
            _browserFrame = Uno.UI.NativeElementHosting.BrowserHtmlElement.CreateHtmlElement("iframe");
            _browserFrame.SetHtmlAttribute(("title", "MiniBrowser document"), ("sandbox", "allow-forms allow-scripts allow-presentation"),
                ("referrerpolicy", "no-referrer"));
            _browserFrame.SetCssStyle(("border", "0"), ("width", "100%"), ("height", "100%"), ("background", "white"));
            _browserFrame.RegisterHtmlEventHandler("load", (_, _) => _browserStatus!.Text = "Page loaded");
            _webHost!.Children.Clear();
            _webHost.Children.Add(new ContentControl { Content = _browserFrame,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch });
        }
        _browserFrame.SetHtmlAttribute("src", uri.AbsoluteUri);
        await Task.CompletedTask;
#else
        if (_web is null)
        {
            var web = new WebView2();
            _web = web;
            _webHost!.Children.Clear();
            _webHost.Children.Add(web);
            try
            {
                await web.EnsureCoreWebView2Async();
                if (web.CoreWebView2 is { } core)
                {
                    core.NavigationStarting += (sender, e) =>
                    {
                        if (IsHttp(e.Uri, out var target)) RecordNavigation(target!);
                        else if (e.Uri != "about:blank") e.Cancel = true;
                    };
                    core.NavigationCompleted += (_, e) =>
                    {
                        _browserStatus.Text = e.IsSuccess ? "Page loaded" : "This page could not be displayed. Use the menu to open it externally.";
                    };
                }
            }
            catch { _webHost.Children.Remove(web); _web = null; _browserStatus.Text = "Embedded browsing unavailable. Use the menu to open this address externally."; throw; }
        }
        _web.Source = uri;
#endif
    }

    private void StopBrowser()
    {
#if UNOPLAY_BROWSER
        _browserFrame?.SetHtmlAttribute("src", "about:blank");
#else
        _web?.CoreWebView2?.Stop();
#endif
    }
    private void CloseBrowserHost()
    {
#if UNOPLAY_BROWSER
        _webHost?.Children.Clear();
        _browserFrame?.Dispose();
        _browserFrame = null;
#else
        _web?.Close();
        _web = null;
#endif
    }
    private void BrowserAction(MiniBrowserCommand command)
    {
        if (_browserWindow is null)
            return;
        switch (command)
        {
            case MiniBrowserCommand.Back:
                if (_historyIndex > 0)
                    _ = Guard(() => NavigateBrowser(_webHistory[--_historyIndex]));
                break;
            case MiniBrowserCommand.Forward:
                if (_historyIndex + 1 < _webHistory.Count)
                    _ = Guard(() => NavigateBrowser(_webHistory[++_historyIndex]));
                break;
            case MiniBrowserCommand.Stop:
                StopBrowser();
                if (_browserStatus is not null)
                    _browserStatus.Text = "Stopped";
                break;
            case MiniBrowserCommand.Refresh:
#if UNOPLAY_BROWSER
                if (_historyIndex >= 0) _ = Guard(() => NavigateBrowser(_webHistory[_historyIndex]));
#else
                _web?.CoreWebView2?.Reload();
#endif
                break;
            case MiniBrowserCommand.Menu:
                var menu = new MenuFlyout();
                void Add(string text, Action action)
                {
                    var item = new MenuFlyoutItem { Text = text };
                    item.Click += (_, _) => action();
                    menu.Items.Add(item);
                }
                Add("Open in system browser", () => _ = Guard(async () => { if (IsHttp(_address?.Text, out var address)) await Launcher.LaunchUriAsync(address!); }));
                Add("Play this address", () => _ = Guard(async () => { if (IsHttp(_address?.Text, out var address)) { int first = Queue.Tracks.Count; await AddLocation(address!); if (first < Queue.Tracks.Count) await PlayIndex(first); } }));
                Add("Media library", ShowLibrary);
                Add("Close MiniBrowser", () => _browserWindow.Visibility = Visibility.Collapsed);
                menu.ShowAt(_browserWindow, new Point(70, _browserWindow.Height - 17));
                break;
        }
    }
}
