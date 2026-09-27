using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoGallery.App.Editing;
using PhotoGallery.App.Services;
using PhotoGallery.Core;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.Media;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.System;

namespace PhotoGallery.App.Controls;

/// <summary>
/// Non-destructive editor: rotate/flip, crop (free or fixed aspect), light and colour (with Auto).
/// The canvas shows the whole rotated image; the crop box is an overlay in normalised coordinates.
/// </summary>
public sealed partial class EditorControl : UserControl
{
    private static AppServices S => App.Services;
    private static readonly double?[] Aspects = [null, 0, 1, 4.0 / 3, 3.0 / 2, 16.0 / 9, 9.0 / 16]; // 0 = the image's own aspect

    private MediaItem? _item;
    private CanvasBitmap? _source;
    private EditOperations _ops = EditOperations.None;
    private EditOperations _openedWith = EditOperations.None;
    private Rect _fit;                 // where the rotated image is drawn, in DIPs
    private Size _rotatedSize;         // rotated image size in source pixels
    private bool _suppress;

    public EditorControl()
    {
        InitializeComponent();
        Unloaded += (_, _) => ReleaseSource();
        // Accelerators work wherever focus is (the canvas and crop handles don't take focus).
        AddShortcut(VirtualKey.Escape, VirtualKeyModifiers.None, () => _ = RequestCloseAsync());
        AddShortcut(VirtualKey.S, VirtualKeyModifiers.Control, () => _ = SaveCopyAsync());
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
        CopyNameRun.Text = Path.GetFileName(NextCopyPath(item.Path));
        OverwriteButton.IsEnabled = EditRenderer.CanWriteFormat(item.Path);
        _ops = S.Edits.Get(item.Id) ?? EditOperations.None;
        _openedWith = _ops;
        SmartCropText.Visibility = Visibility.Collapsed;
        ShowLight();
        _suppress = true;
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
        PlaceHandle(HandleTop, box.Left + box.Width / 2, box.Top);
        PlaceHandle(HandleBottom, box.Left + box.Width / 2, box.Bottom);
        PlaceHandle(HandleLeft, box.Left, box.Top + box.Height / 2);
        PlaceHandle(HandleRight, box.Right, box.Top + box.Height / 2);

        // Dim everything outside the crop box (even-odd: image rect minus crop rect).
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(new RectangleGeometry { Rect = _fit });
        group.Children.Add(new RectangleGeometry { Rect = box });
        Shade.Data = group;

        var (w, h) = (Math.Round(Crop.Width * _rotatedSize.Width), Math.Round(Crop.Height * _rotatedSize.Height));
        var scaleToOriginal = _item is { Width: > 0 } item ? Math.Max(item.Width, item.Height) / Math.Max(_source!.Size.Width, _source.Size.Height) : 1;
        SizeText.Text = $"{w * scaleToOriginal:N0} × {h * scaleToOriginal:N0} px";
    }

    private IEnumerable<Border> Handles => [HandleTopLeft, HandleTopRight, HandleBottomLeft, HandleBottomRight, HandleTop, HandleBottom, HandleLeft, HandleRight];

    private static void PlaceHandle(Border handle, double x, double y)
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

    // Handles use plain pointer capture (press → move → release), which works the same for mouse, pen and touch.
    private Point? _dragFrom;

    private void OnHandlePressed(object sender, PointerRoutedEventArgs e)
    {
        var element = (UIElement)sender;
        if (!element.CapturePointer(e.Pointer)) return;
        _dragFrom = e.GetCurrentPoint(Overlay).Position;
        e.Handled = true;
    }

