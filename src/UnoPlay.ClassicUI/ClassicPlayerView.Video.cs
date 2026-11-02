using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace UnoPlay.ClassicUI;

public sealed partial class ClassicPlayerView
{
    private ClassicSurface? _videoFrame;
    private bool _videoFocus, _videoMoved;
    private int _videoWidth = 375, _videoHeight = 259, _videoPressed = -1;
    private double _videoX, _videoY;
    private (int Width, int Height) _videoLayout;
    private static readonly string[] VideoCommands = ["Fullscreen video", "Original video size", "Double video size", "Video information", "Video menu"];

    private void SetupVideo(SpriteAtlas atlas)
    {
        _videoFrame = new ClassicSurface(atlas, "Video window");
        _desk.Children.Add(_videoFrame);
        _desk.Children.Add(_video);
        _video.DoubleTapped += (_, e) => { e.Handled = true; _ = Guard(Engine.ToggleVideoFullscreenAsync); };
        _video.PointerPressed += (_, _) => SetVideoFocus(true);
    }
    private void SetVideoFocus(bool value)
    {
        if (_videoFocus == value) return;
        _videoFocus = value;
        if (_videoFrame is not null) PaintVideo();
        DrawAll();
    }
    private void ArrangeVideo()
    {
        var panel = _videoFrame;
        if (panel is null) return;
        bool show = Engine.HasVideo && _videoEnabled;
        panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _video.Opacity = show ? 1 : 0;
        _video.IsHitTestVisible = show;
        if (!show)
        {
            _video.Height = _video.Width = 1;
            Canvas.SetLeft(_video,0); Canvas.SetTop(_video,0);
            return;
        }
        // A 250-pixel lower bezel is the minimum supported by VIDEO.BMP.
        int w = Math.Clamp(_videoWidth, 250, Math.Max(250,(int)ActualWidth));
        int h = Math.Clamp(_videoHeight, 116, Math.Max(116,(int)ActualHeight));
        var layout = (Width:w, Height:h);
        if (_videoLayout != layout)
        {
            _videoLayout = layout;
            panel.DefineSize(w,h);
            BuildVideoTargets();
        }
        if (!_videoMoved)
        {
            _videoX = Math.Max(0, ActualWidth - w - 8);
            _videoY = Math.Max(0, ActualHeight - h - 8);
        }
        _videoX = Math.Clamp(_videoX,0,Math.Max(0,ActualWidth - 40));
        _videoY = Math.Clamp(_videoY,0,Math.Max(0,ActualHeight - 14));
        Canvas.SetLeft(panel,_videoX); Canvas.SetTop(panel,_videoY);
        Canvas.SetLeft(_video,_videoX+11); Canvas.SetTop(_video,_videoY+20);
        Canvas.SetZIndex(panel,_videoFocus ? 5 : 1);
        Canvas.SetZIndex(_video,_videoFocus ? 6 : 2);
        _video.Width = w-19; _video.Height = h-58;
        PaintVideo();
    }

