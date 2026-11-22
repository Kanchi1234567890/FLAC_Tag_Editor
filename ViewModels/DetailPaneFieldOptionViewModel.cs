using System;
using CommunityToolkit.Mvvm.ComponentModel;
using FLAC_Tag_Editor.Models;

namespace FLAC_Tag_Editor.ViewModels;

/// <summary>
/// 右ペイン（詳細パネル）の 1 表示項目分の表示状態を管理する ViewModel。
/// </summary>
public sealed partial class DetailPaneFieldOptionViewModel : ViewModelBase
{
    private readonly Action<DetailPaneFieldOptionViewModel> _onVisibilityChanged;
    private bool _suppressCallbacks;

    [ObservableProperty]
    private bool _isVisible;

    public DetailPaneFieldOptionViewModel(
        DetailPaneFieldDefinition definition,
        Action<DetailPaneFieldOptionViewModel> onVisibilityChanged)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _onVisibilityChanged = onVisibilityChanged ?? throw new ArgumentNullException(nameof(onVisibilityChanged));
        _isVisible = definition.DefaultVisible;
    }

    public DetailPaneFieldDefinition Definition { get; }

    public DetailPaneFieldKind Kind => Definition.Kind;

    public string Key => Definition.Key;

    public string DisplayName => Definition.DisplayName;

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

    internal DetailPaneFieldSettings ToSettings()
    {
        return new DetailPaneFieldSettings
        {
            Key = Key,
            IsVisible = IsVisible
        };
    }

    partial void OnIsVisibleChanged(bool value)
    {
        if (_suppressCallbacks)
        {
            return;
        }

        _onVisibilityChanged(this);
    }
}
