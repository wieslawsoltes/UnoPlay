using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using UnoPlay.Spotify;
using Windows.System;

namespace UnoPlay.ClassicUI;

public sealed partial class ClassicPlayerView
{
    private enum SpotifyBrowseMode { Playlists, Playlist, Liked, Search, Album, Queue }
    private sealed record SpotifyBrowseRow(string Label, int Position, SpotifyTrack? Track = null,
        SpotifyPlaylist? Playlist = null, SpotifyAlbum? Album = null, SpotifyArtist? Artist = null)
    {
        public string? Uri => Track?.Uri ?? Playlist?.Uri ?? Album?.Uri ?? Artist?.Uri;
        public string? ExternalUrl => Track?.ExternalUrl ?? Playlist?.ExternalUrl ?? Album?.ExternalUrl ?? Artist?.ExternalUrl;
        public override string ToString() => Label;
    }
    private sealed record SpotifyDeviceRow(SpotifyDevice Device)
    {
        public override string ToString() => Device.Name + (Device.IsActive ? " · active" : "") + (Device.IsRestricted ? " · restricted" : "");
    }

    private ClassicWindowFrame? _spotifyWindow;
    private StackPanel? _spotifySetup, _spotifySignedIn;
    private Grid? _spotifyContent;
    private TextBox? _spotifyClientId, _spotifySearch;
    private TextBlock? _spotifyAccount, _spotifyStatus, _spotifyBrowseTitle, _spotifyPageInfo, _spotifyTrackDetails;
    private ListView? _spotifyItems;
    private ComboBox? _spotifyDevices, _spotifySearchType, _spotifyRepeat;
    private CheckBox? _spotifyShuffle;
    private Image? _spotifyArtwork;
    private HyperlinkButton? _spotifyTrackLink;
    private Slider? _spotifySeek, _spotifyVolume;
    private Button? _spotifyPreviousPage, _spotifyNextPage, _spotifyPlaySelected, _spotifyActions, _spotifyPlaylistActions;
    private Button? _spotifyPlayPause, _spotifyPrevious, _spotifyNext, _spotifyTransfer, _spotifyConnect;
    private readonly CancellationTokenSource _spotifyUiLifetime = new();
    private CancellationTokenSource? _spotifyBrowseCancellation;
    private SpotifyBrowseMode _spotifyBrowseMode;
    private SpotifyPlaylist? _spotifyCurrentPlaylist;
    private SpotifyAlbum? _spotifyCurrentAlbum;
    private string _spotifySearchTerm = "", _spotifySearchKind = "Tracks";
    private string? _spotifyLoadedAccount, _spotifyUiMessage, _spotifyArtworkUrl;
    private int _spotifyOffset, _spotifyTotal, _spotifyPageSize = 50, _spotifyBrowseGeneration, _spotifyBusy;
    private bool _spotifyHasMore, _spotifyUpdatingTransport, _spotifySeekPending, _spotifyVolumePending;

    private void ShowSpotify()
    {
        if (_spotifyWindow is null)
            BuildSpotifyWindow();
        _spotifyWindow!.Visibility = Visibility.Visible;
        _spotify.IsMonitoring = true;
        FocusTool(_spotifyWindow);
        UpdateSpotifyView();
    }

