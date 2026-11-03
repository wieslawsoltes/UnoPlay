using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using UnoPlay.Spotify;
using Windows.Storage;

namespace UnoPlay.ClassicUI;

public sealed partial class ClassicPlayerView
{
    private async Task LoadSpotifyPage(SpotifyBrowseMode mode, int offset = 0, SpotifyPlaylist? playlist = null, SpotifyAlbum? album = null)
    {
        if (!_spotify.IsSignedIn || _spotifyItems is null) return;
        CancelSpotifyBrowse();
        _spotifyBrowseCancellation = CancellationTokenSource.CreateLinkedTokenSource(_spotifyUiLifetime.Token);
        var token = _spotifyBrowseCancellation.Token;
        int generation = _spotifyBrowseGeneration;
        IReadOnlyList<SpotifyBrowseRow> rows;
        int total, pageSize = mode == SpotifyBrowseMode.Search ? 10 : 50;
        bool hasMore;
        string heading;
        switch (mode)
        {
            case SpotifyBrowseMode.Playlists:
                var playlists = await _spotify.Api.GetPlaylistsAsync(offset, pageSize, token);
                rows = playlists.Items.Select((item, index) => PlaylistRow(item, offset + index)).ToArray();
                (total, hasMore, heading) = (playlists.Total, playlists.HasMore, "Your playlists");
                break;
            case SpotifyBrowseMode.Playlist:
                if (playlist is null) return;
                playlist = await _spotify.Api.GetPlaylistAsync(playlist.Id, token);
                var items = await _spotify.Api.GetPlaylistItemsAsync(playlist.Id, offset, pageSize, token);
                rows = items.Items.Select(item => TrackRow(item.Track, item.Position)).ToArray();
                (total, hasMore, heading) = (items.Total, items.HasMore, playlist.Name);
                break;
            case SpotifyBrowseMode.Liked:
                var saved = await _spotify.Api.GetSavedTracksAsync(offset, pageSize, token);
                rows = saved.Items.Select((item, index) => TrackRow(item.Track, offset + index)).ToArray();
                (total, hasMore, heading) = (saved.Total, saved.HasMore, "Liked songs");
                break;
            case SpotifyBrowseMode.Album:
                if (album is null) return;
                var tracks = await _spotify.Api.GetAlbumTracksAsync(album.Id, offset, pageSize, token);
                rows = tracks.Items.Select((track, index) => TrackRow(track with { Album = album }, offset + index)).ToArray();
                (total, hasMore, heading) = (tracks.Total, tracks.HasMore, album.Name);
                break;
            case SpotifyBrowseMode.Queue:
                var queue = await _spotify.Api.GetQueueAsync(token);
                rows = queue.Items.Select(TrackRow).ToArray();
                (total, hasMore, heading) = (rows.Count, false, "Upcoming in Spotify");
                break;
            default:
                var found = await _spotify.Api.SearchAsync(_spotifySearchTerm, offset, pageSize, token);
                switch (_spotifySearchKind)
                {
                    case "Playlists":
                        rows = found.Playlists.Items.Select((item, index) => PlaylistRow(item, offset + index)).ToArray();
                        (total, hasMore) = (found.Playlists.Total, found.Playlists.HasMore);
                        break;
                    case "Albums":
                        rows = found.Albums.Items.Select((item, index) => new SpotifyBrowseRow(item.Name + " · " + string.Join(", ", item.Artists.Select(artist => artist.Name)), offset + index, Album: item)).ToArray();
                        (total, hasMore) = (found.Albums.Total, found.Albums.HasMore);
                        break;
                    case "Artists":
                        rows = found.Artists.Items.Select((item, index) => new SpotifyBrowseRow(item.Name, offset + index, Artist: item)).ToArray();
                        (total, hasMore) = (found.Artists.Total, found.Artists.HasMore);
                        break;
                    default:
                        rows = found.Tracks.Items.Select((item, index) => TrackRow(item, offset + index)).ToArray();
                        (total, hasMore) = (found.Tracks.Total, found.Tracks.HasMore);
                        break;
                }
                hasMore &= offset + pageSize <= 1000;
                heading = _spotifySearchKind + " matching “" + _spotifySearchTerm + "”";
                break;
        }
        token.ThrowIfCancellationRequested();
        if (generation != _spotifyBrowseGeneration || _disposed) return;
        _spotifyBrowseMode = mode;
        _spotifyCurrentPlaylist = mode == SpotifyBrowseMode.Playlist ? playlist : null;
        _spotifyCurrentAlbum = mode == SpotifyBrowseMode.Album ? album : null;
        _spotifyOffset = offset;
        _spotifyTotal = total;
        _spotifyPageSize = pageSize;
        _spotifyHasMore = hasMore;
        _spotifyItems.ItemsSource = rows;
        _spotifyItems.SelectedItem = rows.FirstOrDefault();
        _spotifyBrowseTitle!.Text = heading;
        _spotifyPageInfo!.Text = rows.Count == 0 ? "No items" : $"{offset + 1}–{offset + rows.Count} of {total}";
        UpdateSpotifyActions();
    }

