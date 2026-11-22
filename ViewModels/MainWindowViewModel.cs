using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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

public partial class MainWindowViewModel : ViewModelBase, IWindowCloseGuard, IDisposable
{
    private const string OpenFolderDialogTitle = "FLACフォルダを選択";
    private const string AppendFolderDialogTitle = "追加するFLACフォルダを選択";
    private const string OpenFilesDialogTitle = "FLACファイルを選択";
    private const string AppendFilesDialogTitle = "追加するFLACファイルを選択";
    private const string AddArtworkDialogTitle = "追加するアートワーク画像を選択";
    private const string ReplaceArtworkDialogTitle = "差し替えるアートワーク画像を選択";
    private const string SingleArtworkDropOverlayText = "ここにドロップして追加";
    private const string DefaultArtworkLoadingMessage = "アートワークを読み込み中...";
    private const string DropArtworkLoadingMessage = "ドロップされたアートワークを取得中...";
    private const string InitialStatusMessage = "フォルダまたはFLACファイルを選択してください";
    private const string DuplicateFileNameErrorMessage = "同一フォルダ内に同名のファイルが重複しています。";
    private const string CloseWhileSavingMessage = "保存処理の完了後にウィンドウを閉じてください";
    private const string SaveWhileBusyMessage = "他の処理の完了後に再度お試しください";
    private const string ClearedTracksStatusMessage = "一覧をクリアしました";

    private enum UnsavedChangesPromptReason
    {
        ApplicationClose,
        ReplaceTracks,
        ClearTracks
    }

