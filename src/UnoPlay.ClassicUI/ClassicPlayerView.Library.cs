using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UnoPlay.ClassicSkins;
using UnoPlay.Core;
using UnoPlay.Media;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace UnoPlay.ClassicUI;

public sealed partial class ClassicPlayerView
{
    private static readonly string[] AudioVideo = [".mp3", ".mp4", ".m4a", ".aac", ".ogg", ".oga", ".ogv", ".opus", ".wav", ".flac", ".webm", ".m4v", ".mov", ".mkv"];
    private static readonly string[] ListFormats = [".m3u", ".m3u8", ".pls"];
    private readonly Dictionary<Track, TimeSpan> _knownDurations = new(ReferenceEqualityComparer.Instance);
    private TimeSpan _totalDuration, _selectedDuration;
    private bool _showingDialog;

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Notify(ex.Message); }
    }

    private void Menu(string group, FrameworkElement el)
    { if (!OpenClassicMenu(group, el)) NativeMenu(group, el); }
    private void ShowMenu(string group, FrameworkElement anchor, Point location)
    {
        var menu = new MenuFlyout();
        void Add(string name, Action action)
        {
            var item = new MenuFlyoutItem { Text = name };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        void Async(string name, Func<Task> action) => Add(name, () => _ = Guard(action));
        void Sep() => menu.Items.Add(new MenuFlyoutSeparator());
        switch (group)
        {
            case "add":
                Async("Add files…", () => PickMedia(false)); Async("Add folder…", PickFolder); Async("Add URL…", OpenUrl); break;
            case "remove":
                Add("Remove selected", () => RemoveSelected(false)); Add("Crop to selection", () => RemoveSelected(true)); Add("Clear playlist", ClearList); break;
            case "select":
                Add("Select all", SelectAll); Add("Select none", () => { _selected.Clear(); SelectionChanged(); });
                Add("Invert selection", () => { foreach (var track in Queue.Tracks) if (!_selected.Remove(track)) _selected.Add(track); SelectionChanged(); }); break;
            case "tools":
                Async("Jump to track…", Jump); Async("Track information", TrackInfo);
                Add("Move selection up", () => MoveSelection(-1)); Add("Move selection down", () => MoveSelection(1));
                Add("Sort by title", SortPlaylist); Add("Reverse list", ReversePlaylist); break;
            case "lists":
                Async("Open playlist…", PickPlaylist); Async("Save playlist…", SavePlaylist); Add("New playlist", ClearList); break;
            case "presets": BuildPresetMenu(menu); break;
            default:
                Async("Open files…    L", () => PickMedia(true)); Async("Open URL…    Ctrl+L", OpenUrl);
                Async("Open playlist…", PickPlaylist); Async("Save playlist…", SavePlaylist); Sep();
                Async("Load classic skin (.wsz)…", PickSkin); Add("Use built-in skin", () => ApplySkin(ClassicSkin.Default)); Sep();
                Add(_visible[1] ? "Hide equalizer" : "Show equalizer", () => Toggle(PanelId.Equalizer));
                Add(_visible[2] ? "Hide playlist" : "Show playlist", () => Toggle(PanelId.Playlist));
                Add("Toggle video window", () => { _videoEnabled = !_videoEnabled; ArrangeVideo(); });
                Add("Toggle double size", Zoom);
                Add("Arrange windows", () => { _windowMoved = false; ArrangeWorkspace(true); });
                Sep(); Async("Jump to track…    J", Jump); Async("Track information", TrackInfo); Async("About UnoPlay / shortcuts", About); break;
        }
        menu.ShowAt(anchor, location);
    }

    private void ApplyEq()
    { Engine.SetEqualizer(_eq, _preamp, _bands); Draw(PanelId.Equalizer); }
    private void RememberEq()
    {
        if (_loaded is null) return;
        _autoPresets[_loaded.Location] = [_preamp, .. _bands]; Notify("TRACK PRESET SAVED");
    }
    private void RecallEq()
    {
        if (_loaded is null || !_autoPresets.TryGetValue(_loaded.Location, out var values)) return;
        _preamp = values[0]; Array.Copy(values, 1, _bands, 0, 10); ApplyEq();
    }

    private async Task PickMedia(bool play)
    {
        int first = Queue.Tracks.Count;
        if (OperatingSystem.IsBrowser())
        {
            foreach (var file in await PlayerEngine.PickBrowserFilesAsync())
            {
                _browserFiles.Add(file);
                Queue.Tracks.Add(new Track(file.Uri.AbsoluteUri, Path.GetFileNameWithoutExtension(file.Name), Source: file));
            }
        }
        else
        {
            var picker = OpenPicker(AudioVideo);
            foreach (var file in await picker.PickMultipleFilesAsync()) AddStorage(file);
        }
        if (play && first < Queue.Tracks.Count) await PlayIndex(first);
        else if (first < Queue.Tracks.Count) Notify($"{Queue.Tracks.Count - first} FILES ADDED");
    }

    private static FileOpenPicker OpenPicker(IEnumerable<string> extensions)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
        foreach (string ext in extensions) picker.FileTypeFilter.Add(ext);
        return picker;
    }

    private void AddStorage(StorageFile file) => Queue.Tracks.Add(new Track(string.IsNullOrEmpty(file.Path) ? file.Name : file.Path,
        Path.GetFileNameWithoutExtension(file.Name), Source: file));

    private async Task PickFolder()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        foreach (var file in await folder.GetFilesAsync())
            if (AudioVideo.Contains(Path.GetExtension(file.Name), StringComparer.OrdinalIgnoreCase)) AddStorage(file);
    }

    private async Task PickSkin()
    {
        var file = await OpenPicker([".wsz", ".zip"]).PickSingleFileAsync();
        if (file is null) return;
        using var stream = await file.OpenStreamForReadAsync();
        ApplySkin(ClassicSkin.Load(stream));
        Notify(Path.GetFileNameWithoutExtension(file.Name));
    }

    private async Task PickPlaylist()
    {
        var file = await OpenPicker(ListFormats).PickSingleFileAsync();
        if (file is null) return;
        using var stream = await file.OpenStreamForReadAsync();
        if (stream.CanSeek && stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("Playlist is larger than 4 MiB.");
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        string contents = await reader.ReadToEndAsync();
        Uri? baseUri = !OperatingSystem.IsBrowser() && Path.IsPathRooted(file.Path) ? new Uri(file.Path) : null;
        var tracks = PlaylistFile.Read(contents, Path.GetExtension(file.Name), baseUri);
        if (tracks.Count == 0) throw new InvalidDataException("No playable entries in this playlist.");
        // Browser file grants identify files by object URL. Resolve playlist-relative names against already granted local files.
        var grants = Queue.Tracks.Where(t => t.Source is BrowserMediaFile)
            .GroupBy(t => ((BrowserMediaFile)t.Source!).Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            if (grants.TryGetValue(Path.GetFileName(track.Location.Replace('\\', '/')), out var matching) && matching.Length == 1 &&
                !Uri.TryCreate(track.Location, UriKind.Absolute, out _))
                Queue.Tracks.Add(track with { Location = matching[0].Location, Source = matching[0].Source });
            else Queue.Tracks.Add(track);
        }
        Notify($"{tracks.Count} PLAYLIST ENTRIES ADDED");
    }

    private async Task SavePlaylist()
    {
        var picker = new FileSavePicker { SuggestedFileName = "UnoPlay", SuggestedStartLocation = PickerLocationId.MusicLibrary };
        picker.FileTypeChoices.Add("M3U playlist", new List<string> { ".m3u" });
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        var tracks = Queue.Tracks.Select(t => t.Source is BrowserMediaFile local ? t with { Location = local.Name } : t);
        await FileIO.WriteTextAsync(file, PlaylistFile.WriteM3u(tracks));
        Notify("PLAYLIST SAVED");
    }

    private async Task<string?> Prompt(string title, string caption, string action, string placeholder)
    {
        if (_showingDialog) return null;
        _showingDialog = true;
        try
        {
            var input = new TextBox { Header = caption, PlaceholderText = placeholder, MinWidth = 230 };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark, Title = title, Content = input,
                PrimaryButtonText = action, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text.Trim() : null;
        }
        finally { _showingDialog = false; }
    }

    private async Task OpenUrl()
    {
        var value = await Prompt("Open location", "Audio, video, or stream URL", "Play", "https://…");
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new ArgumentException("Enter an HTTP or HTTPS media URL.");
        Queue.Tracks.Add(new Track(uri.AbsoluteUri, Uri.UnescapeDataString(uri.Segments.LastOrDefault()?.Trim('/') ?? "") is { Length: > 0 } name ? name : uri.Host));
        await PlayIndex(Queue.Tracks.Count - 1);
    }
    private async Task Jump()
    {
        var search = await Prompt("Jump to track", "Title or playlist number", "Play", "Track title");
        if (string.IsNullOrWhiteSpace(search)) return;
        int index = int.TryParse(search, out var n) && n > 0 && n <= Queue.Tracks.Count ? n - 1
            : Queue.Tracks.Select((t, i) => t.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ? i : -1).FirstOrDefault(i => i >= 0, -1);
        if (index >= 0) await PlayIndex(index); else Notify("NO MATCHING TRACK");
    }

    private async Task ShowInfo(string title, string message)
    {
        if (_showingDialog) return;
        _showingDialog = true;
        try { await new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark, Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "OK" }.ShowAsync(); }
        finally { _showingDialog = false; }
    }

    private Task TrackInfo()
    {
        var track = _selected.FirstOrDefault() ?? _loaded ?? Queue.Tracks.FirstOrDefault();
        return track is null ? ShowInfo("Track information", "The playlist is empty.")
            : ShowInfo(track.Title, (track.Source is BrowserMediaFile file ? file.Name : track.Location)
                + (ReferenceEquals(track, _loaded) ? $"\n\n{Clock(Engine.Position)} / {Clock(Engine.Duration)}" : ""));
    }
    private Task About() => ShowInfo("UnoPlay Classic", "Open audio, video, or a stream from the title-bar menu. Load a classic .wsz skin to replace the player, equalizer and playlist artwork.\n\nZ / X / C / V / B   Previous, play, pause, stop, next\nL   Open files      Ctrl+L   Open location\nJ   Jump to track      R / S   Repeat / shuffle\n← / →   Seek 5 seconds      ↑ / ↓   Volume\n\nDouble-click a title bar to roll it up. Drag a title bar to dock windows. Drag the playlist’s lower-right grip to resize.");
    private async Task ClosePlayer()
    {
        if (CloseRequested is not null) { CloseRequested(this, EventArgs.Empty); return; }
        if (_showingDialog) return;
        _showingDialog = true;
        try
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Close player?", Content = "Stop playback and close the current playlist.", PrimaryButtonText = "Close playlist", CloseButtonText = "Cancel" };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) ClearList();
        }
        finally { _showingDialog = false; }
    }

    private async Task Play()
    {
        if (_loaded is not null && ReferenceEquals(_loaded, Queue.Current)) { _stopped = false; await Engine.PlayAsync(); }
        else if (Queue.Tracks.Count > 0) await PlayIndex(Math.Max(0, IndexOf(Queue.Current)));
        else await PickMedia(true);
        DrawAll();
    }
    private Task Pause() { Engine.Pause(); DrawAll(); return Task.CompletedTask; }
    private Task Stop() { _loadRequest++; Engine.Stop(); _stopped = true; DrawAll(); return Task.CompletedTask; }
    private async Task Advance(bool manual)
    {
        var next = Queue.Next(manual);
        if (next is null) await Stop(); else await Open(next);
    }
    private async Task Previous()
    {
        if (Engine.Position.TotalSeconds > 3 && Engine.CanSeek) { Engine.Seek(TimeSpan.Zero); return; }
        var prev = Queue.Previous();
        if (prev is not null) await Open(prev);
    }
    private async Task PlayIndex(int index)
    {
        if (index < 0 || index >= Queue.Tracks.Count) return;
        var track = Queue.Select(index);
        if (track is not null) await Open(track);
    }
    private async Task Open(Track track)
    {
        var generation = ++_loadRequest;
        _loaded = track; _stopped = false;
        if (track.Source is StorageFile file) await Engine.OpenAsync(file);
        else if (Uri.TryCreate(track.Location, UriKind.Absolute, out var uri) &&
                 (uri.Scheme is "http" or "https" or "blob" || !OperatingSystem.IsBrowser() && uri.IsFile))
            await Engine.OpenAsync(uri);
        else
        {
            _loaded = null; _stopped = true; Engine.Stop();
            throw new InvalidDataException("Select the referenced local media files before importing this playlist.");
        }
        if (generation != _loadRequest) return;
        if (_auto) RecallEq();
        EnsureVisible(IndexOf(track));
        await Engine.PlayAsync();
        if (generation == _loadRequest) DrawAll();
    }

    private int IndexOf(Track? track)
    {
        if (track is null) return -1;
        for (int i = 0; i < Queue.Tracks.Count; i++) if (ReferenceEquals(Queue.Tracks[i], track)) return i;
        return -1;
    }
    private void EnsureVisible(int index)
    {
        if (index < _top) _top = Math.Max(0,index);
        else if (index >= _top + Rows) _top = index - Rows + 1;
    }

    private void SelectRow(int index)
    {
        if (index < 0 || index >= Queue.Tracks.Count) return;
        bool ctrl = Modifier(VirtualKey.Control);
        if (!ctrl) _selected.Clear();
        if (Modifier(VirtualKey.Shift) && _selectionAnchor >= 0)
        {
            for (int i = Math.Min(index,_selectionAnchor); i <= Math.Min(Queue.Tracks.Count - 1, Math.Max(index,_selectionAnchor)); i++)
                _selected.Add(Queue.Tracks[i]);
        }
        else
        {
            if (!ctrl || !_selected.Remove(Queue.Tracks[index])) _selected.Add(Queue.Tracks[index]);
            _selectionAnchor = index;
        }
        SelectionChanged();
    }
    private void SelectionChanged()
    {
        _selectedDuration = SumDuration(_selected);
        Draw(PanelId.Playlist);
    }
    private TimeSpan SumDuration(IEnumerable<Track> tracks)
        => TimeSpan.FromSeconds(Math.Min(TimeSpan.MaxValue.TotalSeconds - 1, tracks.Sum(t => Math.Max(0, (t.Duration ?? _knownDurations.GetValueOrDefault(t)).TotalSeconds))));
    private void SelectAll() { _selected.UnionWith(Queue.Tracks); SelectionChanged(); }
    private void ClearList() { _ = Stop(); _loaded = null; Queue.Clear(); _selected.Clear(); _knownDurations.Clear(); _selectionAnchor = -1; ReleaseUnusedFiles(); SelectionChanged(); DrawAll(); }
    private void ReleaseUnusedFiles()
    {
        var used = Queue.Tracks.Select(track => track.Source).OfType<BrowserMediaFile>().ToHashSet();
        for (int i = _browserFiles.Count - 1; i >= 0; i--)
            if (_browserFiles[i] is BrowserMediaFile file && !used.Contains(file)) { file.Dispose(); _browserFiles.RemoveAt(i); }
    }
    private void RemoveSelected(bool crop)
    {
        for (int i = Queue.Tracks.Count - 1; i >= 0; i--)
        {
            var track = Queue.Tracks[i];
            if (_selected.Contains(track) == crop) continue;
            if (ReferenceEquals(track, _loaded)) { _ = Stop(); _loaded = null; }
            _knownDurations.Remove(track);
            Queue.RemoveAt(i);
        }
        ReleaseUnusedFiles(); SelectionChanged(); DrawAll();
    }
    private void MoveSelection(int step)
    {
        var indices = Enumerable.Range(0,Queue.Tracks.Count).Where(i => _selected.Contains(Queue.Tracks[i]));
        foreach (var i in (step < 0 ? indices : indices.Reverse()).ToArray())
            if (i + step >= 0 && i + step < Queue.Tracks.Count && !_selected.Contains(Queue.Tracks[i + step])) Queue.Move(i, i + step);
        Draw(PanelId.Playlist);
    }
    private void SortPlaylist()
    {
        var tracks = Queue.Tracks.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        for (int i = 0; i < tracks.Length; i++) { int index = IndexOf(tracks[i]); if (index != i) Queue.Move(index, i); }
    }
    private void ReversePlaylist()
    { for (int i = 1; i < Queue.Tracks.Count; i++) Queue.Move(i, 0); }
}
