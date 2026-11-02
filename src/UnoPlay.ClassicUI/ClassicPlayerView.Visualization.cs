using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UnoPlay.ClassicSkins;

namespace UnoPlay.ClassicUI;

public sealed partial class ClassicPlayerView
{
    private readonly ClassicAnalyzer _spectrum = new(76, 16), _miniSpectrum = new(38, 5);
    private SpectrumStyle _spectrumStyle;
    private ScopeStyle _scopeStyle = ScopeStyle.Lines;
    private bool _thinBars, _drawPeaks = true;
    private double _falloff = 1.8;

    private void VisualizationMenu(FrameworkElement anchor)
    {
        var menu = new MenuFlyout();
        void Choice(string text, bool selected, Action change)
        {
            var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = selected };
            item.Click += (_, _) => { change(); Draw(PanelId.Main); QueuePreferencesSave(); };
            menu.Items.Add(item);
        }
        Choice("Spectrum analyzer", _visMode == 1, () => _visMode = 1);
        Choice("Oscilloscope", _visMode == 2, () => _visMode = 2);
        Choice("Off", _visMode == 0, () => _visMode = 0);
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var style in Enum.GetValues<SpectrumStyle>())
            Choice("Analyzer · " + style, _spectrumStyle == style, () => { _spectrumStyle = style; _visMode = 1; });
        Choice("Thin bands", _thinBars, () => _thinBars = !_thinBars);
        Choice("Peak indicators", _drawPeaks, () => _drawPeaks = !_drawPeaks);
        foreach (var (name, speed) in new[] { ("Slow decay", .8), ("Medium decay", 1.8), ("Fast decay", 3.4) })
            Choice(name, _falloff == speed, () => _falloff = speed);
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var style in Enum.GetValues<ScopeStyle>())
            Choice("Scope · " + style, _scopeStyle == style, () => { _scopeStyle = style; _visMode = 2; });
        menu.ShowAt(anchor);
    }
}
