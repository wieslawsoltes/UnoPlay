using Microsoft.UI.Xaml;
using UnoPlay.ClassicUI;

namespace UnoPlay;

public sealed partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new Window { Title = "UnoPlay", Content = new ClassicPlayerView() };
        _window.Activate();
    }
}
