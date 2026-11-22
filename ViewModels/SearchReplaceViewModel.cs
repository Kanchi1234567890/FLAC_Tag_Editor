using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FLAC_Tag_Editor.Models;
using FLAC_Tag_Editor.Services.Interfaces;

namespace FLAC_Tag_Editor.ViewModels;

/// <summary>
/// 検索・一括置換バーおよび置換一覧プレビューの状態・コマンドを管理する ViewModel。
/// </summary>
public sealed partial class SearchReplaceViewModel : ViewModelBase
{
    private const string DuplicateFileNameWarning = "同一フォルダ内に同名のファイルが重複します。";
    private const string ExistingFileConflictWarning = "同名の別ファイルがフォルダ内に既に存在します。";
    private const string RegexTimeoutErrorMessage = "正規表現の検索がタイムアウトしました。パターンを見直してください。";
    private const string CustomTagAllDisplayName = "独自タグ (すべて)";
    private const string CustomTagOptionPrefix = "独自タグ: ";

    private static readonly SearchReplaceTargetOption[] FixedTargetOptions =
    [
        new(SearchReplaceTarget.All, "すべて"),
        new(SearchReplaceTarget.FileName, "ファイル名"),
        new(SearchReplaceTarget.Title, "タイトル"),
        new(SearchReplaceTarget.Artist, "アーティスト"),
        new(SearchReplaceTarget.Album, "アルバム"),
        new(SearchReplaceTarget.AlbumArtist, "アルバムアーティスト"),
        new(SearchReplaceTarget.Genre, "ジャンル"),
        new(SearchReplaceTarget.Comment, "コメント"),
        new(SearchReplaceTarget.CustomTags, CustomTagAllDisplayName)
    ];

    private readonly ITextSearchReplaceService _searchReplaceService;
    private readonly Func<IReadOnlyList<TrackItemViewModel>> _getAllTracks;
    private readonly Func<IReadOnlyList<TrackItemViewModel>> _getDisplayedTracks;
    private readonly Func<TrackItemViewModel?> _getSelectedTrack;
    private readonly Action<TrackItemViewModel> _selectTrack;
    private readonly Func<bool> _isBusy;
    private readonly Action _onFilterChanged;
    private readonly Action<string> _onReplaceApplied;
    private readonly Func<IReadOnlyList<CustomTagDefinitionViewModel>> _getCustomTagDefinitions;

