#:package Svg.Skia@5.2.3
#:property ManagePackageVersionsCentrally=false
#:property Nullable=enable

// Renders the app icon from the SVGs beside this file into everything that ships:
//   src/PhotoGallery.App/Assets/PhotoGallery.ico   the exe, window, taskbar and Start menu (16 to 256 px)
//   src/PhotoGallery.App/Assets/TitleBarIcon.svg   the title bar (the small design, drawn at the screen's scale)
//   src/PhotoGallery.Remote/web/icon.svg           the web app's sign-in page
//   src/PhotoGallery.Remote/web/icon-small.svg     the web app's tab and header
//   src/PhotoGallery.Remote/web/apple-touch-icon.png  a phone's home screen (opaque, 180 px)
//
//   dotnet run tools/icons/MakeIcons.cs                     write them
//   dotnet run tools/icons/MakeIcons.cs -- --preview <dir>  also write preview.png: every size, on dark and light
//
// icon.svg is the full design. Below 30 px its detail turns to mush, so icon-16.svg (16 px) and icon-24.svg (20 and
// 24 px) redraw it on a pixel grid with fewer, bolder shapes.

using System.Runtime.CompilerServices;
using SkiaSharp;
using Svg.Skia;

var here = Here();
var root = Path.GetFullPath(Path.Combine(here, "..", ".."));
var assets = Path.Combine(root, "src", "PhotoGallery.App", "Assets");
var web = Path.Combine(root, "src", "PhotoGallery.Remote", "web");

var full = Load(Path.Combine(here, "icon.svg"));
var small16 = Load(Path.Combine(here, "icon-16.svg"));
var small24 = Load(Path.Combine(here, "icon-24.svg"));

// The sizes Windows asks for at 100% to 400% scale (small icons 16-64, large 32-256); it scales the nearest one down.
int[] icoSizes = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256];
SKPicture For(int size) => size switch { < 20 => small16, < 30 => small24, _ => full };

WriteIco(Path.Combine(assets, "PhotoGallery.ico"), icoSizes.Select(s => (s, Render(For(s), s))).ToList());
File.Copy(Path.Combine(here, "icon-16.svg"), Path.Combine(assets, "TitleBarIcon.svg"), overwrite: true);
File.Copy(Path.Combine(here, "icon.svg"), Path.Combine(web, "icon.svg"), overwrite: true);
File.Copy(Path.Combine(here, "icon-16.svg"), Path.Combine(web, "icon-small.svg"), overwrite: true);
// iOS fills a transparent icon with black and rounds the corners itself: give it the app's dark surface, with room.
File.WriteAllBytes(Path.Combine(web, "apple-touch-icon.png"), Encode(Render(full, 180, background: new SKColor(0x20, 0x20, 0x20), inset: 18)));
Console.WriteLine($"Wrote {assets}\\PhotoGallery.ico, TitleBarIcon.svg and the web icons in {web}.");

if (args is ["--preview", var previewDir])
{
    Directory.CreateDirectory(previewDir);
    var path = Path.Combine(previewDir, "preview.png");
    File.WriteAllBytes(path, Encode(Preview(icoSizes, For)));
    File.WriteAllBytes(Path.Combine(previewDir, "preview-256.png"), Encode(Render(full, 256, background: new SKColor(0x20, 0x20, 0x20))));
    Console.WriteLine($"Wrote {path} and preview-256.png.");
}

static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

static SKPicture Load(string path)
{
    var svg = new SKSvg();
    return svg.Load(path) ?? throw new InvalidOperationException($"Couldn't read {path}.");
}

/// <summary>The picture drawn at size × size (straight alpha), optionally on a background and inset from the edges.</summary>
static SKBitmap Render(SKPicture picture, int size, SKColor? background = null, int inset = 0)
{
    var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul));
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(background ?? SKColors.Transparent);
    var bounds = picture.CullRect;
    var scale = (size - 2f * inset) / Math.Max(bounds.Width, bounds.Height);
    canvas.Translate(inset, inset);
    canvas.Scale(scale);
    canvas.DrawPicture(picture);
    return bitmap;
}

