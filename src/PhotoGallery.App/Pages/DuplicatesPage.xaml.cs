using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Duplicates;

namespace PhotoGallery.App.Pages;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class MemberItem(DuplicateMember member, GroupItem group) : Observable
{
    private ImageSource? _thumbnail;
    private bool _recycled;
    private bool _loading;

    public DuplicateMember Member { get; } = member;
    public GroupItem Group { get; } = group;
    public string Folder => Path.GetDirectoryName(Member.Path) ?? "";
    public string Details =>
        $"{Format.FileSize(Member.FileSize)}" + (Member.Width > 0 ? $" · {Member.Width:N0}×{Member.Height:N0}" : "") +
        $" · {Member.TakenLocal.ToString("d MMM yyyy HH:mm:ss", CultureInfo.CurrentCulture)}";
    public Visibility KeepVisibility => Member.IsSuggestedKeep ? Visibility.Visible : Visibility.Collapsed;

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    public bool IsRecycled
    {
        get => _recycled;
        set
        {
            _recycled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RecycledVisibility));
            OnPropertyChanged(nameof(CanRecycle));
            OnPropertyChanged(nameof(Opacity));
        }
    }

    public Visibility RecycledVisibility => _recycled ? Visibility.Visible : Visibility.Collapsed;
    public bool CanRecycle => !_recycled;
    public double Opacity => _recycled ? 0.6 : 1;

    public async Task EnsureThumbnailAsync()
    {
        if (_thumbnail is not null || _loading) return;
        _loading = true;
        var id = Member.Id;
        var path = await Task.Run(() => App.Services.Thumbnails.GetOrCreateAsync(id, Member.Path));
        if (path is not null) Thumbnail = new BitmapImage(new Uri(path)) { DecodePixelWidth = 400 };
    }
}

public sealed class GroupItem : Observable
{
    public GroupItem(DuplicateGroup group)
    {
        Group = group;
        Members = group.Members.Select(m => new MemberItem(m, this)).ToList();
    }

    public DuplicateGroup Group { get; }
    public IReadOnlyList<MemberItem> Members { get; }

    public string Header
    {
        get
        {
            var remaining = Members.Count(m => !m.IsRecycled);
            var kind = Group.Kind == DuplicateKind.Exact ? "Exact copies" : "Similar photos";
            return $"{kind} · {remaining} of {Members.Count} remaining · {Format.FileSize(Members.Where(m => !m.IsRecycled && !m.Member.IsSuggestedKeep).Sum(m => m.Member.FileSize))} reclaimable";
        }
    }

    public void Refresh() => OnPropertyChanged(nameof(Header));
}

/// <summary>Scan for exact and similar duplicates; review each group and recycle the extra copies.</summary>
public sealed partial class DuplicatesPage : Page
{
    private List<GroupItem> _groups = [];
    private CancellationTokenSource? _scan;

    public DuplicatesPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required; // keep scan results between visits
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _scan?.Cancel();

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        if (_scan is not null)
        {
            _scan.Cancel();
            return;
        }
        _scan = new CancellationTokenSource();
        ScanText.Text = "Cancel";
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        var services = App.Services;
        var finder = new DuplicateFinder(services.Database, (id, path, ct) => services.Thumbnails.GetOrCreateAsync(id, path, ct, background: true));
        var progress = new Progress<DuplicateScanProgress>(p =>
        {
            Progress.IsIndeterminate = p.Total == 0;
            Progress.Maximum = Math.Max(1, p.Total);
            Progress.Value = p.Done;
            SummaryText.Text = $"{p.Stage}… {p.Done:N0} / {p.Total:N0}";
        });
        try
        {
            var groups = await Task.Run(() => finder.FindAsync(progress, _scan.Token));
            _groups = groups.Select(g => new GroupItem(g)).ToList();
            ApplyKindFilter();
            var exact = groups.Where(g => g.Kind == DuplicateKind.Exact).ToList();
            var similar = groups.Count - exact.Count;
            SummaryText.Text = groups.Count == 0
                ? "No duplicates found."
                : $"{exact.Count:N0} groups of exact copies ({Format.FileSize(exact.Sum(g => g.ReclaimableBytes))} reclaimable) and {similar:N0} groups of similar photos. " +
                  "The suggested copy to keep has the highest resolution and an original file name.";
        }
        catch (OperationCanceledException)
        {
            SummaryText.Text = "Scan cancelled. Hashes computed so far are kept, so the next scan picks up where this one stopped.";
        }
        catch (Exception ex)
        {
            PhotoGallery.Core.Log.Error("Duplicate scan failed", ex);
            SummaryText.Text = $"The scan failed: {ex.Message}";
        }
        finally
        {
            _scan = null;
            ScanText.Text = "Find duplicates";
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e) => ApplyKindFilter();

