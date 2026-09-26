using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoGallery.App.Editing;
using PhotoGallery.App.Services;
using PhotoGallery.Core;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Media;
using PhotoGallery.Core.Transcripts;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;

namespace PhotoGallery.App.Controls;

/// <summary>Full-screen viewer over the current grid list: photos (zoom), videos, and Live/motion playback.</summary>
public sealed partial class ViewerControl : UserControl
{
    private static AppServices S => App.Services;

    private IReadOnlyList<MediaSummary> _items = [];
    private int _index;
    private MediaItem? _current;
    private CancellationTokenSource _loadCts = new();
    private bool _changed;
    private bool _suppressRating;
    private bool _playingMotion;
    /// <summary>The user stepped, paused or saved during Live Photo motion: stay on the video instead of returning to the still.</summary>
    private bool _motionPinned;
    /// <summary>The file the player is showing (the video itself, or the Live Photo's motion clip).</summary>
    private string? _videoPath;
    private bool _savingFrame;
    private DispatcherTimer? _toastTimer;

    private readonly MediaPlayer _player = new() { AutoPlay = true };

    public ViewerControl()
    {
        InitializeComponent();
        Player.SetMediaPlayer(_player);
        _player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_playingMotion && !_motionPinned) StopPlayback();
        });
        _player.PlaybackSession.PositionChanged += (session, _) =>
        {
            var position = session.Position;
            DispatcherQueue.TryEnqueue(() =>
            {
                FrameTimeText.Text = FormatPosition(position);
                FollowTranscript(position);
            });
        };
        S.Transcription.Progress += (mediaId, fraction) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_current?.Id != mediaId || TranscriptProgress.Visibility != Visibility.Visible) return;
            TranscriptProgress.IsIndeterminate = false;
            TranscriptProgress.Value = fraction;
            TranscriptStatusText.Text = $"Transcribing… {fraction:P0}";
        });
        S.Transcription.StateChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (_current is { Kind: MediaKind.Video } && _transcriptLines.Count == 0 && S.Transcription.DownloadProgress is { } d &&
                TranscriptProgress.Visibility == Visibility.Visible)
            {
                TranscriptProgress.IsIndeterminate = false;
                TranscriptProgress.Value = d;
                TranscriptStatusText.Text = $"Downloading the speech model (1.6 GB, once)… {d:P0}";
            }
        });
        _player.PlaybackSession.PlaybackStateChanged += (session, _) =>
        {
            // Pausing part-way through Live Photo motion means "let me look at this frame".
            if (_playingMotion && session.PlaybackState == MediaPlaybackState.Paused &&
                session.Position < session.NaturalDuration - TimeSpan.FromMilliseconds(150))
                _motionPinned = true;
        };
        Unloaded += (_, _) => StopPlayback();
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            if (Visibility != Visibility.Visible || App.MainWindow.IsEditorOpen) return;
            args.Handled = true;
            Close();
        };
        KeyboardAccelerators.Add(escape);
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    /// <summary>The photo currently shown.</summary>
    public long? CurrentId => _current?.Id;

    /// <summary>Raised when the viewer closes: (index of the item last shown, whether ratings/tags changed).</summary>
    public event Action<int, bool>? Closed;

    public void Show(IReadOnlyList<MediaSummary> items, int index)
    {
        _items = items;
        _changed = false;
        InfoToggle.IsChecked = InfoColumn.Width.Value > 0;
        _ = ShowIndexAsync(index);
        // Take focus after the opening click/double-click has finished, or the grid behind keeps it (and its arrow keys).
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Focus(FocusState.Programmatic));
    }

    private async Task ShowIndexAsync(int index)
    {
        if (_items.Count == 0) return;
        _index = Math.Clamp(index, 0, _items.Count - 1);
        await _loadCts.CancelAsync();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        StopPlayback();
        ErrorText.Visibility = Visibility.Collapsed;
        PrevButton.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _index < _items.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
        Zoom.ChangeView(0, 0, 1, disableAnimation: true);

        var id = _items[_index].Id;
        var item = await Task.Run(() => S.Media.Get(id), ct);
        if (ct.IsCancellationRequested) return;
        if (item is null)
        {
            ShowError("This file is no longer in the library.");
            return;
        }
        _current = item;
        ShowDetails(item);
        ShowTranscript(item, ct);
        PrefetchMotion(item, ct);

        if (item.Kind == MediaKind.Video)
        {
            Photo.Source = null;
            ShowVideo(item.Path, isMotion: false);
            return;
        }

        // Thumbnail first for instant feedback, then the full image decoded at screen resolution.
        FitPhoto();
        LoadingRing.IsActive = true;
        Photo.Source = S.Thumbnails.TryGetCached(item.Id, out var thumb) ? new BitmapImage(new Uri(thumb)) : null;

        if (await Task.Run(() => S.Edits.Get(id), ct) is { } edits)
        {
            await ShowEditedAsync(item, edits, ct);
            return;
        }
        try
        {
            var full = new BitmapImage();
            full.ImageFailed += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (_current?.Id == id && Photo.Source is null) ShowError("Windows can't decode this file. A codec extension may be missing.");
            });
            var scale = XamlRoot?.RasterizationScale ?? 1.0;
            var longest = Math.Max(Stage.ActualWidth, Stage.ActualHeight) * scale * 2; // 2x leaves room to zoom
            if (Math.Max(item.Width, item.Height) > longest)
            {
                if (item.Width >= item.Height) full.DecodePixelWidth = (int)longest;
                else full.DecodePixelHeight = (int)longest;
            }
            full.ImageOpened += (_, _) => DispatcherQueue.TryEnqueue(() => LoadingRing.IsActive = false);
            full.UriSource = new Uri(item.Path);
            Photo.Source = full;
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            ShowError(ex.Message);
        }
        finally
        {
            if (Photo.Source is null) LoadingRing.IsActive = false;
        }
    }

    /// <summary>Renders the photo with its saved edits at screen resolution.</summary>
    private async Task ShowEditedAsync(MediaItem item, EditOperations edits, CancellationToken ct)
    {
        try
        {
            var scale = XamlRoot?.RasterizationScale ?? 1.0;
            var longest = (int)Math.Min(8192, Math.Max(Stage.ActualWidth, Stage.ActualHeight) * scale * 2);
            using var bitmap = await EditRenderer.RenderPreviewAsync(item.Path, edits, longest);
            if (ct.IsCancellationRequested) return;
            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(bitmap);
            if (!ct.IsCancellationRequested) Photo.Source = source;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"Rendering edits for {item.Path} failed", ex);
            ShowError("Couldn't apply the saved edits to this photo.");
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private void ShowDetails(MediaItem item)
    {
        TitleText.Text = item.FileName;
        SubtitleText.Text = $"{_index + 1:N0} of {_items.Count:N0}";
        TakenText.Text = item.TakenLocal.ToString("dddd, MMMM d, yyyy  h:mm tt", CultureInfo.CurrentCulture) +
                         (item.DateSource == DateSource.FileModified ? "  (file date)" : "");
        FileText.Text = $"{Format.FileSize(item.FileSize)}" + (item.Width > 0 ? $" · {item.Width:N0} × {item.Height:N0}" : "") +
                        (item.Kind == MediaKind.Video && item.DurationMs > 0 ? $" · {Format.Duration(item.DurationMs)}" : "");
        FolderText.Text = Path.GetDirectoryName(item.Path);

        var camera = string.Join(" ", new[] { item.CameraMake, item.CameraModel }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
        CameraPanel.Visibility = camera.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CameraText.Text = camera;

        LocationPanel.Visibility = item.Latitude is not null ? Visibility.Visible : Visibility.Collapsed;
        LocationLink.Content = item.Latitude is { } lat && item.Longitude is { } lon
            ? string.Create(CultureInfo.InvariantCulture, $"{lat:F5}, {lon:F5}")
            : null;

        LiveButton.Visibility = item.Motion is MotionSource.LocalPair or MotionSource.Embedded or MotionSource.Cloud
            ? Visibility.Visible
            : Visibility.Collapsed;
        MotionPanel.Visibility = LiveButton.Visibility;
        MotionText.Text = item.Motion switch
        {
            MotionSource.LocalPair => "Live Photo · video stored next to the photo",
            MotionSource.Embedded => "Motion photo · video embedded in the file",
            MotionSource.Cloud => S.LiveVideo.IsConnected ? "Live Photo · video stored in OneDrive" : "Live Photo · video stored in OneDrive (connect OneDrive to play)",
            _ => "",
        };

        EditButton.Visibility = Visibility.Visible;
        ToolTipService.SetToolTip(EditButton, item.Kind == MediaKind.Video ? "Edit video: rotate, trim, remove sound (E)" : "Edit (E)");
        var edits = S.Edits.Get(item.Id);
        EditedPanel.Visibility = edits is null ? Visibility.Collapsed : Visibility.Visible;
        EditedText.Text = edits is null ? "" : DescribeEdits(edits);

        _suppressRating = true;
        Rating.Value = item.Rating > 0 ? item.Rating : -1;
        _suppressRating = false;

        RefreshTags();
        RefreshAlbums();
    }

    private void RefreshTags()
    {
        TagList.ItemsSource = _current is null ? null : S.Collections.GetTagsFor(_current.Id);
        var faces = _current is null ? [] : S.People.GetFacesIn(_current.Id);
        PeopleList.ItemsSource = faces;
        PeoplePanel.Visibility = faces.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Boxes are fractions of the photo as shot: they only line up when the picture shown isn't rotated or cropped.
        var edits = _current is null ? null : S.Edits.Get(_current.Id);
        _faces = _current is { Kind: not MediaKind.Video } && edits is null or { Rotation: 0, FlipHorizontal: false, Crop: null }
            ? faces.Where(f => f.Box is not null).ToList()
            : [];
        ShowFace(null);
    }

    private void OnPersonClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not FaceRow face || S.People.Get(face.PersonId) is not { } person) return;
        App.MainWindow.NavigateFromViewer(() => PhotoGallery.App.Pages.PeoplePage.Open(person));
    }

    /// <summary>Faces in the photo on screen that have a box.</summary>
    private List<FaceRow> _faces = [];
    private FaceRow? _pointedName;

    private void OnPersonPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointedName = ((FrameworkElement)sender).Tag as FaceRow;
        ShowFace(_faces.FirstOrDefault(f => f.PersonId == _pointedName?.PersonId));
    }

    private void OnPersonPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointedName = null;
        ShowFace(null);
    }

    /// <summary>Pointing at someone in the photo outlines their face and names them.</summary>
    private void OnPhotoPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_faces.Count == 0 || _pointedName is not null || PhotoLayer.ActualWidth <= 0) return;
        var p = e.GetCurrentPoint(PhotoLayer).Position;
        double w = PhotoLayer.ActualWidth, h = PhotoLayer.ActualHeight;
        // Generous target: the face plus some room around it; the smallest (nearest) face wins when they overlap.
        var hit = _faces.Where(f =>
            {
                var (x, y, bw, bh) = f.Box!.Value.In(w, h);
                return p.X >= x - bw * 0.3 && p.X <= x + bw * 1.3 && p.Y >= y - bh * 0.3 && p.Y <= y + bh * 1.6;
            })
            .MinBy(f => f.Box!.Value.Area);
        ShowFace(hit);
    }

    private void OnPhotoPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_pointedName is null) ShowFace(null);
    }

    private void ShowFace(FaceRow? face)
    {
        if (face?.Box is not { } box || Photo.Source is null || PhotoLayer.ActualWidth <= 0)
        {
            FaceBoxOuter.Visibility = Visibility.Collapsed;
            FaceLabel.Visibility = Visibility.Collapsed;
            return;
        }
        double w = PhotoLayer.ActualWidth, h = PhotoLayer.ActualHeight;
        var (bx, by, bw, bh) = box.In(w, h);
        // A little larger than OneDrive's box, which hugs the features.
        double left = bx - bw * 0.08, top = by - bh * 0.08, width = bw * 1.16, height = bh * 1.16;
        Canvas.SetLeft(FaceBoxOuter, left);
        Canvas.SetTop(FaceBoxOuter, top);
        FaceBoxOuter.Width = Math.Max(8, width);
        FaceBoxOuter.Height = Math.Max(8, height);
        FaceBoxOuter.Visibility = Visibility.Visible;

        FaceLabelText.Text = face.DisplayName;
        FaceLabel.Visibility = Visibility.Visible;
        FaceLabel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var labelWidth = FaceLabel.DesiredSize.Width;
        var below = top + height + 6;
        Canvas.SetLeft(FaceLabel, Math.Clamp(left + width / 2 - labelWidth / 2, 0, Math.Max(0, w - labelWidth)));
        Canvas.SetTop(FaceLabel, below + FaceLabel.DesiredSize.Height <= h ? below : Math.Max(0, top - FaceLabel.DesiredSize.Height - 6));
    }

    private void RefreshAlbums()
    {
        if (_current is null) return;
        var ids = S.Collections.GetAlbumsContaining(_current.Id);
        var names = S.Collections.GetAlbums().Where(a => ids.Contains(a.Id)).Select(a => a.Name).ToList();
        AlbumsText.Text = names.Count == 0 ? "Not in any album" : string.Join(", ", names);
    }

    private void ShowError(string message)
    {
        LoadingRing.IsActive = false;
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    // ---------- Motion / video ----------

    private async void OnPlayMotion(object sender, RoutedEventArgs e) => await PlayMotionAsync();

    private async Task PlayMotionAsync()
    {
        if (_current is not { } item || _playingMotion) return;
        LiveBusy.IsActive = true;
        try
        {
            var (result, path) = await S.Motion.GetVideoAsync(item, _loadCts.Token);
            if (_current?.Id != item.Id) return;
            switch (result)
            {
                case MotionResult.Ready when path is not null:
                    ShowVideo(path, isMotion: true);
                    break;
                case MotionResult.NeedsSignIn:
                    if (await Dialogs.ConfirmAsync(XamlRoot, "Connect OneDrive to play Live Photos",
                            S.LiveVideo.IsConnected
                                ? "Your OneDrive sign-in has expired. Sign in again to keep playing Live Photos stored in the cloud."
                                : "This Live Photo's video is only stored in OneDrive. Sign in to OneDrive once and the gallery can play it (and every other Live Photo).",
                            "Connect"))
                        App.MainWindow.NavigateFromViewer(() => App.MainWindow.Navigate(typeof(PhotoGallery.App.Pages.OneDriveConnectPage), null));
                    break;
                case MotionResult.None:
                    App.MainWindow.ShowStatus("OneDrive has no motion for this photo.");
                    ShowDetails(item);
                    break;
                case MotionResult.Unavailable when item.Motion == MotionSource.Cloud:
                    App.MainWindow.ShowStatus("Couldn't download the Live Photo video. Check your connection and try again.");
                    break;
                default:
                    App.MainWindow.ShowStatus("Couldn't load the motion for this photo.");
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            LiveBusy.IsActive = false;
        }
    }

    /// <summary>Downloads a cloud Live Photo's video in the background so LIVE plays instantly.</summary>
    private static void PrefetchMotion(MediaItem item, CancellationToken ct)
    {
        if (item.Motion != MotionSource.Cloud || !S.LiveVideo.IsConnected) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, ct); // skip items the user is just flicking past
                await S.Motion.GetVideoAsync(item, ct);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Error($"Prefetching Live Photo video for {item.Path} failed", ex);
            }
        }, ct);
    }

    /// <summary>Plays a video file with the frame tools; <paramref name="isMotion"/> for a Live Photo's clip over its still.</summary>
    private void ShowVideo(string path, bool isMotion)
    {
        _playingMotion = isMotion;
        _motionPinned = false;
        _videoPath = path;
        Player.AreTransportControlsEnabled = true; // seek bar for scrubbing
        Player.Visibility = Visibility.Visible;
        FrameBar.Visibility = Visibility.Visible;
        ShowPhotoButton.Visibility = isMotion ? Visibility.Visible : Visibility.Collapsed;
        SaveVideoButton.Visibility = isMotion ? Visibility.Visible : Visibility.Collapsed;
        FrameTimeText.Text = FormatPosition(TimeSpan.Zero);
        _player.Source = MediaSource.CreateFromUri(new Uri(path));
    }

    private void StopPlayback()
    {
        _playingMotion = false;
        _motionPinned = false;
        _videoPath = null;
        _player.Pause();
        _player.Source = null;
        Player.Visibility = Visibility.Collapsed;
        FrameBar.Visibility = Visibility.Collapsed;
    }

    private static string FormatPosition(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds:000}";

    private void OnPreviousFrame(object sender, RoutedEventArgs e) => StepFrame(forward: false);

    private void OnNextFrame(object sender, RoutedEventArgs e) => StepFrame(forward: true);

    private void StepFrame(bool forward)
    {
        if (_videoPath is null) return;
        _motionPinned = true;
        if (forward) _player.StepForwardOneFrame();
        else _player.StepBackwardOneFrame();
    }

    private void OnShowPhoto(object sender, RoutedEventArgs e) => StopPlayback();

    private void OnEditVideo(object sender, RoutedEventArgs e) => OpenVideoEditor();

    private void OpenVideoEditor()
    {
        if (_current is not { } item) return;
        var path = _videoPath ?? (item.Kind == MediaKind.Video ? item.Path : null);
        if (path is null) return;
        var livePhoto = item.Kind != MediaKind.Video;
        var position = _player.PlaybackSession.Position;
        _motionPinned = true;
        _player.Pause();
        App.MainWindow.OpenVideoEditor(item, path, livePhoto, position, saved =>
        {
            if (saved) _changed = true;
            Focus(FocusState.Programmatic);
        });
    }

    private bool _savingVideo;

    /// <summary>Saves the Live Photo's motion (as it is) as an MP4 next to the photo.</summary>
    private async void OnSaveLiveVideo(object sender, RoutedEventArgs e)
    {
        if (_current is not { } item || _videoPath is not { } source || _savingVideo) return;
        _savingVideo = true;
        _motionPinned = true;
        SaveVideoButton.IsEnabled = false;
        var target = VideoExport.NextPath(item.Path, livePhoto: true);
        try
        {
            var progress = new Progress<double>(p => SaveVideoText.Text = $"Saving… {p:P0}");
            var location = item is { Latitude: { } lat, Longitude: { } lon } ? (lat, lon) : ((double, double)?)null;
            await VideoExport.ExportAsync(source, Core.Editing.VideoEdits.None, target, item.TakenLocal, location, progress, CancellationToken.None);
            var indexed = await Task.Run(() => S.Indexing.IndexFileNow(target));
            if (indexed is not null) S.Media.SetDerivedFrom(indexed.Id, item.Id);
            _changed = true;
            ShowToast($"Saved {Path.GetFileName(target)} next to the photo");
        }
        catch (Exception ex)
        {
            Log.Error($"Saving the Live Photo video of {item.Path} failed", ex);
            ShowToast($"Couldn't save the video: {ex.Message}");
        }
        finally
        {
            _savingVideo = false;
            SaveVideoButton.IsEnabled = true;
            SaveVideoText.Text = "Save video";
        }
    }

    private void TogglePlayPause()
    {
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _player.Pause();
        else _player.Play();
    }

    private async void OnSaveFrame(object sender, RoutedEventArgs e) => await SaveFrameAsync();

    /// <summary>Saves the frame on screen as a photo next to the video (or the Live Photo), and adds it to the library.</summary>
    private async Task SaveFrameAsync()
    {
        if (_current is not { } item || _videoPath is not { } video || _savingFrame) return;
        _savingFrame = true;
        _motionPinned = true;
        SaveFrameButton.IsEnabled = false;
        _player.Pause();
        var position = _player.PlaybackSession.Position;
        var isLivePhoto = item.Kind != MediaKind.Video;
        try
        {
            var saved = await VideoFrames.SaveSnapshotAsync(item, video, position, isLivePhoto);
            var indexed = await Task.Run(() => S.Indexing.IndexFileNow(saved));
            if (indexed is not null) S.Media.SetDerivedFrom(indexed.Id, item.Id);
            _changed = true; // the gallery reloads to show the new photo
            ShowToast($"Saved {Path.GetFileName(saved)} in {Path.GetFileName(Path.GetDirectoryName(saved))}");
        }
        catch (Exception ex)
        {
            Log.Error($"Saving a frame of {video} at {position} failed", ex);
            ShowToast($"Couldn't save the frame: {ex.Message}");
        }
        finally
        {
            _savingFrame = false;
            SaveFrameButton.IsEnabled = true;
        }
    }

    /// <summary>A short message over the viewer (it covers the window's status bar).</summary>
    public void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer?.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };
        _toastTimer.Start();
    }

    // ---------- Navigation ----------

    private void OnPrevious(object sender, RoutedEventArgs e) => _ = ShowIndexAsync(_index - 1);

    private void OnNext(object sender, RoutedEventArgs e) => _ = ShowIndexAsync(_index + 1);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    public void Close()
    {
        _loadCts.Cancel();
        StopPlayback();
        Photo.Source = null;
        Closed?.Invoke(_index, _changed);
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (App.MainWindow.IsEditorOpen || FocusManager.GetFocusedElement(XamlRoot) is TextBox or AutoSuggestBox) return;
        if (IsDown(VirtualKey.Menu)) return; // Alt+arrows are app back/forward
        switch (e.Key)
        {
            case VirtualKey.Left when _index > 0:
                _ = ShowIndexAsync(_index - 1);
                break;
            case VirtualKey.Right when _index < _items.Count - 1:
                _ = ShowIndexAsync(_index + 1);
                break;
            case VirtualKey.Escape:
                Close();
                break;
            case VirtualKey.Space when _videoPath is not null:
                TogglePlayPause();
                break;
            case VirtualKey.Space when _current?.Kind != MediaKind.Video:
                _ = PlayMotionAsync();
                break;
            case (VirtualKey)188 when _videoPath is not null: // ,
                StepFrame(forward: false);
                break;
            case (VirtualKey)190 when _videoPath is not null: // .
                StepFrame(forward: true);
                break;
            case VirtualKey.S when _videoPath is not null && !IsDown(VirtualKey.Control):
                _ = SaveFrameAsync();
                break;
            case VirtualKey.E:
                OpenEditor();
                break;
            case VirtualKey.I:
                InfoToggle.IsChecked = !InfoToggle.IsChecked;
                OnToggleInfo(InfoToggle, new RoutedEventArgs());
                break;
            case VirtualKey.C when IsDown(VirtualKey.Control) && IsDown(VirtualKey.Shift):
                if (_current is not null) Clipboard.CopyPaths([_current.Path]);
                break;
            case >= VirtualKey.Number0 and <= VirtualKey.Number5:
                SetRating(e.Key - VirtualKey.Number0);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e) => FitPhoto();

    /// <summary>At zoom 1 the image fills the stage (letterboxed by Stretch=Uniform); the ScrollViewer zooms from there.</summary>
    private void FitPhoto()
    {
        if (Stage.ActualWidth <= 0) return;
        PhotoFrame.Width = Stage.ActualWidth;
        PhotoFrame.Height = Stage.ActualHeight;
        Photo.MaxWidth = Stage.ActualWidth;
        Photo.MaxHeight = Stage.ActualHeight;
    }

    private void OnZoomDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Zoom.ZoomFactor > 1.01f)
        {
            Zoom.ChangeView(0, 0, 1);
        }
        else
        {
            var p = e.GetPosition(PhotoFrame);
            const float factor = 3f;
            Zoom.ChangeView(p.X * factor - Zoom.ViewportWidth / 2, p.Y * factor - Zoom.ViewportHeight / 2, factor);
        }
    }

    private void OnToggleInfo(object sender, RoutedEventArgs e) =>
        InfoColumn.Width = InfoToggle.IsChecked == true ? new GridLength(320) : new GridLength(0);

    // ---------- Info pane edits ----------

    private void OnRatingChanged(RatingControl sender, object args)
    {
        if (_suppressRating) return;
        SetRating(sender.Value < 1 ? 0 : (int)sender.Value);
    }

    private void SetRating(int rating)
    {
        if (_current is null) return;
        S.Media.SetRating([_current.Id], rating);
        _current.Rating = rating;
        _items[_index].Rating = rating;
        _changed = true;
        _suppressRating = true;
        Rating.Value = rating > 0 ? rating : -1;
        _suppressRating = false;
    }

    private void OnTagBoxTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var text = sender.Text.Trim();
        sender.ItemsSource = text.Length == 0
            ? null
            : S.Collections.GetTags().Where(t => t.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)).Select(t => t.Name).Take(8).ToList();
    }

    private void OnTagSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var name = (args.ChosenSuggestion as string ?? args.QueryText).Trim();
        if (_current is null || name.Length == 0) return;
        S.Collections.AddTag([_current.Id], name);
        sender.Text = "";
        _changed = true;
        RefreshTags();
    }

    private void OnRemoveTag(object sender, RoutedEventArgs e)
    {
        if (_current is null || ((FrameworkElement)sender).Tag is not long tagId) return;
        S.Collections.RemoveTag([_current.Id], tagId);
        _changed = true;
        RefreshTags();
    }

    private void OnTagClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not TagRow tag) return;
        var withCount = S.Collections.GetTags().FirstOrDefault(t => t.Id == tag.Id) ?? tag;
        App.MainWindow.NavigateFromViewer(() => PhotoGallery.App.Pages.TagsPage.Open(withCount));
    }

    private async void OnAlbumMenuOpening(object sender, object e)
    {
        AlbumMenu.Items.Clear();
        var newAlbum = new MenuFlyoutItem { Text = "New album…", Icon = new SymbolIcon(Symbol.Add) };
        newAlbum.Click += async (_, _) =>
        {
            if (_current is null) return;
            if (await Dialogs.PromptAsync(XamlRoot, "New album", "Album name", "") is { Length: > 0 } name)
            {
                S.Collections.AddToAlbum(S.Collections.CreateAlbum(name), [_current.Id]);
                RefreshAlbums();
            }
        };
        AlbumMenu.Items.Add(newAlbum);
        var albums = await Task.Run(S.Collections.GetAlbums);
        if (albums.Count > 0) AlbumMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var album in albums)
        {
            var entry = new MenuFlyoutItem { Text = album.Name };
            entry.Click += (_, _) =>
            {
                if (_current is null) return;
                S.Collections.AddToAlbum(album.Id, [_current.Id]);
                RefreshAlbums();
            };
            AlbumMenu.Items.Add(entry);
        }
    }

    private void OnOpenLocation(object sender, RoutedEventArgs e)
    {
        if (_current is not null) Process.Start("explorer.exe", $"/select,\"{_current.Path}\"");
    }

    private async void OnOpenOneDrive(object sender, RoutedEventArgs e)
    {
        if (_current is not null) await OpenInOneDriveAsync(_current);
    }

    private static async Task OpenInOneDriveAsync(MediaItem item)
    {
        if (S.Settings.ToOneDrivePath(item.Path) is not { } remote)
        {
            App.MainWindow.ShowStatus("This file isn't in your OneDrive folder.");
            return;
        }
        var url = (await S.OneDrive.GetItemAsync(remote))?.WebUrl;
        if (url is null)
        {
            App.MainWindow.ShowStatus(S.OneDrive.IsSignedIn ? "OneDrive couldn't find this file." : "Sign in to OneDrive in Settings first.");
            return;
        }
        await Launcher.LaunchUriAsync(new Uri(url));
    }

    private static string DescribeEdits(EditOperations e)
    {
        var parts = new List<string>();
        if (e.Rotation != 0) parts.Add("rotated");
        if (e.FlipHorizontal) parts.Add("flipped");
        if (e.Crop is { IsFull: false }) parts.Add("cropped");
        if (e.HasColorAdjustments) parts.Add("light adjusted");
        var text = string.Join(", ", parts);
        return text.Length == 0 ? "Edited" : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private void OnEdit(object sender, RoutedEventArgs e) => OpenEditor();

    private void OpenEditor()
    {
        // While a video or Live Photo motion is on screen, E edits the video.
        if (_videoPath is not null || _current?.Kind == MediaKind.Video)
        {
            OpenVideoEditor();
            return;
        }
        if (_current is not { } item) return;
        StopPlayback();
        App.MainWindow.OpenEditor(item, saved =>
        {
            if (saved) _changed = true;
            _ = ShowIndexAsync(_index);
            Focus(FocusState.Programmatic);
        });
    }

    private void OnRevertEdits(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        S.Edits.Save(_current.Id, EditOperations.None);
        S.Thumbnails.Invalidate([_current.Id]);
        _changed = true;
        _ = ShowIndexAsync(_index);
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (_current is not null) Clipboard.CopyPaths([_current.Path]);
    }

    private async void OnOpenMap(object sender, RoutedEventArgs e)
    {
        if (_current is { Latitude: { } lat, Longitude: { } lon })
            await Launcher.LaunchUriAsync(new Uri(string.Create(CultureInfo.InvariantCulture,
                $"https://www.openstreetmap.org/?mlat={lat}&mlon={lon}#map=16/{lat}/{lon}")));
    }

    // ---- Transcript ----

    private List<TranscriptLine> _transcriptLines = [];
    private Transcript? _transcript;
    private TranscriptLine? _currentLine;
    private DateTime _userScrolledAt;
    /// <summary>Which tab videos open on; remembers the last choice.</summary>
    private bool _preferDetails;

    /// <summary>For a video: shows its transcript, or asks for it (ahead of the background work) and shows progress.</summary>
    private async void ShowTranscript(MediaItem item, CancellationToken ct)
    {
        var isVideo = item.Kind == MediaKind.Video;
        SideTabs.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        SetTranscriptLines(null);
        if (!isVideo)
        {
            ShowSidePane(details: true);
            return;
        }
        SideTabs.SelectedItem = _preferDetails ? DetailsTab : TranscriptTab;
        ShowSidePane(details: _preferDetails);

        var transcript = await Task.Run(() => S.Transcripts.Get(item.Id));
        if (ct.IsCancellationRequested) return;
        if (transcript is null)
        {
            if (item.OnlineOnly)
            {
                TranscriptStatusText.Text = "This video is only in OneDrive; it's transcribed once it's downloaded.";
                return;
            }
            TranscriptStatusText.Text = S.Transcription.DownloadProgress is { } d
                ? $"Downloading the speech model (1.6 GB, once)… {d:P0}"
                : "Transcribing…";
            TranscriptProgress.Visibility = Visibility.Visible;
            TranscriptProgress.IsIndeterminate = true;
            transcript = await S.Transcription.RequestAsync(item.Id);
            if (ct.IsCancellationRequested || _current?.Id != item.Id) return;
        }
        ShowTranscriptResult(transcript);
    }

    private void ShowTranscriptResult(Transcript? transcript)
    {
        TranscriptProgress.Visibility = Visibility.Collapsed;
        RetryTranscriptButton.Visibility = transcript?.Status == TranscriptStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
        _transcript = transcript;
        switch (transcript)
        {
            case null:
                TranscriptStatusText.Text = "Couldn't transcribe this video.";
                SetTranscriptLines(null);
                break;
            case { Status: TranscriptStatus.NoAudio }:
                TranscriptStatusText.Text = "This video has no sound.";
                SetTranscriptLines(null);
                break;
            case { Status: TranscriptStatus.Failed }:
                TranscriptStatusText.Text = $"Couldn't transcribe this video: {transcript.Error}";
                SetTranscriptLines(null);
                break;
            case { HasSpeech: false }:
                TranscriptStatusText.Text = "No speech in this video.";
                SetTranscriptLines(null);
                break;
            default:
                var paragraphs = TranscriptFormatter.Paragraphs(transcript.Segments);
                var speakers = paragraphs.Select(p => p.Speaker).Distinct().Count(s => s is not null);
                SetTranscriptLines(paragraphs.Select(p => new TranscriptLine(p, speakers > 1)).ToList());
                TranscriptStatusText.Text = LanguageName(transcript.Language) is { } language ? $"Spoken {language}" : "";
                break;
        }
    }

    private void SetTranscriptLines(List<TranscriptLine>? lines)
    {
        _transcriptLines = lines ?? [];
        _currentLine = null;
        TranscriptList.ItemsSource = _transcriptLines;
        CopyTranscriptButton.Visibility = _transcriptLines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (lines is null)
        {
            _transcript = null;
            RetryTranscriptButton.Visibility = Visibility.Collapsed;
        }
        TranscriptScroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    private static string? LanguageName(string? code)
    {
        if (string.IsNullOrEmpty(code) || code == "auto") return null;
        try
        {
            return $"in {new System.Globalization.CultureInfo(code).EnglishName}";
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return $"in {code}";
        }
    }

    private void OnSideTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (SideTabs.Visibility != Visibility.Visible) return;
        _preferDetails = ReferenceEquals(sender.SelectedItem, DetailsTab);
        ShowSidePane(_preferDetails);
    }

    private void ShowSidePane(bool details)
    {
        InfoPane.Visibility = details ? Visibility.Visible : Visibility.Collapsed;
        TranscriptPane.Visibility = details ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnTranscriptSeek(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not TranscriptLine line || _videoPath is null) return;
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(line.Start);
        _player.Play();
        _userScrolledAt = default;
        FollowTranscript(_player.PlaybackSession.Position);
    }

    private void OnTranscriptUserScroll(object sender, object e) => _userScrolledAt = DateTime.UtcNow;

    /// <summary>Highlights what's being said now and keeps it in view (unless the reader scrolled away just now).</summary>
    private void FollowTranscript(TimeSpan position)
    {
        if (_transcriptLines.Count == 0 || _playingMotion) return;
        var t = position.TotalSeconds;
        var line = _transcriptLines.LastOrDefault(l => l.Start <= t + 0.2);
        if (line is not null && t > line.End + 3) line = null; // in a long silence after it
        if (ReferenceEquals(line, _currentLine)) return;
        if (_currentLine is not null) _currentLine.IsCurrent = false;
        _currentLine = line;
        if (line is null) return;
        line.IsCurrent = true;
        if (DateTime.UtcNow - _userScrolledAt > TimeSpan.FromSeconds(5) && TranscriptList.ContainerFromItem(line) is UIElement container)
            container.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.25, AnimationDesired = true });
    }

    private void OnCopyTranscript(object sender, RoutedEventArgs e)
    {
        if (_transcript is null) return;
        var text = TranscriptFormatter.PlainText(TranscriptFormatter.Paragraphs(_transcript.Segments), s => $"Speaker {s}");
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        ShowToast("Transcript copied");
    }

    private void OnRetryTranscript(object sender, RoutedEventArgs e)
    {
        if (_current is not { } item) return;
        S.Transcripts.Delete(item.Id);
        ShowTranscript(item, _loadCts.Token);
    }
}
