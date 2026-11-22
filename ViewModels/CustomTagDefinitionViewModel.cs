using System;
using CommunityToolkit.Mvvm.ComponentModel;
using FLAC_Tag_Editor.Models;

namespace FLAC_Tag_Editor.ViewModels;

/// <summary>
/// アプリ全体で共有される独自タグ定義 1 件分の ViewModel。
/// </summary>
public sealed partial class CustomTagDefinitionViewModel : ViewModelBase
{
    private readonly Action<CustomTagDefinitionViewModel>? _onVisibilityChanged;
    private bool _suppressCallbacks;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    [NotifyPropertyChangedFor(nameof(HasCustomLabel))]
    [NotifyPropertyChangedFor(nameof(FlyoutDisplayText))]
    private string _key;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    [NotifyPropertyChangedFor(nameof(HasCustomLabel))]
    [NotifyPropertyChangedFor(nameof(FlyoutDisplayText))]
    private string _label;

    [ObservableProperty]
    private bool _isVisible;

    public CustomTagDefinitionViewModel(
        CustomTagDefinition definition,
        Action<CustomTagDefinitionViewModel>? onVisibilityChanged = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        _key = definition.Key;
        _label = definition.Label;
        _isVisible = definition.IsVisible;
        _onVisibilityChanged = onVisibilityChanged;
    }

    /// <summary>
    /// 右ペインの見出しに表示するラベル（Label 未設定時は Key）。
    /// </summary>
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Key : Label;

    /// <summary>
    /// Key と異なるカスタム Label が設定されているかどうか。
    /// </summary>
    public bool HasCustomLabel =>
        !string.IsNullOrWhiteSpace(Label)
        && !string.Equals(Label, Key, StringComparison.Ordinal);

    /// <summary>
    /// 表示項目 Flyout 用の表示文字列（Label がある場合は「Label (Key)」）。
    /// </summary>
    public string FlyoutDisplayText => HasCustomLabel ? $"{Label} ({Key})" : Key;

    public CustomTagDefinition ToModel()
    {
        return new CustomTagDefinition(Key, Label, IsVisible);
    }

    internal void UpdateFrom(CustomTagDefinition definition)
    {
        _suppressCallbacks = true;
        try
        {
            Key = definition.Key;
            Label = definition.Label;
            IsVisible = definition.IsVisible;
        }
        finally
        {
            _suppressCallbacks = false;
        }
    }

    internal void SetIsVisibleSilently(bool isVisible)
    {
        _suppressCallbacks = true;
        try
        {
            IsVisible = isVisible;
        }
        finally
        {
            _suppressCallbacks = false;
        }
    }

    partial void OnIsVisibleChanged(bool value)
    {
        if (_suppressCallbacks)
        {
            return;
        }

        _onVisibilityChanged?.Invoke(this);
    }
}
