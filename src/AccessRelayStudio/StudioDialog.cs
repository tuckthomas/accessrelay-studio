using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EliteScriptStudio;

internal static class StudioDialog
{
    internal static async Task<bool> ConfirmDeletionAsync(XamlRoot root, ElementTheme theme, string kind, string name, string detail,
        string? title = null)
    {
        var dialog = Create(root, theme);
        dialog.Title = title ?? $"Delete {kind} profile?";
        dialog.Content = new TextBlock
        {
            Text = $"Permanently delete “{name}” from AccessRelay Studio?\n\n{detail}",
            TextWrapping = TextWrapping.Wrap
        };
        dialog.PrimaryButtonText = "Delete";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Close;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    internal static ContentDialog Create(XamlRoot root, ElementTheme theme)
    {
        var dialog = new ContentDialog { XamlRoot = root, RequestedTheme = theme };
        var app = Application.Current.Resources;
        dialog.Resources.ThemeDictionaries["Dark"] = new ResourceDictionary
        {
            ["ContentDialogTopOverlay"] = app["StudioVoiceSurfaceBrush"],
            ["ContentDialogBackground"] = app["StudioWindowBrush"],
            ["ContentDialogBorderBrush"] = app["StudioVoiceOutlineBrush"],
            ["ContentDialogSeparatorBorderBrush"] = app["StudioVoiceOutlineBrush"],
            ["ButtonBackground"] = app["StudioControlBrush"],
            ["AccentButtonBackground"] = app["StudioAccentBrush"],
            ["AccentButtonForeground"] = app["StudioOnAccentBrush"]
        };
        return dialog;
    }
}
