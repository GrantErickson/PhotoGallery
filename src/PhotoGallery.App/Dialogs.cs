using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PhotoGallery.App;

public static class Dialogs
{
    /// <summary>Asks for a line of text; returns null if cancelled.</summary>
    public static async Task<string?> PromptAsync(XamlRoot root, string title, string placeholder, string initial, string primary = "OK")
    {
        var box = new TextBox { PlaceholderText = placeholder, Text = initial, MinWidth = 300 };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = box,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        box.Loaded += (_, _) => box.SelectAll();
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string primary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = message,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
