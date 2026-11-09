using EliteScriptStudio.Audio;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace EliteScriptStudio;

public sealed partial class VoiceProfileEditor : UserControl, IDisposable
{
    private readonly WindowId _windowId;
    private readonly VoiceProfiles _profiles;
    private readonly Brush _dropOutline;
    private readonly Brush _dropHighlight;
    private VoiceTrimEditor? _editor;
    private string? _source;
    private CancellationTokenSource? _analysis;
    private CancellationTokenSource? _saving;
    private CancellationTokenSource? _cleaning;
    private VoiceMusicRemoval.Result? _cleaned;
    private (TimeSpan Start, TimeSpan End) _cleanedTrim;
    private bool _browsing;
    private bool _disposed;

    internal VoiceProfileEditor(WindowId windowId, VoiceProfiles profiles)
    {
        _windowId = windowId;
        _profiles = profiles;
        InitializeComponent();
        _dropOutline = DropZone.BorderBrush;
        _dropHighlight = (Brush)Application.Current.Resources["StudioAccentBrush"];
        Unloaded += (_, _) => PausePlayback();
    }

    internal event EventHandler<VoiceProfile>? ProfileSaved;

    private bool CanChooseFile => !_disposed && !_browsing && _saving is null && _cleaning is null;
    private bool HasCleanedSelection => _cleaned is not null && _editor is { } editor
        && editor.Start >= _cleanedTrim.Start && editor.End <= _cleanedTrim.End;

    private async void BrowseClicked(object sender, RoutedEventArgs args)
    {
        if (!CanChooseFile)
            return;
        _browsing = true;
        UpdateButtons();
        string? path = null;
        try
        {
            var picker = new FileOpenPicker(_windowId) { SuggestedStartLocation = PickerLocationId.MusicLibrary };
            foreach (string extension in VoiceProfiles.ClipExtensions)
                picker.FileTypeFilter.Add(extension);
            path = (await picker.PickSingleFileAsync())?.Path;
        }
        catch (Exception error)
        {
            if (!_disposed)
                SetStatus($"Couldn’t open the file picker. {error.Message}");
        }
        finally
        {
            _browsing = false;
            if (!_disposed)
                UpdateButtons();
        }
        if (path is not null)
            await LoadClipAsync(path);
    }

    private void FileDragOver(object sender, DragEventArgs args)
    {
        bool accepts = CanChooseFile && args.DataView.Contains(StandardDataFormats.StorageItems);
        args.AcceptedOperation = accepts ? DataPackageOperation.Copy : DataPackageOperation.None;
        DropZone.BorderBrush = accepts ? _dropHighlight : _dropOutline;
        args.Handled = true;
    }

    private void FileDragLeave(object sender, DragEventArgs args) => DropZone.BorderBrush = _dropOutline;

    private async void FileDropped(object sender, DragEventArgs args)
    {
        DropZone.BorderBrush = _dropOutline;
        if (!CanChooseFile || !args.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        args.Handled = true;
        var deferral = args.GetDeferral();
        string? path = null;
        try
        {
            var items = await args.DataView.GetStorageItemsAsync();
            if (_disposed)
                return;
            if (items.Count != 1 || items[0] is not StorageFile file || string.IsNullOrWhiteSpace(file.Path))
                SetStatus("Drop one local audio or video file.");
            else
                path = file.Path;
        }
        catch (Exception error)
        {
            if (!_disposed)
                SetStatus($"Couldn’t open the dropped file. {error.Message}");
        }
        finally { deferral.Complete(); }
        if (path is not null)
            await LoadClipAsync(path);
    }

    private async Task LoadClipAsync(string path)
    {
        if (!CanChooseFile)
            return;
        if (!VoiceProfiles.ClipExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            SetStatus("Choose a supported audio or video file, such as WAV, MP3, MP4, or WebM.");
            return;
        }

        ClearClip();
        using var analysis = new CancellationTokenSource();
        _analysis = analysis;
        var editor = new VoiceTrimEditor();
        editor.SetPreviewSource(path);
        editor.SelectionChanged += TrimChanged;
        _editor = editor;
        _source = path;
        TrimHost.Content = editor;
        FileName.Text = Path.GetFileName(path);
        FileName.Visibility = EditCard.Visibility = Visibility.Visible;
        string suggestedName = Path.GetFileNameWithoutExtension(path);
        ProfileName.Text = suggestedName[..Math.Min(ProfileName.MaxLength, suggestedName.Length)];
        UpdateButtons();

        try
        {
            var waveform = await VoiceProfiles.ReadWaveformAsync(path, analysis.Token);
            if (!_disposed && !analysis.IsCancellationRequested)
            {
                editor.SetWaveform(waveform);
                if (!VoiceMusicRemoval.IsAvailable)
                    SetCleanupStatus("The music removal model is missing from this build.");
            }
        }
        catch (OperationCanceledException) when (analysis.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_disposed && !analysis.IsCancellationRequested)
                editor.ShowError($"Couldn’t read the audio track. {error.Message}");
        }
        finally
        {
            if (ReferenceEquals(_analysis, analysis))
                _analysis = null;
            if (!_disposed)
                UpdateButtons();
        }
    }

