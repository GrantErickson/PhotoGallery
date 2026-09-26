using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace PhotoGallery.Core.Duplicates;

public static class ContentHashes
{
    private const int Chunk = 64 * 1024;

    /// <summary>
    /// SHA-256 over the file size plus its first and last 64 KB (128-bit hex). Reads at most 128 KB, so it is
    /// cheap even for large videos; together with an equal size it identifies byte-identical copies.
    /// </summary>
    public static string QuickHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, Chunk, FileOptions.RandomAccess);
        var length = stream.Length;
        var buffer = new byte[8 + 2 * Chunk];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, length);
        var head = stream.ReadAtLeast(buffer.AsSpan(8, Chunk), Chunk, throwOnEndOfStream: false);
        var used = 8 + head;
        if (length > Chunk)
        {
            stream.Seek(Math.Max(Chunk, length - Chunk), SeekOrigin.Begin);
            used += stream.ReadAtLeast(buffer.AsSpan(used, Chunk), Chunk, throwOnEndOfStream: false);
        }
        return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, used)), 0, 16);
    }

    /// <summary>
    /// 64-bit difference hash (dHash) of an image: shrink to 9×8 grey, one bit per horizontal gradient.
    /// Resized/re-encoded copies of a photo stay within a few bits of each other.
    /// </summary>
    public static async Task<long> PerceptualHashAsync(string imagePath)
    {
        var file = await StorageFile.GetFileFromPathAsync(imagePath);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var transform = new BitmapTransform { ScaledWidth = 9, ScaledHeight = 8, InterpolationMode = BitmapInterpolationMode.Fant };
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        return DifferenceHash(pixels.DetachPixelData(), 9, 8);
    }

    /// <summary>dHash from a BGRA8 buffer of (width × height), width = 9, height = 8.</summary>
    internal static long DifferenceHash(ReadOnlySpan<byte> bgra, int width, int height)
    {
        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width - 1; x++)
            {
                if (Luma(bgra, (y * width + x) * 4) < Luma(bgra, (y * width + x + 1) * 4)) hash |= 1UL << bit;
                bit++;
            }
        }
        return (long)hash;
    }

    private static int Luma(ReadOnlySpan<byte> p, int i) => p[i] * 114 + p[i + 1] * 587 + p[i + 2] * 299;

    public static int Distance(long a, long b) => BitOperations.PopCount((ulong)(a ^ b));
}
