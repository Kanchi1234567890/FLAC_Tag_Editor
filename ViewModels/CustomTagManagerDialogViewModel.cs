using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FLAC_Tag_Editor.Models;

namespace FLAC_Tag_Editor.ViewModels;

/// <summary>
/// 独自タグ定義管理ダイアログの 1 行分の編集 ViewModel。
/// </summary>
public sealed partial class CustomTagDefinitionRowViewModel : ViewModelBase
{
    private readonly Action _onChanged;
    private readonly Action<CustomTagDefinitionRowViewModel> _onMoveUp;
    private readonly Action<CustomTagDefinitionRowViewModel> _onMoveDown;
    private readonly Action<CustomTagDefinitionRowViewModel> _onRemove;

    [ObservableProperty]
    private string _key;

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKeyError))]
    private string _keyValidationError = string.Empty;

    [ObservableProperty]
    private bool _canMoveUp;

    [ObservableProperty]
    private bool _canMoveDown;

    [ObservableProperty]
    private bool _isFocusRequested;

    public CustomTagDefinitionRowViewModel(
        string key,
        string label,
        bool isVisible,
        Action onChanged,
        Action<CustomTagDefinitionRowViewModel> onMoveUp,
        Action<CustomTagDefinitionRowViewModel> onMoveDown,
        Action<CustomTagDefinitionRowViewModel> onRemove,
        bool isFocusRequested = false)
    {
        _key = key ?? string.Empty;
        _label = label ?? string.Empty;
        _isVisible = isVisible;
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _onMoveUp = onMoveUp ?? throw new ArgumentNullException(nameof(onMoveUp));
        _onMoveDown = onMoveDown ?? throw new ArgumentNullException(nameof(onMoveDown));
        _onRemove = onRemove ?? throw new ArgumentNullException(nameof(onRemove));
        _isFocusRequested = isFocusRequested;
    }

    public bool HasKeyError => !string.IsNullOrEmpty(KeyValidationError);

    partial void OnKeyChanged(string value)
    {
        if (value is null)
        {
            Key = string.Empty;
            return;
        }

        _onChanged();
    }

    partial void OnLabelChanged(string value)
    {
        if (value is null)
        {
            Label = string.Empty;
            return;
        }

        _onChanged();
    }

    partial void OnCanMoveUpChanged(bool value)
    {
        MoveUpCommand.NotifyCanExecuteChanged();
    }

    partial void OnCanMoveDownChanged(bool value)
    {
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp()
    {
        _onMoveUp(this);
    }

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown()
    {
        _onMoveDown(this);
    }

    [RelayCommand]
    private void Remove()
    {
        _onRemove(this);
    }

    public CustomTagDefinition ToModel()
    {
        return new CustomTagDefinition(
            Key.Trim(),
            CustomTagDefinitionRules.NormalizeLabel(Label),
            IsVisible);
    }
}

/// <summary>
/// 独自タグ定義管理ダイアログの ViewModel。
/// 作業用コピーを編集し、OK 確定時のみ呼び出し元へ新しい定義リストとクリーンアップ指定を返す。
/// </summary>
public sealed partial class CustomTagManagerDialogViewModel : ViewModelBase
{
    private readonly HashSet<string> _savedCustomColumnKeys;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool _canConfirm = true;

    [ObservableProperty]
    private string _validationSummaryText = string.Empty;

