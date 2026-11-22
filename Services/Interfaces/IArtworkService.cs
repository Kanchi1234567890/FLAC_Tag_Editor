using System.Threading;
using System.Threading.Tasks;
using FLAC_Tag_Editor.Models;

namespace FLAC_Tag_Editor.Services.Interfaces;

/// <summary>
/// アートワーク画像のローカル読み込み・Web取得・D&D解決およびフォーマット正規化を担うサービスインターフェース。
/// </summary>
public interface IArtworkService
{
    /// <summary>
    /// ローカル画像ファイルを読み込み、FLAC埋め込み用に検証・正規化（JPEG/PNG保持、その他は透過判定のうえPNG/JPEG変換）して返す。
    /// </summary>
    Task<ArtworkImage> LoadFromFileAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 画像URL（http/https または data:image URI）から画像を取得し、FLAC埋め込み用に検証・正規化して返す。
    /// </summary>
    Task<ArtworkImage> DownloadFromUrlAsync(string imageUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// ドラッグ＆ドロップから抽出された候補データを解決し、アートワーク画像を返す。
    /// </summary>
    Task<ArtworkImage> ResolveFromDropAsync(ArtworkDropPayload payload, CancellationToken cancellationToken = default);
}
