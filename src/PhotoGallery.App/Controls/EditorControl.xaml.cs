using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoGallery.App.Editing;
using PhotoGallery.App.Services;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.Media;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.System;

namespace PhotoGallery.App.Controls;

/// <summary>
/// Non-destructive editor: rotate/flip, crop (free or fixed aspect), exposure/brightness/contrast.
/// The canvas shows the whole rotated image; the crop box is an overlay in normalised coordinates.
/// </summary>
public sealed partial class EditorControl : UserControl
{
    private static AppServices S => App.Services;
    private static readonly double?[] Aspects = [null, 0, 1, 4.0 / 3, 3.0 / 2, 16.0 / 9, 9.0 / 16]; // 0 = the image's own aspect

    private MediaItem? _item;
    private CanvasBitmap? _source;
    private EditOperations _ops = EditOperations.None;
    private Rect _fit;                 // where the rotated image is drawn, in DIPs
    private Size _rotatedSize;         // rotated image size in source pixels
    private bool _suppress;

    public EditorControl()
    {
        InitializeComponent();
        Unloaded += (_, _) => ReleaseSource();
        // Accelerators work wherever focus is (the canvas and crop handles don't take focus).
        AddShortcut(VirtualKey.Escape, VirtualKeyModifiers.None, () => Close(saved: false));
        AddShortcut(VirtualKey.S, VirtualKeyModifiers.Control, Save);
        AddShortcut((VirtualKey)219, VirtualKeyModifiers.None, () => Rotate(clockwise: false)); // [
        AddShortcut((VirtualKey)221, VirtualKeyModifiers.None, () => Rotate(clockwise: true));  // ]
    }

