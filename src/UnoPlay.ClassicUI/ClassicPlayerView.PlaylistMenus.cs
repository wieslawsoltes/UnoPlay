using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace UnoPlay.ClassicUI;

public sealed partial class ClassicPlayerView
{
    private Canvas? _popupLayer;
    private sealed record MenuCommand(string Name, int AtlasY, Action Run);

    // The five playlist menus are themselves bitmap controls. Rows unfold upward from
    // the pressed button; right/left cap pixels belong to the skin, not the host theme.
    private bool OpenClassicMenu(string group, FrameworkElement anchor)
    {
        void Later(Func<Task> action) => _ = Guard(action);
        var (x, pressedX, barX, commands) = group switch
        {
            "add" => (0, 23, 48, new MenuCommand[] {
                new("Add URL",111,() => Later(OpenUrl)), new("Add folder",130,() => Later(PickFolder)),
                new("Add files",149,() => Later(() => PickMedia(false))) }),
            "remove" => (54, 77, 100, new MenuCommand[] {
                new("Remove all",111,ClearList), new("Crop playlist",130,() => RemoveSelected(true)),
                new("Remove selected",149,() => RemoveSelected(false)), new("Remove options",168,() => NativeMenu("remove",anchor)) }),
            "select" => (104, 127, 150, new MenuCommand[] {
                new("Invert selection",111,InvertSelection), new("Select none",130,() => { _selected.Clear(); SelectionChanged(); }),
                new("Select all",149,SelectAll) }),
            "tools" => (154,177,200, new MenuCommand[] {
                new("Sort playlist",111,() => NativeMenu("tools",anchor)), new("File information",130,() => Later(TrackInfo)),
                new("Playlist options",149,() => NativeMenu("tools",anchor)) }),
            "lists" => (204,227,250, new MenuCommand[] {
                new("New playlist",111,ClearList), new("Save playlist",130,() => Later(SavePlaylist)), new("Load playlist",149,() => Later(PickPlaylist)) }),
            _ => (0, 0, 0, Array.Empty<MenuCommand>())
        };
        if (commands.Length == 0) return false;
        DismissClassicMenu();
        var layer = new Canvas { Background = ClassicSurface.Brush(0), Width = ActualWidth, Height = ActualHeight };
        _popupLayer = layer;
        var menu = new ClassicSurface(Panel(PanelId.Playlist).Atlas, "Playlist " + group + " menu");
        int h = 18 * commands.Length;
        menu.DefineSize(25, h);
        menu.SetZoom(_scale);
        var pt = anchor.TransformToVisual(_desk).TransformPoint(new Point(0,0));
        Canvas.SetLeft(menu, Math.Max(0, pt.X - 3 * _scale));
        Canvas.SetTop(menu, Math.Max(0, pt.Y - (h - 18) * _scale));
        layer.Children.Add(menu);
        _desk.Children.Add(layer);
        Canvas.SetZIndex(layer, 10);
        layer.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, layer)) { DismissClassicMenu(); e.Handled = true; }
        };
        void DrawMenu(int pressed = -1)
        {
            menu.Begin();
            menu.Sprite("pledit", barX, 111, 3, h, 0, 0);
            for (int i = 0; i < commands.Length; i++)
                menu.Sprite("pledit", i == pressed ? pressedX : x, commands[i].AtlasY, 22, 18, 3, i * 18);
            menu.End();
        }
        for (int i = 0; i < commands.Length; i++)
        {
            int index = i;
            var item = new Button { Opacity = 0, Content = commands[i].Name, MinWidth = 0, MinHeight = 0 };
            AutomationProperties.SetName(item, commands[i].Name);
            ToolTipService.SetToolTip(item, commands[i].Name);
            item.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => DrawMenu(index)), true);
            item.PointerEntered += (_, _) => DrawMenu(index);
            item.PointerExited += (_, _) => DrawMenu();
            item.Click += (_, _) => { DismissClassicMenu(); commands[index].Run(); };
            menu.PlaceTarget(item, 3, i * 18, 22, 18);
            menu.Targets.Children.Add(item);
        }
        DrawMenu();
        ((Button)menu.Targets.Children[^1]).Focus(FocusState.Programmatic);
        return true;
    }

    private void NativeMenu(string group, FrameworkElement anchor) => ShowMenu(group, anchor, new Point(0, anchor.ActualHeight));
    private void DismissClassicMenu()
    { if (_popupLayer is not null) { _desk.Children.Remove(_popupLayer); _popupLayer = null; } }
    private void InvertSelection()
    { foreach (var track in Queue.Tracks) if (!_selected.Remove(track)) _selected.Add(track); SelectionChanged(); }
}
