using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FLAC_Tag_Editor.Models;
using FLAC_Tag_Editor.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace FLAC_Tag_Editor.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private const string OpenFolderDialogTitle = "FLACフォルダを選択";
    private const string OpenFilesDialogTitle = "FLACファイルを選択";
    private const string InitialStatusMessage = "フォルダまたはFLACファイルを選択してください";

    private readonly IFlacTagService? _flacTagService;
    private readonly IFileDialogService? _fileDialogService;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _saveCts;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTrack))]
    [NotifyCanExecuteChangedFor(nameof(SaveSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertSelectedCommand))]
    private TrackItemViewModel? _selectedTrack;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertAllCommand))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertAllCommand))]
    private bool _isSaving;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDirtyTracks))]
    [NotifyPropertyChangedFor(nameof(DirtyCountText))]
    [NotifyCanExecuteChangedFor(nameof(SaveAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertAllCommand))]
    private int _dirtyTrackCount;

    [ObservableProperty]
    private string _statusMessage = InitialStatusMessage;

    [ObservableProperty]
    private string _currentSourcePath = string.Empty;

    /// <summary>
    /// XAMLデザインプレビュー用コンストラクタ。
    /// </summary>
    public MainWindowViewModel()
    {
        var sampleTrack = new FlacTrack
        {
            FilePath = @"C:\Music\Sample Album\01 - Sample Track.flac",
            FileName = "01 - Sample Track.flac",
            FileSizeBytes = 38_420_112,
            Title = "Sample Track",
            Artist = "Sample Artist",
            Album = "Sample Album",
            AlbumArtist = "Sample Artist",
            Year = 2026,
            TrackNumber = 1,
            TrackCount = 10,
            DiscNumber = 1,
            DiscCount = 1,
            Genre = "Electronic",
            Comment = "Design-time preview track",
            Duration = TimeSpan.FromSeconds(245),
            SampleRate = 96000,
            BitsPerSample = 24,
            Channels = 2,
            Bitrate = 2890,
            CustomTags =
            [
                new CustomTagEntry("REPLAYGAIN_TRACK_GAIN", "-6.42 dB"),
                new CustomTagEntry("LABEL", "Sample Records")
            ]
        };

        var item = new TrackItemViewModel(sampleTrack);
        AttachTrack(item);
        Tracks.Add(item);
        SelectedTrack = Tracks[0];
        CurrentSourcePath = @"C:\Music\Sample Album";
        StatusMessage = "1 件のFLACファイルを読み込みました";
    }

    [ActivatorUtilitiesConstructor]
    public MainWindowViewModel(
        IFlacTagService flacTagService,
        IFileDialogService fileDialogService)
    {
        _flacTagService = flacTagService ?? throw new ArgumentNullException(nameof(flacTagService));
        _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
    }

    public ObservableCollection<TrackItemViewModel> Tracks { get; } = new();

    public bool IsBusy => IsLoading || IsSaving;

    public bool HasTracks => Tracks.Count > 0;

    public bool HasSelectedTrack => SelectedTrack is not null;

    public bool HasDirtyTracks => DirtyTrackCount > 0;

    public string DirtyCountText => HasDirtyTracks ? $"未保存: {DirtyTrackCount} 件" : string.Empty;

    public string TrackCountText => $"{Tracks.Count} トラック";

    private bool CanOpen => !IsBusy;

    private bool CanSaveSelected => !IsBusy && SelectedTrack is { IsDirty: true, HasValidationErrors: false };

    private bool CanSaveAll => !IsBusy && Tracks.Any(t => t.IsDirty && !t.HasValidationErrors);

    private bool CanRevertSelected => !IsBusy && SelectedTrack is { IsDirty: true };

    private bool CanRevertAll => !IsBusy && HasDirtyTracks;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenFolderAsync()
    {
        if (_fileDialogService is null || _flacTagService is null)
        {
            return;
        }

        var folderPath = await _fileDialogService.OpenFolderAsync(OpenFolderDialogTitle);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        await LoadCoreAsync(
            ct => _flacTagService.LoadTracksFromFolderAsync(folderPath, includeSubdirectories: true, ct),
            folderPath);
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenFilesAsync()
    {
        if (_fileDialogService is null || _flacTagService is null)
        {
            return;
        }

        var filePaths = await _fileDialogService.OpenFlacFilesAsync(OpenFilesDialogTitle);
        if (filePaths.Count == 0)
        {
            return;
        }

        var sourceLabel = filePaths.Count == 1
            ? filePaths[0]
            : $"{Path.GetDirectoryName(filePaths[0])} ({filePaths.Count} ファイル選択)";

        await LoadCoreAsync(
            ct => _flacTagService.LoadTracksFromFilesAsync(filePaths, ct),
            sourceLabel);
    }

    [RelayCommand(CanExecute = nameof(CanSaveSelected))]
    private async Task SaveSelectedAsync()
    {
        if (_flacTagService is null || SelectedTrack is not { IsDirty: true } target)
        {
            return;
        }

        if (target.HasValidationErrors)
        {
            StatusMessage = $"保存エラー ({target.FileName}): {target.ValidationErrorMessage}";
            return;
        }

        _saveCts?.Dispose();
        _saveCts = new CancellationTokenSource();
        var cancellationToken = _saveCts.Token;

        IsSaving = true;
        StatusMessage = $"{target.FileName} を保存中...";

        try
        {
            var editedTrack = target.BuildEditedTrack();
            var result = await _flacTagService.SaveTracksAsync([editedTrack], cancellationToken);
            if (result.Errors.Count > 0)
            {
                StatusMessage = $"保存エラー ({target.FileName}): {result.Errors[0].Message}";
                return;
            }

            if (result.SavedTracks.Count > 0)
            {
                target.AcceptChanges(result.SavedTracks[0]);
                RefreshDirtyState();
                StatusMessage = $"{target.FileName} を保存しました";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "保存をキャンセルしました";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存エラー ({target.FileName}): {ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveAll))]
    private async Task SaveAllAsync()
    {
        if (_flacTagService is null)
        {
            return;
        }

        var dirtyTracks = Tracks.Where(t => t.IsDirty).ToList();
        if (dirtyTracks.Count == 0)
        {
            return;
        }

        _saveCts?.Dispose();
        _saveCts = new CancellationTokenSource();
        var cancellationToken = _saveCts.Token;

        IsSaving = true;
        StatusMessage = $"{dirtyTracks.Count} 件のFLACファイルを保存中...";

        try
        {
            var editedTracks = dirtyTracks.Select(t => t.BuildEditedTrack()).ToList();
            var result = await _flacTagService.SaveTracksAsync(editedTracks, cancellationToken);

            var savedByPath = new Dictionary<string, FlacTrack>(StringComparer.OrdinalIgnoreCase);
            foreach (var saved in result.SavedTracks)
            {
                savedByPath[saved.FilePath] = saved;
            }

            foreach (var item in dirtyTracks)
            {
                if (savedByPath.TryGetValue(item.FilePath, out var savedTrack))
                {
                    item.AcceptChanges(savedTrack);
                }
            }

            RefreshDirtyState();
            StatusMessage = BuildSaveStatusMessage(result);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "保存をキャンセルしました";
        }
        catch (Exception ex)
        {
            StatusMessage = $"一括保存エラー: {ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRevertSelected))]
    private void RevertSelected()
    {
        if (SelectedTrack is not { IsDirty: true } target)
        {
            return;
        }

        target.RevertChanges();
        RefreshDirtyState();
        StatusMessage = $"{target.FileName} の変更を破棄しました";
    }

    [RelayCommand(CanExecute = nameof(CanRevertAll))]
    private void RevertAll()
    {
        var dirtyTracks = Tracks.Where(t => t.IsDirty).ToList();
        if (dirtyTracks.Count == 0)
        {
            return;
        }

        foreach (var item in dirtyTracks)
        {
            item.RevertChanges();
        }

        RefreshDirtyState();
        StatusMessage = $"{dirtyTracks.Count} 件の変更を破棄しました";
    }

    private async Task LoadCoreAsync(
        Func<CancellationToken, Task<TrackLoadResult>> loadOperation,
        string sourceDescription)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var cancellationToken = _loadCts.Token;

        IsLoading = true;
        StatusMessage = "FLACファイルを読み込み中...";

        try
        {
            var result = await loadOperation(cancellationToken);
            ReplaceTracks(result, sourceDescription);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "読み込みをキャンセルしました";
        }
        catch (Exception ex)
        {
            StatusMessage = $"読み込みエラー: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 現在のトラック一覧を新しい読み込み結果で置き換える（将来的に追記モードも分離可能）。
    /// </summary>
    private void ReplaceTracks(TrackLoadResult result, string sourceDescription)
    {
        ClearTracks();

        foreach (var track in result.Tracks)
        {
            var item = new TrackItemViewModel(track);
            AttachTrack(item);
            Tracks.Add(item);
        }

        SelectedTrack = Tracks.FirstOrDefault();
        CurrentSourcePath = sourceDescription;

        OnPropertyChanged(nameof(HasTracks));
        OnPropertyChanged(nameof(TrackCountText));
        RefreshDirtyState();

        StatusMessage = BuildLoadStatusMessage(result);
    }

    private void ClearTracks()
    {
        SelectedTrack = null;

        foreach (var item in Tracks)
        {
            DetachTrack(item);
            item.Dispose();
        }

        Tracks.Clear();
        DirtyTrackCount = 0;
    }

    private void AttachTrack(TrackItemViewModel item)
    {
        item.PropertyChanged += OnTrackItemPropertyChanged;
    }

    private void DetachTrack(TrackItemViewModel item)
    {
        item.PropertyChanged -= OnTrackItemPropertyChanged;
    }

    private void OnTrackItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(TrackItemViewModel.IsDirty), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(TrackItemViewModel.HasValidationErrors), StringComparison.Ordinal))
        {
            RefreshDirtyState();
        }
    }

    private void RefreshDirtyState()
    {
        DirtyTrackCount = Tracks.Count(t => t.IsDirty);
        SaveSelectedCommand.NotifyCanExecuteChanged();
        RevertSelectedCommand.NotifyCanExecuteChanged();
        SaveAllCommand.NotifyCanExecuteChanged();
        RevertAllCommand.NotifyCanExecuteChanged();
    }

    private static string BuildLoadStatusMessage(TrackLoadResult result)
    {
        if (result.Tracks.Count == 0 && result.Errors.Count == 0)
        {
            return "FLACファイルが見つかりませんでした";
        }

        if (result.Errors.Count > 0)
        {
            return $"{result.Tracks.Count} 件読み込み完了（{result.Errors.Count} 件エラー）";
        }

        return $"{result.Tracks.Count} 件のFLACファイルを読み込みました";
    }

    private static string BuildSaveStatusMessage(TrackSaveResult result)
    {
        if (result.Errors.Count > 0)
        {
            var firstError = result.Errors[0].Message;
            return $"{result.SavedTracks.Count} 件保存完了（{result.Errors.Count} 件エラー: {firstError}）";
        }

        return $"{result.SavedTracks.Count} 件のFLACファイルを保存しました";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _saveCts = null;

        ClearTracks();
    }
}
