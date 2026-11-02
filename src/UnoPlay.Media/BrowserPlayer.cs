#if UNOPLAY_BROWSER
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Uno.UI.NativeElementHosting;
using Windows.Storage;

namespace UnoPlay.Media;

public sealed partial class PlayerEngine
{
    private readonly string _id;
    private readonly BrowserHtmlElement _html;
    private readonly byte[] _spectrum = new byte[75];
    private readonly byte[] _waveform = new byte[75];
    private string? _ownedUrl;

    public PlayerEngine()
    {
        // Create through Uno so the DOM renderer registers the native handle
        // before ContentPresenter performs its first attach/layout pass.
        _html = BrowserHtmlElement.CreateHtmlElement("div");
        _id = _html.ElementId;
        BrowserNative.Create(_id, OnEvent);
        _html.ExecuteJavascript("globalThis.UnoPlayMedia.attach(element); return '';");
        View = new ContentControl
        {
            Content = _html,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
    }

    public bool IsPlaying => BrowserNative.State(_id, "playing") != 0;
    public bool HasVideo => BrowserNative.State(_id, "video") != 0;
    public bool CanSeek => BrowserNative.State(_id, "seekable") != 0;
    public bool SupportsDsp => BrowserNative.State(_id, "dsp") != 0;
    public TimeSpan Position => TimeSpan.FromSeconds(BrowserNative.State(_id, "position"));
    public TimeSpan Duration => TimeSpan.FromSeconds(BrowserNative.State(_id, "duration"));

    private void OnEvent(string kind, string message)
    {
        if (_disposed) return;
        switch (kind)
        {
            case "state": RaiseStateChanged(); break;
            case "ended": RaiseEnded(); break;
            case "error": RaiseFailed(message); break;
            case "command":
                _ = DispatchTransportAsync(message switch
                {
                    "play" => MediaTransportCommand.Play,
                    "pause" => MediaTransportCommand.Pause,
                    "stop" => MediaTransportCommand.Stop,
                    "previoustrack" => MediaTransportCommand.Previous,
                    "nexttrack" => MediaTransportCommand.Next,
                    _ => throw new ArgumentOutOfRangeException(nameof(message))
                });
                break;
        }
    }

    private void OpenUri(Uri source)
    {
        string? old = _ownedUrl;
        BrowserNative.Open(_id, source.OriginalString);
        _ownedUrl = null;
        if (old != null) BrowserNative.Revoke(old);
    }

    // Consumers that use Uno's StorageFile API can provide a file from any
    // storage provider. The browser-native picker below avoids the WASM copy.
    private async Task OpenFileAsync(StorageFile file, int generation)
    {
        using var blob = BrowserNative.CreateBlob();
        using var stream = await file.OpenStreamForReadAsync();
        var buffer = new byte[128 * 1024];
        int count;
        while ((count = await stream.ReadAsync(buffer)) != 0)
        {
            if (generation != _openGeneration) return;
            BrowserNative.AppendBlob(blob, buffer, count);
        }
        if (generation != _openGeneration) return;
        var url = BrowserNative.FinishBlob(blob, file.ContentType);
        try
        {
            OpenUri(new Uri(url));
            _ownedUrl = url;
        }
        catch
        {
            BrowserNative.Revoke(url);
            throw;
        }
    }

    public static async Task<IReadOnlyList<BrowserMediaFile>> PickBrowserFilesAsync()
    {
        using var json = JsonDocument.Parse(await BrowserNative.Pick());
        return json.RootElement.EnumerateArray()
            .Select(f => new BrowserMediaFile(f[0].GetString()!, f[1].GetString()!)).ToArray();
    }

    public Task PlayAsync() => BrowserNative.Play(_id);
    public void Pause() => BrowserNative.Pause(_id);
    public void Seek(TimeSpan position) => BrowserNative.Seek(_id, position.TotalSeconds);
    private void ApplyVolume(double value) => BrowserNative.Volume(_id, value);
    private void ApplyBalance(double value) => BrowserNative.Balance(_id, value);
    private void ApplyEqualizer(double[] gains) => BrowserNative.Equalize(_id, gains);

    /// <summary>Seventy-five logarithmically spaced FFT peaks. The buffer is reused.</summary>
    public byte[] GetSpectrum()
    {
        BrowserNative.Sample(_id, _spectrum, false);
        return _spectrum;
    }

    /// <summary>Seventy-five unsigned waveform samples centered at 128. The buffer is reused.</summary>
    public byte[] GetWaveform()
    {
        BrowserNative.Sample(_id, _waveform, true);
        return _waveform;
    }

    private void DisposeTransport()
    {
        ((ContentControl)View).Content = null;
        _html.Dispose();
        BrowserNative.Dispose(_id);
        if (_ownedUrl != null) BrowserNative.Revoke(_ownedUrl);
    }
}

internal static partial class BrowserNative
{
    private const string Api = "globalThis.UnoPlayMedia.";
    [JSImport(Api + "create")] internal static partial void Create(string id, [JSMarshalAs<JSType.Function<JSType.String, JSType.String>>] Action<string, string> notify);
    [JSImport(Api + "dispose")] internal static partial void Dispose(string id);
    [JSImport(Api + "open")] internal static partial void Open(string id, string url);
    [JSImport(Api + "play")] internal static partial Task Play(string id);
    [JSImport(Api + "pause")] internal static partial void Pause(string id);
    [JSImport(Api + "seek")] internal static partial void Seek(string id, double seconds);
    [JSImport(Api + "volume")] internal static partial void Volume(string id, double volume);
    [JSImport(Api + "balance")] internal static partial void Balance(string id, double balance);
    [JSImport(Api + "equalize")] internal static partial void Equalize(string id, double[] gains);
    [JSImport(Api + "state")] internal static partial double State(string id, string key);
    [JSImport(Api + "sample")] internal static partial void Sample(string id, [JSMarshalAs<JSType.MemoryView>] Span<byte> output, bool waveform);
    [JSImport(Api + "pick")] internal static partial Task<string> Pick();
    [JSImport(Api + "createBlob")] internal static partial JSObject CreateBlob();
    [JSImport(Api + "appendBlob")] internal static partial void AppendBlob(JSObject blob, byte[] data, int count);
    [JSImport(Api + "finishBlob")] internal static partial string FinishBlob(JSObject blob, string type);
    [JSImport(Api + "revoke")] internal static partial void Revoke(string url);
}
#endif
