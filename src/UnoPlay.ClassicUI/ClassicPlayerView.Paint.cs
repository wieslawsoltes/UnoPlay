using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;

namespace UnoPlay.ClassicUI;

public sealed partial class ClassicPlayerView
{
    private void DrawMain(ClassicSurface p)
    {
        bool shade = _shaded[0], active = !_videoFocus && !_auxActive && _active == PanelId.Main;
        if (!shade) p.Sprite("main", 0, 0, 275, 116, 0, 0);
        p.Sprite("titlebar", 27, shade ? (active ? 29 : 42) : (active ? 0 : 15), 275, 14, 0, 0);
        p.Sprite("titlebar", 0, Down("Options") ? 9 : 0, 9, 9, 6, 3);
        p.Sprite("titlebar", 9, Down("Minimize") ? 9 : 0, 9, 9, 244, 3);
        p.Sprite("titlebar", Down("Shade main") ? 9 : 0, shade ? 27 : 18, 9, 9, 254, 3);
        p.Sprite("titlebar", 18, Down("Close") ? 9 : 0, 9, 9, 264, 3);
        double fraction = Engine.Duration.TotalSeconds > 0 ? Math.Clamp(Engine.Position.TotalSeconds / Engine.Duration.TotalSeconds, 0, 1) : 0;
        var clock = TimeDisplay;
        if (shade)
        {
            DrawAnalyzer(p, true);
            p.Text((_remaining ? "-" : "") + Clock(clock), 127, 4, 7);
            p.Sprite("titlebar", 0, 36, 17, 7, 226, 4);
            p.Sprite("titlebar", fraction < .34 ? 17 : fraction > .66 ? 23 : 20, 36, 3, 7, 226 + (int)Math.Round(fraction * 14), 4);
            return;
        }
        p.Sprite("titlebar", 304, 0, 8, 43, 10, 22);
        p.Sprite("playpaus", _stopped ? 18 : Engine.IsPlaying ? 0 : 9, 0, 9, 9, 26, 28);
        int minutes = (int)Math.Clamp(clock.TotalMinutes, 0, 99);
        p.Sprite("nums_ex", _remaining ? 99 : 90, 0, 9, 13, 36, 26);
        Digit(p, minutes / 10, 48); Digit(p, minutes % 10, 60);
        Digit(p, clock.Seconds / 10, 78); Digit(p, clock.Seconds % 10, 90);
        if (_notice is not null && DateTime.UtcNow > _noticeUntil) _notice = null;
        var title = _notice ?? (_loaded is not null ? TitleOf(_loaded) : "UNOPLAY CLASSIC");
        int start = 0;
        if (title.Length > 30)
        {
            string loop = title + "   ***   ";
            start = (int)(_ticks / 3 % loop.Length);
            title = loop + loop;
        }
        p.Text(title, 111, 27, 30, start);
        var tags = CurrentTags;
        p.Text(tags is { KilobitsPerSecond: > 0 } ? tags.KilobitsPerSecond.ToString().PadLeft(3) : "", 111, 43, 3);
        p.Text(tags is { SampleRate: > 0 } ? ((int)Math.Round(tags.SampleRate / 1000d)).ToString().PadLeft(2) : "", 156, 43, 2);
        p.Sprite("monoster", 29, tags?.Channels == 1 ? 0 : 12, 27, 12, 212, 41);
        p.Sprite("monoster", 0, tags?.Channels >= 2 ? 0 : 12, 29, 12, 239, 41);
        var volume = Math.Clamp(Engine.Volume, 0, 1);
        var balance = Math.Clamp(Engine.Balance, -1, 1);
        p.Sprite("volume", 0, 15 * (int)(27 * volume), 68, 13, 107, 57);
        p.Sprite("volume", Down("Volume") ? 0 : 15, 422, 14, 11, 107 + (int)(volume * 51), 58);
        p.Sprite("balance", 9, 15 * (int)(27 * Math.Abs(balance)), 38, 13, 177, 57);
        p.Sprite("balance", Down("Balance") ? 0 : 15, 422, 14, 11, 177 + (int)Math.Round((balance + 1) * 12), 58);
        p.Sprite("shufrep", Down("Equalizer") ? 46 : 0, _visible[1] ? 73 : 61, 23, 12, 219, 58);
        p.Sprite("shufrep", Down("Playlist") ? 69 : 23, _visible[2] ? 73 : 61, 23, 12, 242, 58);
        p.Sprite("posbar", 0, 0, 248, 10, 16, 72);
        if (!_stopped && Engine.CanSeek)
            p.Sprite("posbar", Down("Seek") ? 278 : 248, 0, 29, 10, 16 + (int)Math.Round(fraction * 219), 72);
        else p.Picture(null, 16, 72, 29, 10);
        for (int i = 0; i < 5; i++)
            p.Sprite("cbuttons", i * 23, Down(TransportNames[i]) ? 18 : 0, i == 4 ? 22 : 23, 18, 16 + i * 23, 88);
        p.Sprite("cbuttons", 114, Down("Open files") ? 16 : 0, 22, 16, 136, 89);
        p.Sprite("shufrep", 28, (Queue.Shuffle ? 30 : 0) + (Down("Shuffle") ? 15 : 0), 47, 15, 164, 89);
        p.Sprite("shufrep", 0, (Queue.Repeat ? 30 : 0) + (Down("Repeat") ? 15 : 0), 28, 15, 210, 89);
        DrawAnalyzer(p);
    }

