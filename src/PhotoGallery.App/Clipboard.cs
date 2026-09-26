using Windows.ApplicationModel.DataTransfer;

namespace PhotoGallery.App;

public static class Clipboard
{
    /// <summary>Copies full paths as text (one per line) and tells the user.</summary>
    public static void CopyPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, paths));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        App.MainWindow.ShowStatus(paths.Count == 1 ? $"Copied {paths[0]}" : $"Copied {paths.Count:N0} paths");
    }
}
