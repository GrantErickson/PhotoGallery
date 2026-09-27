using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using PhotoGallery.App.Pages;
using PhotoGallery.Core.Transcripts;

namespace PhotoGallery.App.Controls;

/// <summary>
/// A transcript paragraph in the viewer: its start time (click to play from there), whether it's being said now, and
/// where the searched-for words are in it.
/// </summary>
public sealed class TranscriptLine(TranscriptParagraph paragraph, bool showSpeaker) : Observable, IFindableLine
{
    private static readonly Brush Clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    private bool _isCurrent;

    public double Start => paragraph.Start;
    public double End => paragraph.End;
    public string Text => paragraph.Text;
    /// <summary>Where the words being found are in <see cref="Text"/>.</summary>
    public IReadOnlyList<TextMatch> Matches { get; set; } = [];
    /// <summary>Which of <see cref="Matches"/> is selected (-1 for none).</summary>
    public int CurrentMatch { get; set; } = -1;
    /// <summary>The text block showing this paragraph, so its highlights can change in place.</summary>
    public Microsoft.UI.Xaml.Controls.TextBlock? View { get; set; }
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