    private static SpotifyBrowseRow TrackRow(SpotifyTrack? track, int position)
    {
        string label = track is null ? "Unavailable item" : track.Name +
            (track.IsExplicit ? " [E]" : "") +
            (string.IsNullOrWhiteSpace(track.ArtistName) ? "" : " · " + track.ArtistName) +
            " · " + Clock(TimeSpan.FromMilliseconds(track.DurationMs)) +
            (track.IsLocal ? " · local to Spotify" : !track.IsPlayable ? " · unavailable" : "");
        return new SpotifyBrowseRow($"{position + 1}. {label}", position, Track: track);
    }
    private static SpotifyBrowseRow PlaylistRow(SpotifyPlaylist playlist, int position) => new(
        playlist.Name + " · " + playlist.OwnerName + (playlist.TotalItems is int count ? $" · {count} items" : ""), position, Playlist: playlist);

    private Task SearchSpotify()
    {
        string query = _spotifySearch!.Text.Trim();
        if (query.Length == 0) throw new ArgumentException("Enter a Spotify search term.");
        _spotifySearchTerm = query;
        _spotifySearchKind = _spotifySearchType!.SelectedItem as string ?? "Tracks";
        return LoadSpotifyPage(SpotifyBrowseMode.Search);
    }
    private Task RefreshSpotifyPage() => LoadSpotifyPage(_spotifyBrowseMode, _spotifyOffset, _spotifyCurrentPlaylist, _spotifyCurrentAlbum);

    private async Task RefreshSpotifyDevices()
    {
        string? accountId = _spotify.Profile?.Id;
        var devices = await _spotify.GetDevicesAsync();
        if (_disposed || !_spotify.IsSignedIn || _spotify.Profile?.Id != accountId || _spotifyDevices is null) return;
        string? previous = (_spotifyDevices.SelectedItem as SpotifyDeviceRow)?.Device.Id;
        var rows = devices.Select(device => new SpotifyDeviceRow(device)).ToArray();
        _spotifyDevices.ItemsSource = rows;
        _spotifyDevices.SelectedItem = rows.FirstOrDefault(row => row.Device.Id == _spotify.SelectedDeviceId)
            ?? rows.FirstOrDefault(row => row.Device.Id == previous)
            ?? rows.FirstOrDefault(row => row.Device.IsActive);
        if (rows.Length == 0)
            _spotifyUiMessage = _spotify.SupportsBrowserPlayer ? "Use this browser to create a Spotify playback device, or open Spotify on another device and refresh." : "Open Spotify on a device, then refresh the device list.";
    }

    private Task OpenSpotifySelection()
    {
        return _spotifyItems?.SelectedItem switch
        {
            SpotifyBrowseRow { Playlist: { } playlist } => LoadSpotifyPage(SpotifyBrowseMode.Playlist, playlist: playlist),
            SpotifyBrowseRow { Album: { } album } => LoadSpotifyPage(SpotifyBrowseMode.Album, album: album),
            _ => PlaySpotifySelection()
        };
    }
    private Task PlaySpotifySelection()
    {
        if (_spotifyItems?.SelectedItem is not SpotifyBrowseRow row || row.Uri is null) return Task.CompletedTask;
        if (row.Track is { IsLocal: true } or { IsPlayable: false })
            throw new InvalidOperationException("This item is unavailable for playback through Spotify's SDK.");
        if (row.Track is null) return PlaySpotifyAsync(contextUri: row.Uri);
        if (_spotifyBrowseMode == SpotifyBrowseMode.Playlist && _spotifyCurrentPlaylist is not null)
            return PlaySpotifyAsync(contextUri: _spotifyCurrentPlaylist.Uri, offset: row.Position);
        if (_spotifyBrowseMode == SpotifyBrowseMode.Album && _spotifyCurrentAlbum is not null)
            return PlaySpotifyAsync(contextUri: _spotifyCurrentAlbum.Uri, offset: row.Position);
        return PlaySpotifyAsync(uris: [row.Track.Uri]);
    }

