using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using PhotoGallery.App.Editing;
using PhotoGallery.App.Services;
using PhotoGallery.Core;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;

namespace PhotoGallery.App.Controls;

/// <summary>Basic video editing — rotate, trim, remove sound — saved as a new MP4 beside the original.</summary>
public sealed partial class VideoEditorControl : UserControl
{
    private static AppServices S => App.Services;

    private readonly MediaPlayer _player = new() { AutoPlay = false };
    private MediaItem? _item;
    private string? _videoPath;
    private bool _livePhoto;
    private VideoEdits _edits = VideoEdits.None;
    private TimeSpan _duration;
    private bool _updatingScrubber;
    private CancellationTokenSource? _export;

    public VideoEditorControl()
    {
        InitializeComponent();
        Player.SetMediaPlayer(_player);
        _player.PlaybackSession.PositionChanged += (session, _) =>
        {
            var position = session.Position;
            DispatcherQueue.TryEnqueue(() => OnPosition(position));
        };
        _player.PlaybackSession.PlaybackStateChanged += (session, _) =>
        {
            var playing = session.PlaybackState == MediaPlaybackState.Playing;
            DispatcherQueue.TryEnqueue(() => PlayIcon.Glyph = playing ? "" : "");
        };
        _player.MediaOpened += (player, _) =>
        {
            var duration = player.PlaybackSession.NaturalDuration;
            DispatcherQueue.TryEnqueue(() =>
            {
                _duration = duration;
                Scrubber.Maximum = Math.Max(0.001, duration.TotalSeconds);
                UpdateTrimTexts();
            });
        };

        AddShortcut(VirtualKey.Escape, VirtualKeyModifiers.None, () => _ = RequestCloseAsync());
        AddShortcut(VirtualKey.S, VirtualKeyModifiers.Control, () => _ = SaveAsync());
        AddShortcut(VirtualKey.Space, VirtualKeyModifiers.None, TogglePlay);
        AddShortcut((VirtualKey)188, VirtualKeyModifiers.None, () => Step(forward: false)); // ,
        AddShortcut((VirtualKey)190, VirtualKeyModifiers.None, () => Step(forward: true));  // .
        AddShortcut((VirtualKey)219, VirtualKeyModifiers.None, () => Rotate(clockwise: false)); // [
        AddShortcut((VirtualKey)221, VirtualKeyModifiers.None, () => Rotate(clockwise: true));  // ]
        AddShortcut(VirtualKey.I, VirtualKeyModifiers.None, SetStart);
        AddShortcut(VirtualKey.O, VirtualKeyModifiers.None, SetEnd);
    }

    /// <summary>Raised when the editor closes; true if a video was saved.</summary>
    public event Action<bool>? Closed;

    public bool HasUnsavedChanges => _item is not null && _edits != VideoEdits.None;

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

    /// <summary>Opens <paramref name="videoPath"/> for editing; <paramref name="item"/> is the video, or the Live Photo it belongs to.</summary>
    public void Open(MediaItem item, string videoPath, bool livePhoto, TimeSpan startAt)
    {
        _item = item;
        _videoPath = videoPath;
        _livePhoto = livePhoto;
        _edits = VideoEdits.None;
        MuteBox.IsChecked = false;
        TitleText.Text = livePhoto ? "Edit Live Photo video" : "Edit video";
        FileText.Text = item.FileName;
        SaveText.Text = "Save as MP4";
        TargetRun.Text = Path.GetFileName(VideoExport.NextPath(item.Path, livePhoto));
        ApplyRotation();
        _player.Source = MediaSource.CreateFromUri(new Uri(videoPath));
        _player.PlaybackSession.Position = startAt;
        Focus(FocusState.Programmatic);
    }

    // ---------- Playback ----------

