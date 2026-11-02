using Microsoft.UI.Xaml.Controls;
using UnoPlay.Core;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace UnoPlay.ClassicUI;

public sealed partial class ClassicPlayerView
{
    private IReadOnlyList<EqualizerPreset> _importedPresets = [];

    private void BuildPresetMenu(MenuFlyout menu)
    {
        void ActionItem(string name, Func<Task> action)
        {
            var item = new MenuFlyoutItem { Text = name };
            item.Click += (_, _) => _ = Guard(action);
            menu.Items.Add(item);
        }

        void Catalog(string title, IReadOnlyList<EqualizerPreset> presets)
        {
            var group = new MenuFlyoutSubItem { Text = title };
            foreach (var preset in presets)
            {
                var item = new MenuFlyoutItem { Text = preset.Name };
                item.Click += (_, _) => SelectPreset(preset);
                group.Items.Add(item);
            }
            menu.Items.Add(group);
        }

        Catalog("Factory presets", ClassicEqualizerPresets.All);
        if (_importedPresets.Count > 0) Catalog("Imported presets", _importedPresets);
        ActionItem("Load preset or bank…", ImportPresets);
        ActionItem("Save current preset…", ExportPreset);
        menu.Items.Add(new MenuFlyoutSeparator());
        ActionItem("Save preset for this track", () => { RememberEq(); return Task.CompletedTask; });
        ActionItem("Load preset for this track", () => { RecallEq(); return Task.CompletedTask; });
    }

    private void SelectPreset(EqualizerPreset preset)
    {
        for (int i = 0; i < _bands.Length; i++) _bands[i] = preset.GetBand(i);
        _preamp = preset.Preamp;
        _eq = _loaded is null || Engine.SupportsDsp;
        ApplyEq();
        QueuePreferencesSave();
        Notify(preset.Name.ToUpperInvariant());
    }

    private async Task ImportPresets()
    {
        var file = await OpenPicker([".eqf", ".q1", ".q2"]).PickSingleFileAsync();
        if (file is null) return;
        var info = await file.GetBasicPropertiesAsync();
        if (info.Size > EqualizerFile.MaxFileBytes) throw new InvalidDataException("EQ preset bank is too large.");
        using var source = await file.OpenStreamForReadAsync();
        var data = new byte[checked((int)info.Size)];
        await source.ReadExactlyAsync(data);
        var presets = EqualizerFile.Read(data);
        if (presets.Count == 0) throw new InvalidDataException("No presets in this EQ library.");
        _importedPresets = presets;
        if (presets.Count == 1) SelectPreset(presets[0]);
        else Notify($"{presets.Count} PRESETS IN IMPORTED PRESETS");
    }

    private async Task ExportPreset()
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.MusicLibrary, SuggestedFileName = "UnoPlay" };
        picker.FileTypeChoices.Add("Winamp equalizer preset", new List<string> { ".eqf" });
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        // Winamp's Load EQF command looks up this exact record name.
        byte[] data = EqualizerFile.Write([EqualizerPreset.FromDecibels("Entry1", _bands, _preamp)]);
        await FileIO.WriteBytesAsync(file, data);
        Notify("EQ PRESET SAVED");
    }
}
