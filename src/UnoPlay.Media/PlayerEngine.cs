using Microsoft.UI.Xaml;
using Windows.Storage;

namespace UnoPlay.Media;

/// <summary>A UI-thread media transport. A single engine owns its video view and one active source.</summary>
public sealed partial class PlayerEngine : IDisposable
{
    private double _volume = 1;
    private double _balance;
    private bool _disposed;
    private int _openGeneration;

    public FrameworkElement View { get; }
    public event EventHandler? StateChanged;
    public event EventHandler? Ended;
    public event EventHandler<string>? Failed;

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Clamp(value, 0, 1);
            ApplyVolume(_volume);
        }
    }

    public double Balance
    {
        get => _balance;
        set
        {
            _balance = Clamp(value, -1, 1);
            ApplyBalance(_balance);
        }
    }

    public void SetEqualizer(bool enabled, double preampDb, IReadOnlyList<double> bandsDb)
    {
        ArgumentNullException.ThrowIfNull(bandsDb);
        if (bandsDb.Count != 10) throw new ArgumentException("The classic equalizer has ten bands.", nameof(bandsDb));
        var gains = new double[11];
        if (enabled)
        {
            gains[0] = Clamp(preampDb, -12, 12);
            for (int i = 0; i < 10; i++) gains[i + 1] = Clamp(bandsDb[i], -12, 12);
        }
        ApplyEqualizer(gains);
    }

    public Task OpenAsync(Uri source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        return OpenUriAsync(source, ++_openGeneration);
    }

    public Task OpenAsync(StorageFile file)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(file);
        return OpenFileAsync(file, ++_openGeneration);
    }

    public void Stop()
    {
        _openGeneration++;
        Pause();
        if (CanSeek) Seek(TimeSpan.Zero);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _openGeneration++;
        DisposeTransport();
    }

    private static double Clamp(double value, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : throw new ArgumentOutOfRangeException(nameof(value));

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
    private void RaiseEnded() => Ended?.Invoke(this, EventArgs.Empty);
    private void RaiseFailed(string error) => Failed?.Invoke(this, error);
}

/// <summary>A file held by the browser. Dispose after its final queue entry is removed.</summary>
public sealed class BrowserMediaFile : IDisposable
{
    private bool _disposed;
    internal BrowserMediaFile(string name, string url, long length, long lastModified)
        => (Name, Uri, Length, LastModified) = (name, new Uri(url), length, lastModified);
    public long Length { get; }
    public long LastModified { get; }
    public string Name { get; }
    public Uri Uri { get; }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
#if UNOPLAY_BROWSER
        BrowserNative.Revoke(Uri.AbsoluteUri);
#endif
    }
}
