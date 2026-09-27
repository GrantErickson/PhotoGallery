using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoGallery.Core.Transcripts;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI.Core;

namespace PhotoGallery.App.Controls;

/// <summary>A line or paragraph the find box can mark (a transcript paragraph, a line of text read from a photo).</summary>
public interface IFindableLine
{
    string Text { get; }
    /// <summary>Where the words being found are in <see cref="Text"/>.</summary>
    IReadOnlyList<TextMatch> Matches { get; set; }
    /// <summary>Which of <see cref="Matches"/> is selected (-1 for none).</summary>
    int CurrentMatch { get; set; }
    /// <summary>The text block showing the line, so its marks can change in place.</summary>
    TextBlock? View { get; set; }
}

/// <summary>
/// Find as you type over lines shown in an items control: matches are whole words, found as the search index finds
/// them (<see cref="SearchHighlighter"/>), marked yellow with the selected one orange; the count shows "2 of 5";
/// previous/next buttons and Enter / Shift+Enter move between matches and scroll them into view.
/// </summary>
public sealed class FindController
{
    private static readonly Brush Mark = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xE0, 0x66));
    private static readonly Brush CurrentMark = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x9F, 0x1C));
    private static readonly Brush MarkText = new SolidColorBrush(Microsoft.UI.Colors.Black);

    private readonly TextBox _box;
    private readonly TextBlock _count;
    private readonly Button _previous;
    private readonly Button _next;
    private readonly ItemsControl _list;
    private readonly DispatcherQueueTimer _typing;
    private IReadOnlyList<IFindableLine> _lines = [];
    private List<(IFindableLine Line, int Index)> _matches = [];
    private int _index = -1;
    private bool _setting;

    public FindController(TextBox box, TextBlock count, Button previous, Button next, ItemsControl list)
    {
        _box = box;
        _count = count;
        _previous = previous;
        _next = next;
        _list = list;
        _typing = box.DispatcherQueue.CreateTimer();
        _typing.Interval = TimeSpan.FromMilliseconds(200);
        _typing.IsRepeating = false;
        _typing.Tick += (_, _) => FindTyped();
        box.TextChanged += (_, _) =>
        {
            if (_setting) return;
            _typing.Stop();
            _typing.Start(); // wait for a pause in typing
        };
        box.KeyDown += OnKeyDown;
        previous.Click += (_, _) => GoTo(_index - 1);
        next.Click += (_, _) => GoTo(_index + 1);
    }

    /// <summary>What's being found (null when the box is empty).</summary>
    public string? Query { get; private set; }

    public IReadOnlyList<IFindableLine> Lines => _lines;

    /// <summary>A match was selected and scrolled to.</summary>
    public event Action<IFindableLine>? MatchSelected;

    /// <summary>The words being found changed (after typing, or new lines).</summary>
    public event Action? Changed;

    public bool IsFocused => ReferenceEquals(FocusManager.GetFocusedElement(_box.XamlRoot), _box);

    /// <summary>New lines to find in, starting with <paramref name="query"/> (shown in the box) and its first match.</summary>
    public void SetLines(IReadOnlyList<IFindableLine> lines, string? query)
    {
        _typing.Stop();
        _lines = lines;
        _setting = true;
        _box.Text = query ?? "";
        _setting = false;
        Query = Normalize(query);
        Apply(jump: true);
    }

    public void Focus()
    {
        _box.Focus(FocusState.Keyboard);
        _box.SelectAll();
    }

    /// <summary>Empties the box (Esc); true if there was something to clear.</summary>
    public bool ClearText()
    {
        if (_box.Text.Length == 0) return false;
        _box.Text = "";
        FindTyped();
        return true;
    }

    /// <summary>For the line's text block, when it's created or reused: shows the text with its marks.</summary>
    public static void Attach(TextBlock view, IFindableLine line)
    {
        line.View = view;
        view.Text = line.Text;
        Paint(line);
    }

    private static string? Normalize(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void FindTyped()
    {
        _typing.Stop();
        Query = Normalize(_box.Text);
        Apply(jump: true);
    }

    private void Apply(bool jump)
    {
        _matches = [];
        foreach (var line in _lines)
        {
            line.Matches = SearchHighlighter.Find(line.Text, Query);
            line.CurrentMatch = -1;
            for (var i = 0; i < line.Matches.Count; i++) _matches.Add((line, i));
            Paint(line);
        }
        _index = -1;
        _previous.IsEnabled = _next.IsEnabled = _matches.Count > 1;
        if (_matches.Count > 0 && jump) GoTo(0);
        else UpdateCount();
        Changed?.Invoke();
    }

    private void UpdateCount() => _count.Text =
        Query is null ? "" : _matches.Count == 0 ? "No matches" : _index < 0 ? $"{_matches.Count}" : $"{_index + 1} of {_matches.Count}";

    private void GoTo(int index)
    {
        if (_matches.Count == 0) return;
        index = (index % _matches.Count + _matches.Count) % _matches.Count;
        if (_index >= 0 && _index < _matches.Count)
        {
            var previous = _matches[_index].Line;
            previous.CurrentMatch = -1;
            Paint(previous);
        }
        _index = index;
        var (line, match) = _matches[index];
        line.CurrentMatch = match;
        Paint(line);
        UpdateCount();
        MatchSelected?.Invoke(line);
        _list.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _list.UpdateLayout();
            if (_list.ContainerFromItem(line) is UIElement container)
                container.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.15, AnimationDesired = true });
        });
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        if (_typing.IsRunning)
        {
            FindTyped(); // Enter before the pause: find now
            return;
        }
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        GoTo(shift ? _index - 1 : _index + 1);
    }

    /// <summary>Marks a line's matches: the selected one orange, the rest yellow.</summary>
    private static void Paint(IFindableLine line)
    {
        if (line.View is not { } text) return;
        text.TextHighlighters.Clear();
        if (line.Matches.Count == 0) return;
        var marker = new TextHighlighter { Background = Mark, Foreground = MarkText };
        var current = new TextHighlighter { Background = CurrentMark, Foreground = MarkText };
        for (var i = 0; i < line.Matches.Count; i++)
            (i == line.CurrentMatch ? current : marker).Ranges.Add(new TextRange { StartIndex = line.Matches[i].Start, Length = line.Matches[i].Length });
        text.TextHighlighters.Add(marker);
        if (current.Ranges.Count > 0) text.TextHighlighters.Add(current);
    }
}
