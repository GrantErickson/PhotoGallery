using PhotoGallery.Core.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace PhotoGallery.App;

public static class Clipboard
{
    private static CancellationTokenSource? _copying;
    private static bool _copiedFiles;

    /// <summary>Copies full paths as text (one per line) and tells the user.</summary>
    public static void CopyPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, paths));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        App.MainWindow.ShowStatus(paths.Count == 1 ? $"Copied {paths[0]}" : $"Copied {paths.Count:N0} paths");
    }

    /// <summary>
    /// Copies photos and videos, to paste into another app or a folder: as files (see <see cref="Handoff"/>), and a
    /// single photo also as a picture, for apps that only take pictures (Paint, some chats).
    /// </summary>
    public static async Task CopyAsync(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return;
        _copying?.Cancel();
        var cts = _copying = new CancellationTokenSource();
        try
        {
            var items = await Handoff.ItemsAsync(ids);
            var progress = new Progress<int>(done =>
            {
                if (!cts.IsCancellationRequested)
                    App.MainWindow.ShowStatus(items.Count == 1 ? "Copying…" : $"Copying {done + 1:N0} of {items.Count:N0}…", sticky: true);
            });
            var (files, failed) = await Handoff.FilesAsync(items, progress, cts.Token);
            if (files.Count == 0)
            {
                App.MainWindow.ShowStatus(items.Count <= 1 ? "Couldn't copy it: the file can't be read" : "Couldn't copy them: the files can't be read");
                return;
            }
            var storage = await Handoff.StorageFilesAsync(files);
            cts.Token.ThrowIfCancellationRequested();

            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetStorageItems(storage);
            if (items is [{ Kind: not MediaKind.Video }] && storage is [var photo])
                package.SetBitmap(RandomAccessStreamReference.CreateFromFile(photo));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            _copiedFiles = true;
            App.MainWindow.ShowStatus(failed > 0
                ? $"Copied {files.Count:N0} of {items.Count:N0} ({failed:N0} couldn't be read)"
                : files.Count == 1 ? $"Copied {Path.GetFileName(items[0].Path)}" : $"Copied {files.Count:N0} items");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PhotoGallery.Core.Log.Error("Copy failed", ex);
            App.MainWindow.ShowStatus($"Couldn't copy: {ex.Message}");
        }
    }

    /// <summary>
    /// When the app closes: what it copied stays on the clipboard whole. Without this, Windows keeps only the picture
    /// and the files can't be pasted into a folder any more.
    /// </summary>
    public static void KeepAfterExit()
    {
        if (!_copiedFiles) return;
        try
        {
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Something else was copied since ("not the owner of the data on the clipboard"): that's for its own app to keep.
        }
    }
}