    private void BuildSpotifyWindow()
    {
        _spotifyWindow = MakeTool(ClassicWindowStyle.General, "Spotify", 690, 650);
        var layout = new StackPanel { Spacing = 6, Margin = new Thickness(8) };
        var header = new Grid { ColumnSpacing = 10, Background = ClassicSurface.Brush(0xff121212), Padding = new Thickness(4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var logo = new HyperlinkButton
        {
            NavigateUri = new Uri("https://open.spotify.com/"), Padding = new Thickness(0),
            Content = new Image { Source = new BitmapImage(new Uri("ms-appx:///Assets/spotify-logo.png")), Width = 94, Height = 28, Stretch = Stretch.Uniform }
        };
        AutomationProperties.SetName(logo, "Open Spotify");
        header.Children.Add(logo);
        _spotifyAccount = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_spotifyAccount, 1);
        header.Children.Add(_spotifyAccount);
        layout.Children.Add(header);

        _spotifySetup = new StackPanel { Spacing = 5 };
        _spotifySetup.Children.Add(new TextBlock
        {
            Text = "Connect your Spotify account to browse your library and playlists. Playback requires Spotify Premium.",
            FontSize = 12, TextWrapping = TextWrapping.Wrap
        });
        _spotifyClientId = new TextBox { Header = "Spotify app client ID", PlaceholderText = "Public client ID from Spotify for Developers", MaxLength = 32, FontSize = 12 };
        AutomationProperties.SetName(_spotifyClientId, "Spotify client ID");
        _spotifySetup.Children.Add(_spotifyClientId);
        var setupActions = SpotifyBar();
        setupActions.Children.Add(SpotifyButton("Sign in with Spotify", () => _spotify.SignInAsync(_spotifyClientId.Text.Trim())));
        setupActions.Children.Add(Command("Setup instructions", () => _ = RunSpotify(SpotifySetupInstructions)));
        _spotifySetup.Children.Add(SpotifyScrollBar(setupActions));
        layout.Children.Add(_spotifySetup);

        _spotifySignedIn = new StackPanel { Spacing = 5 };
        var accountActions = SpotifyBar();
        _spotifyConnect = SpotifyButton("Use this browser", async () => { await _spotify.ConnectBrowserAsync(); await RefreshSpotifyDevices(); });
        accountActions.Children.Add(_spotifyConnect);
        accountActions.Children.Add(SpotifyButton("Refresh devices", RefreshSpotifyDevices));
        accountActions.Children.Add(SpotifyButton("Sign out", async () => { CancelSpotifyBrowse(); await _spotify.SignOutAsync(); UpdateSpotifyView(); }));
        accountActions.Children.Add(Command("Account access", () => _ = Guard(() => OpenSpotifyWeb("https://www.spotify.com/account/apps/"))));
        _spotifySignedIn.Children.Add(SpotifyScrollBar(accountActions));
        var deviceLine = new Grid { ColumnSpacing = 5 };
        deviceLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        deviceLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _spotifyDevices = new ComboBox { PlaceholderText = "Choose a Spotify Connect device", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12, MinHeight = 28 };
        AutomationProperties.SetName(_spotifyDevices, "Spotify playback device");
        _spotifyDevices.SelectionChanged += (_, _) => UpdateSpotifyActions();
        deviceLine.Children.Add(_spotifyDevices);
        _spotifyTransfer = SpotifyButton("Use device", async () =>
        {
            if (_spotifyDevices.SelectedItem is not SpotifyDeviceRow row) return;
            await _spotify.SelectDeviceAsync(row.Device.Id);
            await _spotify.RefreshPlaybackAsync();
        });
        Grid.SetColumn(_spotifyTransfer, 1);
        deviceLine.Children.Add(_spotifyTransfer);
        _spotifySignedIn.Children.Add(deviceLine);
        layout.Children.Add(_spotifySignedIn);

        _spotifyContent = RowsGrid(GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto);
        _spotifyContent.RowSpacing = 5;
        var navigation = SpotifyBar();
        navigation.Children.Add(SpotifyButton("Playlists", () => LoadSpotifyPage(SpotifyBrowseMode.Playlists)));
        navigation.Children.Add(SpotifyButton("Liked songs", () => LoadSpotifyPage(SpotifyBrowseMode.Liked)));
        navigation.Children.Add(SpotifyButton("Queue", () => LoadSpotifyPage(SpotifyBrowseMode.Queue)));
        navigation.Children.Add(SpotifyButton("New playlist", () => EditSpotifyPlaylist(null)));
        navigation.Children.Add(SpotifyButton("Open link", OpenSpotifyLocation));
        Row(_spotifyContent, SpotifyScrollBar(navigation), 0);

        var searchLine = new Grid { ColumnSpacing = 5 };
        searchLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _spotifySearch = new TextBox { PlaceholderText = "Search Spotify", FontSize = 12, MinWidth = 0, MinHeight = 28 };
        AutomationProperties.SetName(_spotifySearch, "Search Spotify");
        _spotifySearch.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; _ = RunSpotify(SearchSpotify); } };
        searchLine.Children.Add(_spotifySearch);
        _spotifySearchType = new ComboBox { ItemsSource = new[] { "Tracks", "Playlists", "Albums", "Artists" }, SelectedIndex = 0, FontSize = 11, MinHeight = 28 };
        AutomationProperties.SetName(_spotifySearchType, "Spotify search type");
        Grid.SetColumn(_spotifySearchType, 1);
        searchLine.Children.Add(_spotifySearchType);
        var searchButton = SpotifyButton("Search", SearchSpotify);
        Grid.SetColumn(searchButton, 2);
        searchLine.Children.Add(searchButton);
        Row(_spotifyContent, searchLine, 1);

