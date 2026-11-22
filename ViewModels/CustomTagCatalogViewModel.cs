using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FLAC_Tag_Editor.Models;
using FLAC_Tag_Editor.Services.Interfaces;

namespace FLAC_Tag_Editor.ViewModels;

/// <summary>
/// アプリ全体の独自タグ定義カタログを保持し、<c>custom_tags.toml</c> への永続化を担う共有 ViewModel。
/// </summary>
public sealed class CustomTagCatalogViewModel : ViewModelBase, IDisposable
{
    private readonly ICustomTagDefinitionService? _definitionService;
    private readonly Action<string>? _reportStatus;
    private readonly object _saveLock = new();

    private Task _saveTask = Task.CompletedTask;
    private bool _disposed;

    public CustomTagCatalogViewModel(
        ICustomTagDefinitionService? definitionService = null,
        Action<string>? reportStatus = null,
        IEnumerable<CustomTagDefinition>? initialDefinitions = null)
    {
        _definitionService = definitionService;
        _reportStatus = reportStatus;

        if (initialDefinitions is not null)
        {
            foreach (var def in initialDefinitions)
            {
                Definitions.Add(new CustomTagDefinitionViewModel(def, OnDefinitionVisibilityChanged));
            }
        }
    }

    /// <summary>
    /// 登録されている独自タグ定義一覧（表示順）。
    /// </summary>
    public ObservableCollection<CustomTagDefinitionViewModel> Definitions { get; } = [];

    /// <summary>
    /// 独自タグ定義が 1 件以上登録されているかどうか。
    /// </summary>
    public bool HasDefinitions => Definitions.Count > 0;

    /// <summary>
    /// 表示状態（IsVisible == true）の独自タグ定義が 1 件以上あるかどうか。
    /// </summary>
    public bool HasVisibleDefinitions => Definitions.Any(d => d.IsVisible);

    /// <summary>
    /// 定義の追加・削除・並び替え・ラベル変更が行われたときに発火するイベント。
    /// </summary>
    public event EventHandler? DefinitionsChanged;

    /// <summary>
    /// 表示状態の変更を含め、カタログ状態が変化したときに発火するイベント。
    /// </summary>
    public event EventHandler? CatalogStateChanged;

    /// <summary>
    /// 現在の定義一覧のイミュータブルなスナップショットを返す。
    /// </summary>
    public IReadOnlyList<CustomTagDefinition> GetSnapshot()
    {
        return Definitions.Select(d => d.ToModel()).ToList();
    }

    /// <summary>
    /// 指定されたキーに対応する定義を検索する（大文字・小文字区別なし）。
    /// </summary>
    public bool TryGetDefinition(string? key, out CustomTagDefinitionViewModel definition)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            var trimmed = key.Trim();
            foreach (var item in Definitions)
            {
                if (string.Equals(item.Key, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    definition = item;
                    return true;
                }
            }
        }

