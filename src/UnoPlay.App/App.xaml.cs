using Microsoft.UI.Xaml;
using UnoPlay.ClassicUI;
using UnoPlay.Core;

namespace UnoPlay;

public sealed partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var player = new ClassicPlayerView();
        var window = new Window { Title = "UnoPlay", Content = player };
        _window = window;
#if __WASM__
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
    }
}