    private readonly HashSet<string> _matchingTrackPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _isResetting;
    private bool _isUpdatingTargetOptions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterActive))]
    private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPreviewPane))]
    private bool _isReplaceVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPreviewPane))]
    private bool _isPreviewVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchPattern))]
    [NotifyPropertyChangedFor(nameof(HasValidPattern))]
    [NotifyPropertyChangedFor(nameof(IsFilterActive))]
    [NotifyCanExecuteChangedFor(nameof(ClearSearchPatternCommand))]
    private string _searchPattern = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReplacementText))]
    [NotifyCanExecuteChangedFor(nameof(ClearReplacementTextCommand))]
    private string _replacementText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPreserveExtensionOption))]
    private SearchReplaceTargetOption _selectedTargetOption;

    [ObservableProperty]
    private bool _useRegex;

    [ObservableProperty]
    private bool _isCaseSensitive;

    [ObservableProperty]
    private bool _preserveFileExtension = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterActive))]
    private bool _filterMatchingOnly = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPatternError))]
    [NotifyPropertyChangedFor(nameof(HasValidPattern))]
    [NotifyPropertyChangedFor(nameof(IsFilterActive))]
    private string _patternError = string.Empty;

    [ObservableProperty]
    private string _matchSummaryText = string.Empty;

    [ObservableProperty]
    private string _previewSummaryText = string.Empty;

    [ObservableProperty]
    private int _matchingTrackCount;

    [ObservableProperty]
    private int _totalMatchCount;

    [ObservableProperty]
    private SearchReplacePreviewItem? _selectedPreviewItem;

    [ObservableProperty]
    private bool _isSearchInputFocusRequested;

    [ObservableProperty]
    private bool _isReplaceInputFocusRequested;

    public SearchReplaceViewModel(
        ITextSearchReplaceService searchReplaceService,
        Func<IReadOnlyList<TrackItemViewModel>> getAllTracks,
        Func<IReadOnlyList<TrackItemViewModel>> getDisplayedTracks,
        Func<TrackItemViewModel?> getSelectedTrack,
        Action<TrackItemViewModel> selectTrack,
        Func<bool> isBusy,
        Action onFilterChanged,
        Action<string> onReplaceApplied,
        Func<IReadOnlyList<CustomTagDefinitionViewModel>>? getCustomTagDefinitions = null)
    {
        _searchReplaceService = searchReplaceService ?? throw new ArgumentNullException(nameof(searchReplaceService));
        _getAllTracks = getAllTracks ?? throw new ArgumentNullException(nameof(getAllTracks));
        _getDisplayedTracks = getDisplayedTracks ?? throw new ArgumentNullException(nameof(getDisplayedTracks));
        _getSelectedTrack = getSelectedTrack ?? throw new ArgumentNullException(nameof(getSelectedTrack));
        _selectTrack = selectTrack ?? throw new ArgumentNullException(nameof(selectTrack));
        _isBusy = isBusy ?? throw new ArgumentNullException(nameof(isBusy));
        _onFilterChanged = onFilterChanged ?? throw new ArgumentNullException(nameof(onFilterChanged));
        _onReplaceApplied = onReplaceApplied ?? throw new ArgumentNullException(nameof(onReplaceApplied));
        _getCustomTagDefinitions = getCustomTagDefinitions ?? (static () => []);

        TargetOptions.ReplaceAll(FixedTargetOptions);
        _selectedTargetOption = TargetOptions[0];
        RefreshTargetOptions(refreshResultsIfSelectionChanged: false);
    }

    public BulkObservableCollection<SearchReplaceTargetOption> TargetOptions { get; } = new();

    public ObservableCollection<SearchReplacePreviewItem> PreviewItems { get; } = new();

    public bool HasSearchPattern => !string.IsNullOrEmpty(SearchPattern);

    public bool HasReplacementText => !string.IsNullOrEmpty(ReplacementText);

    public bool HasPatternError => !string.IsNullOrEmpty(PatternError);

    public bool HasValidPattern => !string.IsNullOrEmpty(SearchPattern) && !HasPatternError;

    public bool ShowPreserveExtensionOption =>
        SelectedTargetOption?.Target is SearchReplaceTarget.All or SearchReplaceTarget.FileName;

    public bool ShowPreviewPane => IsOpen && IsReplaceVisible && IsPreviewVisible;

    public bool HasPreviewItems => PreviewItems.Count > 0;

    public bool IsFilterActive => IsOpen && FilterMatchingOnly && HasValidPattern;

    public bool IsExecutingReplace { get; private set; }

    private bool CanNavigate => !_isBusy() && HasValidPattern && MatchingTrackCount > 0;

    private bool CanReplaceSelected =>
        !_isBusy()
        && HasValidPattern
        && _getSelectedTrack() is { } selected
        && _matchingTrackPaths.Contains(selected.FilePath);

    private bool CanReplaceAll => !_isBusy() && HasValidPattern && MatchingTrackCount > 0;

    private bool CanTogglePreview => !_isBusy() && IsReplaceVisible;

    /// <summary>
    /// 指定されたトラックが現在の検索条件に一致するかを返す。
    /// </summary>
    public bool IsTrackMatching(TrackItemViewModel track)
    {
        return _matchingTrackPaths.Contains(track.FilePath);
    }

    /// <summary>
    /// 現在の検索・置換条件からクエリオブジェクトを生成する。
    /// </summary>
    public SearchReplaceQuery BuildQuery()
    {
        var selected = SelectedTargetOption ?? FixedTargetOptions[0];
        return new SearchReplaceQuery
        {
            SearchPattern = SearchPattern,
            Replacement = ReplacementText,
            Target = selected.Target,
            CustomTagKey = selected.Target == SearchReplaceTarget.SpecificCustomTag
                ? selected.CustomTagKey
                : null,
            UseRegex = UseRegex,
            IgnoreCase = !IsCaseSensitive,
            PreserveFileExtension = PreserveFileExtension
        };
    }

    /// <summary>
    /// 独自タグ定義カタログおよび読み込み済みトラックの未登録独自タグキーに基づき、対象フィールド選択肢を再同期する。
    /// </summary>
    /// <returns>フォールバック等により実効的な検索対象が変化した場合は true。</returns>
    public bool RefreshTargetOptions(bool refreshResultsIfSelectionChanged = true)
    {
        var newOptions = BuildTargetOptions();
        if (HasSameTargetOptions(TargetOptions, newOptions)
            && SelectedTargetOption is not null
            && TargetOptions.Contains(SelectedTargetOption))
        {
            return false;
        }

        var previousTarget = SelectedTargetOption?.Target ?? SearchReplaceTarget.All;
        var previousCustomTagKey = SelectedTargetOption?.CustomTagKey;
        var nextSelection = ResolveNextTargetOption(newOptions, previousTarget, previousCustomTagKey);

        var effectiveSelectionChanged =
            nextSelection.Target != previousTarget
            || !string.Equals(nextSelection.CustomTagKey, previousCustomTagKey, StringComparison.OrdinalIgnoreCase);

        _isUpdatingTargetOptions = true;
        try
        {
            TargetOptions.ReplaceAll(newOptions);
            SelectedTargetOption = nextSelection;
            // ComboBox の Reset 同期で選択表示が外れないよう、同値の場合も含めて明示通知する
            OnPropertyChanged(nameof(SelectedTargetOption));
            OnPropertyChanged(nameof(ShowPreserveExtensionOption));
        }
        finally
        {
            _isUpdatingTargetOptions = false;
        }

        if (refreshResultsIfSelectionChanged && effectiveSelectionChanged && IsOpen)
        {
            RefreshResults(notifyFilter: true);
        }

        return effectiveSelectionChanged;
    }

    /// <summary>
    /// 一致件数・プレビュー・コマンド実行可否を最新状態へ再計算する。
    /// </summary>
    public void RefreshResults(bool notifyFilter = true)
    {
        _matchingTrackPaths.Clear();
        MatchingTrackCount = 0;
        TotalMatchCount = 0;

        if (!IsOpen)
        {
            PatternError = string.Empty;
            MatchSummaryText = string.Empty;
            ClearPreviewItems();
            NotifyCommandStates();
            if (notifyFilter)
            {
                _onFilterChanged();
            }

            return;
        }

        var query = BuildQuery();
        if (!query.HasSearchPattern)
        {
            PatternError = string.Empty;
            MatchSummaryText = string.Empty;
            ClearPreviewItems();
            NotifyCommandStates();
            if (notifyFilter)
            {
                _onFilterChanged();
            }

            return;
        }

        var patternValidationError = _searchReplaceService.ValidatePattern(query);
        if (patternValidationError is not null)
        {
            PatternError = patternValidationError;
            MatchSummaryText = string.Empty;
            ClearPreviewItems();
            NotifyCommandStates();
            if (notifyFilter)
            {
                _onFilterChanged();
            }

            return;
        }

        PatternError = string.Empty;
        var allTracks = _getAllTracks();
        var matchingTracks = 0;
        var totalMatches = 0;

        try
        {
            foreach (var track in allTracks)
            {
                var count = track.CountSearchMatches(query, _searchReplaceService);
                if (count > 0)
                {
                    _matchingTrackPaths.Add(track.FilePath);
                    matchingTracks++;
                    totalMatches += count;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            _matchingTrackPaths.Clear();
            PatternError = RegexTimeoutErrorMessage;
            MatchSummaryText = string.Empty;
            ClearPreviewItems();
            NotifyCommandStates();
            if (notifyFilter)
            {
                _onFilterChanged();
            }

            return;
        }

        MatchingTrackCount = matchingTracks;
        TotalMatchCount = totalMatches;
        MatchSummaryText = matchingTracks > 0
            ? $"{matchingTracks} トラック / {totalMatches} 件一致"
            : "一致なし";

        if (ShowPreviewPane)
        {
            RebuildPreviewItems(query, allTracks);
        }
        else
        {
            ClearPreviewItems();
        }

        NotifyCommandStates();
        if (notifyFilter)
        {
            _onFilterChanged();
        }
    }

    /// <summary>
    /// 外部の状態変化（Busy 状態・選択トラック変更等）に応じてコマンド可否を更新する。
    /// </summary>
    public void NotifyCommandStates()
    {
        FindNextCommand.NotifyCanExecuteChanged();
        FindPreviousCommand.NotifyCanExecuteChanged();
        ReplaceSelectedCommand.NotifyCanExecuteChanged();
        ReplaceAllCommand.NotifyCanExecuteChanged();
        TogglePreviewCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 一覧クリア時に検索・置換バーを閉じ、フィルタ設定を既定（ON）へ戻して入力文字列・検索結果をリセットする。
    /// </summary>
    public void ResetForClearedTracks()
    {
        _isResetting = true;
        try
        {
            IsOpen = false;
            IsPreviewVisible = false;
            FilterMatchingOnly = true;
            SearchPattern = string.Empty;
            ReplacementText = string.Empty;
        }
        finally
        {
            _isResetting = false;
        }

        RefreshResults(notifyFilter: false);
    }

    [RelayCommand]
    private void OpenFind()
    {
        IsOpen = true;
        RefreshResults(notifyFilter: true);
        RequestSearchFocus();
    }

    [RelayCommand]
    private void OpenReplace()
    {
        IsOpen = true;
        IsReplaceVisible = true;
        AutoEnablePreviewIfReplacementTextExists();
        RefreshResults(notifyFilter: true);
        if (string.IsNullOrEmpty(SearchPattern))
        {
            RequestSearchFocus();
        }
        else
        {
            RequestReplaceFocus();
        }
    }

    [RelayCommand]
    private void ToggleBar()
    {
        if (IsOpen)
        {
            Close();
            return;
        }

        IsOpen = true;
        IsReplaceVisible = true;
        AutoEnablePreviewIfReplacementTextExists();
        RefreshResults(notifyFilter: true);
        RequestSearchFocus();
    }

    [RelayCommand]
    private void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        _isResetting = true;
        try
        {
            IsOpen = false;
            IsPreviewVisible = false;
            FilterMatchingOnly = true;
        }
        finally
        {
            _isResetting = false;
        }

        RefreshResults(notifyFilter: true);
    }

    [RelayCommand(CanExecute = nameof(HasSearchPattern))]
    private void ClearSearchPattern()
    {
        if (string.IsNullOrEmpty(SearchPattern))
        {
            return;
        }

        SearchPattern = string.Empty;
        RequestSearchFocus();
    }

    [RelayCommand(CanExecute = nameof(HasReplacementText))]
    private void ClearReplacementText()
    {
        if (string.IsNullOrEmpty(ReplacementText))
        {
            return;
        }

        ReplacementText = string.Empty;
        RequestReplaceFocus();
    }

    [RelayCommand]
    private void ToggleReplaceSection()
    {
        IsReplaceVisible = !IsReplaceVisible;
        if (!IsReplaceVisible)
        {
            IsPreviewVisible = false;
        }
        else
        {
            AutoEnablePreviewIfReplacementTextExists();
        }

        RefreshResults(notifyFilter: false);
    }

    [RelayCommand(CanExecute = nameof(CanTogglePreview))]
    private void TogglePreview()
    {
        IsPreviewVisible = !IsPreviewVisible;
        RefreshResults(notifyFilter: false);
    }

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void FindNext()
    {
        NavigateMatch(forward: true);
    }

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void FindPrevious()
    {
        NavigateMatch(forward: false);
    }

    [RelayCommand(CanExecute = nameof(CanReplaceSelected))]
    private void ReplaceSelected()
    {
        if (_getSelectedTrack() is not { } target)
        {
            return;
        }

        ExecuteReplace([target]);
    }

    [RelayCommand(CanExecute = nameof(CanReplaceAll))]
    private void ReplaceAll()
    {
        var allTracks = _getAllTracks();
        var targets = allTracks
            .Where(track => _matchingTrackPaths.Contains(track.FilePath))
            .ToList();

        ExecuteReplace(targets);
    }

    partial void OnSearchPatternChanged(string value)
    {
        if (value is null)
        {
            SearchPattern = string.Empty;
            return;
        }

        if (_isResetting)
        {
            return;
        }

        RefreshResults(notifyFilter: true);
    }

    partial void OnReplacementTextChanged(string? oldValue, string newValue)
    {
        if (newValue is null)
        {
            ReplacementText = string.Empty;
            return;
        }

        if (_isResetting)
        {
            return;
        }

        if (string.IsNullOrEmpty(oldValue)
            && !string.IsNullOrEmpty(newValue)
            && IsOpen
            && IsReplaceVisible
            && !IsPreviewVisible)
        {
            IsPreviewVisible = true;
        }

        if (ShowPreviewPane)
        {
            RefreshResults(notifyFilter: false);
        }
    }

    partial void OnSelectedTargetOptionChanged(SearchReplaceTargetOption value)
    {
        if (_isUpdatingTargetOptions)
        {
            return;
        }

        if (value is null)
        {
            SelectedTargetOption = TargetOptions.Count > 0 ? TargetOptions[0] : FixedTargetOptions[0];
            return;
        }

        RefreshResults(notifyFilter: true);
    }

    partial void OnUseRegexChanged(bool value)
    {
        RefreshResults(notifyFilter: true);
    }

    partial void OnIsCaseSensitiveChanged(bool value)
    {
        RefreshResults(notifyFilter: true);
    }

    partial void OnPreserveFileExtensionChanged(bool value)
    {
        RefreshResults(notifyFilter: true);
    }

    partial void OnFilterMatchingOnlyChanged(bool value)
    {
        if (_isResetting)
        {
            return;
        }

        RefreshResults(notifyFilter: true);
    }

    partial void OnIsOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPreviewPane));
    }

    partial void OnSelectedPreviewItemChanged(SearchReplacePreviewItem? value)
    {
        if (value is null)
        {
            return;
        }

        var track = _getAllTracks().FirstOrDefault(
            item => string.Equals(item.FilePath, value.TrackFilePath, StringComparison.OrdinalIgnoreCase));
        if (track is not null)
        {
            _selectTrack(track);
        }
    }

    private void AutoEnablePreviewIfReplacementTextExists()
    {
        if (IsOpen && IsReplaceVisible && !string.IsNullOrEmpty(ReplacementText) && !IsPreviewVisible)
        {
            IsPreviewVisible = true;
        }
    }

    private void RequestSearchFocus()
    {
        IsSearchInputFocusRequested = false;
        IsSearchInputFocusRequested = true;
    }

    private void RequestReplaceFocus()
    {
        IsReplaceInputFocusRequested = false;
        IsReplaceInputFocusRequested = true;
    }

    private void NavigateMatch(bool forward)
    {
        var displayedTracks = _getDisplayedTracks();
        if (displayedTracks.Count == 0 || _matchingTrackPaths.Count == 0)
        {
            return;
        }

        var selected = _getSelectedTrack();
        var currentIndex = selected is not null
            ? FindTrackIndex(displayedTracks, selected)
            : -1;

        var step = forward ? 1 : -1;
        var startIndex = currentIndex >= 0
            ? currentIndex + step
            : (forward ? 0 : displayedTracks.Count - 1);

        for (var offset = 0; offset < displayedTracks.Count; offset++)
        {
            var index = (startIndex + (offset * step)) % displayedTracks.Count;
            if (index < 0)
            {
                index += displayedTracks.Count;
            }

            var candidate = displayedTracks[index];
            if (_matchingTrackPaths.Contains(candidate.FilePath))
            {
                _selectTrack(candidate);
                return;
            }
        }
    }

    private void ExecuteReplace(IReadOnlyList<TrackItemViewModel> targetTracks)
    {
        if (targetTracks.Count == 0 || !HasValidPattern)
        {
            return;
        }

        var query = BuildQuery();
        var allTracks = _getAllTracks();
        var blockedFileNamePaths = CollectBlockedFileNameTrackPaths(query, targetTracks, allTracks);

        var affectedTracks = 0;
        var replacedFields = 0;
        var totalMatches = 0;
        var skippedFileNames = 0;

        IsExecutingReplace = true;
        try
        {
            foreach (var track in targetTracks)
            {
                var allowFileNameReplace = !blockedFileNamePaths.Contains(track.FilePath);
                var outcome = track.ApplySearchReplace(query, _searchReplaceService, allowFileNameReplace);
                if (outcome.ReplacedFieldCount > 0)
                {
                    affectedTracks++;
                    replacedFields += outcome.ReplacedFieldCount;
                    totalMatches += outcome.MatchCount;
                }

                if (outcome.SkippedInvalidFileName)
                {
                    skippedFileNames++;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            PatternError = RegexTimeoutErrorMessage;
            NotifyCommandStates();
            return;
        }
        finally
        {
            IsExecutingReplace = false;
        }

        var statusMessage = BuildReplaceStatusMessage(affectedTracks, replacedFields, totalMatches, skippedFileNames);
        _onReplaceApplied(statusMessage);
        RefreshResults(notifyFilter: true);
    }

    private void RebuildPreviewItems(SearchReplaceQuery query, IReadOnlyList<TrackItemViewModel> allTracks)
    {
        PreviewItems.Clear();

        var rawItems = new List<SearchReplacePreviewItem>();
        var proposedFileNamesByTrackPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var track in allTracks)
            {
                var items = track.BuildReplacePreviews(query, _searchReplaceService, out var proposedFileName);
                if (items.Count > 0)
                {
                    rawItems.AddRange(items);
                }

                if (!string.IsNullOrEmpty(proposedFileName))
                {
                    proposedFileNamesByTrackPath[track.FilePath] = proposedFileName;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            PatternError = RegexTimeoutErrorMessage;
            ClearPreviewItems();
            return;
        }

        var conflictReasons = DetectFileNameConflicts(allTracks, proposedFileNamesByTrackPath);

        var warningCount = 0;
        foreach (var item in rawItems)
        {
            var finalItem = item;
            if (item.FieldTarget == SearchReplaceTarget.FileName
                && !item.HasWarning
                && conflictReasons.TryGetValue(item.TrackFilePath, out var conflictReason))
            {
                finalItem = item with { WarningMessage = conflictReason };
            }

            if (finalItem.HasWarning)
            {
                warningCount++;
            }

            PreviewItems.Add(finalItem);
        }

        OnPropertyChanged(nameof(HasPreviewItems));
        PreviewSummaryText = PreviewItems.Count == 0
            ? "置換による変更はありません"
            : warningCount > 0
                ? $"変更プレビュー: {PreviewItems.Count} 項目（警告 {warningCount} 件はスキップされます）"
                : $"変更プレビュー: {PreviewItems.Count} 項目";
    }

    private HashSet<string> CollectBlockedFileNameTrackPaths(
        SearchReplaceQuery query,
        IReadOnlyList<TrackItemViewModel> targetTracks,
        IReadOnlyList<TrackItemViewModel> allTracks)
    {
        if (query.Target is not SearchReplaceTarget.All and not SearchReplaceTarget.FileName)
        {
            return [];
        }

        var proposedFileNamesByTrackPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in targetTracks)
        {
            _ = track.BuildReplacePreviews(query, _searchReplaceService, out var proposedFileName);
            if (!string.IsNullOrEmpty(proposedFileName))
            {
                proposedFileNamesByTrackPath[track.FilePath] = proposedFileName;
            }
        }

        if (proposedFileNamesByTrackPath.Count == 0)
        {
            return [];
        }

        var conflicts = DetectFileNameConflicts(allTracks, proposedFileNamesByTrackPath);
        return new HashSet<string>(conflicts.Keys, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> DetectFileNameConflicts(
        IReadOnlyList<TrackItemViewModel> allTracks,
        Dictionary<string, string> proposedFileNamesByTrackPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (proposedFileNamesByTrackPath.Count == 0)
        {
            return result;
        }

        var loadedOriginalPaths = new HashSet<string>(
            allTracks.Select(track => track.FilePath),
            StringComparer.OrdinalIgnoreCase);

        var targetPathCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var proposedTargetPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var track in allTracks)
        {
            var effectiveFileName = proposedFileNamesByTrackPath.TryGetValue(track.FilePath, out var proposed)
                ? proposed
                : track.FileName;
            var targetPath = Path.Combine(track.DirectoryPath, effectiveFileName);
            targetPathCounts[targetPath] = targetPathCounts.GetValueOrDefault(targetPath) + 1;

            if (proposedFileNamesByTrackPath.ContainsKey(track.FilePath))
            {
                proposedTargetPaths[track.FilePath] = targetPath;
            }
        }

        foreach (var (trackFilePath, targetPath) in proposedTargetPaths)
        {
            if (targetPathCounts.GetValueOrDefault(targetPath) > 1)
            {
                result[trackFilePath] = DuplicateFileNameWarning;
                continue;
            }

            if (!string.Equals(trackFilePath, targetPath, StringComparison.OrdinalIgnoreCase)
                && !loadedOriginalPaths.Contains(targetPath)
                && File.Exists(targetPath))
            {
                result[trackFilePath] = ExistingFileConflictWarning;
            }
        }

        return result;
    }

    private void ClearPreviewItems()
    {
        if (PreviewItems.Count > 0)
        {
            PreviewItems.Clear();
            OnPropertyChanged(nameof(HasPreviewItems));
        }

        PreviewSummaryText = string.Empty;
        SelectedPreviewItem = null;
    }

    private static int FindTrackIndex(IReadOnlyList<TrackItemViewModel> tracks, TrackItemViewModel target)
    {
        for (var i = 0; i < tracks.Count; i++)
        {
            if (ReferenceEquals(tracks[i], target))
            {
                return i;
            }
        }

        return -1;
    }

    private List<SearchReplaceTargetOption> BuildTargetOptions()
    {
        var options = new List<SearchReplaceTargetOption>(FixedTargetOptions.Length + 8);
        options.AddRange(FixedTargetOptions);

        var definedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in _getCustomTagDefinitions())
        {
            var trimmedKey = definition.Key?.Trim() ?? string.Empty;
            if (trimmedKey.Length == 0 || !definedKeys.Add(trimmedKey))
            {
                continue;
            }

            var displayName = FormatCustomTagDefinitionOptionDisplayName(definition, trimmedKey);
            options.Add(new SearchReplaceTargetOption(
                SearchReplaceTarget.SpecificCustomTag,
                displayName,
                trimmedKey));
        }

        var unregisteredKeys = new List<string>();
        var seenUnregistered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in _getAllTracks())
        {
            foreach (var customTag in track.UnregisteredCustomTags)
            {
                var trimmedKey = customTag.Key?.Trim() ?? string.Empty;
                if (trimmedKey.Length == 0
                    || definedKeys.Contains(trimmedKey)
                    || !seenUnregistered.Add(trimmedKey))
                {
                    continue;
                }

                unregisteredKeys.Add(trimmedKey);
            }
        }

        unregisteredKeys.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var key in unregisteredKeys)
        {
            options.Add(new SearchReplaceTargetOption(
                SearchReplaceTarget.SpecificCustomTag,
                $"{CustomTagOptionPrefix}{key}",
                key));
        }

        return options;
    }

    private static string FormatCustomTagDefinitionOptionDisplayName(
        CustomTagDefinitionViewModel definition,
        string trimmedKey)
    {
        return definition.HasCustomLabel
            ? $"{CustomTagOptionPrefix}{definition.Label} ({trimmedKey})"
            : $"{CustomTagOptionPrefix}{trimmedKey}";
    }

    private static bool HasSameTargetOptions(
        IReadOnlyList<SearchReplaceTargetOption> current,
        IReadOnlyList<SearchReplaceTargetOption> desired)
    {
        if (current.Count != desired.Count)
        {
            return false;
        }

        for (var i = 0; i < current.Count; i++)
        {
            var left = current[i];
            var right = desired[i];
            if (left.Target != right.Target
                || !string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal)
                || !string.Equals(left.CustomTagKey, right.CustomTagKey, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static SearchReplaceTargetOption ResolveNextTargetOption(
        IReadOnlyList<SearchReplaceTargetOption> options,
        SearchReplaceTarget previousTarget,
        string? previousCustomTagKey)
    {
        if (previousTarget == SearchReplaceTarget.SpecificCustomTag)
        {
            if (!string.IsNullOrWhiteSpace(previousCustomTagKey))
            {
                foreach (var option in options)
                {
                    if (option.Target == SearchReplaceTarget.SpecificCustomTag
                        && string.Equals(option.CustomTagKey, previousCustomTagKey, StringComparison.OrdinalIgnoreCase))
                    {
                        return option;
                    }
                }
            }

            foreach (var option in options)
            {
                if (option.Target == SearchReplaceTarget.CustomTags)
                {
                    return option;
                }
            }

            return options[0];
        }

        foreach (var option in options)
        {
            if (option.Target == previousTarget)
            {
                return option;
            }
        }

        return options[0];
    }

    private static string BuildReplaceStatusMessage(
        int affectedTracks,
        int replacedFields,
        int totalMatches,
        int skippedFileNames)
    {
        if (affectedTracks == 0)
        {
            return skippedFileNames > 0
                ? $"置換を適用できませんでした（不正または重複するファイル名 {skippedFileNames} 件をスキップ）"
                : "置換による変更はありませんでした";
        }

        if (skippedFileNames > 0)
        {
            return $"{affectedTracks} トラック・{replacedFields} 項目（{totalMatches} 箇所）を置換しました（ファイル名警告 {skippedFileNames} 件スキップ・未保存）";
        }

        return $"{affectedTracks} トラック・{replacedFields} 項目（{totalMatches} 箇所）を置換しました（未保存）";
    }
}
