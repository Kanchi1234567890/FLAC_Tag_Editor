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
    private const string OpenArtworkDialogTitle = "アートワーク画像を選択";
    private const string InitialStatusMessage = "フォルダまたはFLACファイルを選択してください";
    private const string DuplicateFileNameErrorMessage = "同一フォルダ内に同名のファイルが重複しています。";
    private static readonly TrackGridColumnKind[] DefaultGridColumns =
    [
        TrackGridColumnKind.Dirty,
        TrackGridColumnKind.Track,
        TrackGridColumnKind.Title,
        TrackGridColumnKind.Artist,
        TrackGridColumnKind.Album,
        TrackGridColumnKind.AlbumArtist,
        TrackGridColumnKind.Year,
        TrackGridColumnKind.Duration,
        TrackGridColumnKind.FileName
    ];

    private readonly IFlacTagService? _flacTagService;
    private readonly IFileDialogService? _fileDialogService;
    private readonly IArtworkService? _artworkService;
    private readonly IClipboardService? _clipboardService;
    private readonly ITsvPasteService? _tsvPasteService;
    private readonly ITextSearchReplaceService _textSearchReplaceService;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _saveCts;
    private CancellationTokenSource? _artworkCts;
    private bool _isUpdatingExternalValidation;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTrack))]
    [NotifyCanExecuteChangedFor(nameof(SaveSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(DropArtworkCommand))]
    private TrackItemViewModel? _selectedTrack;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(DropArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(PasteTsvCommand))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(DropArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(PasteTsvCommand))]
    private bool _isSaving;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(DropArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(PasteTsvCommand))]
    private bool _isArtworkLoading;

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
        _textSearchReplaceService = new DesignTimeTextSearchReplaceService();
        SearchReplace = CreateSearchReplaceViewModel();

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
        DisplayedTracks.Add(item);
        SelectedTrack = Tracks[0];
        CurrentSourcePath = @"C:\Music\Sample Album";
        StatusMessage = "1 件のFLACファイルを読み込みました";
    }

    [ActivatorUtilitiesConstructor]
    public MainWindowViewModel(
        IFlacTagService flacTagService,
        IFileDialogService fileDialogService,
        IArtworkService artworkService,
        IClipboardService clipboardService,
        ITsvPasteService tsvPasteService,
        ITextSearchReplaceService textSearchReplaceService)
    {
        _flacTagService = flacTagService ?? throw new ArgumentNullException(nameof(flacTagService));
        _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
        _artworkService = artworkService ?? throw new ArgumentNullException(nameof(artworkService));
        _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        _tsvPasteService = tsvPasteService ?? throw new ArgumentNullException(nameof(tsvPasteService));
        _textSearchReplaceService = textSearchReplaceService ?? throw new ArgumentNullException(nameof(textSearchReplaceService));
        SearchReplace = CreateSearchReplaceViewModel();
    }

    public ObservableCollection<TrackItemViewModel> Tracks { get; } = new();

    public ObservableCollection<TrackItemViewModel> DisplayedTracks { get; } = new();

    public SearchReplaceViewModel SearchReplace { get; }

    public bool IsBusy => IsLoading || IsSaving || IsArtworkLoading;

    public bool HasTracks => Tracks.Count > 0;

    public bool HasDisplayedTracks => DisplayedTracks.Count > 0;

    public bool IsFilterEmpty => HasTracks && !HasDisplayedTracks;

    public bool HasSelectedTrack => SelectedTrack is not null;

    public bool HasDirtyTracks => DirtyTrackCount > 0;

    public string DirtyCountText => HasDirtyTracks ? $"未保存: {DirtyTrackCount} 件" : string.Empty;

    public string TrackCountText => SearchReplace.IsFilterActive
        ? $"{DisplayedTracks.Count} / {Tracks.Count} トラック（フィルタ中）"
        : $"{Tracks.Count} トラック";

    private bool CanOpen => !IsBusy;

    private bool CanSaveSelected => !IsBusy && SelectedTrack is { IsDirty: true, HasValidationErrors: false };

    private bool CanSaveAll => !IsBusy && Tracks.Any(t => t.IsDirty && !t.HasValidationErrors);

    private bool CanRevertSelected => !IsBusy && SelectedTrack is { IsDirty: true };

    private bool CanRevertAll => !IsBusy && HasDirtyTracks;

    private bool CanEditArtwork => !IsBusy && SelectedTrack is not null;

    private bool CanRemoveArtwork => !IsBusy && SelectedTrack is { HasArtwork: true };

    private bool CanPasteTsv => !IsBusy && HasTracks;

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

    [RelayCommand(CanExecute = nameof(CanEditArtwork))]
    private async Task SelectArtworkAsync()
    {
        if (_fileDialogService is null || _artworkService is null || SelectedTrack is not { } target)
        {
            return;
        }

        var imagePath = await _fileDialogService.OpenImageFileAsync(OpenArtworkDialogTitle);
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return;
        }

        await ApplyArtworkCoreAsync(
            target,
            ct => _artworkService.LoadFromFileAsync(imagePath, ct),
            "アートワークを読み込み中...");
    }

    [RelayCommand(CanExecute = nameof(CanRemoveArtwork))]
    private void RemoveArtwork()
    {
        if (SelectedTrack is not { HasArtwork: true } target)
        {
            return;
        }

        target.RemoveArtwork();
        RefreshDirtyState();
        StatusMessage = $"{target.FileName} のアートワークを削除しました（未保存）";
    }

    [RelayCommand(CanExecute = nameof(CanEditArtwork))]
    private async Task DropArtworkAsync(ArtworkDropPayload? payload)
    {
        if (_artworkService is null || SelectedTrack is not { } target)
        {
            return;
        }

        if (payload is null || !payload.HasAnyData)
        {
            StatusMessage = "ドロップされたデータから画像を取得できませんでした";
            return;
        }

        await ApplyArtworkCoreAsync(
            target,
            ct => _artworkService.ResolveFromDropAsync(payload, ct),
            "ドロップされたアートワークを取得中...");
    }

    [RelayCommand(CanExecute = nameof(CanPasteTsv))]
    private async Task PasteTsvAsync(TsvPasteRequest? request)
    {
        if (_clipboardService is null || _tsvPasteService is null || Tracks.Count == 0)
        {
            return;
        }

        string? clipboardText;
        try
        {
            clipboardText = await _clipboardService.GetTextAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"クリップボード読み取りエラー: {ex.Message}";
            return;
        }

        var parsedRows = _tsvPasteService.Parse(clipboardText);
        if (parsedRows.Count == 0)
        {
            StatusMessage = "クリップボードに貼り付け可能なテキストがありません";
            return;
        }

        var (orderedTracks, startRowIndex, orderedColumns, startColumnIndex) = ResolvePasteTarget(request);
        if (orderedTracks.Count == 0 || orderedColumns.Count == 0)
        {
            StatusMessage = "貼り付け対象のトラックまたは列がありません";
            return;
        }

        var appliedCells = 0;
        var skippedReadOnlyCells = 0;
        var skippedInvalidCells = 0;
        var truncatedRows = 0;
        var truncatedCells = 0;
        var affectedRows = new HashSet<int>();
        var affectedColumns = new HashSet<int>();

        for (var rowOffset = 0; rowOffset < parsedRows.Count; rowOffset++)
        {
            var targetRowIndex = startRowIndex + rowOffset;
            if (targetRowIndex < 0 || targetRowIndex >= orderedTracks.Count)
            {
                truncatedRows += parsedRows.Count - rowOffset;
                break;
            }

            var track = orderedTracks[targetRowIndex];
            var sourceCells = parsedRows[rowOffset];

            for (var colOffset = 0; colOffset < sourceCells.Count; colOffset++)
            {
                var targetColIndex = startColumnIndex + colOffset;
                if (targetColIndex < 0 || targetColIndex >= orderedColumns.Count)
                {
                    truncatedCells += sourceCells.Count - colOffset;
                    break;
                }

                var columnKind = orderedColumns[targetColIndex];
                var outcome = track.ApplyGridCellValue(columnKind, sourceCells[colOffset]);
                switch (outcome)
                {
                    case GridCellApplyResult.Applied:
                        appliedCells++;
                        affectedRows.Add(targetRowIndex);
                        affectedColumns.Add(targetColIndex);
                        break;

                    case GridCellApplyResult.SkippedReadOnly:
                        skippedReadOnlyCells++;
                        break;

                    case GridCellApplyResult.SkippedInvalid:
                        skippedInvalidCells++;
                        break;
                }
            }
        }

        RefreshDirtyState();
        if (SearchReplace.IsOpen)
        {
            SearchReplace.RefreshResults(notifyFilter: true);
        }

        StatusMessage = BuildPasteStatusMessage(
            affectedRows.Count,
            affectedColumns.Count,
            appliedCells,
            skippedReadOnlyCells,
            skippedInvalidCells,
            truncatedRows,
            truncatedCells);
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
                var savedTrack = result.SavedEntries.Count > 0
                    ? result.SavedEntries[0].Track
                    : result.SavedTracks[0];
                target.AcceptChanges(savedTrack);
                RefreshDirtyState();
                if (SearchReplace.IsOpen)
                {
                    SearchReplace.RefreshResults(notifyFilter: true);
                }

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

            var savedByOriginalPath = new Dictionary<string, FlacTrack>(StringComparer.OrdinalIgnoreCase);
            if (result.SavedEntries.Count > 0)
            {
                foreach (var entry in result.SavedEntries)
                {
                    savedByOriginalPath[entry.OriginalFilePath] = entry.Track;
                }
            }
            else
            {
                foreach (var saved in result.SavedTracks)
                {
                    savedByOriginalPath[saved.FilePath] = saved;
                }
            }

            foreach (var item in dirtyTracks)
            {
                if (savedByOriginalPath.TryGetValue(item.FilePath, out var savedTrack))
                {
                    item.AcceptChanges(savedTrack);
                }
            }

            RefreshDirtyState();
            if (SearchReplace.IsOpen)
            {
                SearchReplace.RefreshResults(notifyFilter: true);
            }

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
        if (SearchReplace.IsOpen)
        {
            SearchReplace.RefreshResults(notifyFilter: true);
        }

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
        if (SearchReplace.IsOpen)
        {
            SearchReplace.RefreshResults(notifyFilter: true);
        }

        StatusMessage = $"{dirtyTracks.Count} 件の変更を破棄しました";
    }

    private async Task ApplyArtworkCoreAsync(
        TrackItemViewModel target,
        Func<CancellationToken, Task<ArtworkImage>> fetchOperation,
        string progressMessage)
    {
        _artworkCts?.Cancel();
        _artworkCts?.Dispose();
        _artworkCts = new CancellationTokenSource();
        var cancellationToken = _artworkCts.Token;

        IsArtworkLoading = true;
        StatusMessage = progressMessage;

        try
        {
            var artwork = await fetchOperation(cancellationToken);
            target.SetArtwork(artwork.Data, artwork.MimeType);
            RefreshDirtyState();
            StatusMessage = BuildArtworkAppliedMessage(target.FileName, artwork);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "アートワーク取得をキャンセルしました";
        }
        catch (Exception ex)
        {
            StatusMessage = $"アートワーク取得エラー: {ex.Message}";
        }
        finally
        {
            IsArtworkLoading = false;
        }
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

        CurrentSourcePath = sourceDescription;

        OnPropertyChanged(nameof(HasTracks));
        SearchReplace.RefreshResults(notifyFilter: false);
        SyncDisplayedTracks();
        SelectedTrack = DisplayedTracks.FirstOrDefault();
        RefreshDirtyState();

        StatusMessage = BuildLoadStatusMessage(result);
    }

    private void ClearTracks()
    {
        SelectedTrack = null;
        DisplayedTracks.Clear();

        foreach (var item in Tracks)
        {
            DetachTrack(item);
            item.Dispose();
        }

        Tracks.Clear();
        DirtyTrackCount = 0;
        OnPropertyChanged(nameof(HasDisplayedTracks));
        OnPropertyChanged(nameof(IsFilterEmpty));
        OnPropertyChanged(nameof(TrackCountText));
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
        if (_isUpdatingExternalValidation)
        {
            return;
        }

        if (string.Equals(e.PropertyName, nameof(TrackItemViewModel.IsDirty), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(TrackItemViewModel.HasValidationErrors), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(TrackItemViewModel.HasArtwork), StringComparison.Ordinal))
        {
            RefreshDirtyState();
        }

        if (SearchReplace.IsOpen
            && !SearchReplace.IsExecutingReplace
            && IsSearchableTrackProperty(e.PropertyName))
        {
            SearchReplace.RefreshResults(notifyFilter: false);
        }
    }

    partial void OnSelectedTrackChanged(TrackItemViewModel? value)
    {
        SearchReplace.NotifyCommandStates();
    }

    partial void OnIsLoadingChanged(bool value)
    {
        SearchReplace.NotifyCommandStates();
    }

    partial void OnIsSavingChanged(bool value)
    {
        SearchReplace.NotifyCommandStates();
    }

    partial void OnIsArtworkLoadingChanged(bool value)
    {
        SearchReplace.NotifyCommandStates();
    }

    private void RefreshDirtyState()
    {
        ValidateDuplicateFileNames();
        DirtyTrackCount = Tracks.Count(t => t.IsDirty);
        SaveSelectedCommand.NotifyCanExecuteChanged();
        RevertSelectedCommand.NotifyCanExecuteChanged();
        SaveAllCommand.NotifyCanExecuteChanged();
        RevertAllCommand.NotifyCanExecuteChanged();
        SelectArtworkCommand.NotifyCanExecuteChanged();
        RemoveArtworkCommand.NotifyCanExecuteChanged();
        DropArtworkCommand.NotifyCanExecuteChanged();
        PasteTsvCommand.NotifyCanExecuteChanged();
        SearchReplace.NotifyCommandStates();
    }

    private SearchReplaceViewModel CreateSearchReplaceViewModel()
    {
        return new SearchReplaceViewModel(
            _textSearchReplaceService,
            getAllTracks: () => Tracks,
            getDisplayedTracks: () => DisplayedTracks,
            getSelectedTrack: () => SelectedTrack,
            selectTrack: SelectTrackFromSearch,
            isBusy: () => IsBusy,
            onFilterChanged: SyncDisplayedTracks,
            onReplaceApplied: OnSearchReplaceApplied);
    }

    private void SelectTrackFromSearch(TrackItemViewModel track)
    {
        if (!DisplayedTracks.Contains(track))
        {
            return;
        }

        SelectedTrack = track;
    }

    private void OnSearchReplaceApplied(string statusMessage)
    {
        RefreshDirtyState();
        StatusMessage = statusMessage;
    }

    private void SyncDisplayedTracks()
    {
        var desiredTracks = SearchReplace.IsFilterActive
            ? Tracks.Where(SearchReplace.IsTrackMatching).ToList()
            : Tracks.ToList();

        if (!HasSameSequence(DisplayedTracks, desiredTracks))
        {
            var previousSelected = SelectedTrack;

            DisplayedTracks.Clear();
            foreach (var item in desiredTracks)
            {
                DisplayedTracks.Add(item);
            }

            if (previousSelected is not null && DisplayedTracks.Contains(previousSelected))
            {
                SelectedTrack = previousSelected;
            }
            else
            {
                SelectedTrack = DisplayedTracks.FirstOrDefault();
            }
        }

        OnPropertyChanged(nameof(HasDisplayedTracks));
        OnPropertyChanged(nameof(IsFilterEmpty));
        OnPropertyChanged(nameof(TrackCountText));
        SearchReplace.NotifyCommandStates();
    }

    private void ValidateDuplicateFileNames()
    {
        if (Tracks.Count == 0)
        {
            return;
        }

        var targetCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in Tracks)
        {
            var targetPath = track.TargetFilePath;
            targetCounts[targetPath] = targetCounts.GetValueOrDefault(targetPath) + 1;
        }

        _isUpdatingExternalValidation = true;
        try
        {
            foreach (var track in Tracks)
            {
                var isDuplicate = targetCounts.GetValueOrDefault(track.TargetFilePath) > 1;
                track.SetExternalFileNameError(isDuplicate ? DuplicateFileNameErrorMessage : null);
            }
        }
        finally
        {
            _isUpdatingExternalValidation = false;
        }
    }

    private static bool HasSameSequence(
        ObservableCollection<TrackItemViewModel> current,
        List<TrackItemViewModel> desired)
    {
        if (current.Count != desired.Count)
        {
            return false;
        }

        for (var i = 0; i < current.Count; i++)
        {
            if (!ReferenceEquals(current[i], desired[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSearchableTrackProperty(string? propertyName)
    {
        return propertyName is nameof(TrackItemViewModel.FileName)
            or nameof(TrackItemViewModel.Title)
            or nameof(TrackItemViewModel.Artist)
            or nameof(TrackItemViewModel.Album)
            or nameof(TrackItemViewModel.AlbumArtist)
            or nameof(TrackItemViewModel.Genre)
            or nameof(TrackItemViewModel.Comment)
            or nameof(TrackItemViewModel.IsDirty);
    }

    private (
        IReadOnlyList<TrackItemViewModel> OrderedTracks,
        int StartRowIndex,
        IReadOnlyList<TrackGridColumnKind> OrderedColumns,
        int StartColumnIndex) ResolvePasteTarget(TsvPasteRequest? request)
    {
        var tracksByPath = new Dictionary<string, TrackItemViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Tracks)
        {
            tracksByPath.TryAdd(item.FilePath, item);
        }

        var orderedTracks = new List<TrackItemViewModel>(Tracks.Count);
        if (request is { OrderedFilePaths.Count: > 0 })
        {
            foreach (var path in request.OrderedFilePaths)
            {
                if (tracksByPath.TryGetValue(path, out var track))
                {
                    orderedTracks.Add(track);
                }
            }
        }

        int startRowIndex;
        if (orderedTracks.Count > 0 && request is not null)
        {
            startRowIndex = Math.Clamp(request.StartRowIndex, 0, orderedTracks.Count - 1);
        }
        else
        {
            orderedTracks.AddRange(Tracks);
            startRowIndex = SelectedTrack is not null
                ? Math.Max(0, Tracks.IndexOf(SelectedTrack))
                : 0;
        }

        IReadOnlyList<TrackGridColumnKind> orderedColumns;
        int startColumnIndex;
        if (request is { OrderedColumns.Count: > 0 })
        {
            orderedColumns = request.OrderedColumns;
            startColumnIndex = Math.Clamp(request.StartColumnIndex, 0, orderedColumns.Count - 1);
        }
        else
        {
            orderedColumns = DefaultGridColumns;
            startColumnIndex = Array.FindIndex(DefaultGridColumns, kind => kind.IsEditable());
            if (startColumnIndex < 0)
            {
                startColumnIndex = 0;
            }
        }

        return (orderedTracks, startRowIndex, orderedColumns, startColumnIndex);
    }

    private static string BuildPasteStatusMessage(
        int affectedRowCount,
        int affectedColumnCount,
        int appliedCells,
        int skippedReadOnlyCells,
        int skippedInvalidCells,
        int truncatedRows,
        int truncatedCells)
    {
        var notes = new List<string>(4);
        if (skippedReadOnlyCells > 0)
        {
            notes.Add($"読み取り専用 {skippedReadOnlyCells} 件スキップ");
        }

        if (skippedInvalidCells > 0)
        {
            notes.Add($"書式不正 {skippedInvalidCells} 件スキップ");
        }

        if (truncatedRows > 0)
        {
            notes.Add($"範囲外 {truncatedRows} 行切り捨て");
        }

        if (truncatedCells > 0)
        {
            notes.Add($"列超過 {truncatedCells} セル切り捨て");
        }

        if (appliedCells == 0)
        {
            return notes.Count > 0
                ? $"貼り付け可能な編集セルがありませんでした（{string.Join("・", notes)}）"
                : "貼り付け可能な編集セルがありませんでした";
        }

        if (notes.Count > 0)
        {
            return $"{affectedRowCount} 行 × {affectedColumnCount} 列（{appliedCells} セル）を貼り付けました（{string.Join("・", notes)}・未保存）";
        }

        return $"{affectedRowCount} 行 × {affectedColumnCount} 列（{appliedCells} セル）を貼り付けました（未保存）";
    }

    private static string BuildArtworkAppliedMessage(string fileName, ArtworkImage artwork)
    {
        if (artwork.WasConverted && !string.IsNullOrWhiteSpace(artwork.OriginalFormatName))
        {
            return $"{fileName} にアートワークを設定しました（{artwork.OriginalFormatName} → {artwork.MimeType} に変換・未保存）";
        }

        return $"{fileName} にアートワークを設定しました（{artwork.MimeType}・未保存）";
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

        _artworkCts?.Cancel();
        _artworkCts?.Dispose();
        _artworkCts = null;

        ClearTracks();
    }

    private sealed class DesignTimeTextSearchReplaceService : ITextSearchReplaceService
    {
        public string? ValidatePattern(SearchReplaceQuery query) => null;
        public int CountMatches(string? input, SearchReplaceQuery query, bool isFileName = false) => 0;
        public TextReplaceResult Replace(string? input, SearchReplaceQuery query, bool isFileName = false) =>
            new(input ?? string.Empty, 0, false);
        public FileNameValidationResult ValidateFileName(string? fileName) =>
            FlacFileNameValidator.Validate(fileName);
    }
}