        var pageLine = new Grid { ColumnSpacing = 5 };
        pageLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pageLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel();
        _spotifyBrowseTitle = new TextBlock { Text = "Your playlists", FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        _spotifyPageInfo = new TextBlock { FontSize = 10 };
        heading.Children.Add(_spotifyBrowseTitle);
        heading.Children.Add(_spotifyPageInfo);
        pageLine.Children.Add(heading);
        var paging = SpotifyBar();
        _spotifyPreviousPage = SpotifyButton("Previous page", () => LoadSpotifyPage(_spotifyBrowseMode, Math.Max(0, _spotifyOffset - _spotifyPageSize), _spotifyCurrentPlaylist, _spotifyCurrentAlbum));
        _spotifyNextPage = SpotifyButton("Next page", () => LoadSpotifyPage(_spotifyBrowseMode, _spotifyOffset + _spotifyPageSize, _spotifyCurrentPlaylist, _spotifyCurrentAlbum));
        paging.Children.Add(_spotifyPreviousPage);
        paging.Children.Add(_spotifyNextPage);
        Grid.SetColumn(paging, 1);
        pageLine.Children.Add(paging);
        Row(_spotifyContent, pageLine, 2);

        _spotifyItems = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single, DisplayMemberPath = nameof(SpotifyBrowseRow.Label),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, FontSize = 12, Height = 175
        };
        AutomationProperties.SetName(_spotifyItems, "Spotify library items");
        _spotifyItems.SelectionChanged += (_, _) => UpdateSpotifyActions();
        _spotifyItems.DoubleTapped += (_, _) => _ = RunSpotify(OpenSpotifySelection);
        _spotifyItems.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; _ = RunSpotify(OpenSpotifySelection); } };
        Row(_spotifyContent, _spotifyItems, 3);
        var listActions = SpotifyBar();
        _spotifyPlaySelected = SpotifyButton("Play selected", PlaySpotifySelection);
        listActions.Children.Add(_spotifyPlaySelected);
        _spotifyActions = Command("Item actions", ShowSpotifyItemMenu);
        listActions.Children.Add(_spotifyActions);
        _spotifyPlaylistActions = Command("Playlist actions", ShowSpotifyPlaylistMenu);
        listActions.Children.Add(_spotifyPlaylistActions);
        listActions.Children.Add(SpotifyButton("Refresh list", RefreshSpotifyPage));
        Row(_spotifyContent, SpotifyScrollBar(listActions), 4);
        layout.Children.Add(_spotifyContent);

        layout.Children.Add(BuildSpotifyTransport());
        _spotifyStatus = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        AutomationProperties.SetName(_spotifyStatus, "Spotify status");
        AutomationProperties.SetLiveSetting(_spotifyStatus, AutomationLiveSetting.Polite);
        layout.Children.Add(_spotifyStatus);
        _spotifyWindow.Client = new ScrollViewer
        {
            Content = layout, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        _spotifyWindow.SizeChanged += (_, _) =>
        {
            if (_spotifyItems is not null)
                _spotifyItems.Height = Math.Max(140, _spotifyWindow.Height - 430);
        };
        UpdateSpotifySkin();
    }

    private Grid BuildSpotifyTransport()
    {
        var body = RowsGrid(GridLength.Auto, GridLength.Auto, GridLength.Auto);
        body.RowSpacing = 3;
        var now = new Grid { ColumnSpacing = 8 };
        now.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        now.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _spotifyArtwork = new Image { Width = 64, Height = 64, Stretch = Stretch.Uniform };
        AutomationProperties.SetName(_spotifyArtwork, "Spotify album artwork");
        now.Children.Add(_spotifyArtwork);
        var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _spotifyTrackLink = new HyperlinkButton { Content = "Open Spotify", NavigateUri = new Uri("https://open.spotify.com/"), Padding = new Thickness(0), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left };
        _spotifyTrackDetails = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxLines = 3 };
        details.Children.Add(_spotifyTrackLink);
        details.Children.Add(_spotifyTrackDetails);
        Grid.SetColumn(details, 1);
        now.Children.Add(details);
        Row(body, now, 0);

        _spotifySeek = new Slider { Minimum = 0, Maximum = 1, StepFrequency = 1, MinHeight = 20, IsEnabled = false };
        AutomationProperties.SetName(_spotifySeek, "Spotify seek");
        _spotifySeek.ValueChanged += (_, _) => { if (!_spotifyUpdatingTransport) _spotifySeekPending = true; };
        _spotifySeek.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => CommitSpotifySeek()), true);
        _spotifySeek.KeyUp += (_, _) => CommitSpotifySeek();
        _spotifySeek.PointerCaptureLost += (_, _) => CommitSpotifySeek();
        Row(body, _spotifySeek, 1);

        var controls = SpotifyBar();
        _spotifyPrevious = SpotifyButton("Previous", () => SpotifyCommandAsync(() => _spotify.PreviousAsync()));
        _spotifyPlayPause = SpotifyButton("Play", () => SpotifyCommandAsync(() => _spotify.Playback?.IsPlaying == true ? _spotify.PauseAsync() : _spotify.ResumeAsync()));
        _spotifyNext = SpotifyButton("Next", () => SpotifyCommandAsync(() => _spotify.NextAsync()));
        controls.Children.Add(_spotifyPrevious);
        controls.Children.Add(_spotifyPlayPause);
        controls.Children.Add(_spotifyNext);
        _spotifyShuffle = new CheckBox { Content = "Shuffle", FontSize = 11, MinWidth = 0, MinHeight = 26 };
        _spotifyShuffle.Checked += SpotifyShuffleChanged;
        _spotifyShuffle.Unchecked += SpotifyShuffleChanged;
        controls.Children.Add(_spotifyShuffle);
        _spotifyRepeat = new ComboBox { ItemsSource = new[] { "Repeat off", "Repeat context", "Repeat track" }, SelectedIndex = 0, FontSize = 11, MinHeight = 26 };
        AutomationProperties.SetName(_spotifyRepeat, "Spotify repeat mode");
        _spotifyRepeat.SelectionChanged += (_, _) =>
        {
            if (!_spotifyUpdatingTransport)
                _ = RunSpotify(() => _spotify.SetRepeatAsync(_spotifyRepeat.SelectedIndex switch { 1 => "context", 2 => "track", _ => "off" }));
        };
        controls.Children.Add(_spotifyRepeat);
        _spotifyVolume = new Slider { Minimum = 0, Maximum = 100, Value = 100, StepFrequency = 1, Width = 90, MinHeight = 26 };
        AutomationProperties.SetName(_spotifyVolume, "Spotify volume");
        ToolTipService.SetToolTip(_spotifyVolume, "Spotify volume");
        _spotifyVolume.ValueChanged += (_, _) => { if (!_spotifyUpdatingTransport) _spotifyVolumePending = true; };
        _spotifyVolume.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => CommitSpotifyVolume()), true);
        _spotifyVolume.KeyUp += (_, _) => CommitSpotifyVolume();
        _spotifyVolume.PointerCaptureLost += (_, _) => CommitSpotifyVolume();
        controls.Children.Add(_spotifyVolume);
        Row(body, SpotifyScrollBar(controls), 2);
        return body;
    }

    private static StackPanel SpotifyBar() => new() { Orientation = Orientation.Horizontal, Spacing = 5 };
    private static ScrollViewer SpotifyScrollBar(FrameworkElement content) => new()
    {
        Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Left
    };
    private Button SpotifyButton(string label, Func<Task> action) => Command(label, () => _ = RunSpotify(action));

    private async Task RunSpotify(Func<Task> action)
    {
        if (_disposed) return;
        _spotifyBusy++;
        _spotifyUiMessage = null;
        UpdateSpotifyActions();
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _spotifyUiMessage = ex.Message; }
        finally
        {
            _spotifyBusy--;
            if (!_disposed) UpdateSpotifyView();
        }
    }

    private void UpdateSpotifyView()
    {
        if (_spotifyWindow is null || _disposed) return;
        bool signedIn = _spotify.IsSignedIn;
        _spotifySetup!.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        _spotifySignedIn!.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        _spotifyContent!.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        _spotifyConnect!.Visibility = _spotify.SupportsBrowserPlayer ? Visibility.Visible : Visibility.Collapsed;
        _spotifyAccount!.Text = signedIn ? _spotify.Profile?.DisplayName ?? "Connected to Spotify" : "Spotify library and playback";
        if (!signedIn && string.IsNullOrWhiteSpace(_spotifyClientId!.Text)) _spotifyClientId.Text = _spotify.ClientId;
        string? accountId = signedIn ? _spotify.Profile?.Id : null;
        if (accountId != _spotifyLoadedAccount)
        {
            _spotifyLoadedAccount = accountId;
            CancelSpotifyBrowse();
            _spotifyItems!.ItemsSource = null;
            _spotifyCurrentPlaylist = null;
            _spotifyCurrentAlbum = null;
            _spotifyOffset = _spotifyTotal = 0;
            _spotifyHasMore = false;
            if (!signedIn)
            {
                _spotifyUiMessage = null;
                _spotifyDevices!.ItemsSource = null;
            }
            if (accountId is not null)
                _ = RunSpotify(async () => { await LoadSpotifyPage(SpotifyBrowseMode.Playlists); await RefreshSpotifyDevices(); });
        }
        _spotifyStatus!.Text = _spotifyUiMessage ?? _spotify.Status;
        UpdateSpotifyTransport();
        UpdateSpotifyActions();
    }

    private void UpdateSpotifyTransport()
    {
        if (_spotifyPlayPause is null) return;
        var state = _spotify.Playback;
        var track = state?.Track;
        _spotifyUpdatingTransport = true;
        try
        {
            _spotifyPlayPause.Content = state?.IsPlaying == true ? "Pause" : "Play";
            AutomationProperties.SetName(_spotifyPlayPause, state?.IsPlaying == true ? "Pause Spotify" : "Play Spotify");
            _spotifyShuffle!.IsChecked = state?.ShuffleState == true;
            _spotifyRepeat!.SelectedIndex = state?.RepeatState switch { "context" => 1, "track" => 2, _ => 0 };
            _spotifySeek!.Maximum = Math.Max(1, (track?.DurationMs ?? 0) / 1000d);
            if (!_spotifySeekPending) _spotifySeek.Value = Math.Clamp(_spotify.Position.TotalSeconds, 0, _spotifySeek.Maximum);
            if (!_spotifyVolumePending && state?.Device?.VolumePercent is int volume) _spotifyVolume!.Value = volume;
            _spotifyTrackLink!.Content = track is null ? "Open Spotify" : track.Name + (track.IsExplicit ? " [E]" : "");
            _spotifyTrackLink.NavigateUri = SpotifyWebUri(track?.ExternalUrl) ?? new Uri("https://open.spotify.com/");
            _spotifyTrackDetails!.Text = track is null ? "Choose an item and a playback device." :
                track.ArtistName + (string.IsNullOrWhiteSpace(track.Album?.Name) ? "" : " · " + track.Album.Name) +
                "\n" + Clock(_spotify.Position) + " / " + Clock(TimeSpan.FromMilliseconds(track.DurationMs));
            if (_spotifyArtworkUrl != track?.ImageUrl)
            {
                _spotifyArtworkUrl = track?.ImageUrl;
                _spotifyArtwork!.Source = Uri.TryCreate(_spotifyArtworkUrl, UriKind.Absolute, out var image) && image.Scheme == "https" ? new BitmapImage(image) : null;
            }
        }
        finally { _spotifyUpdatingTransport = false; }
    }

    private void UpdateSpotifyActions()
    {
        if (_spotifyPlaySelected is null) return;
        bool signedIn = _spotify.IsSignedIn, idle = _spotifyBusy == 0;
        var row = _spotifyItems?.SelectedItem as SpotifyBrowseRow;
        var state = _spotify.Playback;
        bool Allowed(string name) => signedIn && state?.DisallowedActions.Contains(name) != true;
        _spotifyPlaySelected.IsEnabled = signedIn && idle && !string.IsNullOrEmpty(row?.Uri) && row.Track?.IsPlayable != false && row.Track?.IsLocal != true;
        _spotifyActions!.IsEnabled = signedIn && idle && row is not null;
        _spotifyPlaylistActions!.IsEnabled = signedIn && idle && (row?.Playlist is not null || _spotifyCurrentPlaylist is not null);
        _spotifyPreviousPage!.IsEnabled = signedIn && idle && _spotifyOffset > 0;
        _spotifyNextPage!.IsEnabled = signedIn && idle && _spotifyHasMore;
        _spotifyTransfer!.IsEnabled = signedIn && idle && _spotifyDevices?.SelectedItem is SpotifyDeviceRow { Device.IsRestricted: false };
        _spotifyPrevious!.IsEnabled = idle && Allowed("skipping_prev");
        _spotifyNext!.IsEnabled = idle && Allowed("skipping_next");
        _spotifyPlayPause!.IsEnabled = idle && Allowed(state?.IsPlaying == true ? "pausing" : "resuming");
        _spotifySeek!.IsEnabled = idle && state?.Track is { DurationMs: > 0 } && Allowed("seeking");
        _spotifyShuffle!.IsEnabled = idle && Allowed("toggling_shuffle");
        _spotifyRepeat!.IsEnabled = idle && Allowed("toggling_repeat_context") && Allowed("toggling_repeat_track");
        _spotifyVolume!.IsEnabled = signedIn && idle && state?.Device?.SupportsVolume == true;
    }

    private void UpdateSpotifySkin()
    {
        _spotifyWindow?.SetSkin(_skin);
        var foreground = ClassicSurface.Brush(_skin.Playlist.Normal);
        foreach (var text in new[] { _spotifyStatus, _spotifyBrowseTitle, _spotifyPageInfo, _spotifyTrackDetails })
            if (text is not null) text.Foreground = foreground;
        if (_spotifyAccount is not null) _spotifyAccount.Foreground = ClassicSurface.Brush(0xffffffff);
        if (_spotifySetup?.Children.FirstOrDefault() is TextBlock setupText) setupText.Foreground = foreground;
        if (_spotifyItems is not null) _spotifyItems.Foreground = foreground;
    }

    private void CommitSpotifySeek()
    {
        if (!_spotifySeekPending || _spotifySeek is null) return;
        _spotifySeekPending = false;
        double seconds = _spotifySeek.Value;
        _ = RunSpotify(() => SpotifyCommandAsync(() => _spotify.SeekAsync(TimeSpan.FromSeconds(seconds))));
    }
    private void CommitSpotifyVolume()
    {
        if (!_spotifyVolumePending || _spotifyVolume is null) return;
        _spotifyVolumePending = false;
        double volume = _spotifyVolume.Value / 100;
        _ = RunSpotify(() => _spotify.SetVolumeAsync(volume));
    }
    private void SpotifyShuffleChanged(object sender, RoutedEventArgs e)
    {
        if (!_spotifyUpdatingTransport) _ = RunSpotify(() => _spotify.SetShuffleAsync(_spotifyShuffle!.IsChecked == true));
    }

    private void CancelSpotifyBrowse()
    {
        _spotifyBrowseGeneration++;
        _spotifyBrowseCancellation?.Cancel();
        _spotifyBrowseCancellation?.Dispose();
        _spotifyBrowseCancellation = null;
    }
    private void CloseSpotifyView()
    {
        CancelSpotifyBrowse();
        _spotifyUiLifetime.Cancel();
        _spotifyUiLifetime.Dispose();
    }
    private static Uri? SpotifyWebUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && (uri.Host == "open.spotify.com" || uri.Host == "www.spotify.com") ? uri : null;
    private static async Task OpenSpotifyWeb(string? value)
    {
        var uri = SpotifyWebUri(value);
        if (uri is not null) await Launcher.LaunchUriAsync(uri);
    }
}