    private void ApplyKindFilter()
    {
        if (Groups is null) return;
        Groups.ItemsSource = KindBox.SelectedIndex switch
        {
            1 => _groups.Where(g => g.Group.Kind == DuplicateKind.Exact).ToList(),
            2 => _groups.Where(g => g.Group.Kind == DuplicateKind.Similar).ToList(),
            _ => _groups,
        };
    }

    private void OnMemberLoaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MemberItem member) _ = member.EnsureThumbnailAsync();
    }

    private static MemberItem? MemberOf(object sender) => (sender as FrameworkElement)?.Tag as MemberItem;

    private void OnOpenMember(object sender, RoutedEventArgs e)
    {
        if (MemberOf(sender) is not { } member) return;
        var ids = member.Group.Members.Where(m => !m.IsRecycled).Select(m => m.Member.Id).ToHashSet();
        var items = App.Services.Media.Query(new MediaFilter { IncludeScreenshots = true }).Where(s => ids.Contains(s.Id)).ToList();
        var index = items.FindIndex(s => s.Id == member.Member.Id);
        if (index >= 0) App.MainWindow.OpenViewer(items, index, (_, _) => { });
    }

    private void OnShowMember(object sender, RoutedEventArgs e)
    {
        if (MemberOf(sender) is { } member) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{member.Member.Path}\"");
    }

    private void OnCopyMember(object sender, RoutedEventArgs e)
    {
        if (MemberOf(sender) is { } member) Clipboard.CopyPaths([member.Member.Path]);
    }

    private async void OnRecycleMember(object sender, RoutedEventArgs e)
    {
        if (MemberOf(sender) is not { } member) return;
        if (member.Group.Members.Count(m => !m.IsRecycled) == 1 &&
            !await Dialogs.ConfirmAsync(XamlRoot, "Recycle the last copy?", "This is the only remaining copy in this group.", "Recycle"))
            return;
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Move to Recycle Bin?",
                $"{member.Member.Path}\n\nThe file is moved to the Recycle Bin (and removed from OneDrive, which keeps it in its own recycle bin).", "Recycle"))
            return;
        Recycle([member]);
    }

    private async void OnRecycleOthers(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GroupItem group) return;
        var extras = group.Members.Where(m => !m.IsRecycled && !m.Member.IsSuggestedKeep).ToList();
        if (extras.Count == 0) return;
        var list = string.Join("\n", extras.Select(m => m.Member.Path));
        if (!await Dialogs.ConfirmAsync(XamlRoot, $"Move {extras.Count} file{(extras.Count == 1 ? "" : "s")} to the Recycle Bin?",
                $"Keeping {group.Members.First(m => m.Member.IsSuggestedKeep).Member.FileName}.\n\n{list}", "Recycle"))
            return;
        Recycle(extras);
    }

    private static void Recycle(IReadOnlyList<MemberItem> members)
    {
        var failed = new List<string>();
        foreach (var member in members)
        {
            if (RecycleBin.Recycle(member.Member.Path))
            {
                member.IsRecycled = true;
                PhotoGallery.Core.Log.Info($"Recycled duplicate {member.Member.Path}");
            }
            else
            {
                failed.Add(member.Member.FileName);
            }
            member.Group.Refresh();
        }
        // The library watcher re-indexes and removes the recycled files from the gallery.
        App.MainWindow.ShowStatus(failed.Count == 0
            ? $"Moved {members.Count} file{(members.Count == 1 ? "" : "s")} to the Recycle Bin."
            : $"Couldn't recycle: {string.Join(", ", failed)}");
    }
}
