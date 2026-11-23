using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using FLAC_Tag_Editor.Models;
using FLAC_Tag_Editor.Services.Interfaces;

namespace FLAC_Tag_Editor.ViewModels;

/// <summary>
/// トラック一覧 DataGrid の表示列・並び順・列幅の状態管理と TOML 永続化を担う ViewModel。
/// 標準列に加え、<c>custom_tags.toml</c> に登録された独自タグ定義を動的列として管理する。
/// </summary>
public sealed partial class TrackGridColumnSettingsViewModel : ViewModelBase, IDisposable
{
    private const int SaveDebounceMilliseconds = 300;
    private const double WidthChangeTolerance = 1.0;
    private const string ResetStatusMessage = "表示列の設定を既定に戻しました";

    private readonly IAppSettingsService? _appSettingsService;
    private readonly Action<string>? _reportStatus;
    private readonly List<TrackGridColumnOptionViewModel> _standardColumns;
    private readonly List<TrackGridColumnOptionViewModel> _columns;
    private readonly Dictionary<TrackGridColumnId, TrackGridColumnOptionViewModel> _columnsById;
    private readonly Dictionary<string, TrackGridColumnSettings> _savedCustomColumnsByKey =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _saveLock = new();

    private Task _saveWorkerTask = Task.CompletedTask;
    private CancellationTokenSource? _delayCts;
    private SynchronizationContext? _lastSyncContext;
    private int _pendingDelayMs;
    private bool _isWorkerRunning;
    private bool _hasPendingSave;
    private bool _hasLoadError;
    private bool _isInitialized;
    private bool _disposed;

    public TrackGridColumnSettingsViewModel(
        IAppSettingsService? appSettingsService = null,
        Action<string>? reportStatus = null)
    {
        _appSettingsService = appSettingsService;
        _reportStatus = reportStatus;

        _standardColumns = TrackGridColumnKindExtensions.AllDefinitions
            .Select(def => new TrackGridColumnOptionViewModel(def, OnOptionVisibilityChangedByUser))
            .ToList();

        _columns = new List<TrackGridColumnOptionViewModel>(_standardColumns);
        _columnsById = _columns.ToDictionary(c => c.ColumnId);

        StandardToggleableColumns = _standardColumns.Where(c => c.IsToggleable).ToList();
        RebuildToggleableCollections();
        UpdateCanToggleStates();
    }

    /// <summary>
    /// 現在アクティブなすべての列定義（変更マーク列・標準列・登録済み独自タグ列）。
    /// </summary>
    public IReadOnlyList<TrackGridColumnOptionViewModel> Columns => _columns;

    /// <summary>
    /// ユーザーが表示／非表示を切り替え可能な全列一覧（コンテキストメニュー等向け）。
    /// </summary>
    public BulkObservableCollection<TrackGridColumnOptionViewModel> ToggleableColumns { get; } = [];

    /// <summary>
    /// 標準列のうち表示／非表示を切り替え可能な一覧（Flyout 標準セクション向け）。
    /// </summary>
    public IReadOnlyList<TrackGridColumnOptionViewModel> StandardToggleableColumns { get; }

    /// <summary>
    /// 独自タグ列のうち表示／非表示を切り替え可能な一覧（Flyout 独自タグセクション向け）。
    /// </summary>
    public BulkObservableCollection<TrackGridColumnOptionViewModel> CustomTagToggleableColumns { get; } = [];

    /// <summary>
    /// 登録済みの独自タグ列が 1 件以上あるかどうか。
    /// </summary>
    public bool HasCustomTagColumns => CustomTagToggleableColumns.Count > 0;

    /// <summary>
    /// 起動時の設定ファイル読み込みで構文・I/O エラーが発生したかどうか。
    /// </summary>
    public bool HasLoadError => _hasLoadError;

    /// <summary>
    /// View 側の DataGrid へ列構成・表示・並び順・幅の再適用を要求するイベント。
    /// </summary>
    public event EventHandler? LayoutRequested;