    private void AddShortcut(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            if (Visibility != Visibility.Visible || _item is null) return;
            args.Handled = true;
            action();
        };
        KeyboardAccelerators.Add(accelerator);
    }

    /// <summary>Raised when the editor closes; true if edits were saved.</summary>
    public event Action<bool>? Closed;

    private CropRect Crop => _ops.Crop ?? CropRect.Full;

    public async Task OpenAsync(MediaItem item)
    {
        _item = item;
        FileText.Text = item.FileName;
        _ops = S.Edits.Get(item.Id) ?? EditOperations.None;
        _suppress = true;
        ExposureSlider.Value = _ops.Exposure;
        BrightnessSlider.Value = _ops.Brightness;
        ContrastSlider.Value = _ops.Contrast;
        AspectBox.SelectedIndex = 0;
        _suppress = false;

        Busy.IsActive = true;
        ReleaseSource();
        try
        {
            // Preview resolution: enough for the stage at this DPI, capped for GPU memory.
            var scale = XamlRoot?.RasterizationScale ?? 1.0;
            var longest = (int)Math.Min(4096, Math.Max(Stage.ActualWidth, Stage.ActualHeight) * scale * 1.5);
            _source = await EditRenderer.LoadAsync(item.Path, Math.Max(1024, longest));
        }
        catch (Exception ex)
        {
            PhotoGallery.Core.Log.Error($"Editor could not load {item.Path}", ex);
            App.MainWindow.ShowStatus("This file can't be edited — Windows couldn't decode it.");
            Closed?.Invoke(false);
            return;
        }
        finally
        {
            Busy.IsActive = false;
        }
        Refresh();
        Focus(FocusState.Programmatic);
    }

    private void ReleaseSource()
    {
        _source?.Dispose();
        _source = null;
    }

    // ---------- Drawing ----------

    private void Refresh()
    {
        EditCanvas.Invalidate();
        UpdateOverlay();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_source is null) return;
        var (image, size) = EditRenderer.Apply(_source, _ops, includeCrop: false);
        _rotatedSize = size;
        _fit = EditRenderer.Fit(size, sender.Size);
        args.DrawingSession.DrawImage(image, _fit, new Rect(0, 0, size.Width, size.Height));
        if (!ReferenceEquals(image, _source)) (image as IDisposable)?.Dispose();
        DispatcherQueue.TryEnqueue(UpdateOverlay);
    }

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Overlay.Width = e.NewSize.Width;
        Overlay.Height = e.NewSize.Height;
        Refresh();
    }

    private void UpdateOverlay()
    {
        var visible = _source is not null && _fit.Width > 0;
        CropBox.Visibility = Shade.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        foreach (var handle in Handles) handle.Visibility = CropBox.Visibility;
        if (!visible) return;

        var box = ToScreen(Crop);
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(CropBox, box.X);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(CropBox, box.Y);
        CropBox.Width = box.Width;
        CropBox.Height = box.Height;

        PlaceHandle(HandleTopLeft, box.Left, box.Top);
        PlaceHandle(HandleTopRight, box.Right, box.Top);
        PlaceHandle(HandleBottomLeft, box.Left, box.Bottom);
        PlaceHandle(HandleBottomRight, box.Right, box.Bottom);

        // Dim everything outside the crop box (even-odd: image rect minus crop rect).
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(new RectangleGeometry { Rect = _fit });
        group.Children.Add(new RectangleGeometry { Rect = box });
        Shade.Data = group;

        var (w, h) = (Math.Round(Crop.Width * _rotatedSize.Width), Math.Round(Crop.Height * _rotatedSize.Height));
        var scaleToOriginal = _item is { Width: > 0 } item ? Math.Max(item.Width, item.Height) / Math.Max(_source!.Size.Width, _source.Size.Height) : 1;
        SizeText.Text = $"{w * scaleToOriginal:N0} × {h * scaleToOriginal:N0} px";
    }

    private IEnumerable<Thumb> Handles => [HandleTopLeft, HandleTopRight, HandleBottomLeft, HandleBottomRight];

    private static void PlaceHandle(Thumb handle, double x, double y)
    {
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(handle, x - handle.Width / 2);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(handle, y - handle.Height / 2);
    }

    private Rect ToScreen(CropRect c) => new(_fit.X + c.X * _fit.Width, _fit.Y + c.Y * _fit.Height, c.Width * _fit.Width, c.Height * _fit.Height);

    // ---------- Crop interaction ----------

    /// <summary>Crop aspect (width/height in pixels) for the current preset, or null for free.</summary>
    private double? LockedAspect =>
        Aspects[Math.Max(0, AspectBox.SelectedIndex)] is { } a
            ? a == 0 ? _rotatedSize.Width / Math.Max(1, _rotatedSize.Height) : a
            : null;

    /// <summary>Normalised height for a normalised width at the locked aspect.</summary>
    private double HeightFor(double width, double aspect) => width * _rotatedSize.Width / (aspect * _rotatedSize.Height);

    private void OnHandleDrag(object sender, DragDeltaEventArgs e)
    {
        if (_fit.Width <= 0) return;
        var corner = (string)((FrameworkElement)sender).Tag;
        var dx = e.HorizontalChange / _fit.Width;
        var dy = e.VerticalChange / _fit.Height;
        var c = Crop;
        double left = c.X, top = c.Y, right = c.X + c.Width, bottom = c.Y + c.Height;
        const double min = 0.03;

        if (corner.Contains('L')) left = Math.Clamp(left + dx, 0, right - min);
        else right = Math.Clamp(right + dx, left + min, 1);
        if (corner.Contains('T')) top = Math.Clamp(top + dy, 0, bottom - min);
        else bottom = Math.Clamp(bottom + dy, top + min, 1);

        if (LockedAspect is { } aspect)
        {
            // Width drives height; the opposite corner stays anchored. Shrink if the height won't fit.
            var width = right - left;
            var height = HeightFor(width, aspect);
            var room = corner.Contains('T') ? bottom : 1 - top;
            if (height > room)
            {
                height = room;
                width = height * aspect * _rotatedSize.Height / _rotatedSize.Width;
                if (corner.Contains('L')) left = right - width;
                else right = left + width;
            }
            if (corner.Contains('T')) top = bottom - height;
            else bottom = top + height;
        }
        SetCrop(new CropRect(left, top, right - left, bottom - top));
    }

    private void OnCropBoxManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (_fit.Width <= 0) return;
        var c = Crop;
        SetCrop(c with { X = c.X + e.Delta.Translation.X / _fit.Width, Y = c.Y + e.Delta.Translation.Y / _fit.Height });
    }

    private void SetCrop(CropRect crop)
    {
        crop = crop.Clamp(0.01);
        _ops = _ops with { Crop = crop.IsFull ? null : crop };
        UpdateOverlay();
    }

    private void OnAspectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || _source is null) return;
        ApplyAspectPreset();
    }

    /// <summary>The largest centred crop with the preset's aspect.</summary>
    private void ApplyAspectPreset()
    {
        if (LockedAspect is not { } aspect || _rotatedSize.Width <= 0) return;
        double w = 1, h = HeightFor(1, aspect);
        if (h > 1)
        {
            h = 1;
            w = aspect * _rotatedSize.Height / _rotatedSize.Width;
        }
        SetCrop(new CropRect((1 - w) / 2, (1 - h) / 2, w, h));
    }

    private void OnResetCrop(object sender, RoutedEventArgs e)
    {
        _suppress = true;
        AspectBox.SelectedIndex = 0;
        _suppress = false;
        SetCrop(CropRect.Full);
    }

    // ---------- Rotate / light ----------

    private void OnRotateLeft(object sender, RoutedEventArgs e) => Rotate(clockwise: false);

    private void OnRotateRight(object sender, RoutedEventArgs e) => Rotate(clockwise: true);

    private void Rotate(bool clockwise)
    {
        _ops = clockwise ? _ops.RotateClockwise() : _ops.RotateCounterClockwise();
        _rotatedSize = new Size(_rotatedSize.Height, _rotatedSize.Width);
        if (LockedAspect is not null) ApplyAspectPreset();
        Refresh();
    }

    private void OnFlip(object sender, RoutedEventArgs e)
    {
        _ops = _ops.FlipVisible();
        Refresh();
    }

    private void OnLightChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppress) return;
        _ops = _ops with { Exposure = ExposureSlider.Value, Brightness = BrightnessSlider.Value, Contrast = ContrastSlider.Value };
        EditCanvas.Invalidate();
    }

    private void OnResetLight(object sender, RoutedEventArgs e)
    {
        _suppress = true;
        ExposureSlider.Value = BrightnessSlider.Value = ContrastSlider.Value = 0;
        _suppress = false;
        _ops = _ops with { Exposure = 0, Brightness = 0, Contrast = 0 };
        EditCanvas.Invalidate();
    }

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        _suppress = true;
        ExposureSlider.Value = BrightnessSlider.Value = ContrastSlider.Value = 0;
        AspectBox.SelectedIndex = 0;
        _suppress = false;
        _ops = EditOperations.None;
        Refresh();
    }

    // ---------- Save / export / close ----------

    private void OnSave(object sender, RoutedEventArgs e) => Save();

    private void Save()
    {
        if (_item is null) return;
        S.Edits.Save(_item.Id, _ops);
        S.Thumbnails.Invalidate([_item.Id]);
        App.MainWindow.ShowStatus(_ops.IsIdentity ? "Edits removed — showing the original." : "Edits saved. The original file is unchanged.");
        Close(saved: true);
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_item is null) return;
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(_item.FileName) + "-edited",
        };
        picker.FileTypeChoices.Add("JPEG image", [".jpg"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow.Handle);
        if (await picker.PickSaveFileAsync() is not { } file) return;
        if (string.Equals(file.Path, _item.Path, StringComparison.OrdinalIgnoreCase))
        {
            App.MainWindow.ShowStatus("Choose a different name — the original is never overwritten.");
            return;
        }
        Busy.IsActive = true;
        try
        {
            await EditRenderer.ExportAsync(_item.Path, _ops, file, _item.TakenLocal);
            App.MainWindow.ShowStatus($"Exported {file.Path}");
        }
        catch (Exception ex)
        {
            PhotoGallery.Core.Log.Error($"Export of {_item.Path} failed", ex);
            App.MainWindow.ShowStatus($"Export failed: {ex.Message}");
        }
        finally
        {
            Busy.IsActive = false;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close(saved: false);

    private void Close(bool saved)
    {
        ReleaseSource();
        Closed?.Invoke(saved);
    }
}
