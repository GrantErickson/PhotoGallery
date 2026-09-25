using Microsoft.UI.Text;
using Windows.UI.Text;

namespace PhotoGallery.App;

/// <summary>An entry in the timeline jump list: a year or a month, pointing at the first grid index in it.</summary>
public sealed class JumpEntry(string label, int index, bool isYear)
{
    public string Label { get; } = label;
    public int Index { get; } = index;
    public double FontSize { get; } = isYear ? 14 : 12;
    public FontWeight Weight { get; } = isYear ? FontWeights.SemiBold : FontWeights.Normal;
}