    /// <summary>
    /// 現在表示中の列を DisplayIndex 昇順で返す（TSV 貼り付けのフォールバック用）。
    /// </summary>
    public IReadOnlyList<TrackGridColumnId> GetVisibleOrderedColumns()
    {
        return _columns
            .Where(c => c.IsVisible)
            .OrderBy(c => c.DisplayIndex)
            .ThenBy(c => c.Definition.DefaultDisplayIndex)
            .Select(c => c.ColumnId)
            .ToList();
    }

    public bool TryGetColumn(TrackGridColumnId columnId, out TrackGridColumnOptionViewModel option)
    {
        return _columnsById.TryGetValue(columnId, out option!);
    }

    public bool TryGetColumn(TrackGridColumnKind kind, out TrackGridColumnOptionViewModel option)
    {
        if (kind == TrackGridColumnKind.CustomTag)
        {
            option = null!;
            return false;
        }

        return _columnsById.TryGetValue(TrackGridColumnId.ForStandard(kind), out option!);
    }

    /// <summary>
    /// <c>settings.toml</c> に保持されている独自タグ列設定のキー一覧を返す（削除済み定義の保持分を含む）。
    /// </summary>
    public IReadOnlyList<string> GetSavedCustomColumnKeys()
    {
        return _savedCustomColumnsByKey.Keys
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 独自タグ定義カタログの変更に合わせて動的列を追加・更新・除外する。
    /// 削除された独自タグの列設定は <c>_savedCustomColumnsByKey</c> に保持し、再登録時の復元に備える。
    /// </summary>
    public void SyncCustomTagDefinitions(IReadOnlyList<CustomTagDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        if (_disposed)
        {
            return;
        }

        // 現在アクティブな独自タグ列のうち、既に保存対象またはカスタマイズ済みの状態を退避する
        var existingCustomOptions = new Dictionary<string, TrackGridColumnOptionViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in _columns)
        {
            if (!option.IsCustomTag || string.IsNullOrWhiteSpace(option.CustomTagKey))
            {
                continue;
            }

            existingCustomOptions.TryAdd(option.CustomTagKey, option);
            if (_savedCustomColumnsByKey.ContainsKey(option.CustomTagKey)
                || option.IsVisible != option.Definition.DefaultVisible
                || option.DisplayIndex != option.Definition.DefaultDisplayIndex
                || Math.Abs(option.Width - option.Definition.DefaultWidth) >= WidthChangeTolerance)
            {
                _savedCustomColumnsByKey[option.CustomTagKey] = option.ToSettings();
            }
        }

        var previousCustomKeyOrder = _columns
            .Where(c => c.IsCustomTag && !string.IsNullOrWhiteSpace(c.CustomTagKey))
            .Select(c => c.CustomTagKey!)
            .ToList();

        var standardCount = _standardColumns.Count;
        var nextColumns = new List<TrackGridColumnOptionViewModel>(standardCount + definitions.Count);
        nextColumns.AddRange(_standardColumns);

        var nextCustomOptions = new List<TrackGridColumnOptionViewModel>(definitions.Count);
        var restoredOptions = new HashSet<TrackGridColumnOptionViewModel>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < definitions.Count; i++)
        {
            var customDef = definitions[i];
            var key = customDef.Key?.Trim() ?? string.Empty;
            if (key.Length == 0 || !seenKeys.Add(key))
            {
                continue;
            }

            var colDef = TrackGridColumnKindExtensions.CreateCustomTagColumnDefinition(
                customDef,
                defaultDisplayIndex: standardCount + nextCustomOptions.Count);

            TrackGridColumnOptionViewModel option;
            if (existingCustomOptions.TryGetValue(key, out var reusable))
            {
                reusable.UpdateDefinition(colDef);
                option = reusable;
            }
            else
            {
                option = new TrackGridColumnOptionViewModel(colDef, OnOptionVisibilityChangedByUser);
                if (_savedCustomColumnsByKey.TryGetValue(key, out var saved))
                {
                    option.SetStateSilently(
                        saved.IsVisible,
                        saved.DisplayIndex,
                        colDef.ClampWidth(saved.Width));
                    restoredOptions.Add(option);
                }
                else
                {
                    var fallbackIndex = nextColumns.Count + nextCustomOptions.Count;
                    option.SetStateSilently(
                        colDef.DefaultVisible,
                        fallbackIndex,
                        colDef.DefaultWidth);
                }
            }

            nextCustomOptions.Add(option);
        }