    private static void Digit(ClassicSurface p, int digit, int x) => p.Sprite("nums_ex", digit * 9, 0, 9, 13, x, 26);

    private static void Upload(WriteableBitmap image, byte[] bytes)
    {
        using (var stream = image.PixelBuffer.AsStream()) stream.Write(bytes);
        image.Invalidate();
    }

    private readonly byte[] _miniVisPixels = new byte[38 * 5 * 4];
    private WriteableBitmap? _miniVisImage;
    private void DrawAnalyzer(ClassicSurface p, bool mini = false)
    {
        if (_visMode == 0) return;
        var colors = _skin.VisualizationColors;
        if (colors.Count < 24) return;
        int width = mini ? 38 : 76, height = mini ? 5 : 16;
        var buffer = mini ? _miniVisPixels : _visPixels;
        var bins = Engine.IsPlaying && Engine.SupportsDsp ? (_visMode == 1 ? Engine.GetSpectrum() : Engine.GetWaveform()) : [];
        (mini ? _miniSpectrum : _spectrum).Render(buffer, colors, bins, _visMode == 1,
            System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency,
            _spectrumStyle, _scopeStyle, _thinBars, _drawPeaks, _falloff);
        var image = mini ? (_miniVisImage ??= new(width,height)) : (_visImage ??= new(width,height));
        Upload(image,buffer);
        p.Picture(image,mini ? 79 : 24, mini ? 5 : 43, width,height);
    }

    private readonly static string[] TransportNames = ["Previous", "Play", "Pause", "Stop", "Next"];
    private readonly static int[] Frequencies = [60, 170, 310, 600, 1000, 3000, 6000, 12000, 14000, 16000];
    private static int BandX(int index) => index == -1 ? 21 : 78 + 18 * index;
    private static string BandName(int index) => index < 0 ? "Preamp" : Frequencies[index] + " Hz";

    private void DrawEq(ClassicSurface p)
    {
        bool active = !_videoFocus && !_auxActive && _active == PanelId.Equalizer;
        if (_shaded[1])
        {
            p.Sprite("eq_ex", 0, active ? 0 : 15, 275, 14, 0, 0);
            double vol = Engine.Volume, bal = (Engine.Balance + 1) / 2;
            p.Sprite("eq_ex", vol < .34 ? 1 : vol > .66 ? 7 : 4, 30, 3, 7, 61 + (int)(vol * 94), 4);
            p.Sprite("eq_ex", bal < .34 ? 11 : bal > .66 ? 17 : 14, 30, 3, 7, 164 + (int)(bal * 40), 4);
            return;
        }
        p.Sprite("eqmain", 0, 0, 275, 116, 0, 0);
        p.Sprite("eqmain", 0, active ? 134 : 149, 275, 14, 0, 0);
        p.Sprite("eqmain", 0, Down("Close equalizer") ? 125 : 116, 9, 9, 264, 3);
        p.Sprite("eqmain", 10 + (_eq ? 59 : 0) + (Down("Enable equalizer") ? 118 : 0), 119, 26, 12, 14, 18);
        p.Sprite("eqmain", 36 + (_auto ? 59 : 0) + (Down("Auto presets") ? 118 : 0), 119, 32, 12, 40, 18);
        p.Sprite("eqmain", 224, Down("Presets") ? 176 : 164, 44, 12, 217, 18);
        for (int i = -1; i < 10; i++)
        {
            var norm = ((i < 0 ? _preamp : _bands[i]) + 12) / 24;
            int frame = (int)Math.Round(norm * 27), x = BandX(i);
            p.Sprite("eqmain", 13 + (frame % 14) * 15, 164 + (frame / 14) * 65, 14, 63, x, 38);
            p.Sprite("eqmain", 0, Down(BandName(i)) ? 176 : 164, 11, 11, x + 1, 38 + (int)Math.Round(51 * (1 - norm)));
        }
        DrawGraph(p);
    }