    private async void SaveClicked(object sender, RoutedEventArgs args)
    {
        if (!CanChooseFile || _analysis is not null || _source is not { } path
            || _editor is not { HasValidSelection: true } editor || string.IsNullOrWhiteSpace(ProfileName.Text))
            return;
        editor.PausePlayback();
        using var save = new CancellationTokenSource();
        _saving = save;
        SetStatus("Saving the trimmed voice profile…");
        UpdateButtons();
        try
        {
            var profile = UseCleanedAudio.IsChecked == true && HasCleanedSelection && _cleaned is { } cleaned
                ? await _profiles.ImportAsync(ProfileName.Text, cleaned.Path, editor.Start - _cleanedTrim.Start,
                    editor.End - _cleanedTrim.Start, save.Token)
                : await _profiles.ImportAsync(ProfileName.Text, path, editor.Start, editor.End, save.Token);
            if (_disposed)
                return;
            ProfileSaved?.Invoke(this, profile);
            SetStatus($"Saved {profile.Name} · {profile.Duration.TotalSeconds:0.000} seconds");
        }
        catch (OperationCanceledException) when (save.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_disposed)
                SetStatus($"Couldn’t save the voice profile. {error.Message}");
        }
        finally
        {
            _saving = null;
            if (!_disposed)
                UpdateButtons();
        }
    }

    private async void RemoveMusicClicked(object sender, RoutedEventArgs args)
    {
        if (_cleaning is { } current)
        {
            current.Cancel();
            SetCleanupStatus("Cancelling music removal…");
            UpdateButtons();
            return;
        }
        if (!CanChooseFile || _analysis is not null || _source is not { } path
            || _editor is not { HasAuditionableSelection: true } editor || HasCleanedSelection)
            return;

        editor.PausePlayback();
        var trim = (editor.Start, editor.End);
        using var cleaning = new CancellationTokenSource();
        _cleaning = cleaning;
        SetCleanupStatus("Separating dialogue from background music locally…");
        UpdateButtons();
        VoiceMusicRemoval.Result? result = null;
        try
        {
            result = await VoiceMusicRemoval.RemoveAsync(path, trim.Start, trim.End, cleaning.Token);
            cleaning.Token.ThrowIfCancellationRequested();
            if (_disposed)
                return;
            ClearCleanedAudio();
            _cleaned = result;
            _cleanedTrim = trim;
            result = null;
            UseCleanedAudio.Visibility = Visibility.Visible;
            UseCleanedAudio.IsChecked = true;
            SetCleanupStatus("Cleaned audio is ready. Play the selection to hear it.");
        }
        catch (OperationCanceledException) when (cleaning.IsCancellationRequested)
        {
            if (!_disposed)
                SetCleanupStatus("Music removal cancelled.");
        }
        catch (Exception error)
        {
            if (!_disposed)
                SetCleanupStatus($"Couldn’t remove background music. {error.Message}");
        }
        finally
        {
            result?.Dispose();
            _cleaning = null;
            if (!_disposed)
                UpdateButtons();
        }
    }

    private void PreviewAudioChanged(object sender, RoutedEventArgs args)
    {
        if (_editor is not { } editor || _source is not { } original)
            return;
        if (UseCleanedAudio.IsChecked == true && HasCleanedSelection && _cleaned is { } cleaned)
            editor.SetPreviewSource(cleaned.Path, _cleanedTrim.Start);
        else
            editor.SetPreviewSource(original);
    }

    private void TrimChanged(object? sender, EventArgs args)
    {
        if (_cleaned is not null)
        {
            bool covered = HasCleanedSelection;
            if (!covered)
                UseCleanedAudio.IsChecked = false;
            UseCleanedAudio.Visibility = covered ? Visibility.Visible : Visibility.Collapsed;
            SetCleanupStatus(covered ? "Cleaned audio is available for this selection."
                : "This selection extends beyond the cleaned audio. Run music removal to include the new range.");
        }
        UpdateButtons();
    }

    private void ClearCleanedAudio()
    {
        var cleaned = _cleaned;
        _cleaned = null;
        UseCleanedAudio.IsChecked = false;
        UseCleanedAudio.Visibility = Visibility.Collapsed;
        cleaned?.Dispose();
        SetCleanupStatus("");
    }

    private void ClearClicked(object sender, RoutedEventArgs args) => ClearClip();

    private void ClearClip()
    {
        _analysis?.Cancel();
        _analysis = null;
        _editor?.Dispose();
        _editor = null;
        _source = null;
        ClearCleanedAudio();
        TrimHost.Content = null;
        EditCard.Visibility = FileName.Visibility = Visibility.Collapsed;
        SetStatus("");
    }

    private void ProfileNameChanged(object sender, TextChangedEventArgs args) => UpdateButtons();

    private void UpdateButtons()
    {
        if (SaveButton is null)
            return;
        BrowseButton.IsEnabled = CanChooseFile;
        ClearButton.IsEnabled = _saving is null && _cleaning is null;
        ProfileName.IsEnabled = _saving is null;
        if (_editor is not null)
            _editor.IsEnabled = _saving is null && _cleaning is null;
        bool ready = CanChooseFile && _analysis is null;
        bool hasName = !string.IsNullOrWhiteSpace(ProfileName.Text);
        SaveButton.IsEnabled = ready && _editor?.HasValidSelection == true && hasName;
        RemoveMusicButton.IsEnabled = _cleaning is { IsCancellationRequested: false }
            || (ready && _editor?.HasAuditionableSelection == true && !HasCleanedSelection && VoiceMusicRemoval.IsAvailable);
        RemoveMusicButton.Content = _cleaning is null ? "Remove background music" : "Cancel music removal";
        CleanupProgress.IsActive = _cleaning is not null;
        CleanupProgress.Visibility = _cleaning is null ? Visibility.Collapsed : Visibility.Visible;
        UseCleanedAudio.IsEnabled = _saving is null && _cleaning is null;
        string saveReason = ready && _editor is { HasValidSelection: false } editor && editor.End - editor.Start >= TimeSpan.FromSeconds(1)
            ? "To save a voice profile, trim the selection to 30 seconds or less."
            : ready && _editor?.HasValidSelection == true && !hasName ? "Enter a profile name to save."
            : "";
        if (SaveRequirement is not null)
        {
            if (SaveRequirement.Text != saveReason)
                SaveRequirement.Text = saveReason;
            SaveRequirement.Visibility = saveReason.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void SetCleanupStatus(string message)
    {
        if (CleanupStatus.Text != message)
            CleanupStatus.Text = message;
        CleanupStatus.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetStatus(string message)
    {
        ImportStatus.Text = message;
        ImportStatus.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void PausePlayback() => _editor?.PausePlayback();

    public void Dispose()
    {
        _disposed = true;
        _saving?.Cancel();
        _cleaning?.Cancel();
        ClearClip();
    }
}
