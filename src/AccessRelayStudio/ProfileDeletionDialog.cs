using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EliteScriptStudio;

internal static class ProfileDeletionDialog
{
    internal static async Task<bool> ConfirmAsync(XamlRoot root, ElementTheme theme, string kind, string name, string detail)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = theme,
            Title = $"Delete {kind} profile?",
            Content = new TextBlock
            {
                Text = $"Permanently delete “{name}” from AccessRelay Studio?\n\n{detail}",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
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
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
