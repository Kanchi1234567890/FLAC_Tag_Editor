using FLAC_Tag_Editor.Models;

namespace FLAC_Tag_Editor.Services.Interfaces;

/// <summary>
/// テキストおよびファイル名に対する検索・正規表現置換・妥当性検証を担うサービスインターフェース。
/// </summary>
public interface ITextSearchReplaceService
{
    /// <summary>
    /// 検索パターン（特に正規表現構文）の妥当性を検証し、問題なければ null を返す。
    /// </summary>
    string? ValidatePattern(SearchReplaceQuery query);

    /// <summary>
    /// 指定テキスト内の検索パターン一致件数を返す。
    /// </summary>
    int CountMatches(string? input, SearchReplaceQuery query, bool isFileName = false);

    /// <summary>
    /// 指定テキストに対して検索・置換を実行し、結果を返す。
    /// </summary>
    TextReplaceResult Replace(string? input, SearchReplaceQuery query, bool isFileName = false);

    /// <summary>
    /// Windows 環境の FLAC ファイル名として妥当かどうかを検証する。
    /// </summary>
    FileNameValidationResult ValidateFileName(string? fileName);
}
