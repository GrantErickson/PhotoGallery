using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoGallery.App.Services;
using PhotoGallery.Core;
using PhotoGallery.Core.Media;

namespace PhotoGallery.App;

/// <summary>
/// Deleting photos and videos: the files go to the Recycle Bin (a Live Photo's own video with its photo; OneDrive keeps
/// its copy in its recycle bin too), then out of the library. Asks first, unless that was switched off.
/// </summary>
public static class Deletion
{
    private static AppServices S => App.Services;

    /// <summary>Deletes the items (after asking); returns the ids that were removed, empty if cancelled.</summary>
    public static async Task<IReadOnlyList<long>> DeleteAsync(XamlRoot root, IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return [];
        var items = await Task.Run(() => ids.Select(S.Media.Get).OfType<MediaItem>().ToList());
        if (items.Count == 0) return [];
        if (S.Settings.ConfirmDelete && !await ConfirmAsync(root, items)) return [];

        var (gone, failed) = await Task.Run(() => Recycle(items));
        var deleted = gone.Count(id => items.Any(i => i.Id == id));
        App.MainWindow.ShowStatus(failed.Count == 0
            ? $"Deleted {Describe(deleted, items.Count(i => i.Kind == MediaKind.Video))} (in the Recycle Bin)."
            : $"Couldn't delete {string.Join(", ", failed)}{(deleted > 0 ? $"; deleted {deleted:N0} others" : "")}.");
        return gone;
    }

    /// <summary>Deletes without asking (another computer asked, through remote access): what went, and what couldn't.</summary>
    public static Task<(List<long> Gone, List<string> Failed)> DeleteWithoutAskingAsync(IReadOnlyCollection<long> ids) =>
        Task.Run(() => Recycle(ids.Select(S.Media.Get).OfType<MediaItem>().ToList()));

    private static async Task<bool> ConfirmAsync(XamlRoot root, List<MediaItem> items)
    {
        var videos = items.Count(i => i.Kind == MediaKind.Video);
        var text = new StringBuilder();
        if (items.Count == 1) text.AppendLine(items[0].Path);
        else
        {
            foreach (var item in items.Take(6)) text.AppendLine(item.FileName);
            if (items.Count > 6) text.AppendLine($"… and {items.Count - 6:N0} more");
        }
        text.AppendLine();
        text.Append(items.Count == 1 ? "It goes to the Recycle Bin" : "They go to the Recycle Bin");
        if (items.Any(i => S.Settings.ToOneDrivePath(i.Path) is not null))
            text.Append(", and OneDrive keeps its copy in its own recycle bin for 30 days");
        text.Append('.');
        var live = items.Count(i => i.Motion == MotionSource.LocalPair);
        if (live > 0) text.Append(items.Count == 1 ? " Its Live Photo video goes too." : " Live Photo videos go with their photos.");

        var dontAsk = new CheckBox { Content = "Don't ask again" };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = items.Count == 1 ? $"Delete this {(videos == 1 ? "video" : "photo")}?" : $"Delete {Describe(items.Count, videos)}?",
            Content = new StackPanel
            {
                Spacing = 12,
                Children = { new TextBlock { Text = text.ToString(), TextWrapping = TextWrapping.Wrap }, dontAsk },
            },
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, // it can be restored from the Recycle Bin
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        if (dontAsk.IsChecked == true)
        {
            S.Settings.ConfirmDelete = false; // Settings can turn it back on
            S.SaveSettings();
        }
        return true;
    }

    private static string Describe(int count, int videos) =>
        videos == 0 ? (count == 1 ? "1 photo" : $"{count:N0} photos")
        : videos == count ? (count == 1 ? "1 video" : $"{count:N0} videos")
        : $"{count:N0} photos and videos";

    private static (List<long> Gone, List<string> Failed) Recycle(List<MediaItem> items)
    {
        var gone = new List<long>();
        var failed = new List<string>();
        var pairs = false;
        foreach (var item in items)
        {
            if (!RecycleWithRetry(item.Path))
            {
                failed.Add(item.FileName);
                continue;
            }
            Log.Info($"Deleted {item.Path} (to the Recycle Bin)");
            gone.Add(item.Id);
            if (item.Motion == MotionSource.LocalPair && item.PairedId is { } videoId && S.Media.Get(videoId) is { } video)
            {
                pairs = true;
                if (RecycleWithRetry(video.Path)) gone.Add(videoId); // otherwise it becomes an ordinary video
            }
        }
        if (gone.Count > 0)
        {
            S.Media.Remove(gone); // cached thumbnails and the like go with them (MediaRepository.Removed)
            if (pairs) S.Media.RecomputeMotion();
        }
        return (gone, failed);
    }

    /// <summary>A video that was just playing can take a moment to be let go of.</summary>
    private static bool RecycleWithRetry(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (RecycleBin.Recycle(path)) return true;
            if (attempt == 3) return false;
            Thread.Sleep(250);
        }
    }
}