    private void OnPosition(TimeSpan position)
    {
        var end = _edits.TrimEnd ?? _duration;
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing && position >= end && end < _duration)
        {
            _player.Pause();
            _player.PlaybackSession.Position = end;
            position = end;
        }
        _updatingScrubber = true;
        Scrubber.Value = position.TotalSeconds;
        _updatingScrubber = false;
        TimeText.Text = $"{Format(position)} / {Format(_duration)}";
    }

    private void OnScrub(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingScrubber) return;
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue);
    }

    private void OnPlayPause(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        var session = _player.PlaybackSession;
        if (session.PlaybackState == MediaPlaybackState.Playing)
        {
            _player.Pause();
            return;
        }
        // Play within the kept range.
        var end = _edits.TrimEnd ?? _duration;
        if (session.Position < _edits.TrimStart || session.Position >= end - TimeSpan.FromMilliseconds(30))
            session.Position = _edits.TrimStart;
        _player.Play();
    }

    private void OnPreviousFrame(object sender, RoutedEventArgs e) => Step(forward: false);

    private void OnNextFrame(object sender, RoutedEventArgs e) => Step(forward: true);

    private void Step(bool forward)
    {
        if (forward) _player.StepForwardOneFrame();
        else _player.StepBackwardOneFrame();
    }

    // ---------- Edits ----------

    private void OnSetStart(object sender, RoutedEventArgs e) => SetStart();

    private void OnSetEnd(object sender, RoutedEventArgs e) => SetEnd();

    private void SetStart()
    {
        var position = _player.PlaybackSession.Position;
        var end = _edits.TrimEnd ?? _duration;
        if (position >= end - TimeSpan.FromMilliseconds(100)) return;
        _edits = _edits with { TrimStart = position };
        UpdateTrimTexts();
    }

    private void SetEnd()
    {
        var position = _player.PlaybackSession.Position;
        if (position <= _edits.TrimStart + TimeSpan.FromMilliseconds(100)) return;
        _edits = _edits with { TrimEnd = position >= _duration - TimeSpan.FromMilliseconds(20) ? null : position };
        UpdateTrimTexts();
    }

    private void OnResetTrim(object sender, RoutedEventArgs e)
    {
        _edits = _edits with { TrimStart = TimeSpan.Zero, TrimEnd = null };
        UpdateTrimTexts();
    }

    private void UpdateTrimTexts()
    {
        var end = _edits.TrimEnd ?? _duration;
        StartText.Text = Format(_edits.TrimStart);
        EndText.Text = Format(end);
        KeptText.Text = $"({Format(_edits.KeptDuration(_duration))} kept)";
        LayoutTrimRange();
    }

    private void OnTrimTrackSizeChanged(object sender, SizeChangedEventArgs e) => LayoutTrimRange();

    private void LayoutTrimRange()
    {
        if (_duration <= TimeSpan.Zero) return;
        // The slider's track is inset by about half a thumb on each side.
        const double inset = 10;
        var usable = Math.Max(0, TrimTrack.ActualWidth - 2 * inset);
        var start = _edits.TrimStart.TotalSeconds / _duration.TotalSeconds;
        var end = (_edits.TrimEnd ?? _duration).TotalSeconds / _duration.TotalSeconds;
        Canvas.SetLeft(TrimRange, inset + start * usable);
        TrimRange.Width = Math.Max(2, (end - start) * usable);
    }

    private void OnRotateLeft(object sender, RoutedEventArgs e) => Rotate(clockwise: false);

    private void OnRotateRight(object sender, RoutedEventArgs e) => Rotate(clockwise: true);

    private void Rotate(bool clockwise)
    {
        _edits = clockwise ? _edits.RotateClockwise() : _edits.RotateCounterClockwise();
        ApplyRotation();
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        _edits = _edits with { Mute = MuteBox.IsChecked == true };
        _player.IsMuted = _edits.Mute;
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _edits = VideoEdits.None;
        MuteBox.IsChecked = false;
        _player.IsMuted = false;
        ApplyRotation();
        UpdateTrimTexts();
    }

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e) => ApplyRotation();

    /// <summary>Turns the preview; a quarter-turned player is sized with swapped sides so it still fits the stage.</summary>
    private void ApplyRotation()
    {
        double w = Stage.ActualWidth, h = Stage.ActualHeight;
        var quarter = _edits.Rotation is 90 or 270;
        Player.Width = quarter ? h : w;
        Player.Height = quarter ? w : h;
        Canvas.SetLeft(Player, (w - Player.Width) / 2);
        Canvas.SetTop(Player, (h - Player.Height) / 2);
        PreviewRotation.Angle = _edits.Rotation;
    }

    // ---------- Save / close ----------

    private async void OnSave(object sender, RoutedEventArgs e) => await SaveAsync();

    private async Task SaveAsync()
    {
        if (_item is not { } item || _videoPath is not { } source || _export is not null) return;
        _player.Pause();
        var target = VideoExport.NextPath(item.Path, _livePhoto);
        _export = new CancellationTokenSource();
        SaveButton.IsEnabled = false;
        SaveText.Text = "Saving…";
        ExportProgress.Value = 0;
        ExportProgress.Visibility = CancelExportButton.Visibility = Visibility.Visible;
        try
        {
            var taken = _livePhoto ? item.TakenLocal : item.TakenLocal + _edits.TrimStart;
            var location = item is { Latitude: { } lat, Longitude: { } lon } ? (lat, lon) : ((double, double)?)null;
            var progress = new Progress<double>(p => ExportProgress.Value = p);
            await VideoExport.ExportAsync(source, _edits, target, taken, location, progress, _export.Token);
            var indexed = await Task.Run(() => S.Indexing.IndexFileNow(target));
            if (indexed is not null) S.Media.SetDerivedFrom(indexed.Id, item.Id);
            App.MainWindow.ShowStatus($"Saved {Path.GetFileName(target)} next to the original.");
            Close(saved: true);
        }
        catch (OperationCanceledException)
        {
            App.MainWindow.ShowStatus("Stopped — nothing was saved.");
        }
        catch (Exception ex)
        {
            Log.Error($"Exporting {source} failed", ex);
            App.MainWindow.ShowStatus($"Couldn't save the video: {ex.Message}");
        }
        finally
        {
            _export = null;
            SaveButton.IsEnabled = true;
            SaveText.Text = "Save as MP4";
            ExportProgress.Visibility = CancelExportButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCancelExport(object sender, RoutedEventArgs e) => _export?.Cancel();

    private void OnCancel(object sender, RoutedEventArgs e) => _ = RequestCloseAsync();

    /// <summary>Back / Esc: close, asking first if edits would be lost (or stopping a save in progress).</summary>
    public async Task RequestCloseAsync()
    {
        if (_export is not null)
        {
            _export.Cancel();
            return;
        }
        if (HasUnsavedChanges &&
            !await Dialogs.ConfirmAsync(XamlRoot, "Discard your changes?", "Leaving the editor now throws away the changes you haven't saved.", "Discard"))
            return;
        Close(saved: false);
    }

    public void CloseWithoutSaving()
    {
        _export?.Cancel();
        Close(saved: false);
    }

    private void Close(bool saved)
    {
        _player.Pause();
        _player.Source = null;
        _item = null;
        Closed?.Invoke(saved);
    }

    private static string Format(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds:000}";
}
