using Microsoft.UI.Xaml;
using UnoPlay.ClassicUI;
using UnoPlay.Core;

namespace UnoPlay.ApplicationHost;

public sealed partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = _window = new Window { Title = "UnoPlay" };
        var player = new ClassicPlayerView();
        window.Content = player;
#if __WASM__
        if (!OperatingSystem.IsBrowser()) throw new PlatformNotSupportedException();
        using var document = System.Runtime.InteropServices.JavaScript.JSHost.GlobalThis.GetPropertyAsJSObject("document");
        var page = new Uri(document!.GetPropertyAsString("baseURI")!);
        var demo = new Uri(page, "media/first-light.mp3");
#else
        var demo = new Uri(Path.Combine(AppContext.BaseDirectory, "media", "first-light.mp3"));
        player.CloseRequested += (_, _) => window.Close();
        player.MinimizeRequested += (_, _) =>
        {
            if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                presenter.Minimize();
        };
#endif
        player.Queue.Tracks.Add(new Track(demo.AbsoluteUri, "UnoPlay – First Light", TimeSpan.FromSeconds(16 * 60.0 / 110)));
        window.Closed += (_, _) => player.Shutdown();
        window.Activate();
        _ = player.RestorePreferencesAsync();
    }
}
