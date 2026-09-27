using Microsoft.UI.Xaml.Controls;
using PhotoGallery.Core.Ocr;
using PhotoGallery.Core.Transcripts;

namespace PhotoGallery.App.Controls;

/// <summary>A line of text read from a photo, as shown (and found in) under the viewer's details.</summary>
public sealed class PhotoTextLine(OcrLine line) : IFindableLine
{
    public OcrLine Line { get; } = line;
    public string Text { get; } = line.Text;
    public IReadOnlyList<TextMatch> Matches { get; set; } = [];
    public int CurrentMatch { get; set; } = -1;
    public TextBlock? View { get; set; }

    /// <summary>The words a match covers (the line's text is its words joined by single spaces), to outline on the photo.</summary>
    public IEnumerable<OcrWord> WordsIn(TextMatch match)
    {
        var offset = 0;
        foreach (var word in Line.Words)
        {
            var end = offset + word.Text.Length;
            if (end > match.Start && offset < match.Start + match.Length) yield return word;
            offset = end + 1;
        }
    }
}