    [ObservableProperty]
    private bool _cleanupUnusedColumnSettings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnusedColumnSettings))]
    [NotifyPropertyChangedFor(nameof(CleanupCheckboxText))]
    private int _unusedColumnSettingsCount;

    public CustomTagManagerDialogViewModel()
        : this(
        [
            new CustomTagDefinition("LYRICIST", "作詞者", IsVisible: true),
            new CustomTagDefinition("COMPOSER", "作曲者", IsVisible: true),
            new CustomTagDefinition("CATALOGNUMBER", string.Empty, IsVisible: true)
        ],
        ["OLD_TAG"])
    {
    }

    public CustomTagManagerDialogViewModel(
        IReadOnlyList<CustomTagDefinition> definitions,
        IReadOnlyList<string>? savedCustomColumnKeys = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        _savedCustomColumnKeys = new HashSet<string>(
            (savedCustomColumnKeys ?? [])
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim()),
            StringComparer.OrdinalIgnoreCase);

        foreach (var def in definitions)
        {
            Items.Add(CreateRow(def.Key, def.Label, def.IsVisible, isFocusRequested: false));
        }

        RevalidateAll();
    }

    public ObservableCollection<CustomTagDefinitionRowViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    public bool HasValidationError => !string.IsNullOrEmpty(ValidationSummaryText);

    public bool HasUnusedColumnSettings => UnusedColumnSettingsCount > 0;

    public string CleanupCheckboxText => HasUnusedColumnSettings
        ? $"確定時に未使用の列設定をクリーンアップ（{UnusedColumnSettingsCount} 件）"
        : "確定時に未使用の列設定をクリーンアップ（対象なし）";

    public event Action<CustomTagManagerResult?>? CloseRequested;

    [RelayCommand]
    private void AddDefinition()
    {
        var row = CreateRow(string.Empty, string.Empty, isVisible: true, isFocusRequested: true);
        Items.Add(row);
        OnPropertyChanged(nameof(HasItems));
        RevalidateAll();
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        RevalidateAll();
        if (!CanConfirm)
        {
            return;
        }

        var definitions = Items.Select(item => item.ToModel()).ToList();
        var shouldCleanup = CleanupUnusedColumnSettings && HasUnusedColumnSettings;
        CloseRequested?.Invoke(new CustomTagManagerResult(definitions, shouldCleanup));
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseRequested?.Invoke(null);
    }

    private CustomTagDefinitionRowViewModel CreateRow(
        string key,
        string label,
        bool isVisible,
        bool isFocusRequested)
    {
        return new CustomTagDefinitionRowViewModel(
            key,
            label,
            isVisible,
            onChanged: RevalidateAll,
            onMoveUp: MoveRowUp,
            onMoveDown: MoveRowDown,
            onRemove: RemoveRow,
            isFocusRequested: isFocusRequested);
    }

    private void MoveRowUp(CustomTagDefinitionRowViewModel row)
    {
        var index = Items.IndexOf(row);
        if (index <= 0)
        {
            return;
        }

        Items.Move(index, index - 1);
        UpdateRowMoveStates();
    }

    private void MoveRowDown(CustomTagDefinitionRowViewModel row)
    {
        var index = Items.IndexOf(row);
        if (index < 0 || index >= Items.Count - 1)
        {
            return;
        }

        Items.Move(index, index + 1);
        UpdateRowMoveStates();
    }

    private void RemoveRow(CustomTagDefinitionRowViewModel row)
    {
        if (Items.Remove(row))
        {
            OnPropertyChanged(nameof(HasItems));
            RevalidateAll();
        }
    }

    private void RevalidateAll()
    {
        var errorCount = 0;
        for (var i = 0; i < Items.Count; i++)
        {
            var current = Items[i];
            var otherKeys = Items
                .Where((_, idx) => idx != i)
                .Select(r => r.Key);

            var error = CustomTagDefinitionRules.ValidateDefinitionKey(current.Key, otherKeys) ?? string.Empty;
            current.KeyValidationError = error;
            if (!string.IsNullOrEmpty(error))
            {
                errorCount++;
            }
        }

        UpdateRowMoveStates();
        UpdateUnusedColumnSettingsState();

        CanConfirm = errorCount == 0;
        ValidationSummaryText = errorCount > 0
            ? $"入力エラーが {errorCount} 件あります。修正してから確定してください。"
            : string.Empty;
        OnPropertyChanged(nameof(HasValidationError));
    }

    private void UpdateUnusedColumnSettingsState()
    {
        var currentKeys = new HashSet<string>(
            Items
                .Select(r => r.Key?.Trim() ?? string.Empty)
                .Where(k => k.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        var unusedCount = _savedCustomColumnKeys.Count(key => !currentKeys.Contains(key));
        UnusedColumnSettingsCount = unusedCount;

        if (unusedCount == 0 && CleanupUnusedColumnSettings)
        {
            CleanupUnusedColumnSettings = false;
        }
    }

    private void UpdateRowMoveStates()
    {
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].CanMoveUp = i > 0;
            Items[i].CanMoveDown = i < Items.Count - 1;
        }
    }
}
