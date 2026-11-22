using System.Collections.Generic;

namespace FLAC_Tag_Editor.Models;

/// <summary>
/// ドラッグ＆ドロップ操作から抽出されたアートワーク候補データ（UI非依存）。
/// </summary>
public sealed class ArtworkDropPayload
{
    public IReadOnlyList<string> FilePaths { get; init; } = [];

    public string? HtmlContent { get; init; }

    public IReadOnlyList<string> TextCandidates { get; init; } = [];

    public byte[]? FallbackBitmapBytes { get; init; }

    public bool HasAnyData =>
        FilePaths.Count > 0
        || !string.IsNullOrWhiteSpace(HtmlContent)
        || TextCandidates.Count > 0
        || FallbackBitmapBytes is { Length: > 0 };
}