    private void PaintVideo()
    {
        if (_videoFrame is not { Visibility: Visibility.Visible } p || _videoLayout.Width < 250) return;
        var (w,h) = _videoLayout;
        int row = _videoFocus ? 0 : 21;
        p.Begin();
        for (int x = 25; x < w-25; x += 25) p.Sprite("video",127,row,Math.Min(25,w-25-x),20,x,0);
        p.Sprite("video",0,row,25,20,0,0);
        p.Sprite("video",26,row,100,20,(w-100)/2,0);
        p.Sprite("video",153,row,25,20,w-25,0);
        if (_videoPressed == 5) p.Sprite("video",148,42,9,9,w-11,3);
        for (int y = 20; y < h-38; y += 29)
        {
            int tile = Math.Min(29,h-38-y);
            p.Sprite("video",127,42,11,tile,0,y); p.Sprite("video",139,42,8,tile,w-8,y);
        }
        p.Sprite("video",0,42,125,38,0,h-38);
        for (int x=125; x<w-125; x+=25) p.Sprite("video",127,81,Math.Min(25,w-125-x),38,x,h-38);
        p.Sprite("video",0,81,125,38,w-125,h-38);
        if (_videoPressed is >= 0 and <= 4) p.Sprite("video",158+_videoPressed*15,42,15,18,9+_videoPressed*15,h-29);
        var dimensions = VideoDimensions();
        string caption = dimensions.Width > 0 && dimensions.Height > 0 ? $"{dimensions.Width} × {dimensions.Height}" : _loaded is null ? "" : TitleOf(_loaded);
        p.Label(caption, _skin.Playlist.Font, _skin.Playlist.Normal, 92, h-27,w-117,14);
        p.End();
    }
    private (int Width,int Height) VideoDimensions()
    {
        int w = Engine.VideoWidth, h = Engine.VideoHeight;
        return w > 0 && h > 0 ? (w,h) : (CurrentTags?.VideoWidth ?? 0, CurrentTags?.VideoHeight ?? 0);
    }
    private void SizeVideo(int factor)
    {
        var (w,h) = VideoDimensions();
        if (w <= 0 || h <= 0) { Notify("VIDEO DIMENSIONS UNAVAILABLE"); return; }
        _videoWidth = w*factor + 19; _videoHeight = h*factor + 58;
        ArrangeVideo();
    }
    private void VideoMenu(FrameworkElement anchor)
    {
        var menu = new MenuFlyout();
        void Item(string name, Action run)
        {
            var item = new MenuFlyoutItem { Text = name };
            item.Click += (_, _) => run(); menu.Items.Add(item);
        }
        Item("Fullscreen", () => _ = Guard(Engine.ToggleVideoFullscreenAsync)); Item("Original size", () => SizeVideo(1));
        Item("Double size", () => SizeVideo(2)); Item("Track information", () => _ = Guard(TrackInfo));
        Item("Hide video", () => { _videoEnabled = false; ArrangeVideo(); QueuePreferencesSave(); });
        menu.ShowAt(anchor);
    }
    private void BuildVideoTargets()
    {
        var panel = _videoFrame!;
        var (w,h) = _videoLayout;
        panel.Targets.Children.Clear();
        VideoGesture(0,0,w,20,false);
        for (int i=0; i<=5; i++)
        {
            int index = i;
            var name = i==5 ? "Close video" : VideoCommands[i];
            var item = new Button { Content=name, Opacity=0, MinWidth=0, MinHeight=0 };
            ClassicSurface.Place(item, i==5 ? w-11 : 9+i*15,i==5 ? 3 : h-29,i==5 ? 9 : 15,i==5 ? 9 : 18);
            AutomationProperties.SetName(item,name); ToolTipService.SetToolTip(item,name); panel.Targets.Children.Add(item);
            item.AddHandler(PointerPressedEvent,new PointerEventHandler((_,_) => { _videoPressed=index; SetVideoFocus(true); PaintVideo(); }),true);
            item.AddHandler(PointerReleasedEvent,new PointerEventHandler((_,_) => { _videoPressed=-1; PaintVideo(); }),true);
            item.PointerCaptureLost += (_,_) => { _videoPressed=-1; PaintVideo(); };
            item.Click += (_,_) =>
            {
                switch(index)
                {
                    case 0: _ = Guard(Engine.ToggleVideoFullscreenAsync); break;
                    case 1: case 2: SizeVideo(index); break;
                    case 3: _ = Guard(TrackInfo); break;
                    case 4: VideoMenu(item); break;
                    case 5: _videoEnabled=false; ArrangeVideo(); QueuePreferencesSave(); break;
                }
            };
        }
        VideoGesture(w-18,h-18,18,18,true);
    }
    private void VideoGesture(int x,int y,int w,int h,bool resize)
    {
        var hit = new Border { Background=ClassicSurface.Brush(0) };
        AutomationProperties.SetName(hit,resize ? "Resize video" : "Video title bar");
        ClassicSurface.Place(hit,x,y,w,h);
        _videoFrame!.Targets.Children.Add(hit);
        bool dragging=false;
        Point start=default, origin=default;
        int startWidth=0, startHeight=0;
        hit.PointerPressed += (_,e) =>
        {
            if (!e.GetCurrentPoint(hit).Properties.IsLeftButtonPressed) return;
            SetVideoFocus(true); ArrangeVideo();
            start=e.GetCurrentPoint(_desk).Position; origin=new Point(_videoX,_videoY);
            startWidth=_videoLayout.Width; startHeight=_videoLayout.Height;
            dragging=hit.CapturePointer(e.Pointer);
        };
        hit.PointerMoved += (_,e) =>
        {
            if (!dragging || resize) return;
            var pt=e.GetCurrentPoint(_desk).Position;
            _videoMoved=true; _videoX=origin.X+pt.X-start.X; _videoY=origin.Y+pt.Y-start.Y; ArrangeVideo();
        };
        hit.PointerReleased += (_,e) =>
        {
            if (!dragging) return;
            var pt=e.GetCurrentPoint(_desk).Position;
            hit.ReleasePointerCapture(e.Pointer);
            if (resize)
            {
                _videoWidth=Math.Max(250,(int)(startWidth+pt.X-start.X));
                _videoHeight=Math.Max(116,(int)(startHeight+pt.Y-start.Y));
                ArrangeVideo();
            }
        };
        hit.PointerCaptureLost += (_,_) => dragging=false;
        if (!resize) hit.DoubleTapped += (_,_) => _ = Guard(Engine.ToggleVideoFullscreenAsync);
    }
}
