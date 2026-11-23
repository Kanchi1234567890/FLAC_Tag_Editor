using System;
using System.Collections.Generic;
using System.Linq;

namespace FLAC_Tag_Editor.Models;

/// <summary>
/// アプリケーション全体の設定ルートモデル（TOML 永続化対象・UI非依存POCO）。
/// </summary>
public sealed class AppSettings
{
    public TrackGridSettings TrackGrid { get; set; } = new();
    public DetailPaneSettings DetailPane { get; set; } = new();
    public AudioPlayerSettings AudioPlayer { get; set; } = new();

    /// <summary>
    /// 既定値で初期化された <see cref="AppSettings"/> を生成する。
    /// </summary>
    public static AppSettings CreateDefault()
    {
        return new AppSettings
        {
            TrackGrid = TrackGridSettings.CreateDefault(),
            DetailPane = DetailPaneSettings.CreateDefault(),
            AudioPlayer = AudioPlayerSettings.CreateDefault()
        };
    }
}

/// <summary>
/// 簡易 FLAC プレーヤーの永続化設定モデル。
/// </summary>
public sealed class AudioPlayerSettings
{
    public const double MinVolume = 0.0;
    public const double MaxVolume = 1.0;
    public const double DefaultVolume = 0.8;

    public double Volume { get; set; } = DefaultVolume;
    public bool IsMuted { get; set; }

    public static AudioPlayerSettings CreateDefault()
    {
        return new AudioPlayerSettings
        {
            Volume = DefaultVolume,
            IsMuted = false
        };
    }

    public static double ClampVolume(double volume)
    {
        if (double.IsNaN(volume) || double.IsInfinity(volume))
        {
            return DefaultVolume;
        }

        return Math.Clamp(volume, MinVolume, MaxVolume);
    }
}

/// <summary>
/// トラック一覧 DataGrid の表示設定モデル。
/// </summary>
public sealed class TrackGridSettings
{
    public List<TrackGridColumnSettings> Columns { get; set; } = [];

    public static TrackGridSettings CreateDefault()
    {
        return new TrackGridSettings
        {
            Columns = TrackGridColumnKindExtensions.AllDefinitions
                .Select(def => new TrackGridColumnSettings
                {
                    Key = def.Key,
                    IsVisible = def.DefaultVisible,
                    DisplayIndex = def.DefaultDisplayIndex,
                    Width = def.DefaultWidth
                })
                .ToList()
        };
    }
}

/// <summary>
/// トラック一覧 DataGrid の 1 列分の永続化設定。
/// </summary>
public sealed class TrackGridColumnSettings
{
    public string Key { get; set; } = string.Empty;
    public bool IsVisible { get; set; } = true;
    public int DisplayIndex { get; set; }
    public double Width { get; set; }
}

/// <summary>
/// 右ペイン（詳細パネル）の表示項目設定モデル。
/// </summary>
public sealed class DetailPaneSettings
{
    public List<DetailPaneFieldSettings> Fields { get; set; } = [];

    public static DetailPaneSettings CreateDefault()
    {
        return new DetailPaneSettings
        {
            Fields = DetailPaneFieldKindExtensions.AllDefinitions
                .Select(def => new DetailPaneFieldSettings
                {
                    Key = def.Key,
                    IsVisible = def.DefaultVisible
                })
                .ToList()
        };
    }
}

/// <summary>
/// 右ペイン表示項目 1 件分の永続化設定。
/// </summary>
public sealed class DetailPaneFieldSettings
{
    public string Key { get; set; } = string.Empty;
    public bool IsVisible { get; set; } = true;
}

/// <summary>
/// 設定ファイルの読み込み結果。
/// </summary>
public sealed record AppSettingsLoadResult(
    AppSettings Settings,
    bool IsLoadedFromFile,
    bool HasLoadError,
    string? ErrorMessage = null);
