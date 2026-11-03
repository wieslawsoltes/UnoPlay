using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace UnoPlay.ClassicUI;

public sealed partial class ClassicPlayerView
{
    private void BuildTargets(PanelId id)
    {
        var panel = Panel(id);
        panel.Targets.Children.Clear();
        int w = WidthOf(id), h = HeightOf(id);
        DragWindow(id, 0, 0, w, id == PanelId.Playlist && !_shaded[2] ? 20 : 14);
        if (id == PanelId.Main)
        {
            Button(id, "Options", 6, 3, 9, 9, el => Menu("options", el));
            Button(id, "Minimize", 244, 3, 9, 9, _ =>
            {
                if (MinimizeRequested is not null) MinimizeRequested(this, EventArgs.Empty);
                else Shade(id);
            });
            Button(id, "Shade main", 254, 3, 9, 9, _ => Shade(id));
            Button(id, "Close", 264, 3, 9, 9, el => _ = Guard(ClosePlayer));
            if (_shaded[0])
            {
                int[] starts = [169, 176, 186, 195, 204, 215, 225];
                for (int i = 0; i < 6; i++)
                {
                    int action = i;
                    Button(id, i == 5 ? "Open files" : TransportNames[i], starts[i], 2, starts[i + 1] - starts[i], 10, _ => Transport(action));
                }
                Button(id, "Visualization", 79, 4, 38, 7, _ => CycleVis());
                Button(id, "Time display", 127, 3, 36, 9, _ => { _remaining = !_remaining; DrawAll(); });
                Slider(id, "Seek", 226, 4, 17, 7, false, 3, n => SeekFraction(n));
                return;
            }
            for (int i = 0; i < 5; i++)
            {
                int action = i;
                Button(id, TransportNames[i], 16 + i * 23, 88, i == 4 ? 22 : 23, 18, _ => Transport(action));
            }
            Button(id, "Open files", 136, 89, 22, 16, el => _ = Guard(() => PickMedia(true)));
            Button(id, "Shuffle", 164, 89, 46, 15, _ => ToggleShuffle());
            Button(id, "Repeat", 210, 89, 28, 15, _ => ToggleRepeat());
            Button(id, "Equalizer", 219, 58, 23, 12, _ => Toggle(PanelId.Equalizer));
            Button(id, "Playlist", 242, 58, 23, 12, _ => Toggle(PanelId.Playlist));
            Button(id, "Time display", 37, 24, 63, 16, _ => { _remaining = !_remaining; DrawAll(); });
            var visualizer = Button(id, "Visualization", 24, 43, 76, 16, _ => CycleVis());
            visualizer.RightTapped += (_, e) => { e.Handled = true; VisualizationMenu(visualizer); };
            Button(id, "Track information", 108, 23, 156, 14, el => _ = Guard(TrackInfo));
            Button(id, "About UnoPlay", 252, 90, 15, 17, el => _ = Guard(About));
            Button(id, "Player menu", 10, 24, 8, 9, el => Menu("options", el));
            Button(id, "Audio settings", 10, 33, 8, 7, _ => Toggle(PanelId.Equalizer));
            Button(id, "File information", 10, 40, 8, 7, el => _ = Guard(TrackInfo));
            Button(id, "Double size", 10, 47, 8, 8, _ => Zoom());
            Button(id, "Analyzer mode", 10, 55, 8, 8, _ => CycleVis());
            Slider(id, "Volume", 107, 57, 65, 13, false, 14, n => SetVolume(n));
            Slider(id, "Balance", 177, 57, 38, 13, false, 14, n => SetBalance(n));
            Slider(id, "Seek", 16, 72, 248, 10, false, 29, n => SeekFraction(n));
            return;
        }
        if (id == PanelId.Equalizer)
        {
            Button(id, "Shade equalizer", 254, 3, 9, 9, _ => Shade(id));
            Button(id, "Close equalizer", 264, 3, 9, 9, _ => Toggle(id));
            if (_shaded[1])
            {
                Slider(id, "Volume", 61, 4, 97, 7, false, 3, n => SetVolume(n));
                Slider(id, "Balance", 164, 4, 43, 7, false, 3, n => SetBalance(n));
                return;
            }
            Button(id, "Enable equalizer", 14, 18, 26, 12, _ =>
            {
                if (_spotifyActive || _loaded is not null && !Engine.SupportsDsp) { Notify("EQUALIZER UNAVAILABLE FOR THIS OUTPUT"); return; }
                _eq = !_eq; ApplyEq();
            });
            Button(id, "Auto presets", 40, 18, 32, 12, _ => { _auto = !_auto; if (_auto) RecallEq(); Draw(id); });
            Button(id, "Presets", 217, 18, 44, 12, el => Menu("presets", el));
            for (int i = -1; i < 10; i++)
            {
                int band = i;
                Slider(id, BandName(i), BandX(i), 38, 14, 63, true, 11, n =>
                {
                    double db = Math.Round((.5 - n) * 48) / 2;
                    if (band < 0) _preamp = db; else _bands[band] = db;
                    ApplyEq();
                    Notify($"{BandName(band)} {db:+0.0;-0.0;0} DB");
                });
            }
            return;
        }
        Button(id, "Shade playlist", w - 21, 3, 9, 9, _ => Shade(id));
        Button(id, "Close playlist", w - 11, 3, 9, 9, _ => Toggle(id));
        if (_shaded[2]) return;
        for (int row = 0; row < Rows; row++)
        {
            int offset = row;
            var hit = Button(id, "Queue row " + (row + 1), 12, 22 + row * 13, w - 32, 13, _ => SelectRow(_top + offset));
            hit.AddHandler(DoubleTappedEvent, new DoubleTappedEventHandler((_, e) => { e.Handled = true; _ = Guard(() => PlayIndex(_top + offset)); }), true);
            hit.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; _ = Guard(() => PlayIndex(_top + offset)); } };
            hit.PointerWheelChanged += PlaylistWheel;
        }
        Slider(id, "Scroll playlist", w - 17, 20, 12, h - 58, true, 18, n => { _top = (int)Math.Round(n * Math.Max(0, Queue.Tracks.Count - Rows)); Draw(id); });
        Button(id, "Add to playlist", 14, h - 30, 22, 18, el => Menu("add", el));
        Button(id, "Remove tracks", 43, h - 30, 22, 18, el => Menu("remove", el));
        Button(id, "Select tracks", 72, h - 30, 22, 18, el => Menu("select", el));
        Button(id, "Playlist tools", 101, h - 30, 22, 18, el => Menu("tools", el));
        Button(id, "Playlist files", w - 44, h - 30, 22, 18, el => Menu("lists", el));
        for (int i = 0; i < 6; i++)
        {
            int action = i;
            Button(id, "Playlist " + (i < 5 ? TransportNames[i] : "Open"), w - 147 + i * 10, h - 16, 10, 10, _ => Transport(action));
        }
        Button(id, "Scroll up", w - 16, h - 37, 10, 6, _ => ScrollBy(-1));
        Button(id, "Scroll down", w - 16, h - 31, 10, 6, _ => ScrollBy(1));
        ResizeGrip();
    }

    private Button Button(PanelId id, string name, int x, int y, int w, int h, Action<FrameworkElement> action)
    {
        var b = new Button { Opacity = 0, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), Content = name };
        bool outside = false;
        AutomationProperties.SetName(b, name);
        AutomationProperties.SetAutomationId(b, name.Replace(" ", ""));
        ToolTipService.SetToolTip(b, name);
        b.AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (!e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) return;
            outside = !Panel(id).AcceptsPoint(e.GetCurrentPoint(Panel(id)).Position);
            if (!outside) { Activate(id); _pressed = name; Draw(id); }
        }), true);
        b.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => { _pressed = null; Draw(id); }), true);
        b.PointerCaptureLost += (_, _) => { if (_pressed == name) { _pressed = null; Draw(id); } };
        b.Click += (_, _) => { if (!outside) { Activate(id); action(b); QueuePreferencesSave(); } outside = false; };
        Panel(id).PlaceTarget(b, x, y, w, h);
        Panel(id).Targets.Children.Add(b);
        return b;
    }

    private Border Gesture(PanelId id, string name, int x, int y, int w, int h)
    {
        var b = new Border { Background = ClassicSurface.Brush(0x00000000) };
        AutomationProperties.SetName(b, name);
        Panel(id).PlaceTarget(b, x, y, w, h);
        Panel(id).Targets.Children.Add(b);
        return b;
    }

    private void Slider(PanelId id, string name, int x, int y, int w, int h, bool vertical, int thumb, Action<double> change)
    {
        var hit = Gesture(id, name, x, y, w, h);
        ToolTipService.SetToolTip(hit, name);
        bool captured = false;
        void Set(PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(hit).Position;
            double n = ((vertical ? pt.Y : pt.X) / Panel(id).Zoom - thumb / 2d) / ((vertical ? h : w) - thumb);
            change(Math.Clamp(n, 0, 1));
        }
        hit.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(hit).Properties.IsLeftButtonPressed || !Panel(id).AcceptsPoint(e.GetCurrentPoint(Panel(id)).Position)) return;
            Activate(id);
            _pressed = name;
            captured = hit.CapturePointer(e.Pointer);
            Set(e); e.Handled = true;
        };
        hit.PointerMoved += (_, e) => { if (captured) { Set(e); e.Handled = true; } };
        hit.PointerReleased += (_, e) => { if (captured) { Set(e); hit.ReleasePointerCapture(e.Pointer); e.Handled = true; } };
        hit.PointerCaptureLost += (_, _) => { captured = false; _pressed = null; Draw(id); };
    }

    private void DragWindow(PanelId id, int x, int y, int w, int h)
    {
        var hit = Gesture(id, id + " title bar", x, y, w, h);
        Point start = default, origin = default;
        bool dragging = false;
        List<(PanelId Id, Point Origin)> docked = [];
        hit.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(hit).Properties.IsLeftButtonPressed || !Panel(id).AcceptsPoint(e.GetCurrentPoint(Panel(id)).Position)) return;
            Activate(id); start = e.GetCurrentPoint(_desk).Position; origin = _points[(int)id];
            docked.Clear();
            if (id == PanelId.Main)
            {
                double bottom = origin.Y + HeightOf(id);
                for (int i = 1; i < 3; i++)
                    if (_visible[i] && Math.Abs(_points[i].X - origin.X) < 2 && Math.Abs(_points[i].Y - bottom) < 2)
                    { docked.Add(((PanelId)i, _points[i])); bottom += HeightOf((PanelId)i); }
            }
            dragging = hit.CapturePointer(e.Pointer);
        };
        hit.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var pt = e.GetCurrentPoint(_desk).Position;
            var target = new Point(origin.X + (pt.X - start.X) / _scale, origin.Y + (pt.Y - start.Y) / _scale);
            if (Math.Abs(target.X - origin.X) + Math.Abs(target.Y - origin.Y) < 2) return;
            _windowMoved = true;
            for (int i = 0; i < 3; i++)
            {
                if (i == (int)id || !_visible[i] || docked.Any(t => (int)t.Id == i)) continue;
                var other = _points[i];
                if (Math.Abs(target.X - other.X) <= 9) target.X = other.X;
                if (Math.Abs(target.X - other.X) < WidthOf(id))
                {
                    if (Math.Abs(target.Y - other.Y - HeightOf((PanelId)i)) <= 9) target.Y = other.Y + HeightOf((PanelId)i);
                    if (Math.Abs(target.Y + HeightOf(id) - other.Y) <= 9) target.Y = other.Y - HeightOf(id);
                }
            }
            SetPosition(id, target);
            foreach (var pair in docked) SetPosition(pair.Id, new Point(pair.Origin.X + target.X - origin.X, pair.Origin.Y + target.Y - origin.Y));
        };
        hit.PointerReleased += (_, e) => hit.ReleasePointerCapture(e.Pointer);
        hit.PointerCaptureLost += (_, _) => dragging = false;
        hit.DoubleTapped += (_, e) => { e.Handled = true; Shade(id); };
    }

    private void ResizeGrip()
    {
        var hit = Gesture(PanelId.Playlist, "Resize playlist", _playlistWidth - 20, _playlistHeight - 20, 20, 20);
        bool dragging = false;
        Point start = default;
        int w = 0, h = 0;
        hit.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(hit).Properties.IsLeftButtonPressed) return;
            start = e.GetCurrentPoint(_desk).Position; w = _playlistWidth; h = _playlistHeight;
            dragging = hit.CapturePointer(e.Pointer);
        };
        hit.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var pt = e.GetCurrentPoint(_desk).Position;
            _playlistWidth = 275 + 25 * Math.Clamp((int)Math.Round((w - 275 + (pt.X - start.X) / _scale) / 25), 0, 48);
            _playlistHeight = 116 + 29 * Math.Clamp((int)Math.Round((h - 116 + (pt.Y - start.Y) / _scale) / 29), 0, 28);
            Panel(PanelId.Playlist).DefineSize(_playlistWidth, _playlistHeight);
            Draw(PanelId.Playlist);
        };
        hit.PointerReleased += (_, e) => hit.ReleasePointerCapture(e.Pointer);
        hit.PointerCaptureLost += (_, _) => { if (dragging) { dragging = false; RefreshLayout(); } };
    }

    private void ScrollBy(int count) { _top = Math.Clamp(_top + count, 0, Math.Max(0, Queue.Tracks.Count - Rows)); Draw(PanelId.Playlist); }
    private void PlaylistWheel(object sender, PointerRoutedEventArgs e)
    { ScrollBy(-Math.Sign(e.GetCurrentPoint(Panel(PanelId.Playlist)).Properties.MouseWheelDelta) * 3); e.Handled = true; }
    private void Toggle(PanelId id) { _visible[(int)id] = !_visible[(int)id]; RefreshLayout(); }
    private void Shade(PanelId id) { _shaded[(int)id] = !_shaded[(int)id]; RefreshLayout(); }
    private void Zoom() { _doubleSize = !_doubleSize; ArrangeWorkspace(reset: !_windowMoved); }
    private void CycleVis()
    { _visMode = (_visMode + 1) % 3; Draw(PanelId.Main); }
    private void SetVolume(double n)
    {
        if (_spotifyActive) { _pendingSpotifyVolume = Math.Clamp(n, 0, 1); ScheduleSpotifyControls(); }
        else Engine.Volume = Math.Clamp(n, 0, 1);
        Notify($"VOLUME {(int)(CurrentVolume * 100)}%");
        Draw(PanelId.Equalizer);
    }
    private void SetBalance(double n)
    {
        if (_spotifyActive || _loaded is not null && !Engine.SupportsDsp) { Notify("BALANCE UNAVAILABLE FOR THIS OUTPUT"); return; }
        Engine.Balance = Math.Abs(.5 - n) < .04 ? 0 : n * 2 - 1;
        Notify($"BALANCE {(Engine.Balance == 0 ? "CENTER" : $"{Math.Abs((int)(Engine.Balance * 100))}% {(Engine.Balance < 0 ? "LEFT" : "RIGHT")}")}");
        Draw(PanelId.Equalizer);
    }
    private void SeekFraction(double n) { SeekCurrent(TimeSpan.FromSeconds(CurrentDuration.TotalSeconds * n)); Draw(PanelId.Main); }
    private void Transport(int i) => _ = Guard(i switch
    {
        0 => Previous, 1 => Play, 2 => Pause, 3 => Stop, 4 => () => Advance(true), _ => () => PickMedia(true)
    });

    private static bool Modifier(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
    private void HandleKey(object sender, KeyRoutedEventArgs e)
    {
        if (!_spotifyActive && Engine.HasVideo && (e.Key == VirtualKey.F11 || (e.Key == VirtualKey.Escape && Engine.IsVideoFullscreen)))
        { e.Handled = true; _ = Guard(Engine.ToggleVideoFullscreenAsync); return; }
        if (_popupLayer is not null && e.Key == VirtualKey.Escape) { DismissClassicMenu(); e.Handled = true; return; }
        if (Modifier(VirtualKey.Menu) && e.Key is VirtualKey.L or VirtualKey.B or VirtualKey.S)
        { e.Handled = true; if (e.Key == VirtualKey.L) ShowLibrary(); else if (e.Key == VirtualKey.S) ShowSpotify(); else ShowMiniBrowser(); return; }
        if (_auxActive) return;
        if (e.OriginalSource is TextBox || (XamlRoot is {} root && FocusManager.GetFocusedElement(root) is TextBox)) return;
        bool ctrl = Modifier(VirtualKey.Control);
        e.Handled = true;
        if (ctrl)
        {
            switch (e.Key)
            {
                case VirtualKey.O: _ = Guard(() => PickMedia(true)); break;
                case VirtualKey.L: _ = Guard(OpenUrl); break;
                case VirtualKey.P: Toggle(PanelId.Playlist); break;
                case VirtualKey.E: Toggle(PanelId.Equalizer); break;
                case VirtualKey.D: Zoom(); break;
                case VirtualKey.A: SelectAll(); break;
                default: e.Handled = false; break;
            }
            return;
        }
        switch (e.Key)
        {
            case VirtualKey.Z: Transport(0); break;
            case VirtualKey.X: Transport(1); break;
            case VirtualKey.C: case VirtualKey.Space: _ = Guard(CurrentIsPlaying ? Pause : Play); break;
            case VirtualKey.V: Transport(3); break;
            case VirtualKey.B: Transport(4); break;
            case VirtualKey.L: _ = Guard(() => PickMedia(true)); break;
            case VirtualKey.J: _ = Guard(Jump); break;
            case VirtualKey.R: ToggleRepeat(); break;
            case VirtualKey.S: ToggleShuffle(); break;
            case VirtualKey.Delete: RemoveSelected(false); break;
            case VirtualKey.Left: case VirtualKey.Right:
                SeekCurrent(TimeSpan.FromSeconds(Math.Clamp(CurrentPosition.TotalSeconds + (e.Key == VirtualKey.Left ? -5 : 5), 0, CurrentDuration.TotalSeconds)));
                break;
            case VirtualKey.Up: case VirtualKey.Down: SetVolume(CurrentVolume + (e.Key == VirtualKey.Up ? .05 : -.05)); break;
            default: e.Handled = false; break;
        }
    }
}
