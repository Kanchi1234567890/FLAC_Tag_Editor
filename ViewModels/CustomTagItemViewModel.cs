using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FLAC_Tag_Editor.Models;

namespace FLAC_Tag_Editor.ViewModels;

/// <summary>
/// 詳細パネルで編集する独自タグ（Vorbis Comment）1件分のViewModel。
/// </summary>
public sealed partial class CustomTagItemViewModel : ViewModelBase
{
    private readonly Action _onChanged;
    private readonly Action<CustomTagItemViewModel> _onRemove;

    [ObservableProperty]
    private string _key;

    [ObservableProperty]
    private string _value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKeyError))]
    private string _keyValidationError = string.Empty;

    [ObservableProperty]
    private bool _isFocusRequested;

    public CustomTagItemViewModel(
        string key,
        string value,
        Action onChanged,
        Action<CustomTagItemViewModel> onRemove,
        bool isFocusRequested = false)
    {
        _key = key ?? string.Empty;
        _value = value ?? string.Empty;
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _onRemove = onRemove ?? throw new ArgumentNullException(nameof(onRemove));
        _isFocusRequested = isFocusRequested;
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

    private void UpdateValidation()
    {
        KeyValidationError = CustomTagEntry.ValidateKey(Key) ?? string.Empty;
    }
}
