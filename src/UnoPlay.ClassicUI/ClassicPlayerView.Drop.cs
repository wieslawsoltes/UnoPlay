using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace UnoPlay.ClassicUI;

public sealed partial class ClassicPlayerView
{
    private void EnableDrop()
    {
        _desk.AllowDrop = true;
        _desk.DragOver += (_, e) =>
        {
            var data = e.DataView;
            e.AcceptedOperation = data.Contains(StandardDataFormats.StorageItems) || data.Contains(StandardDataFormats.WebLink) ||
                data.Contains(StandardDataFormats.Text) ? DataPackageOperation.Copy : DataPackageOperation.None;
            e.Handled = true;
        };
        _desk.Drop += async (_, e) =>
        {
            e.Handled = true;
            var deferral = e.GetDeferral();
            try { await Guard(() => ImportDrop(e.DataView)); }
            finally { deferral.Complete(); }
        };
    }

    private async Task ImportDrop(DataPackageView data)
    {
        int first = Queue.Tracks.Count;
        if (data.Contains(StandardDataFormats.StorageItems))
        {
            var files = (await data.GetStorageItemsAsync()).OfType<StorageFile>().ToArray();
            foreach (var file in files.Where(f => AudioVideo.Contains(Path.GetExtension(f.Name), StringComparer.OrdinalIgnoreCase))) AddStorage(file);
            foreach (var file in files)
            {
                string ext = Path.GetExtension(file.Name);
                if (ext.Equals(".wsz", StringComparison.OrdinalIgnoreCase)) await LoadSkinFile(file);
                else if (ListFormats.Contains(ext, StringComparer.OrdinalIgnoreCase)) await LoadPlaylistFile(file);
            }
        }
        else
        {
            string? text = data.Contains(StandardDataFormats.WebLink) ? (await data.GetWebLinkAsync()).ToString() :
                data.Contains(StandardDataFormats.Text) ? (await data.GetTextAsync()).Trim() : null;
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") await AddLocation(uri);
        }
        if (first < Queue.Tracks.Count && !Engine.IsPlaying) await PlayIndex(first);
    }
}
