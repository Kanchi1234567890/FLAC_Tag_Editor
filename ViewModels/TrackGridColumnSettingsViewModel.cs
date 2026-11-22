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
/// </summary>
public sealed partial class TrackGridColumnSettingsViewModel : ViewModelBase, IDisposable
{
    private const int SaveDebounceMilliseconds = 300;
    private const double WidthChangeTolerance = 1.0;
    private const string ResetStatusMessage = "表示列の設定を既定に戻しました";

    private readonly IAppSettingsService? _appSettingsService;
    private readonly Action<string>? _reportStatus;
    private readonly Dictionary<TrackGridColumnKind, TrackGridColumnOptionViewModel> _columnsByKind;
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

        var columns = TrackGridColumnKindExtensions.AllDefinitions
            .Select(def => new TrackGridColumnOptionViewModel(def, OnOptionVisibilityChangedByUser))
            .ToList();

        Columns = columns;
        ToggleableColumns = columns.Where(c => c.IsToggleable).ToList();
        _columnsByKind = columns.ToDictionary(c => c.Kind);

        UpdateCanToggleStates();
    }

    /// <summary>
    /// すべての列定義（変更マーク列を含む）。
    /// </summary>
    public IReadOnlyList<TrackGridColumnOptionViewModel> Columns { get; }

    /// <summary>
    /// ユーザーが表示／非表示を切り替え可能な列一覧。
    /// </summary>
    public IReadOnlyList<TrackGridColumnOptionViewModel> ToggleableColumns { get; }

    /// <summary>
    /// 起動時の設定ファイル読み込みで構文・I/O エラーが発生したかどうか。
    /// </summary>
    public bool HasLoadError => _hasLoadError;

    /// <summary>
    /// View 側の DataGrid へ列設定（表示・並び順・幅）の再適用を要求するイベント。
    /// </summary>
    public event EventHandler? LayoutRequested;

    /// <summary>
    /// 現在表示中の列を DisplayIndex 昇順で返す（TSV 貼り付けのフォールバック用）。
    /// </summary>
    public IReadOnlyList<TrackGridColumnKind> GetVisibleOrderedColumns()
    {
        return Columns
            .Where(c => c.IsVisible)
            .OrderBy(c => c.DisplayIndex)
            .ThenBy(c => c.Definition.DefaultDisplayIndex)
            .Select(c => c.Kind)
            .ToList();
    }

    public bool TryGetColumn(TrackGridColumnKind kind, out TrackGridColumnOptionViewModel option)
    {
        return _columnsByKind.TryGetValue(kind, out option!);
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

        var snapshotByKind = new Dictionary<TrackGridColumnKind, TrackGridColumnSettings>();
        foreach (var entry in snapshots)
        {
            if (TrackGridColumnKindExtensions.TryGetDefinitionByKey(entry.Key, out var def))
            {
                snapshotByKind[def.Kind] = entry;
            }
        }

        if (snapshotByKind.Count == 0)
        {
            return;
        }

        // 並び順を 0..N-1 に正規化
        var orderedOptions = Columns
            .OrderBy(opt => snapshotByKind.TryGetValue(opt.Kind, out var snap)
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

            if (snapshotByKind.TryGetValue(option.Kind, out var snap)
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
        LayoutRequested?.Invoke(this, EventArgs.Empty);

        _hasLoadError = false;
        ScheduleSave(immediate: true);
        _reportStatus?.Invoke(ResetStatusMessage);
    }

    /// <summary>
    /// 現在の列設定から永続化用の <see cref="TrackGridSettings"/> スナップショットを構築する。
    /// </summary>
    public TrackGridSettings BuildTrackGridSettingsSnapshot()
    {
        return new TrackGridSettings
        {
            Columns = Columns
                .OrderBy(c => c.DisplayIndex)
                .ThenBy(c => c.Definition.DefaultDisplayIndex)
                .Select(c => c.ToSettings())
                .ToList()
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

        UpdateCanToggleStates();
        LayoutRequested?.Invoke(this, EventArgs.Empty);

        _hasLoadError = false;
        ScheduleSave(immediate: true);
    }

    private void ApplyDefaultState()
    {
        foreach (var option in Columns)
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
        var savedByKind = new Dictionary<TrackGridColumnKind, TrackGridColumnSettings>();
        if (settings?.TrackGrid?.Columns is { Count: > 0 } savedColumns)
        {
            foreach (var entry in savedColumns)
            {
                if (entry is null)
                {
                    continue;
                }

                if (TrackGridColumnKindExtensions.TryGetDefinitionByKey(entry.Key, out var def))
                {
                    // 重複キーは先勝ち
                    savedByKind.TryAdd(def.Kind, entry);
                }
            }
        }

        if (savedByKind.Count == 0)
        {
            ApplyDefaultState();
            return;
        }

        // 各列の目標状態（欠損列は既定値で末尾側へ補完）
        var fallbackOrderBase = TrackGridColumnKindExtensions.AllDefinitions.Count;
        var resolved = new List<(TrackGridColumnOptionViewModel Option, bool IsVisible, int RawOrder, double Width)>(Columns.Count);

        foreach (var option in Columns)
        {
            if (savedByKind.TryGetValue(option.Kind, out var saved))
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

        UpdateCanToggleStates();
    }

    private void UpdateCanToggleStates()
    {
        var visibleToggleableCount = ToggleableColumns.Count(c => c.IsVisible);
        foreach (var option in ToggleableColumns)
        {
            option.CanToggle = !option.IsVisible || visibleToggleableCount > 1;
        }
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