    private void ShowSpotifyItemMenu()
    {
        if (_spotifyItems?.SelectedItem is not SpotifyBrowseRow row) return;
        var menu = new MenuFlyout();
        void Add(string label, Func<Task> action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = label, IsEnabled = enabled };
            AutomationProperties.SetAutomationId(item, "SpotifyItemAction" + label.Replace(" ", ""));
            item.Click += (_, _) => _ = RunSpotify(action);
            menu.Items.Add(item);
        }
        if (row.Playlist is not null || row.Album is not null) Add("Browse items", OpenSpotifySelection);
        Add("Play", PlaySpotifySelection, row.Uri is not null && row.Track?.IsPlayable != false && row.Track?.IsLocal != true);
        Add("Open in Spotify", () => OpenSpotifyWeb(row.ExternalUrl), SpotifyWebUri(row.ExternalUrl) is not null);
        Add("Details", () => ShowInfo("Spotify item", row.Label + (row.Track?.Album is { } album ? "\n\n" + album.Name : "") +
            (row.Playlist is { } playlist ? "\n\n" + playlist.Description : "") + "\n\n" + row.ExternalUrl));
        if (row.Uri is not null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("Save to your Spotify library", async () =>
            {
                await _spotify.Api.SaveLibraryItemsAsync([row.Uri], _spotifyUiLifetime.Token);
                _spotifyUiMessage = "Saved to your Spotify library.";
            });
            Add("Remove from your Spotify library…", () => RemoveSpotifySavedItem(row));
        }
        if (row.Track is not null && !row.Track.IsLocal)
        {
            Add("Add to playback queue", () => _spotify.AddToQueueAsync(row.Track.Uri), row.Track.IsPlayable);
            Add("Add to playlist…", () => AddSpotifyToPlaylist(row.Track));
            if (_spotifyCurrentPlaylist is { } current && CanEditSpotifyItems(current))
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                Add("Move up", () => MoveSpotifyItem(row, current, -1), row.Position > 0);
                Add("Move down", () => MoveSpotifyItem(row, current, 1), row.Position + 1 < _spotifyTotal);
                Add("Remove all occurrences from playlist…", () => RemoveSpotifyPlaylistItem(row.Track, current));
            }
        }
        menu.ShowAt(_spotifyActions!);
    }

    private void ShowSpotifyPlaylistMenu()
    {
        var playlist = (_spotifyItems?.SelectedItem as SpotifyBrowseRow)?.Playlist ?? _spotifyCurrentPlaylist;
        if (playlist is null) { _spotifyUiMessage = "Open or select a playlist to edit it."; UpdateSpotifyView(); return; }
        var menu = new MenuFlyout();
        void Add(string label, Func<Task> action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = label, IsEnabled = enabled && _spotifyBusy == 0 };
            AutomationProperties.SetAutomationId(item, "SpotifyPlaylistAction" + label.Replace(" ", ""));
            item.Click += (_, _) => _ = RunSpotify(action);
            menu.Items.Add(item);
        }
        Add("Play playlist", () => PlaySpotifyAsync(contextUri: playlist.Uri));
        Add("Browse playlist", () => LoadSpotifyPage(SpotifyBrowseMode.Playlist, playlist: playlist));
        Add("Open in Spotify", () => OpenSpotifyWeb(playlist.ExternalUrl));
        Add("Edit name, description and visibility…", () => EditSpotifyPlaylist(playlist), playlist.OwnerId == _spotify.Profile?.Id);
        Add("Upload playlist cover…", () => UploadSpotifyCover(playlist), playlist.OwnerId == _spotify.Profile?.Id);
        Add("Add track or episode link…", () => AddSpotifyLinkToPlaylist(playlist), CanEditSpotifyItems(playlist));
        Add("Clear playlist…", () => ClearSpotifyPlaylist(playlist), CanEditSpotifyItems(playlist));
        Add("Save playlist to your library", async () =>
        {
            await _spotify.Api.SaveLibraryItemsAsync([playlist.Uri], _spotifyUiLifetime.Token);
            _spotifyUiMessage = "Playlist saved to your Spotify library.";
        });
        Add("Remove playlist from your library…", () => RemoveSpotifySavedItem(PlaylistRow(playlist, 0)));
        menu.ShowAt(_spotifyPlaylistActions!);
    }

    private bool CanEditSpotifyItems(SpotifyPlaylist playlist) => playlist.OwnerId == _spotify.Profile?.Id || playlist.Collaborative;

    private async Task ClearSpotifyPlaylist(SpotifyPlaylist playlist)
    {
        if (!await ConfirmSpotify("Clear Spotify playlist?", $"Remove every item from “{playlist.Name}”? This changes the playlist for everyone who follows it.", "Clear playlist")) return;
        string snapshot = await _spotify.Api.ReplacePlaylistItemsAsync(playlist.Id, [], _spotifyUiLifetime.Token);
        await LoadSpotifyPage(SpotifyBrowseMode.Playlist, playlist: playlist with { SnapshotId = snapshot });
        _spotifyUiMessage = "Playlist cleared.";
    }

    private async Task MoveSpotifyItem(SpotifyBrowseRow row, SpotifyPlaylist playlist, int direction)
    {
        int newPosition = row.Position + direction;
        if (newPosition < 0 || newPosition >= _spotifyTotal) return;
        string snapshot = await _spotify.Api.ReorderPlaylistItemsAsync(playlist.Id, row.Position,
            direction < 0 ? newPosition : row.Position + 2, snapshotId: playlist.SnapshotId, cancellationToken: _spotifyUiLifetime.Token);
        if (_spotifyCurrentPlaylist?.Id != playlist.Id) return;
        await LoadSpotifyPage(SpotifyBrowseMode.Playlist, newPosition / 50 * 50, playlist with { SnapshotId = snapshot });
        if (_spotifyItems?.ItemsSource is IReadOnlyList<SpotifyBrowseRow> rows)
            _spotifyItems.SelectedItem = rows.FirstOrDefault(item => item.Position == newPosition);
    }

    private async Task RemoveSpotifyPlaylistItem(SpotifyTrack track, SpotifyPlaylist playlist)
    {
        if (!await ConfirmSpotify("Remove playlist item?", $"Remove every occurrence of “{track.Name}” from “{playlist.Name}”?", "Remove all occurrences")) return;
        string snapshot = await _spotify.Api.RemovePlaylistItemsAsync(playlist.Id, [new SpotifyPlaylistRemoval(track.Uri)], playlist.SnapshotId, _spotifyUiLifetime.Token);
        if (_spotifyCurrentPlaylist?.Id == playlist.Id)
            await LoadSpotifyPage(SpotifyBrowseMode.Playlist, playlist: playlist with { SnapshotId = snapshot });
        _spotifyUiMessage = "Removed all occurrences from the playlist.";
    }

    private async Task RemoveSpotifySavedItem(SpotifyBrowseRow row)
    {
        if (row.Uri is null || !await ConfirmSpotify("Remove from Spotify library?", $"Remove “{row.Track?.Name ?? row.Playlist?.Name ?? row.Album?.Name ?? row.Artist?.Name}” from your Spotify library?", "Remove")) return;
        await _spotify.Api.RemoveLibraryItemsAsync([row.Uri], _spotifyUiLifetime.Token);
        if (_spotifyBrowseMode is SpotifyBrowseMode.Playlists or SpotifyBrowseMode.Liked)
            await LoadSpotifyPage(_spotifyBrowseMode);
        _spotifyUiMessage = "Removed from your Spotify library.";
    }

    private async Task EditSpotifyPlaylist(SpotifyPlaylist? playlist)
    {
        if (_showingDialog) return;
        _showingDialog = true;
        try
        {
            var name = new TextBox { Header = "Name", Text = playlist?.Name ?? "", MaxLength = 100 };
            var description = new TextBox { Header = "Description", Text = playlist?.Description ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 85, MaxLength = 300 };
            var isPublic = new CheckBox { Content = "Public playlist", IsChecked = playlist?.IsPublic == true };
            var collaborative = new CheckBox { Content = "Collaborative playlist", IsChecked = playlist?.Collaborative == true };
            AutomationProperties.SetName(name, "Spotify playlist name");
            AutomationProperties.SetAutomationId(name, "SpotifyPlaylistName");
            AutomationProperties.SetName(description, "Spotify playlist description");
            AutomationProperties.SetAutomationId(description, "SpotifyPlaylistDescription");
            AutomationProperties.SetAutomationId(isPublic, "SpotifyPlaylistPublic");
            AutomationProperties.SetAutomationId(collaborative, "SpotifyPlaylistCollaborative");
            isPublic.Checked += (_, _) => collaborative.IsChecked = false;
            collaborative.Checked += (_, _) => isPublic.IsChecked = false;
            var content = new StackPanel { Spacing = 8, MinWidth = 240 };
            content.Children.Add(name); content.Children.Add(description); content.Children.Add(isPublic); content.Children.Add(collaborative);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark, Title = playlist is null ? "Create Spotify playlist" : "Edit Spotify playlist",
                Content = content, PrimaryButtonText = playlist is null ? "Create" : "Save", CloseButtonText = "Cancel", IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text)
            };
            name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (playlist is null)
                playlist = await _spotify.Api.CreatePlaylistAsync(name.Text.Trim(), description.Text, isPublic.IsChecked == true, collaborative.IsChecked == true, _spotifyUiLifetime.Token);
            else
                await _spotify.Api.UpdatePlaylistDetailsAsync(playlist.Id, name.Text.Trim(), description.Text, isPublic.IsChecked == true, collaborative.IsChecked == true, _spotifyUiLifetime.Token);
            await LoadSpotifyPage(SpotifyBrowseMode.Playlist, playlist: playlist);
        }
        finally { _showingDialog = false; }
    }

    private async Task AddSpotifyToPlaylist(SpotifyTrack track)
    {
        if (_showingDialog) return;
        _showingDialog = true;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_spotifyUiLifetime.Token);
        try
        {
            var list = new ListView { SelectionMode = ListViewSelectionMode.Single, DisplayMemberPath = nameof(SpotifyPlaylist.Name), Height = 210, FontSize = 12 };
            AutomationProperties.SetName(list, "Choose Spotify playlist");
            AutomationProperties.SetAutomationId(list, "SpotifyDestinationPlaylist");
            var caption = new TextBlock { Text = "Choose a playlist you own or collaborate on.", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            var more = Command("Load more playlists", () => { });
            AutomationProperties.SetAutomationId(more, "SpotifyLoadMorePlaylists");
            var content = new StackPanel { Spacing = 6, MinWidth = 240 };
            content.Children.Add(caption); content.Children.Add(list); content.Children.Add(more);
            var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark, Title = "Add to Spotify playlist", Content = content, PrimaryButtonText = "Add", CloseButtonText = "Cancel", IsPrimaryButtonEnabled = false };
            list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is SpotifyPlaylist;
            List<SpotifyPlaylist> candidates = [];
            int offset = 0;
            bool loading = false;
            async Task LoadNext()
            {
                if (loading) return;
                loading = true; more.IsEnabled = false;
                try
                {
                    var page = await _spotify.Api.GetPlaylistsAsync(offset, 50, lifetime.Token);
                    candidates.AddRange(page.Items.Where(CanEditSpotifyItems));
                    var selected = list.SelectedItem as SpotifyPlaylist;
                    list.ItemsSource = candidates.ToArray();
                    list.SelectedItem = selected ?? candidates.FirstOrDefault();
                    offset += 50;
                    more.Visibility = page.HasMore ? Visibility.Visible : Visibility.Collapsed;
                    caption.Text = candidates.Count == 0 ? "No editable playlists on this page. Load more or create a playlist." : "Choose a playlist you own or collaborate on.";
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { caption.Text = ex.Message; }
                finally { loading = false; more.IsEnabled = true; }
            }
            more.Click += async (_, _) => await LoadNext();
            await LoadNext();
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || list.SelectedItem is not SpotifyPlaylist target) return;
            await _spotify.Api.AddPlaylistItemsAsync(target.Id, [track.Uri], cancellationToken: _spotifyUiLifetime.Token);
            _spotifyUiMessage = $"Added “{track.Name}” to “{target.Name}”.";
            if (_spotifyCurrentPlaylist?.Id == target.Id) await RefreshSpotifyPage();
        }
        finally { lifetime.Cancel(); _showingDialog = false; }
    }

    private async Task AddSpotifyLinkToPlaylist(SpotifyPlaylist playlist)
    {
        string? value = await Prompt("Add to Spotify playlist", "Track or episode link or URI", "Add", "spotify:track:…");
        if (string.IsNullOrWhiteSpace(value)) return;
        var (type, id) = ParseSpotifyLocation(value);
        if (type is not ("track" or "episode")) throw new ArgumentException("Choose a Spotify track or episode link.");
        await _spotify.Api.AddPlaylistItemsAsync(playlist.Id, [$"spotify:{type}:{id}"], cancellationToken: _spotifyUiLifetime.Token);
        if (_spotifyCurrentPlaylist?.Id == playlist.Id) await RefreshSpotifyPage();
        _spotifyUiMessage = "Item added to the playlist.";
    }

    private async Task UploadSpotifyCover(SpotifyPlaylist playlist)
    {
        var file = await OpenPicker([".jpg", ".jpeg"]).PickSingleFileAsync();
        if (file is null) return;
        var info = await file.GetBasicPropertiesAsync();
        if (info.Size > 192 * 1024) throw new ArgumentException("Choose a JPEG file of at most 192 KiB for Spotify's playlist cover limit.");
        using var stream = await file.OpenStreamForReadAsync();
        byte[] image = new byte[checked((int)info.Size)];
        await stream.ReadExactlyAsync(image, _spotifyUiLifetime.Token);
        await _spotify.Api.UploadPlaylistCoverAsync(playlist.Id, image, _spotifyUiLifetime.Token);
        _spotifyUiMessage = "Playlist cover uploaded.";
    }

    private async Task OpenSpotifyLocation()
    {
        string? value = await Prompt("Open Spotify item", "Spotify link or URI", "Open", "https://open.spotify.com/playlist/…");
        if (string.IsNullOrWhiteSpace(value)) return;
        var (type, id) = ParseSpotifyLocation(value);
        switch (type)
        {
            case "playlist": await LoadSpotifyPage(SpotifyBrowseMode.Playlist, playlist: await _spotify.Api.GetPlaylistAsync(id, _spotifyUiLifetime.Token)); break;
            case "album": await LoadSpotifyPage(SpotifyBrowseMode.Album, album: await _spotify.Api.GetAlbumAsync(id, _spotifyUiLifetime.Token)); break;
            case "artist": await PlaySpotifyAsync(contextUri: "spotify:artist:" + id); break;
            case "track":
            case "episode": await PlaySpotifyAsync(uris: [$"spotify:{type}:{id}"]); break;
            default: throw new ArgumentException("Choose a Spotify track, episode, playlist, album or artist.");
        }
    }
    private static (string Type, string Id) ParseSpotifyLocation(string value)
    {
        string[] parts;
        if (value.StartsWith("spotify:", StringComparison.Ordinal)) parts = value.Split(':').Skip(1).ToArray();
        else if (Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme == "https" && url.Host == "open.spotify.com")
        {
            parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts[0].StartsWith("intl-", StringComparison.Ordinal)) parts = parts[1..];
        }
        else throw new ArgumentException("Enter an open.spotify.com link or a Spotify URI.");
        if (parts.Length != 2 || parts[1].Length != 22 || !parts[1].All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("The Spotify link or URI has an invalid item ID.");
        return (parts[0], parts[1]);
    }

    private Task SpotifySetupInstructions() => ShowInfo("Connect Spotify", "Create an app at developer.spotify.com and enable Web API and Web Playback SDK. Copy its public client ID into UnoPlay.\n\nRegister this redirect URI exactly in your Spotify app settings.\n\n" + _spotify.RedirectUri.AbsoluteUri +
        "\n\nSpotify Premium is required for playback. Development-mode apps require a Premium owner and allowlisted users.\n\nSign-in uses authorization code with PKCE. Spotify handles your password on its own sign-in page.");

    private async Task<bool> ConfirmSpotify(string title, string message, string action)
    {
        if (_showingDialog) return false;
        _showingDialog = true;
        try
        {
            return await new ContentDialog
            {
                XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark, Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = action, CloseButtonText = "Cancel"
            }.ShowAsync() == ContentDialogResult.Primary;
        }
        finally { _showingDialog = false; }
    }
}
