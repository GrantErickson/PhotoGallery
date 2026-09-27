using PhotoGallery.Core.Ocr;
using Windows.Graphics.Imaging;
using OcrEngine = Windows.Media.Ocr.OcrEngine;
using Windows.Storage;

namespace PhotoGallery.App.Imaging;

/// <summary>
/// Reads the text in a photo with Windows' built-in OCR (offline, in the user's languages). Photos are decoded upright
/// at about 1600 px on the long side, where recognition is as good as at full size and several times faster (a 4K
/// screenshot even reads better); tall ones (phone screenshots) get more so their text stays legible. Not for use
/// from two threads at once; make one per worker.
/// </summary>
public sealed class PhotoTextReader
{
    public const string EngineName = "windows-ocr";
    private const int LongSide = 1600;
    private const int TallLongSide = 2600;

    private readonly OcrEngine? _engine = OcrEngine.TryCreateFromUserProfileLanguages();

    /// <summary>False if Windows has no OCR language for the user's languages.</summary>
    public bool IsAvailable => _engine is not null;

    public async Task<PhotoText> ReadAsync(long mediaId, string path, CancellationToken ct = default)
    {
        if (_engine is null) return new PhotoText(mediaId, [], EngineName);
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        double width = decoder.OrientedPixelWidth, height = decoder.OrientedPixelHeight;
        var target = Math.Max(width, height) / Math.Max(1, Math.Min(width, height)) > 1.6 ? TallLongSide : LongSide;
        var scale = Math.Min(1.0, target / Math.Max(width, height));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct);
        var result = await _engine.RecognizeAsync(bitmap).AsTask(ct);

        double longSide = Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
        var lines = result.Lines.Select(line => new OcrLine(line.Words.Select(w => new OcrWord(w.Text,
            Math.Round(w.BoundingRect.X / longSide, 5), Math.Round(w.BoundingRect.Y / longSide, 5),
            Math.Round(w.BoundingRect.Width / longSide, 5), Math.Round(w.BoundingRect.Height / longSide, 5))).ToList()));
        return new PhotoText(mediaId, OcrCleaner.Clean(lines), EngineName);
    }
}