static byte[] Encode(SKBitmap bitmap)
{
    using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

/// <summary>An .ico with one image per size: uncompressed below 64 px (what older readers expect), PNG from 64 up.</summary>
static void WriteIco(string path, List<(int Size, SKBitmap Bitmap)> images)
{
    var entries = images.Select(i => i.Size >= 64 ? Encode(i.Bitmap) : Dib(i.Bitmap)).ToList();
    using var file = new BinaryWriter(File.Create(path));
    file.Write((ushort)0); // reserved
    file.Write((ushort)1); // icon
    file.Write((ushort)images.Count);
    var offset = 6 + 16 * images.Count;
    for (var i = 0; i < images.Count; i++)
    {
        var size = images[i].Size;
        file.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
        file.Write((byte)(size >= 256 ? 0 : size));
        file.Write((byte)0); // no palette
        file.Write((byte)0);
        file.Write((ushort)1); // planes
        file.Write((ushort)32); // bits per pixel
        file.Write(entries[i].Length);
        file.Write(offset);
        offset += entries[i].Length;
    }
    foreach (var entry in entries) file.Write(entry);
}

/// <summary>An icon's bitmap: a BITMAPINFOHEADER (height doubled for the mask), BGRA rows bottom-up, then the AND mask.</summary>
static byte[] Dib(SKBitmap bitmap)
{
    int size = bitmap.Width, maskStride = (size + 31) / 32 * 4;
    using var stream = new MemoryStream();
    using var dib = new BinaryWriter(stream);
    dib.Write(40);
    dib.Write(size);
    dib.Write(size * 2);
    dib.Write((ushort)1);
    dib.Write((ushort)32);
    dib.Write(0); // BI_RGB
    dib.Write(size * size * 4 + maskStride * size);
    dib.Write(0L); // resolution
    dib.Write(0L); // palette
    var pixels = bitmap.GetPixelSpan();
    for (var y = size - 1; y >= 0; y--) dib.Write(pixels.Slice(y * bitmap.RowBytes, size * 4));
    var mask = new byte[maskStride];
    for (var y = size - 1; y >= 0; y--)
    {
        Array.Clear(mask);
        for (var x = 0; x < size; x++)
            if (pixels[y * bitmap.RowBytes + x * 4 + 3] == 0) mask[x / 8] |= (byte)(0x80 >> (x % 8));
        dib.Write(mask);
    }
    dib.Flush();
    return stream.ToArray();
}

/// <summary>Every size at 1:1 and enlarged (pixels kept square), on Windows' dark and light surfaces.</summary>
static SKBitmap Preview(int[] sizes, Func<int, SKPicture> pictureFor)
{
    const int Pad = 16, Zoomed = 192;
    var rowHeight = 2 * Zoomed + 3 * Pad;
    var width = Pad + sizes.Sum(s => Math.Max(s, Zoomed) + Pad);
    var sheet = new SKBitmap(width, 2 * rowHeight);
    using var canvas = new SKCanvas(sheet);
    SKColor[] surfaces = [new(0x20, 0x20, 0x20), new(0xF3, 0xF3, 0xF3)];
    for (var row = 0; row < 2; row++)
    {
        var top = row * rowHeight;
        using var surface = new SKPaint { Color = surfaces[row] };
        canvas.DrawRect(0, top, width, rowHeight, surface);
        var left = Pad;
        foreach (var size in sizes)
        {
            using var image = SKImage.FromBitmap(Render(pictureFor(size), size));
            canvas.DrawImage(image, left, top + Pad, SKSamplingOptions.Default);
            var zoom = Zoomed / size;
            if (zoom > 1)
                canvas.DrawImage(image, SKRect.Create(left, top + Zoomed + 2 * Pad, size * zoom, size * zoom), new SKSamplingOptions(SKFilterMode.Nearest));
            left += Math.Max(size, Zoomed) + Pad;
        }
    }
    return sheet;
}