    private void OnHandleMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragFrom is not { } from) return;
        var to = e.GetCurrentPoint(Overlay).Position;
        _dragFrom = to;
        DragHandle((string)((FrameworkElement)sender).Tag, to.X - from.X, to.Y - from.Y);
        e.Handled = true;
    }

    private void OnHandleReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragFrom = null;
        ((UIElement)sender).ReleasePointerCaptures();
    }

    private void DragHandle(string handle, double deltaX, double deltaY)
    {
        if (_fit.Width <= 0) return;
        var dx = deltaX / _fit.Width;
        var dy = deltaY / _fit.Height;
        var c = Crop;
        double left = c.X, top = c.Y, right = c.X + c.Width, bottom = c.Y + c.Height;
        const double min = 0.03;
        bool moveLeft = handle.Contains('L'), moveRight = handle.Contains('R'), moveTop = handle.Contains('T'), moveBottom = handle.Contains('B');

        if (moveLeft) left = Math.Clamp(left + dx, 0, right - min);
        if (moveRight) right = Math.Clamp(right + dx, left + min, 1);
        if (moveTop) top = Math.Clamp(top + dy, 0, bottom - min);
        if (moveBottom) bottom = Math.Clamp(bottom + dy, top + min, 1);

        if (LockedAspect is { } sideAspect && handle.Length == 1)
        {
            // Side handle with a fixed aspect: the other dimension follows, centred on the box.
            if (moveLeft || moveRight)
            {
                var height = Math.Min(HeightFor(right - left, sideAspect), 1);
                var width = height * sideAspect * _rotatedSize.Height / _rotatedSize.Width;
                if (moveLeft) left = right - width;
                else right = left + width;
                var centerY = (top + bottom) / 2;
                top = Math.Clamp(centerY - height / 2, 0, 1 - height);
                bottom = top + height;
            }
            else
            {
                var width = Math.Min((bottom - top) * sideAspect * _rotatedSize.Height / _rotatedSize.Width, 1);
                var height = HeightFor(width, sideAspect);
                if (moveTop) top = bottom - height;
                else bottom = top + height;
                var centerX = (left + right) / 2;
                left = Math.Clamp(centerX - width / 2, 0, 1 - width);
                right = left + width;
            }
        }
        else if (LockedAspect is { } aspect)
        {
            var corner = handle;
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

    /// <summary>Crops around faces and the most interesting part, in the chosen shape.</summary>
    private async void OnSmartCrop(object sender, RoutedEventArgs e)
    {
        if (_source is null || _rotatedSize.Width <= 0) return;
        SmartCropButton.IsEnabled = false;
        Busy.IsActive = true;
        try
        {
            var (crop, faces) = await SmartCropAnalyzer.FindAsync(_source, _ops, LockedAspect);
            SetCrop(crop);
            SmartCropText.Text = crop.IsFull
                ? "The subject already fills the photo — pick a shape above to crop to it."
                : faces switch
                {
                    0 => "Cropped to the main subject.",
                    1 => "Cropped around 1 face.",
                    _ => $"Cropped around {faces} faces.",
                } + " Adjust the box if needed.";
            SmartCropText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Error($"Smart crop failed for {_item?.Path}", ex);
            App.MainWindow.ShowStatus($"Smart crop failed: {ex.Message}");
        }
        finally
        {
            SmartCropButton.IsEnabled = true;
            Busy.IsActive = false;
        }
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

    /// <summary>Puts the light and colour sliders at the current edit's values.</summary>
    private void ShowLight()
    {
        _suppress = true;
        ExposureSlider.Value = _ops.Exposure;
        BrightnessSlider.Value = _ops.Brightness;
        ContrastSlider.Value = _ops.Contrast;
        SaturationSlider.Value = _ops.Saturation;
        TemperatureSlider.Value = _ops.Temperature;
        TintSlider.Value = _ops.Tint;
        _suppress = false;
    }

    private void OnLightChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppress) return;
        _ops = _ops with
        {
            Exposure = ExposureSlider.Value, Brightness = BrightnessSlider.Value, Contrast = ContrastSlider.Value,
            Saturation = SaturationSlider.Value, Temperature = TemperatureSlider.Value, Tint = TintSlider.Value,
        };
        EditCanvas.Invalidate();
    }

    /// <summary>Levels, white balance and saturation from what's inside the crop, shown on the sliders.</summary>
    private void OnAuto(object sender, RoutedEventArgs e)
    {
        if (_source is null) return;
        try
        {
            var (bgra, width) = EditRenderer.Sample(_source, _ops.WithoutColor, 256);
            _ops = AutoAdjust.Apply(_ops, bgra, width);
        }
        catch (Exception ex)
        {
            Log.Error($"Auto adjust failed for {_item?.Path}", ex);
            App.MainWindow.ShowStatus($"Auto adjust failed: {ex.Message}");
            return;
        }
        ShowLight();
        EditCanvas.Invalidate();
    }

    private void OnResetLight(object sender, RoutedEventArgs e)
    {
        _ops = _ops.WithoutColor;
        ShowLight();
        EditCanvas.Invalidate();
    }

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        _ops = EditOperations.None;
        ShowLight();
        _suppress = true;
        AspectBox.SelectedIndex = 0;
        _suppress = false;
        Refresh();
    }

    // ---------- Save / export / close ----------

    private void OnSave(object sender, RoutedEventArgs e) => Save();

    /// <summary>Keep edits in the gallery only (non-destructive; no file written).</summary>
    private void Save()
    {
        if (_item is null) return;
        S.Edits.Save(_item.Id, _ops);
        S.Thumbnails.Invalidate([_item.Id]);
        App.MainWindow.ShowStatus(_ops.IsIdentity
            ? "Edits removed — showing the original."
            : "Edits kept in the gallery only. No file was changed; use Save as copy or Overwrite to write them to a file.");
        Close(saved: true);
    }

    private async void OnSaveCopy(object sender, RoutedEventArgs e) => await SaveCopyAsync();

    /// <summary>Writes "&lt;name&gt;_N.jpg" next to the original, indexes it and links it to the original.</summary>
    private async Task SaveCopyAsync()
    {
        if (_item is not { } item) return;
        if (_ops.IsIdentity)
        {
            App.MainWindow.ShowStatus("Nothing to save — make an edit first.");
            return;
        }
        var target = NextCopyPath(item.Path);
        Busy.IsActive = true;
        try
        {
            await EditRenderer.WriteFileAsync(item.Path, _ops, target, item.TakenLocal, Location(item));
            var copy = await Task.Run(() => S.Indexing.IndexFileNow(target));
            if (copy is not null) S.Media.SetDerivedFrom(copy.Id, item.Id);
            // The copy carries the edits; the original goes back to showing itself.
            S.Edits.Save(item.Id, EditOperations.None);
            S.Thumbnails.Invalidate([item.Id]);
            App.MainWindow.ShowStatus($"Saved {Path.GetFileName(target)} next to the original.");
            Close(saved: true);
        }
        catch (Exception ex)
        {
            Log.Error($"Saving a copy of {item.Path} failed", ex);
            App.MainWindow.ShowStatus($"Couldn't save the copy: {ex.Message}");
        }
        finally
        {
            Busy.IsActive = false;
        }
    }

    private static (double, double)? Location(MediaItem item) =>
        item is { Latitude: { } lat, Longitude: { } lon } ? (lat, lon) : null;

    /// <summary>"D:\x\IMG_0840.HEIC" → "D:\x\IMG_0840_1.jpg" (the next free number).</summary>
    internal static string NextCopyPath(string original)
    {
        var folder = Path.GetDirectoryName(original)!;
        var name = Path.GetFileNameWithoutExtension(original);
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(folder, $"{name}_{n}.jpg");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private async void OnOverwrite(object sender, RoutedEventArgs e)
    {
        if (_item is not { } item) return;
        if (_ops.IsIdentity)
        {
            App.MainWindow.ShowStatus("Nothing to save — make an edit first.");
            return;
        }
        if (!EditRenderer.CanWriteFormat(item.Path))
        {
            App.MainWindow.ShowStatus($"{Path.GetExtension(item.Path).ToUpperInvariant()} files can't be overwritten here — save a copy instead.");
            return;
        }
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Overwrite the original?",
                $"{item.Path}\n\nThe edited photo replaces this file (and the copy in OneDrive). The current version is moved to the Recycle Bin first, so you can restore it.",
                "Overwrite"))
            return;
        Busy.IsActive = true;
        try
        {
            var temp = Path.Combine(Path.GetDirectoryName(item.Path)!, $".{Guid.NewGuid():N}{Path.GetExtension(item.Path)}");
            await EditRenderer.WriteFileAsync(item.Path, _ops, temp, item.TakenLocal, Location(item));
            if (!RecycleBin.Recycle(item.Path))
            {
                File.Delete(temp);
                throw new IOException("The original couldn't be moved to the Recycle Bin, so it was left unchanged.");
            }
            File.Move(temp, item.Path);
            S.Edits.Save(item.Id, EditOperations.None);
            S.Thumbnails.Invalidate([item.Id]);
            S.Indexing.RequestIndex();
            App.MainWindow.ShowStatus("Original overwritten. The previous version is in the Recycle Bin.");
            Close(saved: true);
        }
        catch (Exception ex)
        {
            Log.Error($"Overwriting {item.Path} failed", ex);
            App.MainWindow.ShowStatus($"Couldn't overwrite: {ex.Message}");
        }
        finally
        {
            Busy.IsActive = false;
        }
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

    public bool HasUnsavedChanges => _item is not null && _ops != _openedWith;

    /// <summary>Back / Esc: close, asking first if there are changes that would be lost.</summary>
    public async Task RequestCloseAsync()
    {
        if (HasUnsavedChanges &&
            !await Dialogs.ConfirmAsync(XamlRoot, "Discard your changes?", "Leaving the editor now throws away the changes you haven't saved.", "Discard"))
            return;
        Close(saved: false);
    }

    /// <summary>Leaving the page altogether (e.g. a menu item): close without prompting.</summary>
    public void CloseWithoutSaving() => Close(saved: false);

    private void Close(bool saved)
    {
        ReleaseSource();
        Closed?.Invoke(saved);
    }
}
