using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace PhotoGallery.Core.Imaging;

/// <summary>A decoded thumbnail: 32-bit BGRA pixels, top-down.</summary>
public sealed record ThumbnailPixels(int Width, int Height, byte[] Bgra);

/// <summary>
/// Probes the Windows thumbnail cache via IShellItemImageFactory (THUMBNAILONLY). Files Explorer has already
/// shown come back in a few milliseconds (~700/s measured). For uncached files the shell answers
/// WTS_E_EXTRACTIONPENDING while a surrogate process renders them at only 5–10/s, so we don't wait: a miss
/// returns null and the caller decodes the file itself. COM calls run on dedicated STA threads.
/// </summary>
public sealed class ShellThumbnailer : IDisposable
{
    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly List<Thread> _threads = [];

    public ShellThumbnailer(int threads)
    {
        for (var i = 0; i < threads; i++)
        {
            var thread = new Thread(Worker) { IsBackground = true, Name = $"ShellThumbnailer {i}", Priority = ThreadPriority.BelowNormal };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            _threads.Add(thread);
        }
    }

    public Task<ThumbnailPixels?> GetAsync(string path, int size, CancellationToken ct = default)
    {
        var work = new WorkItem(path, size, ct, new TaskCompletionSource<ThumbnailPixels?>(TaskCreationOptions.RunContinuationsAsynchronously));
        _queue.Add(work, ct);
        return work.Completion.Task;
    }

    private void Worker()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            if (work.Cancellation.IsCancellationRequested)
            {
                work.Completion.TrySetCanceled(work.Cancellation);
                continue;
            }
            try
            {
                work.Completion.TrySetResult(Extract(work.Path, work.Size));
            }
            catch (Exception ex)
            {
                work.Completion.TrySetException(ex);
            }
        }
    }

    private static ThumbnailPixels? Extract(string path, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null) return null;
        try
        {
            // THUMBNAILONLY: fail instead of returning the file type icon.
            if (factory.GetImage(new NativeSize(size, size), SiigbfThumbnailOnly, out var hbitmap) != 0 || hbitmap == IntPtr.Zero) return null;
            try
            {
                return ToPixels(hbitmap);
            }
            finally
            {
                DeleteObject(hbitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    private static ThumbnailPixels? ToPixels(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<NativeBitmap>(), out var bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0) return null;
        var info = new BitmapInfoHeader
        {
            biSize = Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = bm.bmWidth,
            biHeight = -bm.bmHeight, // negative = top-down rows
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[bm.bmWidth * bm.bmHeight * 4];
        var dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, hbitmap, 0, (uint)bm.bmHeight, pixels, ref info, 0) == 0) return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
        return new ThumbnailPixels(bm.bmWidth, bm.bmHeight, pixels);
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        foreach (var t in _threads) t.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private sealed record WorkItem(string Path, int Size, CancellationToken Cancellation, TaskCompletionSource<ThumbnailPixels?> Completion);

    private const int SiigbfThumbnailOnly = 0x8;

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize(int cx, int cy)
    {
        public readonly int Cx = cx;
        public readonly int Cy = cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItemImageFactory? item);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hObject, int count, out NativeBitmap bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
}
