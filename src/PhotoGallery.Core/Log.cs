namespace PhotoGallery.Core;

/// <summary>Minimal append-only diagnostics log (%LocalAppData%\PhotoGallery\app.log), rotated at 5 MB.</summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private static string? _path;

    public static void Initialize(string directory)
    {
        _path = Path.Combine(directory, "app.log");
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length > 5 * 1024 * 1024)
                File.Move(_path, _path + ".old", overwrite: true);
        }
        catch (IOException)
        {
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        if (_path is null) return;
        lock (Gate)
        {
            try
            {
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }
    }
}
