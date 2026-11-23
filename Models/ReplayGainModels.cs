using System;
using System.Collections.Generic;
using System.Globalization;

namespace FLAC_Tag_Editor.Models;

/// <summary>
/// Vorbis Comment における ReplayGain 標準タグキーおよび書式化ルール。
/// </summary>
public static class ReplayGainTagKeys
{
    public const string TrackGain = "REPLAYGAIN_TRACK_GAIN";
    public const string TrackPeak = "REPLAYGAIN_TRACK_PEAK";
    public const string AlbumGain = "REPLAYGAIN_ALBUM_GAIN";
    public const string AlbumPeak = "REPLAYGAIN_ALBUM_PEAK";

    private const string ReplayGainKeyPrefix = "REPLAYGAIN_";
    private const double ZeroGainThreshold = 0.005;

    /// <summary>
    /// 指定された Vorbis Comment キーが ReplayGain 関連タグ（<c>REPLAYGAIN_*</c>）かどうかを判定する。
    /// </summary>
    public static bool IsReplayGainKey(string? key)
    {
        var trimmed = key?.Trim();
        return !string.IsNullOrWhiteSpace(trimmed)
               && trimmed.StartsWith(ReplayGainKeyPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ゲイン値（dB）を ReplayGain 標準書式（例: <c>-6.42 dB</c>, <c>+0.00 dB</c>）へ変換する。
    /// </summary>
    public static string FormatGain(double gainDb)
    {
        var normalized = Math.Abs(gainDb) < ZeroGainThreshold ? 0.0 : gainDb;
        return string.Format(CultureInfo.InvariantCulture, "{0:+0.00;-0.00;+0.00} dB", normalized);
    }

    /// <summary>
    /// ピーク値を ReplayGain 標準書式（例: <c>0.987654</c>）へ変換する。
    /// </summary>
    public static string FormatPeak(double peak)
    {
        var normalized = double.IsNaN(peak) || double.IsInfinity(peak)
            ? 0.0
            : Math.Max(0.0, peak);
        return normalized.ToString("F6", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// ReplayGain 計算モード。
/// </summary>
public enum ReplayGainCalculationMode
{
    TrackAndAlbum,
    TrackOnly
}

/// <summary>
/// アルバム ReplayGain 計算時のグルーピング基準。
/// </summary>
public enum ReplayGainAlbumGroupingMode
{
    ByAlbumTag,
    ByFolder,
    SingleAlbum
}

/// <summary>
/// アルバムグルーピング基準の選択肢表示モデル。
/// </summary>
public sealed record ReplayGainAlbumGroupingOption(
    ReplayGainAlbumGroupingMode Mode,
    string DisplayName)
{
    public static IReadOnlyList<ReplayGainAlbumGroupingOption> AllOptions { get; } =
    [
        new(ReplayGainAlbumGroupingMode.ByAlbumTag, "アルバムタグ基準（Album + AlbumArtist、空欄はフォルダ）"),
        new(ReplayGainAlbumGroupingMode.ByFolder, "フォルダ単位（同一フォルダを 1 アルバムとみなす）"),
        new(ReplayGainAlbumGroupingMode.SingleAlbum, "単一アルバム扱い（対象すべてを 1 アルバムとして計算）")
    ];
}

/// <summary>
/// ReplayGain 解析対象の 1 トラック分の入力スナップショット（UI 非依存 POCO）。
/// </summary>
public sealed record ReplayGainTrackInput(
    string FilePath,
    string FileName,
    string DirectoryPath,
    string Album,
    string AlbumArtist,
    int SampleRate,
    int BitsPerSample,
    int Channels,
    bool HasReplayGainTags = false);

/// <summary>
/// ReplayGain 計算実行オプション。
/// </summary>
public sealed record ReplayGainOptions(
    string? FfmpegPath,
    ReplayGainCalculationMode Mode,
    ReplayGainAlbumGroupingMode AlbumGrouping);

/// <summary>
/// ReplayGain 計算の進行フェーズ。
/// </summary>
public enum ReplayGainProgressPhase
{
    ValidatingFfmpeg,
    AnalyzingTracks,
    AnalyzingAlbums,
    Completed
}

/// <summary>
/// ReplayGain 計算の進捗情報。
/// </summary>
public sealed record ReplayGainProgress(
    ReplayGainProgressPhase Phase,
    int CompletedSteps,
    int TotalSteps,
    string CurrentItemName,
    string StatusText)
{
    public double ProgressPercent => TotalSteps > 0
        ? Math.Clamp((double)CompletedSteps / TotalSteps * 100.0, 0.0, 100.0)
        : 0.0;
}

/// <summary>
/// FFmpeg 実行ファイルの検証結果。
/// </summary>
public sealed record FfmpegValidationResult(
    bool IsAvailable,
    string? ResolvedPath,
    string? VersionText,
    string? ErrorMessage,
    bool IsAutoDetected);

/// <summary>
/// 1 トラック分の ReplayGain 計算結果。
/// </summary>
public sealed record ReplayGainTrackResult(
    string FilePath,
    string FileName,
    string AlbumGroupLabel,
    bool Succeeded,
    double? TrackGainDb,
    double? TrackPeak,
    double? AlbumGainDb,
    double? AlbumPeak,
    string? ErrorMessage = null)
{
    public string TrackGainText => TrackGainDb.HasValue
        ? ReplayGainTagKeys.FormatGain(TrackGainDb.Value)
        : string.Empty;

    public string TrackPeakText => TrackPeak.HasValue
        ? ReplayGainTagKeys.FormatPeak(TrackPeak.Value)
        : string.Empty;

    public string AlbumGainText => AlbumGainDb.HasValue
        ? ReplayGainTagKeys.FormatGain(AlbumGainDb.Value)
        : string.Empty;

    public string AlbumPeakText => AlbumPeak.HasValue
        ? ReplayGainTagKeys.FormatPeak(AlbumPeak.Value)
        : string.Empty;

    public bool HasError => !Succeeded || !string.IsNullOrWhiteSpace(ErrorMessage);

    public string StatusDisplay => !Succeeded
        ? (string.IsNullOrWhiteSpace(ErrorMessage) ? "エラー" : $"エラー: {ErrorMessage}")
        : (!string.IsNullOrWhiteSpace(ErrorMessage) ? $"一部警告: {ErrorMessage}" : "完了");
}

/// <summary>
/// ReplayGain 一括計算の最終結果。
/// </summary>
public sealed record ReplayGainBatchResult(
    IReadOnlyList<ReplayGainTrackResult> TrackResults,
    string ResolvedFfmpegPath,
    int AlbumGroupCount,
    int SucceededCount,
    int FailedCount);

/// <summary>
/// ReplayGain 計算ダイアログへ渡す起動コンテキスト。
/// </summary>
public sealed record ReplayGainDialogContext(
    IReadOnlyList<ReplayGainTrackInput> AllTracks,
    IReadOnlyList<ReplayGainTrackInput> SelectedTracks,
    bool PreferSelectedTracks,
    string InitialFfmpegPath);

/// <summary>
/// ReplayGain 計算ダイアログから確定されたアクション種別。
/// </summary>
public enum ReplayGainDialogAction
{
    ApplyCalculated,
    ClearTags
}

/// <summary>
/// ReplayGain 計算ダイアログの確定結果。
/// </summary>
public sealed record ReplayGainDialogResult(
    ReplayGainDialogAction Action,
    IReadOnlyList<ReplayGainTrackResult> TrackResults,
    ReplayGainCalculationMode CalculationMode,
    bool AutoSaveAfterApply,
    IReadOnlyList<string>? ClearTargetFilePaths = null);
