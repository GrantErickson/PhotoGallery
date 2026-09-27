using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using PhotoGallery.App.Pages;
using PhotoGallery.Core.Transcripts;

namespace PhotoGallery.App.Controls;

/// <summary>
/// A transcript paragraph in the viewer: its start time (click to play from there), whether it's being said now, and
/// where the searched-for words are in it.
/// </summary>
public sealed class TranscriptLine(TranscriptParagraph paragraph, bool showSpeaker, IReadOnlyList<TextMatch>? matches = null) : Observable
{
    private static readonly Brush Clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    private bool _isCurrent;

    public double Start => paragraph.Start;
    public double End => paragraph.End;
    public string Text => paragraph.Text;
    public IReadOnlyList<TextMatch> Matches { get; } = matches ?? [];
    public string Time => TimeSpan.FromSeconds(paragraph.Start) is var t && t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    public string Speaker => paragraph.Speaker is { } s ? $"Speaker {s}" : "";
    public Visibility SpeakerVisibility => showSpeaker && paragraph.Speaker is not null ? Visibility.Visible : Visibility.Collapsed;

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            OnPropertyChanged(nameof(Background));
        }
    }

    public Brush Background => _isCurrent && Application.Current.Resources["SubtleFillColorSecondaryBrush"] is Brush b ? b : Clear;
}
