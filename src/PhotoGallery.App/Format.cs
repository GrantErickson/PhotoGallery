using Microsoft.UI.Xaml;
using PhotoGallery.Core.Media;

namespace PhotoGallery.App;

/// <summary>Static helpers for x:Bind in templates.</summary>
public static class Format
{
    public static Visibility IsVideo(MediaKind kind) => kind == MediaKind.Video ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility HasMotion(MotionSource motion) =>
        motion is MotionSource.LocalPair or MotionSource.Embedded or MotionSource.Cloud ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility HasRating(int rating) => rating > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static string Stars(int rating) => new('★', Math.Clamp(rating, 0, 5));

    public static string Duration(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    public static string FileSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B",
    };
}
