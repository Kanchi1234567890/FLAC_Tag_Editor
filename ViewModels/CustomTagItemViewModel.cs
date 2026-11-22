using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FLAC_Tag_Editor.Models;

namespace FLAC_Tag_Editor.ViewModels;

/// <summary>
/// 詳細パネルで表示・編集する「未登録の独自タグ」（定義カタログに含まれない Vorbis Comment）1 件分の ViewModel。
/// キーは読み取り専用とし、値の編集・削除・定義カタログへの昇格（「定義を登録」）を提供する。
/// </summary>
public sealed partial class CustomTagItemViewModel : ViewModelBase
{
    private readonly Action _onChanged;
    private readonly Action<CustomTagItemViewModel> _onRemove;
    private readonly Action<CustomTagItemViewModel>? _onRegisterDefinition;

    [ObservableProperty]
    private string _key;

    [ObservableProperty]
    private string _value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKeyError))]
    private string _keyValidationError = string.Empty;

    public CustomTagItemViewModel(
        string key,
        string value,
        Action onChanged,
        Action<CustomTagItemViewModel> onRemove,
        Action<CustomTagItemViewModel>? onRegisterDefinition = null)
    {
        _key = key ?? string.Empty;
        _value = value ?? string.Empty;
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _onRemove = onRemove ?? throw new ArgumentNullException(nameof(onRemove));
        _onRegisterDefinition = onRegisterDefinition;
        UpdateValidation();
    }

    public bool HasKeyError => !string.IsNullOrEmpty(KeyValidationError);

    partial void OnKeyChanged(string value)
    {
        if (value is null)
        {
            Key = string.Empty;
            return;
        }

        UpdateValidation();
        _onChanged();
    }

    partial void OnValueChanged(string value)
    {
        if (value is null)
        {
            Value = string.Empty;
            return;
        }

        _onChanged();
    }

    [RelayCommand]
    private void Remove()
    {
        _onRemove(this);
    }

    [RelayCommand]
    private void RegisterDefinition()
    {
        _onRegisterDefinition?.Invoke(this);
    }

    private void UpdateValidation()
    {
        KeyValidationError = CustomTagEntry.ValidateKey(Key) ?? string.Empty;
    }
}
