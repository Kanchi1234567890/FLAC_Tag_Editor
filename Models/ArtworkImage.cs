namespace FLAC_Tag_Editor.Models;

/// <summary>
/// 読み込み・検証・変換済みのアートワーク画像データ（UI非依存）。
/// </summary>
public sealed record ArtworkImage(
    byte[] Data,
    string MimeType,
    bool WasConverted = false,
    string? OriginalFormatName = null);
