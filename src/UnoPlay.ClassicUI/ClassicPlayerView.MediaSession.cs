using UnoPlay.Media;

namespace UnoPlay.ClassicUI;

public sealed partial class ClassicPlayerView
{
    private void EngineTransportRequested(object? sender, MediaTransportCommand command) => OnUi(() =>
        _ = Guard(command switch
        {
            MediaTransportCommand.Play => Play,
            MediaTransportCommand.Pause => Pause,
            MediaTransportCommand.Stop => Stop,
            MediaTransportCommand.Previous => Previous,
            MediaTransportCommand.Next => () => Advance(true),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        }));

    private void UpdateMediaSession() => Engine.SetNowPlaying(CurrentTags?.Title ?? _loaded?.Title, CurrentTags?.Artist);
}