        definition = null!;
        return false;
    }

    /// <summary>
    /// 起動時に <c>custom_tags.toml</c> を読み込む。
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _definitionService is null)
        {
            return;
        }

        try
        {
            var result = await _definitionService.LoadAsync(cancellationToken);
            if (_disposed)
            {
                return;
            }

            if (result.HasLoadError)
            {
                var detail = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? string.Empty
                    : $"（{result.ErrorMessage}）";
                _reportStatus?.Invoke($"独自タグ定義ファイルの読み込みに失敗しました{detail}");
                return;
            }

            ReplaceDefinitionItems(result.Definitions);
            NotifyCollectionFlags();
            DefinitionsChanged?.Invoke(this, EventArgs.Empty);
            CatalogStateChanged?.Invoke(this, EventArgs.Empty);

            if (result.SkippedEntryCount > 0)
            {
                _reportStatus?.Invoke($"独自タグ定義ファイル内の無効なエントリ {result.SkippedEntryCount} 件をスキップしました");
            }
        }
        catch (OperationCanceledException)
        {
            // 終了キャンセル時は何もしない
        }
        catch (Exception ex)
        {
            _reportStatus?.Invoke($"独自タグ定義ファイルの読み込みに失敗しました（{ex.Message}）");
        }
    }

    /// <summary>
    /// 管理ダイアログ等で確定した定義一覧を反映し、永続化する。
    /// </summary>
    public void ApplyDefinitions(IReadOnlyList<CustomTagDefinition> newDefinitions)
    {
        ArgumentNullException.ThrowIfNull(newDefinitions);
        if (_disposed)
        {
            return;
        }

        var normalized = CustomTagDefinitionRules.Normalize(
            CustomTagDefinitionRules.ToSettings(newDefinitions),
            out _);

        ReplaceDefinitionItems(normalized);
        NotifyCollectionFlags();
        DefinitionsChanged?.Invoke(this, EventArgs.Empty);
        CatalogStateChanged?.Invoke(this, EventArgs.Empty);
        EnqueueSave(GetSnapshot());
    }

    /// <summary>
    /// 未登録タグのキーを定義カタログの末尾へ登録（または既存定義を表示 ON に）し、永続化する。
    /// </summary>
    /// <returns>登録に成功した場合は true、キーが不正で登録できなかった場合は false。</returns>
    public bool RegisterKey(string? key, out string? errorMessage)
    {
        errorMessage = null;
        if (_disposed)
        {
            return false;
        }

        var trimmed = key?.Trim() ?? string.Empty;
        if (TryGetDefinition(trimmed, out var existing))
        {
            if (!existing.IsVisible)
            {
                existing.IsVisible = true;
            }

            DefinitionsChanged?.Invoke(this, EventArgs.Empty);
            CatalogStateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        var validationError = CustomTagDefinitionRules.ValidateDefinitionKey(
            trimmed,
            Definitions.Select(d => d.Key));

        if (validationError is not null)
        {
            errorMessage = validationError;
            _reportStatus?.Invoke($"独自タグ定義を登録できません: {validationError}");
            return false;
        }

        var definition = new CustomTagDefinition(trimmed, string.Empty, IsVisible: true);
        Definitions.Add(new CustomTagDefinitionViewModel(definition, OnDefinitionVisibilityChanged));
        NotifyCollectionFlags();
        DefinitionsChanged?.Invoke(this, EventArgs.Empty);
        CatalogStateChanged?.Invoke(this, EventArgs.Empty);
        EnqueueSave(GetSnapshot());
        _reportStatus?.Invoke($"独自タグ定義「{trimmed}」を登録しました");
        return true;
    }

    /// <summary>
    /// アプリ終了時に進行中の保存タスクが完了するまで待機する。
    /// </summary>
    public void FlushPendingSaveSync()
    {
        Task pending;
        lock (_saveLock)
        {
            pending = _saveTask;
        }

        try
        {
            if (!pending.IsCompleted)
            {
                pending.Wait(TimeSpan.FromSeconds(5));
            }
        }
        catch
        {
            // 終了時の保存失敗でアプリ終了を妨げない
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

    private void ReplaceDefinitionItems(IReadOnlyList<CustomTagDefinition> definitions)
    {
        // 同一キー（OrdinalIgnoreCase）の既存 VM インスタンスを再利用して UI バインディングの揺れを抑える
        var existingByKey = new Dictionary<string, CustomTagDefinitionViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var vm in Definitions)
        {
            existingByKey.TryAdd(vm.Key, vm);
        }

        Definitions.Clear();
        foreach (var def in definitions)
        {
            if (existingByKey.TryGetValue(def.Key, out var reusable))
            {
                reusable.UpdateFrom(def);
                Definitions.Add(reusable);
            }
            else
            {
                Definitions.Add(new CustomTagDefinitionViewModel(def, OnDefinitionVisibilityChanged));
            }
        }
    }

    private void OnDefinitionVisibilityChanged(CustomTagDefinitionViewModel changed)
    {
        if (_disposed)
        {
            return;
        }

        OnPropertyChanged(nameof(HasVisibleDefinitions));
        CatalogStateChanged?.Invoke(this, EventArgs.Empty);
        EnqueueSave(GetSnapshot());
    }

    private void NotifyCollectionFlags()
    {
        OnPropertyChanged(nameof(HasDefinitions));
        OnPropertyChanged(nameof(HasVisibleDefinitions));
    }

    private void EnqueueSave(IReadOnlyList<CustomTagDefinition> snapshot)
    {
        if (_disposed || _definitionService is null)
        {
            return;
        }

        var syncContext = SynchronizationContext.Current;
        lock (_saveLock)
        {
            var previous = _saveTask;
            _saveTask = SaveChainAsync(previous, snapshot, syncContext);
        }
    }

    private async Task SaveChainAsync(
        Task previousTask,
        IReadOnlyList<CustomTagDefinition> snapshot,
        SynchronizationContext? syncContext)
    {
        try
        {
            await previousTask.ConfigureAwait(false);
        }
        catch
        {
            // 直前の保存エラーは握りつぶし、最新のスナップショット保存を試みる
        }

        if (_definitionService is null)
        {
            return;
        }

        try
        {
            await _definitionService.SaveAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                var message = $"独自タグ定義ファイルの保存に失敗しました: {ex.Message}";
                if (syncContext is not null)
                {
                    syncContext.Post(_ => _reportStatus?.Invoke(message), null);
                }
                else
                {
                    _reportStatus?.Invoke(message);
                }
            }
        }
    }
}
