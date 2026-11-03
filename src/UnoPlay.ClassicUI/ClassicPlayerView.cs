using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UnoPlay.ClassicSkins;
using UnoPlay.Core;
using UnoPlay.Media;
using Windows.Foundation;

namespace UnoPlay.ClassicUI;

/// <summary>A self-contained, classic-skinned player workspace. Hosts can supply their own queue and playback engine.</summary>
public sealed partial class ClassicPlayerView : UserControl, IDisposable
{
    private enum PanelId { Main, Equalizer, Playlist }
    private readonly Canvas _desk = new() { Background = ClassicSurface.Brush(0xff151922) };
    private readonly ClassicSurface[] _panels;
    private readonly Border _video = new() { Background = ClassicSurface.Brush(0xff000000) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly Point[] _points = new Point[3];
    private readonly bool[] _visible = [true, true, true], _shaded = new bool[3];
    private readonly double[] _bands = new double[10];
    private readonly HashSet<Track> _selected = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, double[]> _autoPresets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> _browserFiles = new();
    private ClassicSkin _skin = ClassicSkin.Default;
    private PanelId _active;
    private bool _eq, _auto, _remaining, _doubleSize = true, _placed, _windowMoved, _videoEnabled = true, _disposed;
    private int _playlistWidth = 275, _playlistHeight = 116, _top, _visMode = 1, _selectionAnchor = -1;
    private double _preamp, _scale = 1;
    private string? _pressed;
    private Track? _loaded;
    private long _loadRequest;
    private long _ticks;
    private bool _stopped = true;
    private string? _notice;
    private DateTime _noticeUntil;
    private readonly byte[] _visPixels = new byte[76 * 16 * 4];
    private readonly byte[] _graphPixels = new byte[113 * 19 * 4];
    private Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap? _visImage, _graphImage;
    private readonly bool _ownsEngine;
    public PlayQueue Queue { get; }
    public PlayerEngine Engine { get; }
    public ClassicSkin Skin => _skin;
    public event EventHandler? CloseRequested;
    public event EventHandler? MinimizeRequested;

    public ClassicPlayerView() : this(new PlayerEngine(), new PlayQueue(), true) { }
    public ClassicPlayerView(PlayerEngine engine, PlayQueue queue) : this(engine, queue, false) { }

    private ClassicPlayerView(PlayerEngine engine, PlayQueue queue, bool ownsEngine)
    {
        Engine = engine;
        Queue = queue;
        _ownsEngine = ownsEngine;
        IsTabStop = true;
        Content = _desk;
        RequestedTheme = ElementTheme.Dark;
        var atlas = new SpriteAtlas(_skin);
        _panels = [new(atlas, "Main player"), new(atlas, "Graphic equalizer"), new(atlas, "Playlist editor")];
        InitializeCursors();
        _video.Child = engine.View;
        foreach (var panel in _panels) _desk.Children.Add(panel);
        SetupVideo(atlas);
        _desk.RightTapped += (_, e) => { ShowMenu("options", _desk, e.GetPosition(_desk)); e.Handled = true; };
        EnableDrop();
        KeyDown += HandleKey;
        SizeChanged += (_, _) => ArrangeWorkspace(reset: !_windowMoved);
        Loaded += (_, _) =>
        {
            if (_disposed) return;
            UpdateAnimationClock();
            _ = InitializeSpotifyAsync();
            Focus(FocusState.Programmatic);
            ArrangeWorkspace(reset: !_windowMoved);
        };
        Unloaded += (_, _) => _timer.Stop();
        _timer.Tick += (_, _) =>
        {
            if (_disposed) return;
            _ticks++;
            if (_notice is not null && DateTime.UtcNow >= _noticeUntil) _notice = null;
            Draw(PanelId.Main);
            if (_ticks % 6 == 0 && CurrentIsPlaying) Draw(PanelId.Playlist);
            UpdateAnimationClock();
        };
        Queue.Tracks.CollectionChanged += TracksChanged;
        Engine.StateChanged += EngineStateChanged;
        Engine.Ended += EngineEnded;
        Engine.TransportRequested += EngineTransportRequested;
        Engine.Failed += EngineFailed;
        RefreshLayout();
    }

    public void ApplySkin(ClassicSkin skin)
    {
        ArgumentNullException.ThrowIfNull(skin);
        _skin = skin;
        ResetCursors();
        var atlas = new SpriteAtlas(skin);
        foreach (var panel in _panels) panel.Atlas = atlas;
        if (_videoFrame is not null) _videoFrame.Atlas = atlas;
        _libraryWindow?.SetSkin(skin);
        _browserWindow?.SetSkin(skin);
        UpdateSpotifySkin();
        RefreshLayout();
    }

    void IDisposable.Dispose() => Shutdown();

    public void Shutdown()
    {
        if (_disposed) return;
        SavePreferences();
        _preferencesTimer?.Stop();
        _disposed = true;
        ShutdownSpotify();
        CloseBrowserHost();
        ResetCursors();
        CancelProbe();
        _timer.Stop();
        Queue.Tracks.CollectionChanged -= TracksChanged;
        Engine.StateChanged -= EngineStateChanged;
        Engine.Ended -= EngineEnded;
        Engine.TransportRequested -= EngineTransportRequested;
        Engine.Failed -= EngineFailed;
        if (_ownsEngine) Engine.Dispose();
        foreach (var file in _browserFiles) file.Dispose();
        _browserFiles.Clear();
    }

    private ClassicSurface Panel(PanelId id) => _panels[(int)id];
    private int HeightOf(PanelId id) => _shaded[(int)id] ? 14 : id == PanelId.Playlist ? _playlistHeight : 116;
    private int WidthOf(PanelId id) => id == PanelId.Playlist ? _playlistWidth : 275;
    private int Rows => Math.Max(1, (_playlistHeight - 61) / 13);
    private void RefreshLayout()
    {
        DismissClassicMenu();
        for (var i = 0; i < _panels.Length; i++)
        {
            var id = (PanelId)i;
            Panel(id).Visibility = _visible[i] ? Visibility.Visible : Visibility.Collapsed;
            Panel(id).DefineSize(WidthOf(id), HeightOf(id));
            var region = id switch
            {
                PanelId.Main => _shaded[0] ? "WindowShade" : "Normal",
                PanelId.Equalizer => _shaded[1] ? "EqualizerWS" : "Equalizer",
                _ => ""
            };
            Panel(id).SetRegion(_skin.GetRegion(region));
            BuildTargets(id);
        }
        ArrangeWorkspace(reset: !_windowMoved);
        DrawAll();
        QueuePreferencesSave();
    }

    private void ArrangeWorkspace(bool reset)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width < 1 || height < 1) return;
        _scale = _doubleSize && width >= Math.Max(275, _playlistWidth) * 2 + 16 &&
            height >= Enumerable.Range(0, 3).Where(i => _visible[i]).Sum(i => HeightOf((PanelId)i)) * 2 + 16 ? 2 : 1;
        if (width < 275) _scale = width / 275;
        if (reset || !_placed)
        {
            int clusterHeight = Enumerable.Range(0, 3).Where(i => _visible[i]).Sum(i => HeightOf((PanelId)i));
            int clusterWidth = _visible[2] ? _playlistWidth : 275;
            double x = Math.Max(0, Math.Floor((width - clusterWidth * _scale) / (2 * _scale)));
            double y = Math.Max(0, Math.Floor((height - clusterHeight * _scale) / (2 * _scale)));
            for (int i = 0; i < 3; i++)
            {
                _points[i] = new Point(x, y);
                if (_visible[i]) y += HeightOf((PanelId)i);
            }
            _placed = true;
        }
        for (int i = 0; i < 3; i++)
        {
            var panel = _panels[i];
            panel.SetZoom(_scale);
            SetPosition((PanelId)i, _points[i]);
        }
        ArrangeVideo();
    }

    private void SetPosition(PanelId id, Point pt)
    {
        pt.X = Math.Clamp(pt.X, 0, Math.Max(0, ActualWidth / _scale - Math.Min(WidthOf(id), 40)));
        pt.Y = Math.Clamp(pt.Y, 0, Math.Max(0, ActualHeight / _scale - 14));
        _points[(int)id] = pt;
        Canvas.SetLeft(Panel(id), Math.Round(pt.X * _scale));
        Canvas.SetTop(Panel(id), Math.Round(pt.Y * _scale));
    }

    private void Activate(PanelId id)
    {
        ReleaseToolFocus();
        if (_videoFocus) { SetVideoFocus(false); ArrangeVideo(); }
        if (_active == id) return;
        _active = id;
        foreach (var panel in _panels) Canvas.SetZIndex(panel, panel == Panel(id) ? 3 : 1);
        DrawAll();
    }

    private void DrawAll() { Draw(PanelId.Main); Draw(PanelId.Equalizer); Draw(PanelId.Playlist); }
    private void Draw(PanelId id)
    {
        if (!_visible[(int)id]) return;
        var p = Panel(id);
        p.Begin();
        switch (id)
        {
            case PanelId.Main: DrawMain(p); break;
            case PanelId.Equalizer: DrawEq(p); break;
            case PanelId.Playlist: DrawPlaylist(p); break;
        }
        p.End();
    }

    private void UpdateAnimationClock()
    {
        // A stopped or paused player with a static title has nothing to animate.
        bool scrolling = !_stopped && !_shaded[0] && CurrentTitle.Length > 30;
        bool needed = !_disposed && IsLoaded && (_notice is not null || CurrentIsPlaying || scrolling);
        if (needed && !_timer.IsEnabled) _timer.Start();
        else if (!needed && _timer.IsEnabled) _timer.Stop();
    }

    private void Notify(string message)
    {
        _notice = message;
        _noticeUntil = DateTime.UtcNow.AddSeconds(4);
        UpdateAnimationClock();
        Draw(PanelId.Main);
    }

    private static string Clock(TimeSpan t) => $"{(int)Math.Max(0,t.TotalMinutes)}:{Math.Max(0,t.Seconds):00}";
    private TimeSpan TimeDisplay => _remaining && CurrentDuration > CurrentPosition ? CurrentDuration - CurrentPosition : _remaining ? TimeSpan.Zero : CurrentPosition;
    private bool Down(string id) => _pressed == id;
    private void OnUi(Action work)
    {
        if (_disposed) return;
        if (DispatcherQueue.HasThreadAccess) work(); else DispatcherQueue.TryEnqueue(() => { if (!_disposed) work(); });
    }
    private void EngineStateChanged(object? sender, EventArgs e) => OnUi(() =>
    {
        if (_loaded is not null && Engine.Duration > TimeSpan.Zero && _knownDurations.GetValueOrDefault(_loaded) != Engine.Duration)
        {
            _knownDurations[_loaded] = Engine.Duration;
            _totalDuration = SumDuration(Queue.Tracks);
            _selectedDuration = SumDuration(_selected);
        }
        UpdateAnimationClock();
        ArrangeVideo(); DrawAll();
    });
    private void EngineFailed(object? sender, string error) => OnUi(() => { if (!_spotifyActive) { _stopped = true; Notify(error); } });
    private void EngineEnded(object? sender, EventArgs e) => OnUi(() => { if (!_spotifyActive) _ = Guard(() => Advance(false)); });
    private bool _drawQueuePending;
    private void TracksChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        _top = Math.Clamp(_top, 0, Math.Max(0, Queue.Tracks.Count - Rows));
        // Apply a multi-file paste, reorder or playlist import as one visual update.
        if (_drawQueuePending) return;
        _drawQueuePending = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _drawQueuePending = false;
            if (_disposed) return;
            _totalDuration = SumDuration(Queue.Tracks);
            if (_selected.Count > 0)
            {
                var present = Queue.Tracks.ToHashSet(ReferenceEqualityComparer.Instance);
                _selected.RemoveWhere(t => !present.Contains(t));
                _selectedDuration = SumDuration(_selected);
            }
            Draw(PanelId.Playlist);
            RefreshLibrary();
        });
    }
}