    private readonly IFlacTagService? _flacTagService;
    private readonly IFileDialogService? _fileDialogService;
    private readonly IDialogService? _dialogService;
    private readonly IArtworkService? _artworkService;
    private readonly IClipboardService? _clipboardService;
    private readonly ITsvPasteService? _tsvPasteService;
    private readonly ITextSearchReplaceService _textSearchReplaceService;
    private readonly IAppSettingsService? _appSettingsService;
    private readonly ICustomTagDefinitionService? _customTagDefinitionService;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _saveCts;
    private CancellationTokenSource? _artworkCts;
    private CancellationTokenSource? _settingsLoadCts;
    private bool _isUpdatingExternalValidation;
    private bool _isUpdatingSelectedTracks;
    private bool _isBatchUpdatingTracks;
    private int _selectedTrackCount;
    private bool _hasAnySelectedDirtyTrack;
    private bool _hasAnySelectedValidDirtyTrack;
    private bool _hasAnySelectedArtworkTrack;
    private bool _hasMixedArtworkInSelection;
    private string _mixedArtworkNoticeText = string.Empty;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTrack))]
    [NotifyPropertyChangedFor(nameof(CanEditSingleTrackArtworkDetails))]
    [NotifyCanExecuteChangedFor(nameof(SaveSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReplaceArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearAllArtworksCommand))]
    [NotifyCanExecuteChangedFor(nameof(SyncArtworksToSelectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(DropArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectPreviousArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectNextArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveArtworkLeftCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveArtworkRightCommand))]
    private TrackItemViewModel? _selectedTrack;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(CanEditSingleTrackArtworkDetails))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(AppendFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(AppendFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReplaceArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearAllArtworksCommand))]
    [NotifyCanExecuteChangedFor(nameof(SyncArtworksToSelectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(DropArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveArtworkLeftCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveArtworkRightCommand))]
    [NotifyCanExecuteChangedFor(nameof(PasteTsvCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearTrackListCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenCustomTagManagerCommand))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(CanEditSingleTrackArtworkDetails))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(AppendFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(AppendFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReplaceArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearAllArtworksCommand))]
    [NotifyCanExecuteChangedFor(nameof(SyncArtworksToSelectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(DropArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveArtworkLeftCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveArtworkRightCommand))]
    [NotifyCanExecuteChangedFor(nameof(PasteTsvCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearTrackListCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenCustomTagManagerCommand))]
    private bool _isSaving;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(CanEditSingleTrackArtworkDetails))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(AppendFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(AppendFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReplaceArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearAllArtworksCommand))]
    [NotifyCanExecuteChangedFor(nameof(SyncArtworksToSelectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(DropArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveArtworkLeftCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveArtworkRightCommand))]
    [NotifyCanExecuteChangedFor(nameof(PasteTsvCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearTrackListCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenCustomTagManagerCommand))]
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
        ColumnSettings = new TrackGridColumnSettingsViewModel();
        DetailPane = new DetailPaneLayoutViewModel();
        CustomTagCatalog = new CustomTagCatalogViewModel(
            initialDefinitions:
            [
                new CustomTagDefinition("LABEL", "レーベル", IsVisible: true),
                new CustomTagDefinition("LYRICIST", "作詞者", IsVisible: true)
            ]);
        CustomTagCatalog.DefinitionsChanged += OnCustomTagDefinitionsChanged;
        SelectedTracks.CollectionChanged += OnSelectedTracksCollectionChanged;

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

        var item = new TrackItemViewModel(sampleTrack, CustomTagCatalog);
        AttachTrack(item);
        Tracks.Add(item);
        DisplayedTracks.Add(item);
        SelectedTrack = Tracks[0];
        ReplaceSelectedTracks([Tracks[0]]);
        CurrentSourcePath = @"C:\Music\Sample Album";
        StatusMessage = "1 件のFLACファイルを読み込みました";
    }

    [ActivatorUtilitiesConstructor]
    public MainWindowViewModel(
        IFlacTagService flacTagService,
        IFileDialogService fileDialogService,
        IDialogService dialogService,
        IArtworkService artworkService,
        IClipboardService clipboardService,
        ITsvPasteService tsvPasteService,
        ITextSearchReplaceService textSearchReplaceService,
        IAppSettingsService appSettingsService,
        ICustomTagDefinitionService customTagDefinitionService)
    {
        _flacTagService = flacTagService ?? throw new ArgumentNullException(nameof(flacTagService));
        _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _artworkService = artworkService ?? throw new ArgumentNullException(nameof(artworkService));
        _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        _tsvPasteService = tsvPasteService ?? throw new ArgumentNullException(nameof(tsvPasteService));
        _textSearchReplaceService = textSearchReplaceService ?? throw new ArgumentNullException(nameof(textSearchReplaceService));
        _appSettingsService = appSettingsService ?? throw new ArgumentNullException(nameof(appSettingsService));
        _customTagDefinitionService = customTagDefinitionService ?? throw new ArgumentNullException(nameof(customTagDefinitionService));

        SearchReplace = CreateSearchReplaceViewModel();
        ColumnSettings = new TrackGridColumnSettingsViewModel(_appSettingsService, message => StatusMessage = message);
        DetailPane = new DetailPaneLayoutViewModel(_appSettingsService, message => StatusMessage = message);
        CustomTagCatalog = new CustomTagCatalogViewModel(_customTagDefinitionService, message => StatusMessage = message);
        CustomTagCatalog.DefinitionsChanged += OnCustomTagDefinitionsChanged;

        SelectedTracks.CollectionChanged += OnSelectedTracksCollectionChanged;
        _settingsLoadCts = new CancellationTokenSource();
        _ = LoadInitialSettingsAsync(_settingsLoadCts.Token);
    }

    public ObservableCollection<TrackItemViewModel> Tracks { get; } = new();

    public ObservableCollection<TrackItemViewModel> DisplayedTracks { get; } = new();

    public BulkObservableCollection<TrackItemViewModel> SelectedTracks { get; } = new();

    public SearchReplaceViewModel SearchReplace { get; }

    public TrackGridColumnSettingsViewModel ColumnSettings { get; }

    public DetailPaneLayoutViewModel DetailPane { get; }

    public CustomTagCatalogViewModel CustomTagCatalog { get; }

    public bool IsBusy => IsLoading || IsSaving || IsArtworkLoading;

    public bool HasTracks => Tracks.Count > 0;

    public bool HasDisplayedTracks => DisplayedTracks.Count > 0;

    public bool IsFilterEmpty => HasTracks && !HasDisplayedTracks;

    public bool HasSelectedTrack => SelectedTrack is not null;

    public int SelectedTrackCount => _selectedTrackCount;

    public bool HasMultipleSelectedTracks => _selectedTrackCount > 1;

    public string SelectedCountText => HasMultipleSelectedTracks
        ? $"{_selectedTrackCount} 件選択中"
        : string.Empty;

    public string ArtworkTargetSummaryText => HasMultipleSelectedTracks
        ? $"選択中 {_selectedTrackCount} 件（追加・全削除・同期が一括適用）"
        : string.Empty;

    public string ArtworkDropOverlayText => HasMultipleSelectedTracks
        ? $"ここにドロップして {_selectedTrackCount} 件に追加"
        : SingleArtworkDropOverlayText;

    public string SyncArtworksButtonText => HasMultipleSelectedTracks
        ? $"選択中 {_selectedTrackCount} 件へ同期"
        : string.Empty;

    public bool CanEditSingleTrackArtworkDetails => !IsBusy && HasSelectedTrack && !HasMultipleSelectedTracks;

    public bool HasMixedArtworkInSelection => _hasMixedArtworkInSelection;

    public string MixedArtworkNoticeText => _mixedArtworkNoticeText;

    public bool HasDirtyTracks => DirtyTrackCount > 0;

    public bool ShouldInterceptClose => IsSaving || HasDirtyTracks;

    public string DirtyCountText => HasDirtyTracks ? $"未保存: {DirtyTrackCount} 件" : string.Empty;

    public string TrackCountText => SearchReplace.IsFilterActive
        ? $"{DisplayedTracks.Count} / {Tracks.Count} トラック（フィルタ中）"
        : $"{Tracks.Count} トラック";

    private bool CanOpen => !IsBusy;

    private bool CanSaveSelected => !IsBusy && _hasAnySelectedValidDirtyTrack;

    private bool CanSaveAll => !IsBusy && Tracks.Any(t => t.IsDirty && !t.HasValidationErrors);

    private bool CanRevertSelected => !IsBusy && _hasAnySelectedDirtyTrack;

    private bool CanRevertAll => !IsBusy && HasDirtyTracks;

    private bool CanEditArtwork => !IsBusy && _selectedTrackCount > 0;

    private bool CanEditSingleArtworkSlot =>
        !IsBusy && !HasMultipleSelectedTracks && SelectedTrack is { HasSelectedArtwork: true };

    private bool CanClearAllArtworks => !IsBusy && _hasAnySelectedArtworkTrack;

    private bool CanSyncArtworksToSelection =>
        !IsBusy && HasMultipleSelectedTracks && SelectedTrack is not null;

    private bool CanSelectPreviousArtwork => SelectedTrack is { CanSelectPreviousArtwork: true };

    private bool CanSelectNextArtwork => SelectedTrack is { CanSelectNextArtwork: true };

    private bool CanMoveArtworkLeft =>
        !IsBusy && !HasMultipleSelectedTracks && SelectedTrack is { CanMoveSelectedArtworkLeft: true };

    private bool CanMoveArtworkRight =>
        !IsBusy && !HasMultipleSelectedTracks && SelectedTrack is { CanMoveSelectedArtworkRight: true };

    private bool CanPasteTsv => !IsBusy && HasTracks;

    private bool CanClearTracks => !IsBusy && HasTracks;

    public async Task<bool> ConfirmCloseAsync()
    {
        if (IsSaving)
        {
            StatusMessage = CloseWhileSavingMessage;
            return false;
        }

        if (!HasDirtyTracks)
        {
            CancelNonSavingOperations();
            return true;
        }

        var canProceed = await ConfirmUnsavedChangesBeforeActionAsync(UnsavedChangesPromptReason.ApplicationClose);
        if (!canProceed)
        {
            return false;
        }

        CancelNonSavingOperations();
        return true;
    }

    /// <summary>
    /// アプリ終了時に未完了の設定・独自タグ定義保存を同期的にフラッシュする。
    /// </summary>
    public void FlushPendingSavesSync()
    {
        ColumnSettings.FlushPendingSaveSync();
        DetailPane.FlushPendingSaveSync();
        CustomTagCatalog.FlushPendingSaveSync();
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenCustomTagManagerAsync()
    {
        if (_dialogService is null || IsBusy)
        {
            return;
        }

        var currentSnapshot = CustomTagCatalog.GetSnapshot();
        var updatedDefinitions = await _dialogService.ShowCustomTagManagerAsync(currentSnapshot);
        if (updatedDefinitions is null)
        {
            return;
        }

        CustomTagCatalog.ApplyDefinitions(updatedDefinitions);
        StatusMessage = $"独自タグ定義を更新しました（{CustomTagCatalog.Definitions.Count} 件）";
    }

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

        if (!await ConfirmUnsavedChangesBeforeActionAsync(UnsavedChangesPromptReason.ReplaceTracks))
        {
            return;
        }

        await LoadCoreAsync(
            ct => _flacTagService.LoadTracksFromFolderAsync(folderPath, includeSubdirectories: true, ct),
            folderPath,
            TrackLoadMode.Replace);
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task AppendFolderAsync()
    {
        if (_fileDialogService is null || _flacTagService is null)
        {
            return;
        }

        var folderPath = await _fileDialogService.OpenFolderAsync(AppendFolderDialogTitle);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        await LoadCoreAsync(
            ct => _flacTagService.LoadTracksFromFolderAsync(folderPath, includeSubdirectories: true, ct),
            folderPath,
            TrackLoadMode.Append);
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

        if (!await ConfirmUnsavedChangesBeforeActionAsync(UnsavedChangesPromptReason.ReplaceTracks))
        {
            return;
        }

        var sourceLabel = BuildFileSelectionSourceLabel(filePaths);

        await LoadCoreAsync(
            ct => _flacTagService.LoadTracksFromFilesAsync(filePaths, ct),
            sourceLabel,
            TrackLoadMode.Replace);
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task AppendFilesAsync()
    {
        if (_fileDialogService is null || _flacTagService is null)
        {
            return;
        }

        var filePaths = await _fileDialogService.OpenFlacFilesAsync(AppendFilesDialogTitle);
        if (filePaths.Count == 0)
        {
            return;
        }

        var sourceLabel = BuildFileSelectionSourceLabel(filePaths);

        await LoadCoreAsync(
            ct => _flacTagService.LoadTracksFromFilesAsync(filePaths, ct),
            sourceLabel,
            TrackLoadMode.Append);
    }

    [RelayCommand(CanExecute = nameof(CanClearTracks))]
    private async Task ClearTrackListAsync()
    {
        if (!HasTracks || IsBusy)
        {
            return;
        }

        if (!await ConfirmUnsavedChangesBeforeActionAsync(UnsavedChangesPromptReason.ClearTracks))
        {
            return;
        }

        SearchReplace.ResetForClearedTracks();
        ClearTracks();
        CurrentSourcePath = string.Empty;
        OnPropertyChanged(nameof(HasTracks));
        RefreshDirtyState();
        StatusMessage = ClearedTracksStatusMessage;
    }

    [RelayCommand(CanExecute = nameof(CanEditArtwork))]
    private async Task AddArtworkAsync()
    {
        var targets = GetSelectedTracksSnapshot();
        if (_fileDialogService is null || _artworkService is null || targets.Count == 0)
        {
            return;
        }

        var imagePaths = await _fileDialogService.OpenImageFilesAsync(AddArtworkDialogTitle);
        if (imagePaths.Count == 0)
        {
            return;
        }

        await AddArtworksFromFilesCoreAsync(targets, imagePaths);
    }

    [RelayCommand(CanExecute = nameof(CanEditSingleArtworkSlot))]
    private async Task ReplaceArtworkAsync()
    {
        if (_fileDialogService is null || _artworkService is null || HasMultipleSelectedTracks)
        {
            return;
        }

        var target = SelectedTrack;
        if (target is null || !target.HasSelectedArtwork)
        {
            return;
        }

        var imagePath = await _fileDialogService.OpenImageFileAsync(ReplaceArtworkDialogTitle);
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return;
        }

        await ReplaceSingleArtworkCoreAsync(
            target,
            ct => _artworkService.LoadFromFileAsync(imagePath, ct),
            DefaultArtworkLoadingMessage);
    }

    [RelayCommand(CanExecute = nameof(CanEditSingleArtworkSlot))]
    private void RemoveSelectedArtwork()
    {
        if (HasMultipleSelectedTracks || SelectedTrack is not { } target)
        {
            return;
        }

        if (!target.RemoveSelectedArtwork())
        {
            return;
        }

        RefreshDirtyState();
        StatusMessage = $"{target.FileName} のアートワークを 1 枚削除しました（残り {target.ArtworkCount} 枚・未保存）";
    }

    [RelayCommand(CanExecute = nameof(CanClearAllArtworks))]
    private void ClearAllArtworks()
    {
        var targets = GetSelectedTracksSnapshot();
        var removableTargets = targets.Where(t => t.HasArtworkData).ToList();
        if (removableTargets.Count == 0)
        {
            return;
        }

        _isBatchUpdatingTracks = true;
        try
        {
            foreach (var target in removableTargets)
            {
                target.ClearArtworks();
            }
        }
        finally
        {
            _isBatchUpdatingTracks = false;
        }

        RefreshDirtyState();
        var skippedCount = targets.Count - removableTargets.Count;
        if (targets.Count == 1)
        {
            StatusMessage = $"{removableTargets[0].FileName} のアートワークをすべて削除しました（未保存）";
        }
        else if (skippedCount > 0)
        {
            StatusMessage = $"{removableTargets.Count} 件のトラックからアートワークを全削除しました（アートワークなし {skippedCount} 件・未保存）";
        }
        else
        {
            StatusMessage = $"{removableTargets.Count} 件のトラックからアートワークを全削除しました（未保存）";
        }
    }

    [RelayCommand(CanExecute = nameof(CanSyncArtworksToSelection))]
    private void SyncArtworksToSelection()
    {
        var targets = GetSelectedTracksSnapshot();
        if (targets.Count <= 1 || SelectedTrack is not { } sourceTrack || !targets.Contains(sourceTrack))
        {
            return;
        }

        var snapshot = sourceTrack.GetEditedArtworksSnapshot();
        var updatedCount = 0;

        _isBatchUpdatingTracks = true;
        try
        {
            foreach (var target in targets)
            {
                if (ReferenceEquals(target, sourceTrack) || sourceTrack.HasSameArtworkAs(target))
                {
                    continue;
                }

                target.ReplaceAllArtworks(snapshot);
                updatedCount++;
            }
        }
        finally
        {
            _isBatchUpdatingTracks = false;
        }

        RefreshDirtyState();
        if (updatedCount == 0)
        {
            StatusMessage = $"選択中の {targets.Count} 件はすでに同じアートワーク構成です";
        }
        else if (snapshot.Count == 0)
        {
            StatusMessage = $"{updatedCount} 件のトラックのアートワーク構成を「なし」に同期しました（未保存）";
        }
        else
        {
            StatusMessage = $"{updatedCount} 件のトラックへアートワーク構成（{snapshot.Count} 枚）を同期しました（未保存）";
        }
    }

    [RelayCommand(CanExecute = nameof(CanSelectPreviousArtwork))]
    private void SelectPreviousArtwork()
    {
        SelectedTrack?.SelectPreviousArtwork();
        NotifyArtworkCommandStates();
    }

    [RelayCommand(CanExecute = nameof(CanSelectNextArtwork))]
    private void SelectNextArtwork()
    {
        SelectedTrack?.SelectNextArtwork();
        NotifyArtworkCommandStates();
    }

    [RelayCommand(CanExecute = nameof(CanMoveArtworkLeft))]
    private void MoveArtworkLeft()
    {
        if (HasMultipleSelectedTracks || SelectedTrack is not { } target)
        {
            return;
        }

        if (!target.MoveSelectedArtwork(-1))
        {
            return;
        }

        RefreshDirtyState();
        StatusMessage = $"{target.FileName} のアートワーク順序を前へ移動しました（{target.SelectedArtworkPositionText}・未保存）";
    }

    [RelayCommand(CanExecute = nameof(CanMoveArtworkRight))]
    private void MoveArtworkRight()
    {
        if (HasMultipleSelectedTracks || SelectedTrack is not { } target)
        {
            return;
        }

        if (!target.MoveSelectedArtwork(1))
        {
            return;
        }

        RefreshDirtyState();
        StatusMessage = $"{target.FileName} のアートワーク順序を後ろへ移動しました（{target.SelectedArtworkPositionText}・未保存）";
    }

    [RelayCommand(CanExecute = nameof(CanEditArtwork))]
    private async Task DropArtworkAsync(ArtworkDropPayload? payload)
    {
        var targets = GetSelectedTracksSnapshot();
        if (_artworkService is null || targets.Count == 0)
        {
            return;
        }

        if (payload is null || !payload.HasAnyData)
        {
            StatusMessage = "ドロップされたデータから画像を取得できませんでした";
            return;
        }

        await AddSingleArtworkCoreAsync(
            targets,
            ct => _artworkService.ResolveFromDropAsync(payload, ct),
            targets.Count == 1
                ? DropArtworkLoadingMessage
                : $"ドロップされたアートワークを取得中（対象 {targets.Count} 件）...");
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
        var dirtySelectedTracks = GetSelectedTracksSnapshot()
            .Where(t => t.IsDirty)
            .ToList();
        if (dirtySelectedTracks.Count == 0)
        {
            return;
        }

        if (dirtySelectedTracks.Count == 1 && dirtySelectedTracks[0].HasValidationErrors)
        {
            StatusMessage = $"保存エラー ({dirtySelectedTracks[0].FileName}): {dirtySelectedTracks[0].ValidationErrorMessage}";
            return;
        }

        await SaveTracksCoreAsync(
            dirtySelectedTracks,
            useSingleFileNameStatus: dirtySelectedTracks.Count == 1);
    }

    [RelayCommand(CanExecute = nameof(CanSaveAll))]
    private async Task SaveAllAsync()
    {
        await SaveAllCoreAsync();
    }

    private async Task LoadInitialSettingsAsync(CancellationToken cancellationToken)
    {
        await ColumnSettings.LoadAsync(cancellationToken);
        await DetailPane.LoadAsync(cancellationToken);
        await CustomTagCatalog.LoadAsync(cancellationToken);
    }

    private void OnCustomTagDefinitionsChanged(object? sender, EventArgs e)
    {
        if (_disposed || Tracks.Count == 0)
        {
            return;
        }

        _isBatchUpdatingTracks = true;
        try
        {
            foreach (var track in Tracks)
            {
                track.RefreshCustomTagDefinitions();
            }
        }
        finally
        {
            _isBatchUpdatingTracks = false;
        }

        RefreshDirtyState();
        if (SearchReplace.IsOpen && !SearchReplace.IsExecutingReplace)
        {
            SearchReplace.RefreshResults(notifyFilter: true);
        }
    }

    private async Task<bool> SaveAllCoreAsync()
    {
        var dirtyTracks = Tracks.Where(t => t.IsDirty).ToList();
        if (dirtyTracks.Count == 0)
        {
            return true;
        }

        var succeeded = await SaveTracksCoreAsync(dirtyTracks, useSingleFileNameStatus: false);
        return succeeded && !HasDirtyTracks;
    }

    private async Task<bool> SaveTracksCoreAsync(
        IReadOnlyList<TrackItemViewModel> targetTracks,
        bool useSingleFileNameStatus)
    {
        if (_flacTagService is null)
        {
            return false;
        }

        if (IsBusy)
        {
            StatusMessage = SaveWhileBusyMessage;
            return false;
        }

        if (targetTracks.Count == 0)
        {
            return true;
        }

        _saveCts?.Dispose();
        _saveCts = new CancellationTokenSource();
        var cancellationToken = _saveCts.Token;

        IsSaving = true;
        StatusMessage = useSingleFileNameStatus
            ? $"{targetTracks[0].FileName} を保存中..."
            : $"{targetTracks.Count} 件のFLACファイルを保存中...";

        try
        {
            var editedTracks = targetTracks.Select(t => t.BuildEditedTrack()).ToList();
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

            _isBatchUpdatingTracks = true;
            try
            {
                foreach (var item in targetTracks)
                {
                    if (savedByOriginalPath.TryGetValue(item.FilePath, out var savedTrack))
                    {
                        item.AcceptChanges(savedTrack);
                    }
                }
            }
            finally
            {
                _isBatchUpdatingTracks = false;
            }

            RefreshDirtyState();
            if (SearchReplace.IsOpen)
            {
                SearchReplace.RefreshResults(notifyFilter: true);
            }

            if (useSingleFileNameStatus)
            {
                StatusMessage = result.Errors.Count > 0
                    ? $"保存エラー ({targetTracks[0].FileName}): {result.Errors[0].Message}"
                    : $"{targetTracks[0].FileName} を保存しました";
            }
            else
            {
                StatusMessage = BuildSaveStatusMessage(result);
            }

            return result.Errors.Count == 0 && targetTracks.All(t => !t.IsDirty);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "保存をキャンセルしました";
            return false;
        }
        catch (Exception ex)
        {
            StatusMessage = useSingleFileNameStatus
                ? $"保存エラー ({targetTracks[0].FileName}): {ex.Message}"
                : $"一括保存エラー: {ex.Message}";
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRevertSelected))]
    private void RevertSelected()
    {
        var dirtySelectedTracks = GetSelectedTracksSnapshot()
            .Where(t => t.IsDirty)
            .ToList();
        if (dirtySelectedTracks.Count == 0)
        {
            return;
        }

        _isBatchUpdatingTracks = true;
        try
        {
            foreach (var item in dirtySelectedTracks)
            {
                item.RevertChanges();
            }
        }
        finally
        {
            _isBatchUpdatingTracks = false;
        }

        RefreshDirtyState();
        if (SearchReplace.IsOpen)
        {
            SearchReplace.RefreshResults(notifyFilter: true);
        }

        StatusMessage = dirtySelectedTracks.Count == 1
            ? $"{dirtySelectedTracks[0].FileName} の変更を破棄しました"
            : $"選択中の {dirtySelectedTracks.Count} 件の変更を破棄しました";
    }

    [RelayCommand(CanExecute = nameof(CanRevertAll))]
    private void RevertAll()
    {
        var dirtyTracks = Tracks.Where(t => t.IsDirty).ToList();
        if (dirtyTracks.Count == 0)
        {
            return;
        }

        _isBatchUpdatingTracks = true;
        try
        {
            foreach (var item in dirtyTracks)
            {
                item.RevertChanges();
            }
        }
        finally
        {
            _isBatchUpdatingTracks = false;
        }

        RefreshDirtyState();
        if (SearchReplace.IsOpen)
        {
            SearchReplace.RefreshResults(notifyFilter: true);
        }

        StatusMessage = $"{dirtyTracks.Count} 件の変更を破棄しました";
    }

    private async Task<bool> ConfirmUnsavedChangesBeforeActionAsync(UnsavedChangesPromptReason reason)
    {
        if (!HasDirtyTracks || _dialogService is null)
        {
            return true;
        }

        var invalidDirtyCount = Tracks.Count(t => t.IsDirty && t.HasValidationErrors);
        var canSaveAll = invalidDirtyCount == 0 && !IsBusy;

        string? warningMessage = null;
        if (invalidDirtyCount > 0)
        {
            warningMessage = $"入力エラー（予約キー・ファイル名重複など）がある未保存トラックが {invalidDirtyCount} 件あるため、そのまま保存できません。エラーを修正するか、変更を破棄して続行してください。";
        }
        else if (IsBusy)
        {
            warningMessage = "他の処理が実行中のため、いま保存することはできません。処理完了を待つか、変更を破棄して続行してください。";
        }

        var context = reason switch
        {
            UnsavedChangesPromptReason.ApplicationClose => new UnsavedChangesPromptContext(
                Title: "未保存の変更があります",
                Message: $"未保存の変更が {DirtyTrackCount} 件あります。アプリケーションを終了する前に保存しますか？",
                WarningMessage: warningMessage,
                SaveButtonText: "保存して終了",
                DiscardButtonText: "破棄して終了",
                CancelButtonText: "キャンセル",
                CanSave: canSaveAll),
            UnsavedChangesPromptReason.ClearTracks => new UnsavedChangesPromptContext(
                Title: "未保存の変更があります",
                Message: $"未保存の変更が {DirtyTrackCount} 件あります。一覧をクリアする前に保存しますか？",
                WarningMessage: warningMessage,
                SaveButtonText: "保存してクリア",
                DiscardButtonText: "破棄してクリア",
                CancelButtonText: "キャンセル",
                CanSave: canSaveAll),
            _ => new UnsavedChangesPromptContext(
                Title: "未保存の変更があります",
                Message: $"未保存の変更が {DirtyTrackCount} 件あります。一覧を置き換えて読み込み直す前に保存しますか？",
                WarningMessage: warningMessage,
                SaveButtonText: "保存して開く",
                DiscardButtonText: "破棄して開く",
                CancelButtonText: "キャンセル",
                CanSave: canSaveAll)
        };

        var action = await _dialogService.ConfirmUnsavedChangesAsync(context);
        return action switch
        {
            UnsavedChangesAction.SaveAndContinue => await SaveAllCoreAsync(),
            UnsavedChangesAction.DiscardAndContinue => true,
            _ => false
        };
    }

    private async Task AddSingleArtworkCoreAsync(
        IReadOnlyList<TrackItemViewModel> targets,
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
            _isBatchUpdatingTracks = true;
            try
            {
                foreach (var target in targets)
                {
                    target.AddArtwork(artwork.Data, artwork.MimeType);
                }
            }
            finally
            {
                _isBatchUpdatingTracks = false;
            }

            RefreshDirtyState();
            StatusMessage = BuildArtworkAppliedMessage(targets, artwork, isReplace: false);
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

    private async Task AddArtworksFromFilesCoreAsync(
        IReadOnlyList<TrackItemViewModel> targets,
        IReadOnlyList<string> imagePaths)
    {
        if (_artworkService is null)
        {
            return;
        }

        _artworkCts?.Cancel();
        _artworkCts?.Dispose();
        _artworkCts = new CancellationTokenSource();
        var cancellationToken = _artworkCts.Token;

        IsArtworkLoading = true;
        StatusMessage = targets.Count == 1
            ? DefaultArtworkLoadingMessage
            : $"{targets.Count} 件のトラック用アートワークを読み込み中...";

        try
        {
            var loadedImages = new List<ArtworkImage>(imagePaths.Count);
            var errors = new List<string>();

            foreach (var imagePath in imagePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    loadedImages.Add(await _artworkService.LoadFromFileAsync(imagePath, cancellationToken));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{Path.GetFileName(imagePath)}: {ex.Message}");
                }
            }

            if (loadedImages.Count == 0)
            {
                StatusMessage = errors.Count > 0
                    ? $"アートワーク取得エラー: {errors[0]}"
                    : "有効なアートワーク画像を読み込めませんでした";
                return;
            }

            _isBatchUpdatingTracks = true;
            try
            {
                foreach (var target in targets)
                {
                    foreach (var image in loadedImages)
                    {
                        target.AddArtwork(image.Data, image.MimeType);
                    }
                }
            }
            finally
            {
                _isBatchUpdatingTracks = false;
            }

            RefreshDirtyState();
            StatusMessage = loadedImages.Count == 1 && errors.Count == 0
                ? BuildArtworkAppliedMessage(targets, loadedImages[0], isReplace: false)
                : BuildMultiArtworkAddedMessage(targets, loadedImages.Count, errors.Count);
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

    private async Task ReplaceSingleArtworkCoreAsync(
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
            target.ReplaceSelectedArtwork(artwork.Data, artwork.MimeType);
            RefreshDirtyState();
            StatusMessage = BuildArtworkAppliedMessage([target], artwork, isReplace: true);
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
        string sourceDescription,
        TrackLoadMode loadMode)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var cancellationToken = _loadCts.Token;

        IsLoading = true;
        StatusMessage = loadMode == TrackLoadMode.Append
            ? "FLACファイルを追加読み込み中..."
            : "FLACファイルを読み込み中...";

        try
        {
            var result = await loadOperation(cancellationToken);
            if (loadMode == TrackLoadMode.Append)
            {
                AppendTracks(result, sourceDescription);
            }
            else
            {
                ReplaceTracks(result, sourceDescription);
            }
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
    /// 現在のトラック一覧を新しい読み込み結果で置き換える。
    /// </summary>
    private void ReplaceTracks(TrackLoadResult result, string sourceDescription)
    {
        ClearTracks();

        var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in result.Tracks)
        {
            if (!existingPaths.Add(NormalizeFilePath(track.FilePath)))
            {
                continue;
            }

            var item = new TrackItemViewModel(track, CustomTagCatalog);
            AttachTrack(item);
            Tracks.Add(item);
        }

        CurrentSourcePath = sourceDescription;

        OnPropertyChanged(nameof(HasTracks));
        SearchReplace.RefreshResults(notifyFilter: false);
        SyncDisplayedTracks();
        var firstTrack = DisplayedTracks.FirstOrDefault();
        SelectedTrack = firstTrack;
        ReplaceSelectedTracks(firstTrack is not null ? [firstTrack] : []);
        RefreshDirtyState();

        StatusMessage = BuildLoadStatusMessage(result);
    }

    /// <summary>
    /// 現在のトラック一覧の末尾へ、重複ファイルを除外して新しい読み込み結果を追加する。
    /// </summary>
    private void AppendTracks(TrackLoadResult result, string sourceDescription)
    {
        var wasEmpty = Tracks.Count == 0;
        var existingPaths = new HashSet<string>(
            Tracks.Select(t => NormalizeFilePath(t.FilePath)),
            StringComparer.OrdinalIgnoreCase);

        var addedCount = 0;
        var skippedDuplicateCount = 0;

        foreach (var track in result.Tracks)
        {
            var normalizedPath = NormalizeFilePath(track.FilePath);
            if (!existingPaths.Add(normalizedPath))
            {
                skippedDuplicateCount++;
                continue;
            }

            var item = new TrackItemViewModel(track, CustomTagCatalog);
            AttachTrack(item);
            Tracks.Add(item);
            addedCount++;
        }

        if (addedCount > 0)
        {
            CurrentSourcePath = ResolveAppendedSourcePath(wasEmpty, CurrentSourcePath, sourceDescription, Tracks.Count);
        }

        OnPropertyChanged(nameof(HasTracks));
        SearchReplace.RefreshResults(notifyFilter: false);
        SyncDisplayedTracks();

        if (SelectedTrack is null)
        {
            var firstTrack = DisplayedTracks.FirstOrDefault();
            SelectedTrack = firstTrack;
            ReplaceSelectedTracks(firstTrack is not null ? [firstTrack] : []);
        }

        RefreshDirtyState();
        StatusMessage = BuildAppendStatusMessage(addedCount, skippedDuplicateCount, result.Errors.Count);
    }

    private void ClearTracks()
    {
        ReplaceSelectedTracks([]);
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

    private void CancelNonSavingOperations()
    {
        _loadCts?.Cancel();
        _artworkCts?.Cancel();
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
        if (_isUpdatingExternalValidation || _isBatchUpdatingTracks)
        {
            return;
        }

        if (string.Equals(e.PropertyName, nameof(TrackItemViewModel.IsDirty), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(TrackItemViewModel.HasValidationErrors), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(TrackItemViewModel.HasArtwork), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(TrackItemViewModel.HasArtworkData), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(TrackItemViewModel.ArtworkCount), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(TrackItemViewModel.ArtworkInfoDisplay), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(TrackItemViewModel.ArtworkBitmap), StringComparison.Ordinal))
        {
            RefreshDirtyState();
        }
        else if (string.Equals(e.PropertyName, nameof(TrackItemViewModel.SelectedArtwork), StringComparison.Ordinal))
        {
            NotifyArtworkCommandStates();
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
        UpdateSelectionDerivedState();
        SearchReplace.NotifyCommandStates();
    }

    private void OnSelectedTracksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isUpdatingSelectedTracks)
        {
            return;
        }

        UpdateSelectionDerivedState();
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
        SaveAllCommand.NotifyCanExecuteChanged();
        RevertAllCommand.NotifyCanExecuteChanged();
        PasteTsvCommand.NotifyCanExecuteChanged();
        ClearTrackListCommand.NotifyCanExecuteChanged();
        UpdateSelectionDerivedState();
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
        ReplaceSelectedTracks([track]);
    }

    private List<TrackItemViewModel> GetSelectedTracksSnapshot()
    {
        if (Tracks.Count == 0)
        {
            return [];
        }

        var loadedTracks = new HashSet<TrackItemViewModel>(Tracks);
        if (SelectedTracks.Count > 0)
        {
            var result = new List<TrackItemViewModel>(SelectedTracks.Count);
            var seen = new HashSet<TrackItemViewModel>();
            foreach (var track in SelectedTracks)
            {
                if (track is not null && loadedTracks.Contains(track) && seen.Add(track))
                {
                    result.Add(track);
                }
            }

            if (result.Count > 0)
            {
                return result;
            }
        }

        if (SelectedTrack is { } primary && loadedTracks.Contains(primary))
        {
            return [primary];
        }

        return [];
    }

    private void ReplaceSelectedTracks(IReadOnlyList<TrackItemViewModel> desiredTracks)
    {
        var uniqueDesired = new List<TrackItemViewModel>(desiredTracks.Count);
        var desiredSet = new HashSet<TrackItemViewModel>();
        foreach (var track in desiredTracks)
        {
            if (track is not null && desiredSet.Add(track))
            {
                uniqueDesired.Add(track);
            }
        }

        if (SelectedTracks.Count == uniqueDesired.Count
            && SelectedTracks.All(desiredSet.Contains))
        {
            UpdateSelectionDerivedState();
            return;
        }

        _isUpdatingSelectedTracks = true;
        try
        {
            SelectedTracks.ReplaceAll(uniqueDesired);
        }
        finally
        {
            _isUpdatingSelectedTracks = false;
        }

        UpdateSelectionDerivedState();
    }

    private void UpdateSelectionDerivedState()
    {
        var selectedTracks = GetSelectedTracksSnapshot();
        var newCount = selectedTracks.Count;
        var hasDirty = false;
        var hasValidDirty = false;
        var hasArtwork = false;
        var withArtworkCount = 0;
        var hasMixedArtwork = false;
        var referenceTrack = SelectedTrack is { } primary && selectedTracks.Contains(primary)
            ? primary
            : selectedTracks.FirstOrDefault();

        foreach (var track in selectedTracks)
        {
            if (track.IsDirty)
            {
                hasDirty = true;
                if (!track.HasValidationErrors)
                {
                    hasValidDirty = true;
                }
            }

            if (track.HasArtworkData)
            {
                hasArtwork = true;
                withArtworkCount++;
            }

            if (!hasMixedArtwork && referenceTrack is not null && !referenceTrack.HasSameArtworkAs(track))
            {
                hasMixedArtwork = true;
            }
        }

        var newMixedNotice = hasMixedArtwork
            ? $"※ 選択中の {newCount} 件に異なるアートワークが含まれます（設定済み: {withArtworkCount} 件）"
            : string.Empty;

        var countChanged = _selectedTrackCount != newCount;
        var mixedChanged = _hasMixedArtworkInSelection != hasMixedArtwork;
        var mixedNoticeChanged = !string.Equals(_mixedArtworkNoticeText, newMixedNotice, StringComparison.Ordinal);

        _selectedTrackCount = newCount;
        _hasAnySelectedDirtyTrack = hasDirty;
        _hasAnySelectedValidDirtyTrack = hasValidDirty;
        _hasAnySelectedArtworkTrack = hasArtwork;
        _hasMixedArtworkInSelection = hasMixedArtwork;
        _mixedArtworkNoticeText = newMixedNotice;

        if (countChanged)
        {
            OnPropertyChanged(nameof(SelectedTrackCount));
            OnPropertyChanged(nameof(HasMultipleSelectedTracks));
            OnPropertyChanged(nameof(SelectedCountText));
            OnPropertyChanged(nameof(ArtworkTargetSummaryText));
            OnPropertyChanged(nameof(ArtworkDropOverlayText));
            OnPropertyChanged(nameof(SyncArtworksButtonText));
            OnPropertyChanged(nameof(CanEditSingleTrackArtworkDetails));
        }

        if (mixedChanged)
        {
            OnPropertyChanged(nameof(HasMixedArtworkInSelection));
        }

        if (mixedNoticeChanged)
        {
            OnPropertyChanged(nameof(MixedArtworkNoticeText));
        }

        SaveSelectedCommand.NotifyCanExecuteChanged();
        RevertSelectedCommand.NotifyCanExecuteChanged();
        NotifyArtworkCommandStates();
    }

    private void NotifyArtworkCommandStates()
    {
        AddArtworkCommand.NotifyCanExecuteChanged();
        ReplaceArtworkCommand.NotifyCanExecuteChanged();
        RemoveSelectedArtworkCommand.NotifyCanExecuteChanged();
        ClearAllArtworksCommand.NotifyCanExecuteChanged();
        SyncArtworksToSelectionCommand.NotifyCanExecuteChanged();
        DropArtworkCommand.NotifyCanExecuteChanged();
        SelectPreviousArtworkCommand.NotifyCanExecuteChanged();
        SelectNextArtworkCommand.NotifyCanExecuteChanged();
        MoveArtworkLeftCommand.NotifyCanExecuteChanged();
        MoveArtworkRightCommand.NotifyCanExecuteChanged();
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
            var previousSelectedTracks = SelectedTracks.ToList();

            DisplayedTracks.Clear();
            foreach (var item in desiredTracks)
            {
                DisplayedTracks.Add(item);
            }

            var displayedSet = new HashSet<TrackItemViewModel>(DisplayedTracks);
            var retainedSelection = previousSelectedTracks
                .Where(displayedSet.Contains)
                .ToList();

            if (previousSelected is not null && displayedSet.Contains(previousSelected))
            {
                SelectedTrack = previousSelected;
            }
            else
            {
                SelectedTrack = retainedSelection.FirstOrDefault() ?? DisplayedTracks.FirstOrDefault();
            }

            if (retainedSelection.Count == 0 && SelectedTrack is not null)
            {
                retainedSelection.Add(SelectedTrack);
            }

            ReplaceSelectedTracks(retainedSelection);
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

    private static string NormalizeFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(filePath);
        }
        catch
        {
            return filePath;
        }
    }

    private static string BuildFileSelectionSourceLabel(IReadOnlyList<string> filePaths)
    {
        return filePaths.Count == 1
            ? filePaths[0]
            : $"{Path.GetDirectoryName(filePaths[0])} ({filePaths.Count} ファイル選択)";
    }

    private static string ResolveAppendedSourcePath(
        bool wasEmpty,
        string currentSourcePath,
        string newSourceDescription,
        int totalTrackCount)
    {
        if (wasEmpty || string.IsNullOrWhiteSpace(currentSourcePath))
        {
            return newSourceDescription;
        }

        if (string.Equals(currentSourcePath, newSourceDescription, StringComparison.OrdinalIgnoreCase))
        {
            return currentSourcePath;
        }

        return $"複数ソース（合計 {totalTrackCount} 件）";
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
            var visibleColumns = ColumnSettings.GetVisibleOrderedColumns();
            orderedColumns = visibleColumns;
            startColumnIndex = 0;
            for (var i = 0; i < visibleColumns.Count; i++)
            {
                if (visibleColumns[i].IsEditable())
                {
                    startColumnIndex = i;
                    break;
                }
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

    private static string BuildArtworkAppliedMessage(
        IReadOnlyList<TrackItemViewModel> targets,
        ArtworkImage artwork,
        bool isReplace)
    {
        var targetPrefix = targets.Count == 1
            ? $"{targets[0].FileName} に"
            : $"{targets.Count} 件のトラックに";

        var actionText = isReplace ? "アートワークを差し替えました" : "アートワークを追加しました";

        if (artwork.WasConverted && !string.IsNullOrWhiteSpace(artwork.OriginalFormatName))
        {
            return $"{targetPrefix}{actionText}（{artwork.OriginalFormatName} → {artwork.MimeType} に変換・未保存）";
        }

        return $"{targetPrefix}{actionText}（{artwork.MimeType}・未保存）";
    }

    private static string BuildMultiArtworkAddedMessage(
        IReadOnlyList<TrackItemViewModel> targets,
        int addedImageCount,
        int errorCount)
    {
        var targetPrefix = targets.Count == 1
            ? $"{targets[0].FileName} に"
            : $"{targets.Count} 件のトラックに";

        if (errorCount > 0)
        {
            return $"{targetPrefix}アートワークを {addedImageCount} 枚追加しました（{errorCount} 件エラー・未保存）";
        }

        return $"{targetPrefix}アートワークを {addedImageCount} 枚追加しました（未保存）";
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

    private static string BuildAppendStatusMessage(int addedCount, int skippedDuplicateCount, int errorCount)
    {
        if (addedCount == 0 && skippedDuplicateCount == 0 && errorCount == 0)
        {
            return "追加対象のFLACファイルが見つかりませんでした";
        }

        var notes = new List<string>(2);
        if (skippedDuplicateCount > 0)
        {
            notes.Add($"重複 {skippedDuplicateCount} 件スキップ");
        }

        if (errorCount > 0)
        {
            notes.Add($"{errorCount} 件エラー");
        }

        if (addedCount == 0)
        {
            if (skippedDuplicateCount > 0 && errorCount == 0)
            {
                return $"選択されたファイルはすべて読み込み済みです（重複 {skippedDuplicateCount} 件スキップ）";
            }

            return $"追加されたFLACファイルはありません（{string.Join("・", notes)}）";
        }

        if (notes.Count > 0)
        {
            return $"{addedCount} 件のFLACファイルを追加しました（{string.Join("・", notes)}）";
        }

        return $"{addedCount} 件のFLACファイルを追加しました";
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
        CustomTagCatalog.DefinitionsChanged -= OnCustomTagDefinitionsChanged;
        SelectedTracks.CollectionChanged -= OnSelectedTracksCollectionChanged;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _saveCts = null;

        _artworkCts?.Cancel();
        _artworkCts?.Dispose();
        _artworkCts = null;

        _settingsLoadCts?.Cancel();
        _settingsLoadCts?.Dispose();
        _settingsLoadCts = null;

        ColumnSettings.Dispose();
        DetailPane.Dispose();
        CustomTagCatalog.Dispose();

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
