using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoGallery.Core.Editing;

/// <summary>A crop rectangle normalised to 0–1 of the (rotated) image.</summary>
public readonly record struct CropRect(double X, double Y, double Width, double Height)
{
    public static CropRect Full { get; } = new(0, 0, 1, 1);

    public bool IsFull => X <= 0.0001 && Y <= 0.0001 && Width >= 0.9999 && Height >= 0.9999;

    /// <summary>Keeps the rectangle inside the image with a minimum size.</summary>
    public CropRect Clamp(double minSize = 0.02)
    {
        var w = Math.Clamp(Width, minSize, 1);
        var h = Math.Clamp(Height, minSize, 1);
        return new CropRect(Math.Clamp(X, 0, 1 - w), Math.Clamp(Y, 0, 1 - h), w, h);
    }

    /// <summary>The same region after the image is rotated 90° clockwise.</summary>
    public CropRect RotatedClockwise() => new(1 - Y - Height, X, Height, Width);

    /// <summary>The same region after the image is rotated 90° counter-clockwise.</summary>
    public CropRect RotatedCounterClockwise() => new(Y, 1 - X - Width, Height, Width);

    /// <summary>The same region after the image is mirrored left-right.</summary>
    public CropRect FlippedHorizontally() => this with { X = 1 - X - Width };
}

/// <summary>
/// Non-destructive edits, stored as JSON in the Edits table and applied at display/export time.
/// Order of application: colour (exposure, brightness, contrast) → flip → rotate → crop.
/// </summary>
public sealed record EditOperations
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

    /// <summary>Clockwise quarter turns: 0, 90, 180 or 270.</summary>
    public int Rotation { get; init; }
    public bool FlipHorizontal { get; init; }
    /// <summary>Crop in the coordinate space of the flipped and rotated image; null = no crop.</summary>
    public CropRect? Crop { get; init; }
    /// <summary>Exposure in stops, -2..2.</summary>
    public double Exposure { get; init; }
    /// <summary>-1..1.</summary>
    public double Brightness { get; init; }
    /// <summary>-1..1.</summary>
    public double Contrast { get; init; }

    public static EditOperations None { get; } = new();

    [JsonIgnore]
    public bool IsIdentity =>
        Rotation % 360 == 0 && !FlipHorizontal && (Crop is null || Crop.Value.IsFull) &&
        Math.Abs(Exposure) < 0.001 && Math.Abs(Brightness) < 0.001 && Math.Abs(Contrast) < 0.001;

    [JsonIgnore]
    public bool HasColorAdjustments => Math.Abs(Exposure) >= 0.001 || Math.Abs(Brightness) >= 0.001 || Math.Abs(Contrast) >= 0.001;

    /// <summary>True when width and height swap relative to the original.</summary>
    [JsonIgnore]
    public bool SwapsDimensions => Rotation is 90 or 270;

    public EditOperations RotateClockwise() => this with { Rotation = (Rotation + 90) % 360, Crop = Crop?.RotatedClockwise() };

    public EditOperations RotateCounterClockwise() => this with { Rotation = (Rotation + 270) % 360, Crop = Crop?.RotatedCounterClockwise() };

    /// <summary>Mirrors the image as it currently appears (after rotation), so the visible result flips left-right.</summary>
    public EditOperations FlipVisible() => (Rotation is 90 or 270
            ? this with { FlipHorizontal = !FlipHorizontal, Rotation = (Rotation + 180) % 360 }
            : this with { FlipHorizontal = !FlipHorizontal })
        with { Crop = Crop?.FlippedHorizontally() };

    /// <summary>Output size in pixels for an original (EXIF-oriented) image of the given size.</summary>
    public (int Width, int Height) OutputSize(int width, int height)
    {
        var (w, h) = SwapsDimensions ? (height, width) : (width, height);
        if (Crop is { } c && !c.IsFull) (w, h) = ((int)Math.Round(w * c.Width), (int)Math.Round(h * c.Height));
        return (Math.Max(1, w), Math.Max(1, h));
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static EditOperations FromJson(string json) => JsonSerializer.Deserialize<EditOperations>(json, Json) ?? None;
}
