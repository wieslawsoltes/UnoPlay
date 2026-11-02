using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Storage;

namespace UnoPlay.ClassicUI;

internal sealed record WorkspacePreferences(double Volume, double Balance, bool Repeat, bool Shuffle,
    bool Equalizer, bool Auto, double Preamp, double[] Bands, int Visualizer, bool Remaining,
    bool DoubleSize, bool Video, bool[] Visible, bool[] Rolled, bool Moved, double[] X, double[] Y,
    int PlaylistWidth, int PlaylistHeight);

[JsonSerializable(typeof(WorkspacePreferences))]
internal partial class PreferencesJson : JsonSerializerContext { }

public sealed partial class ClassicPlayerView
{
    private const string PreferencesKey = "unoplay.workspace.v1";
    private const string SkinKey = "unoplay.skin.v1";
    private DispatcherTimer? _preferencesTimer;
    private bool _preferencesEnabled;
    private long _skinGeneration;

    /// <summary>Opt the workspace into the application's local settings and skin cache.</summary>
    public async Task RestorePreferencesAsync()
    {
        if (_preferencesEnabled || _disposed) return;
        _preferencesEnabled = true;
        // Save after a complete gesture. Sliders and window movement can produce hundreds of intermediate values.
        _preferencesTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _preferencesTimer.Tick += (_, _) => { _preferencesTimer.Stop(); SavePreferences(); };
        AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => QueuePreferencesSave()), true);
        AddHandler(KeyUpEvent, new KeyEventHandler((_, _) => QueuePreferencesSave()), true);
        try
        {
            var settings = ApplicationData.Current.LocalSettings.Values;
            if (settings.TryGetValue(PreferencesKey, out var data) && data is string json
                && JsonSerializer.Deserialize(json, PreferencesJson.Default.WorkspacePreferences) is { } p
                && p is { Bands.Length: 10, Visible.Length: 3, Rolled.Length: 3, X.Length: 3, Y.Length: 3 })
            {
                Engine.Volume = Math.Clamp(p.Volume, 0, 1);
                Engine.Balance = Math.Clamp(p.Balance, -1, 1);
                Queue.Repeat = p.Repeat;
                Queue.Shuffle = p.Shuffle;
                _eq = p.Equalizer; _auto = p.Auto; _preamp = Math.Clamp(p.Preamp, -12, 12);
                for (int i = 0; i < 10; i++) _bands[i] = Math.Clamp(p.Bands[i], -12, 12);
                _visMode = Math.Clamp(p.Visualizer, 0, 2); _remaining = p.Remaining;
                _doubleSize = p.DoubleSize; _videoEnabled = p.Video;
                p.Visible.CopyTo(_visible, 0); _visible[0] = true;
                p.Rolled.CopyTo(_shaded, 0);
                _playlistWidth = Math.Clamp(p.PlaylistWidth, 275, 2000);
                _playlistHeight = Math.Clamp(p.PlaylistHeight, 116, 1600);
                _windowMoved = _placed = p.Moved;
                for (int i = 0; i < 3; i++) _points[i] = new Point(p.X[i], p.Y[i]);
                ApplyEq();
                RefreshLayout();
            }
            if (settings.TryGetValue(SkinKey, out var name) && name is string saved && Guid.TryParseExact(saved, "N", out _))
            {
                long generation = _skinGeneration;
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync(saved + ".wsz");
                using var stream = await file.OpenStreamForReadAsync();
                if (!_disposed && generation == _skinGeneration) ApplySkin(ClassicSkins.ClassicSkin.Load(stream));
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Could not restore UnoPlay preferences: {ex.Message}"); }
    }

    private void QueuePreferencesSave()
    {
        if (!_preferencesEnabled || _disposed) return;
        _preferencesTimer!.Stop();
        _preferencesTimer.Start();
    }

    private void SavePreferences()
    {
        if (!_preferencesEnabled) return;
        try
        {
            var p = new WorkspacePreferences(Engine.Volume, Engine.Balance, Queue.Repeat, Queue.Shuffle, _eq, _auto,
                _preamp, _bands, _visMode, _remaining, _doubleSize, _videoEnabled, _visible, _shaded, _windowMoved,
                _points.Select(p => p.X).ToArray(), _points.Select(p => p.Y).ToArray(), _playlistWidth, _playlistHeight);
            ApplicationData.Current.LocalSettings.Values[PreferencesKey] = JsonSerializer.Serialize(p, PreferencesJson.Default.WorkspacePreferences);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Could not save UnoPlay preferences: {ex.Message}"); }
    }

    private async Task CacheSkinAsync(StorageFile? source)
    {
        long generation = ++_skinGeneration;
        if (!_preferencesEnabled) return;
        var settings = ApplicationData.Current.LocalSettings.Values;
        var folder = ApplicationData.Current.LocalFolder;
        settings.TryGetValue(SkinKey, out var previous);
        if (source is not null)
        {
            string name = Guid.NewGuid().ToString("N");
            var copy = await source.CopyAsync(folder, name + ".wsz");
            if (generation != _skinGeneration) { await copy.DeleteAsync(); return; }
            settings[SkinKey] = name;
        }
        else settings.Remove(SkinKey);
        if (previous is string old && Guid.TryParseExact(old, "N", out _)
            && await folder.TryGetItemAsync(old + ".wsz") is StorageFile oldFile) await oldFile.DeleteAsync();
    }
}
