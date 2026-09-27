using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace PhotoGallery.App;

/// <summary>A year or month in the timeline's jump list; <see cref="IsCurrent"/> marks where the grid is scrolled to.</summary>
public sealed class JumpEntry(string label, int index, bool isYear) : Pages.Observable
{
    private bool _isCurrent;

    public string Label { get; } = label;
    public int Index { get; } = index;
    public bool IsYear { get; } = isYear;
    public double FontSize { get; } = isYear ? 14 : 12;

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            OnPropertyChanged(nameof(Weight));
            OnPropertyChanged(nameof(Foreground));
        }
    }

    public FontWeight Weight => _isCurrent || IsYear ? FontWeights.SemiBold : FontWeights.Normal;

    public Brush? Foreground => Application.Current.Resources[_isCurrent ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"] as Brush;
}
