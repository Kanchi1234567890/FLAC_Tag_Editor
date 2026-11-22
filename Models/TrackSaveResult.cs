using System.Collections.Generic;

namespace FLAC_Tag_Editor.Models;

/// <summary>
/// FLACファイル群の保存結果（保存成功した最新トラック一覧と失敗エラー一覧）。
/// </summary>
public sealed class TrackSaveResult
{
    public IReadOnlyList<FlacTrack> SavedTracks { get; init; } = [];
    public IReadOnlyList<TrackSaveError> Errors { get; init; } = [];
}

/// <summary>
/// 個別ファイルの保存失敗情報。
/// </summary>
public sealed record TrackSaveError(string FilePath, string Message);
