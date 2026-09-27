using System.Runtime.InteropServices;

namespace PhotoGallery.App;

/// <summary>
/// Moves files to the Windows Recycle Bin. A file that can't be recycled (too big for it, or on a drive without one)
/// is only deleted for good after Windows' own warning.
/// </summary>
public static class RecycleBin
{
    /// <summary>The window Windows' warnings belong to.</summary>
    public static IntPtr Owner { get; set; }

    /// <summary>Returns true if the file was recycled (or was already gone).</summary>
    public static bool Recycle(string path)
    {
        if (!File.Exists(path)) return true;
        var op = new ShFileOpStruct
        {
            hwnd = Owner,
            wFunc = FoDelete,
            pFrom = path + "\0\0", // double-null-terminated list
            fFlags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent | FofWantNukeWarning,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted && !File.Exists(path);
    }

    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofWantNukeWarning = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOp);
}
