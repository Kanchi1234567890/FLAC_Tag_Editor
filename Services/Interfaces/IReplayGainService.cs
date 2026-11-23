using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FLAC_Tag_Editor.Models;

namespace FLAC_Tag_Editor.Services.Interfaces;

/// <summary>
/// FFmpeg 外部プロセスを用いて FLAC ファイルの ReplayGain を解析するサービスインターフェース。
/// </summary>
public interface IReplayGainService
{
    /// <summary>
    /// 指定パスまたは自動検出で FFmpeg 実行ファイルの存在と動作を検証する。
    /// </summary>
    Task<FfmpegValidationResult> ValidateFfmpegAsync(
        string? configuredPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定されたトラック一覧に対して ReplayGain（トラック／アルバム）を計算する。
    /// </summary>
    Task<ReplayGainBatchResult> CalculateAsync(
        IReadOnlyList<ReplayGainTrackInput> tracks,
        ReplayGainOptions options,
        IProgress<ReplayGainProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