        nextColumns.AddRange(nextCustomOptions);

        // トグル可能な列がすべて非表示になってしまった場合は標準列の既定表示へフォールバック
        if (nextColumns.Where(c => c.IsToggleable).All(c => !c.IsVisible))
        {
            foreach (var option in _standardColumns)
            {
                option.SetIsVisibleSilently(option.Definition.DefaultVisible);
            }
        }

        // DisplayIndex を 0..N-1 の一意な連番へ正規化
        // 再登録で復元された列は、削除時に同位置へ繰り上がった既存列より前に挿入して元の位置を復元する
        var ordered = nextColumns
            .OrderBy(c => c.DisplayIndex)
            .ThenBy(c => restoredOptions.Contains(c) ? 0 : 1)
            .ThenBy(c => c.Definition.DefaultDisplayIndex)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var option = ordered[i];
            if (option.DisplayIndex != i)
            {
                option.SetStateSilently(option.IsVisible, i, option.Width);
            }
        }

        _columns.Clear();
        _columns.AddRange(nextColumns);

        _columnsById.Clear();
        foreach (var option in _columns)
        {
            _columnsById[option.ColumnId] = option;
        }

        RebuildToggleableCollections();
        UpdateCanToggleStates();
        LayoutRequested?.Invoke(this, EventArgs.Empty);

        var nextCustomKeyOrder = nextCustomOptions
            .Select(c => c.CustomTagKey!)
            .ToList();
        var customColumnSetChanged = previousCustomKeyOrder.Count != nextCustomKeyOrder.Count
            || !previousCustomKeyOrder
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(
                    nextCustomKeyOrder.OrderBy(k => k, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);

        if (customColumnSetChanged && _isInitialized && !_hasLoadError)
        {
            UpdateSavedCustomColumnsCache(onlyAlreadySavedOrActive: true);
            ScheduleSave(immediate: false);
        }
    }

    /// <summary>
    /// 現在定義されている独自タグキー以外の削除済み独自タグ列設定を <c>settings.toml</c> からクリーンアップする。
    /// </summary>
    /// <returns>削除した未使用列設定の件数。</returns>
    public int CleanupUnusedCustomColumnSettings(IEnumerable<string> activeCustomTagKeys)
    {
        ArgumentNullException.ThrowIfNull(activeCustomTagKeys);
        if (_disposed)
        {
            return 0;
        }

        var activeKeySet = new HashSet<string>(
            activeCustomTagKeys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim()),
            StringComparer.OrdinalIgnoreCase);

        var keysToRemove = _savedCustomColumnsByKey.Keys
            .Where(k => !activeKeySet.Contains(k))
            .ToList();

        if (keysToRemove.Count == 0)
        {
            return 0;
        }

        foreach (var key in keysToRemove)
        {
            _savedCustomColumnsByKey.Remove(key);
        }

        _hasLoadError = false;
        ScheduleSave(immediate: true);
        return keysToRemove.Count;
    }

    /// <summary>
    /// 起動時に設定ファイルを読み込み、正規化して各列へ反映する。
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _appSettingsService is null)
        {
            _isInitialized = true;
            return;
        }

        try
        {
            var loadResult = await _appSettingsService.LoadAsync(cancellationToken);
            if (_disposed)
            {
                return;
            }

            if (loadResult.HasLoadError)
            {
                _hasLoadError = true;
                ApplyDefaultState();
                LayoutRequested?.Invoke(this, EventArgs.Empty);

                var detail = string.IsNullOrWhiteSpace(loadResult.ErrorMessage)
                    ? string.Empty
                    : $"（{loadResult.ErrorMessage}）";
                _reportStatus?.Invoke($"設定ファイルの読み込みに失敗したため既定の列設定を使用します{detail}");
                return;
            }

            _hasLoadError = false;
            if (loadResult.IsLoadedFromFile)
            {
                ApplyNormalizedSettings(loadResult.Settings);
                LayoutRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            // 終了キャンセル時は何もしない
        }
        catch (Exception ex)
        {
            _hasLoadError = true;
            ApplyDefaultState();
            LayoutRequested?.Invoke(this, EventArgs.Empty);
            _reportStatus?.Invoke($"設定ファイルの読み込みに失敗したため既定の列設定を使用します（{ex.Message}）");
        }
        finally
        {
            _isInitialized = true;
        }
    }

    /// <summary>
    /// DataGrid 上のユーザー操作（列の並び替え・幅のリサイズ）を ViewModel へ同期し、変更があれば保存を予約する。
    /// </summary>
    public void SyncFromGridLayout(IReadOnlyList<TrackGridColumnSettings> snapshots)
    {
        if (_disposed || snapshots is null || snapshots.Count == 0)
        {
            return;
        }

        // 起動時の設定ロード完了前に発生したレイアウトイベントによる上書きを防止
        if (_appSettingsService is not null && !_isInitialized)
        {
            return;
        }

        var snapshotById = new Dictionary<TrackGridColumnId, TrackGridColumnSettings>();
        foreach (var entry in snapshots)
        {
            if (TrackGridColumnId.TryParseSettingKey(entry.Key, out var colId)
                && _columnsById.ContainsKey(colId))
            {
                snapshotById[colId] = entry;
            }
        }

        if (snapshotById.Count == 0)
        {
            return;
        }

        // 並び順を 0..N-1 に正規化
        var orderedOptions = _columns
            .OrderBy(opt => snapshotById.TryGetValue(opt.ColumnId, out var snap)
                ? snap.DisplayIndex
                : opt.DisplayIndex)
            .ThenBy(opt => opt.DisplayIndex)
            .ThenBy(opt => opt.Definition.DefaultDisplayIndex)
            .ToList();

        var hasChanges = false;
        for (var i = 0; i < orderedOptions.Count; i++)
        {
            var option = orderedOptions[i];
            var newDisplayIndex = i;
            var newWidth = option.Width;

            if (snapshotById.TryGetValue(option.ColumnId, out var snap)
                && option.CanResize
                && option.IsVisible
                && double.IsFinite(snap.Width)
                && snap.Width > 0)
            {
                var clamped = option.Definition.ClampWidth(snap.Width);
                if (Math.Abs(clamped - option.Width) >= WidthChangeTolerance)
                {
                    newWidth = clamped;
                }
            }

            if (option.DisplayIndex != newDisplayIndex
                || Math.Abs(newWidth - option.Width) >= WidthChangeTolerance)
            {
                option.SetStateSilently(option.IsVisible, newDisplayIndex, newWidth);
                hasChanges = true;
            }
        }

        if (!hasChanges)
        {
            return;
        }

        UpdateSavedCustomColumnsCache();
        _hasLoadError = false;
        ScheduleSave(immediate: false);
    }

    [RelayCommand]
    private void ResetColumns()
    {
        if (_disposed)
        {
            return;
        }

        ApplyDefaultState();
        UpdateSavedCustomColumnsCache();
        LayoutRequested?.Invoke(this, EventArgs.Empty);

        _hasLoadError = false;
        ScheduleSave(immediate: true);
        _reportStatus?.Invoke(ResetStatusMessage);
    }

    /// <summary>
    /// 現在の列設定から永続化用の <see cref="TrackGridSettings"/> スナップショットを構築する。
    /// 削除済み独自タグの保持済み列設定も末尾へ残して出力する。
    /// </summary>
    public TrackGridSettings BuildTrackGridSettingsSnapshot()
    {
        var activeSettings = _columns
            .OrderBy(c => c.DisplayIndex)
            .ThenBy(c => c.Definition.DefaultDisplayIndex)
            .Select(c => c.ToSettings())
            .ToList();

        var activeCustomKeySet = new HashSet<string>(
            _columns
                .Where(c => c.IsCustomTag && !string.IsNullOrWhiteSpace(c.CustomTagKey))
                .Select(c => c.CustomTagKey!),
            StringComparer.OrdinalIgnoreCase);

        var orphanedSettings = _savedCustomColumnsByKey
            .Where(pair => !activeCustomKeySet.Contains(pair.Key))
            .OrderBy(pair => pair.Value.DisplayIndex)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => CloneColumnSettings(pair.Value))
            .ToList();

        activeSettings.AddRange(orphanedSettings);

        return new TrackGridSettings
        {
            Columns = activeSettings
        };
    }

    /// <summary>
    /// アプリケーション終了前に、デバウンス待ちの未保存設定を同期的にフラッシュする。
    /// </summary>
    public void FlushPendingSaveSync()
    {
        if (_appSettingsService is null)
        {
            return;
        }

        Task workerTask;
        lock (_saveLock)
        {
            if (_hasPendingSave)
            {
                _pendingDelayMs = 0;
                _delayCts?.Cancel();
                if (!_isWorkerRunning)
                {
                    _isWorkerRunning = true;
                    _saveWorkerTask = RunSaveWorkerAsync();
                }
            }

            workerTask = _saveWorkerTask;
        }

        try
        {
            if (!workerTask.IsCompleted)
            {
                workerTask.Wait(TimeSpan.FromSeconds(5));
            }
        }
        catch
        {
            // 終了時の設定保存失敗でアプリ終了を妨げない
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        FlushPendingSaveSync();
        _disposed = true;
    }

    private void OnOptionVisibilityChangedByUser(TrackGridColumnOptionViewModel changedOption)
    {
        if (_disposed)
        {
            return;
        }

        // トグル可能な列がすべて非表示になるのを防止
        if (!changedOption.IsVisible && ToggleableColumns.All(c => !c.IsVisible))
        {
            changedOption.SetIsVisibleSilently(true);
            UpdateCanToggleStates();
            return;
        }

        UpdateSavedCustomColumnsCache();
        UpdateCanToggleStates();
        LayoutRequested?.Invoke(this, EventArgs.Empty);

        _hasLoadError = false;
        ScheduleSave(immediate: true);
    }

    private void ApplyDefaultState()
    {
        foreach (var option in _columns)
        {
            option.SetStateSilently(
                option.Definition.DefaultVisible,
                option.Definition.DefaultDisplayIndex,
                option.Definition.DefaultWidth);
        }

        UpdateCanToggleStates();
    }

    private void ApplyNormalizedSettings(AppSettings? settings)
    {
        var savedById = new Dictionary<TrackGridColumnId, TrackGridColumnSettings>();
        _savedCustomColumnsByKey.Clear();

        if (settings?.TrackGrid?.Columns is { Count: > 0 } savedColumns)
        {
            foreach (var entry in savedColumns)
            {
                if (entry is null)
                {
                    continue;
                }

                if (TrackGridColumnId.TryParseSettingKey(entry.Key, out var colId))
                {
                    // 重複キーは先勝ち
                    if (savedById.TryAdd(colId, entry) && colId.IsCustomTag && colId.CustomTagKey is not null)
                    {
                        _savedCustomColumnsByKey[colId.CustomTagKey] = CloneColumnSettings(entry);
                    }
                }
            }
        }

        if (savedById.Count == 0)
        {
            ApplyDefaultState();
            return;
        }

        // 各列の目標状態（欠損列は既定値で末尾側へ補完）
        var fallbackOrderBase = Math.Max(_columns.Count, savedById.Values.Max(v => v.DisplayIndex) + 1);
        var resolved = new List<(TrackGridColumnOptionViewModel Option, bool IsVisible, int RawOrder, double Width)>(_columns.Count);

        foreach (var option in _columns)
        {
            if (savedById.TryGetValue(option.ColumnId, out var saved))
            {
                var visible = option.IsToggleable ? saved.IsVisible : true;
                var width = option.Definition.ClampWidth(saved.Width);
                resolved.Add((option, visible, saved.DisplayIndex, width));
            }
            else
            {
                resolved.Add((
                    option,
                    option.Definition.DefaultVisible,
                    fallbackOrderBase + option.Definition.DefaultDisplayIndex,
                    option.Definition.DefaultWidth));
            }
        }

        // すべてのトグル可能列が非表示だった場合は既定表示へフォールバック
        if (resolved.Where(x => x.Option.IsToggleable).All(x => !x.IsVisible))
        {
            for (var i = 0; i < resolved.Count; i++)
            {
                var item = resolved[i];
                resolved[i] = (item.Option, item.Option.Definition.DefaultVisible, item.RawOrder, item.Width);
            }
        }

        // DisplayIndex を 0..N-1 の一意な連番へ正規化
        var ordered = resolved
            .OrderBy(x => x.RawOrder)
            .ThenBy(x => x.Option.Definition.DefaultDisplayIndex)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var item = ordered[i];
            item.Option.SetStateSilently(item.IsVisible, i, item.Width);
        }

        UpdateSavedCustomColumnsCache(onlyAlreadySavedOrActive: true);
        UpdateCanToggleStates();
    }

    private void UpdateSavedCustomColumnsCache(bool onlyAlreadySavedOrActive = false)
    {
        foreach (var option in _columns)
        {
            if (!option.IsCustomTag || string.IsNullOrWhiteSpace(option.CustomTagKey))
            {
                continue;
            }

            if (onlyAlreadySavedOrActive && !_savedCustomColumnsByKey.ContainsKey(option.CustomTagKey))
            {
                continue;
            }

            _savedCustomColumnsByKey[option.CustomTagKey] = option.ToSettings();
        }
    }

    private void RebuildToggleableCollections()
    {
        var customToggleable = _columns
            .Where(c => c.IsCustomTag && c.IsToggleable)
            .ToList();

        CustomTagToggleableColumns.ReplaceAll(customToggleable);

        var allToggleable = new List<TrackGridColumnOptionViewModel>(
            StandardToggleableColumns.Count + customToggleable.Count);
        allToggleable.AddRange(StandardToggleableColumns);
        allToggleable.AddRange(customToggleable);
        ToggleableColumns.ReplaceAll(allToggleable);

        OnPropertyChanged(nameof(HasCustomTagColumns));
    }

    private void UpdateCanToggleStates()
    {
        var visibleToggleableCount = ToggleableColumns.Count(c => c.IsVisible);
        foreach (var option in ToggleableColumns)
        {
            option.CanToggle = !option.IsVisible || visibleToggleableCount > 1;
        }
    }

    private static TrackGridColumnSettings CloneColumnSettings(TrackGridColumnSettings source)
    {
        return new TrackGridColumnSettings
        {
            Key = source.Key,
            IsVisible = source.IsVisible,
            DisplayIndex = source.DisplayIndex,
            Width = source.Width
        };
    }

    private void ScheduleSave(bool immediate)
    {
        if (_disposed || _appSettingsService is null)
        {
            return;
        }

        lock (_saveLock)
        {
            _hasPendingSave = true;
            _pendingDelayMs = immediate ? 0 : SaveDebounceMilliseconds;
            _lastSyncContext = SynchronizationContext.Current;
            _delayCts?.Cancel();

            if (!_isWorkerRunning)
            {
                _isWorkerRunning = true;
                _saveWorkerTask = RunSaveWorkerAsync();
            }
        }
    }

    private async Task RunSaveWorkerAsync()
    {
        while (true)
        {
            int delayMs;
            CancellationTokenSource? delayCts = null;
            SynchronizationContext? syncContext;

            lock (_saveLock)
            {
                if (!_hasPendingSave)
                {
                    _isWorkerRunning = false;
                    _delayCts?.Dispose();
                    _delayCts = null;
                    return;
                }

                delayMs = _pendingDelayMs;
                syncContext = _lastSyncContext;

                if (delayMs > 0)
                {
                    _delayCts?.Dispose();
                    delayCts = new CancellationTokenSource();
                    _delayCts = delayCts;
                }
                else
                {
                    _hasPendingSave = false;
                }
            }

            if (delayCts is not null)
            {
                try
                {
                    await Task.Delay(delayMs, delayCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    continue;
                }

                lock (_saveLock)
                {
                    if (!_hasPendingSave)
                    {
                        continue;
                    }

                    _hasPendingSave = false;
                }
            }

            if (_appSettingsService is null)
            {
                continue;
            }

            try
            {
                var trackGridSnapshot = BuildTrackGridSettingsSnapshot();
                await _appSettingsService
                    .UpdateAsync(settings => settings.TrackGrid = trackGridSnapshot, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!_disposed)
                {
                    var errorMessage = $"設定ファイルの保存に失敗しました: {ex.Message}";
                    if (syncContext is not null)
                    {
                        syncContext.Post(_ => _reportStatus?.Invoke(errorMessage), null);
                    }
                    else
                    {
                        _reportStatus?.Invoke(errorMessage);
                    }
                }
            }
        }
    }
}
