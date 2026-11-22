using System;
using CommunityToolkit.Mvvm.ComponentModel;
using FLAC_Tag_Editor.Models;

namespace FLAC_Tag_Editor.ViewModels;

/// <summary>
/// トラック一覧 DataGrid の 1 列分の表示設定（表示状態・表示順・幅）を管理する ViewModel。
/// </summary>
public sealed partial class TrackGridColumnOptionViewModel : ViewModelBase
{
    private readonly Action<TrackGridColumnOptionViewModel> _onVisibilityChanged;
    private bool _suppressCallbacks;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private int _displayIndex;

    [ObservableProperty]
    private double _width;

    [ObservableProperty]
    private bool _canToggle = true;

    public TrackGridColumnOptionViewModel(
        TrackGridColumnDefinition definition,
        Action<TrackGridColumnOptionViewModel> onVisibilityChanged)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _onVisibilityChanged = onVisibilityChanged ?? throw new ArgumentNullException(nameof(onVisibilityChanged));

        _suppressCallbacks = true;
        try
        {
            _isVisible = definition.DefaultVisible;
            _displayIndex = definition.DefaultDisplayIndex;
            _width = definition.DefaultWidth;
            _canToggle = definition.IsToggleable;
        }
        finally
        {
            _suppressCallbacks = false;
        }
    }

    public TrackGridColumnDefinition Definition { get; }

    public TrackGridColumnKind Kind => Definition.Kind;

    public string Key => Definition.Key;

    public string DisplayName => Definition.DisplayName;

    public bool IsToggleable => Definition.IsToggleable;

    public bool CanResize => Definition.CanResize;

    public double DefaultWidth => Definition.DefaultWidth;

    public double MinWidth => Definition.MinWidth;

    /// <summary>
    /// ViewModel 内部からの同期・読み込み時にコールバックを抑止して値を適用する。
    /// </summary>
    internal void SetStateSilently(bool isVisible, int displayIndex, double width)
    {
        _suppressCallbacks = true;
        try
        {
            IsVisible = Definition.IsToggleable ? isVisible : true;
            DisplayIndex = displayIndex;
            Width = Definition.ClampWidth(width);
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
            IsVisible = Definition.IsToggleable ? isVisible : true;
        }
        finally
        {
            _suppressCallbacks = false;
        }
    }

    internal TrackGridColumnSettings ToSettings()
    {
        return new TrackGridColumnSettings
        {
            Key = Key,
            IsVisible = IsVisible,
            DisplayIndex = DisplayIndex,
            Width = Definition.ClampWidth(Width)
        };
    }

    partial void OnIsVisibleChanged(bool value)
    {
        if (_suppressCallbacks)
        {
            return;
        }

        if (!Definition.IsToggleable && !value)
        {
            SetIsVisibleSilently(true);
            return;
        }

        _onVisibilityChanged(this);
    }
}
