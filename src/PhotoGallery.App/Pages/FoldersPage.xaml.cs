using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PhotoGallery.Core.Data;

namespace PhotoGallery.App.Pages;

/// <summary>Browse the library by its folder tree; children are created lazily as nodes expand.</summary>
public sealed partial class FoldersPage : Page
{
    private ILookup<long?, FolderRow> _children = Enumerable.Empty<FolderRow>().ToLookup(f => (long?)f.Id);
    private FolderRow? _selected;

    public FoldersPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required; // keep tree state when navigating away and back
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (Tree.RootNodes.Count > 0) return;
        var folders = await Task.Run(App.Services.Media.GetFolders);
        _children = folders.ToLookup(f => f.ParentId);
        foreach (var root in _children[null])
        {
            var node = CreateNode(root);
            Tree.RootNodes.Add(node);
            node.IsExpanded = true;
        }
    }

    private TreeViewNode CreateNode(FolderRow folder) => new()
    {
        Content = folder,
        HasUnrealizedChildren = _children[folder.Id].Any(),
    };

    private void OnExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        var node = args.Node;
        if (!node.HasUnrealizedChildren || node.Content is not FolderRow folder) return;
        foreach (var child in _children[folder.Id].OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            node.Children.Add(CreateNode(child));
        node.HasUnrealizedChildren = false;
    }

    private void OnItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode { Content: FolderRow folder }) Select(folder);
    }

    private void OnSubfoldersClick(object sender, RoutedEventArgs e)
    {
        if (_selected is not null) Select(_selected);
    }

    private void Select(FolderRow folder)
    {
        _selected = folder;
        FolderTitle.Text = folder.Path;
        Hint.Visibility = Visibility.Collapsed;
        Gallery.Visibility = Visibility.Visible;
        Gallery.EmptyMessage = SubfoldersBox.IsChecked == true ? "No photos or videos in this folder." : "No photos directly in this folder — try including subfolders.";
        Gallery.BaseFilter = new MediaFilter { FolderId = folder.Id, IncludeSubfolders = SubfoldersBox.IsChecked == true };
    }
}