    private void DrawGraph(ClassicSurface p)
    {
        var sheet = _skin.GetBitmap("eqmain");
        if (sheet is null || sheet.Width < 116 || sheet.Height < 315) return;
        var pixels = sheet.Bgra32.Span;
        for (int y = 0; y < 19; y++)
            pixels.Slice((294 + y) * sheet.Width * 4, 113 * 4).CopyTo(_graphPixels.AsSpan(y * 113 * 4));
        int preampY = (int)Math.Round((12 - _preamp) * .75);
        pixels.Slice(314 * sheet.Width * 4, 113 * 4).CopyTo(_graphPixels.AsSpan(preampY * 113 * 4));
        int last = -1;
        for (int x = 0; x < 113; x++)
        {
            double pos = x * 9d / 112;
            int i = Math.Min(8, (int)pos);
            double gain = _bands[i] + (_bands[i + 1] - _bands[i]) * (pos - i);
            int y = Math.Clamp((int)Math.Round((12 - gain) * .75), 0, 18);
            if (last < 0) last = y;
            for (int j = Math.Min(last, y); j <= Math.Max(last, y); j++)
                pixels.Slice(((294 + j) * sheet.Width + 115) * 4, 4).CopyTo(_graphPixels.AsSpan((j * 113 + x) * 4));
            last = y;
        }
        _graphImage ??= new(113, 19);
        Upload(_graphImage, _graphPixels);
        p.Picture(_graphImage, 86, 17, 113, 19);
    }

    private void DrawPlaylist(ClassicSurface p)
    {
        int w = _playlistWidth, h = _playlistHeight;
        bool active = !_videoFocus && !_auxActive && _active == PanelId.Playlist;
        if (_shaded[2])
        {
            for (int x = 0; x < w; x += 25) p.Sprite("pledit", 72, 57, Math.Min(25, w - x), 14, x, 0);
            p.Sprite("pledit", 72, 42, 25, 14, 0, 0);
            p.Sprite("pledit", 99, active ? 42 : 57, 50, 14, w - 50, 0);
            p.Text(_loaded is not null ? TitleOf(_loaded) : "PLAYLIST", 5, 4, (w - 75) / 5);
            p.Text(Clock(Engine.Position), w - 62, 4, 6);
            return;
        }
        int header = active ? 0 : 21;
        for (int x = 25; x < w - 25; x += 25) p.Sprite("pledit", 127, header, Math.Min(25, w - 25 - x), 20, x, 0);
        p.Sprite("pledit", 0, header, 25, 20, 0, 0);
        p.Sprite("pledit", 26, header, 100, 20, (w - 100) / 2, 0);
        p.Sprite("pledit", 153, header, 25, 20, w - 25, 0);
        for (int y = 20; y < h - 38; y += 29)
        {
            int tile = Math.Min(29, h - 38 - y);
            p.Sprite("pledit", 0, 42, 12, tile, 0, y);
            p.Sprite("pledit", 31, 42, 20, tile, w - 20, y);
        }
        for (int x = 125; x < w - 150; x += 25) p.Sprite("pledit", 179, 0, Math.Min(25, w - 150 - x), 38, x, h - 38);
        p.Sprite("pledit", 0, 72, 125, 38, 0, h - 38);
        p.Sprite("pledit", 126, 72, 150, 38, w - 150, h - 38);
        var palette = _skin.Playlist;
        p.Fill(palette.NormalBG, 12, 20, w - 32, h - 58);
        for (int row = 0; row < Rows && row + _top < Queue.Tracks.Count; row++)
        {
            int index = _top + row;
            var track = Queue.Tracks[index];
            var y = 22 + row * 13;
            if (_selected.Contains(track)) p.Fill(palette.SelectedBG, 12, y, w - 32, 13);
            uint color = ReferenceEquals(track, _loaded) ? palette.Current : palette.Normal;
            p.Label($"{index + 1}. {TitleOf(track)}", palette.Font, color, 14, y, w - 75, 13);
            var duration = track.Duration ?? (_knownDurations.TryGetValue(track, out var known) ? known : (TimeSpan?)null);
            if (duration.HasValue) p.Label(Clock(duration.Value), palette.Font, color, w - 56, y, 34, 13);
        }
        int scrollY = 20 + (int)((h - 76) * _top / (double)Math.Max(1, Queue.Tracks.Count - Rows));
        p.Sprite("pledit", Down("Scroll playlist") ? 61 : 52, 53, 8, 18, w - 15, scrollY);
        var total = _totalDuration;
        p.Text($"{Clock(_selectedDuration)} / {Clock(total)}", w - 143, h - 28, 15);
        p.Text(Clock(TimeDisplay), w - 84, h - 15, 6);
    }
}
