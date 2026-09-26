namespace PhotoGallery.Core.Media;

/// <summary>
/// OneDrive Files On-Demand: a cloud-only placeholder downloads the whole file as soon as anything reads its
/// content. The gallery must never do that on its own (a new 600 MB video would be pulled down just to index it).
/// </summary>
public static class CloudFiles
{
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    public static bool IsOnlineOnly(FileAttributes attributes) =>
        (attributes & (RecallOnDataAccess | FileAttributes.Offline)) != 0;

    public static bool IsOnlineOnly(string path)
    {
        try
        {
            return IsOnlineOnly(File.GetAttributes(path));
        }
        catch (IOException)
        {
            return false;
        }
    }
}
