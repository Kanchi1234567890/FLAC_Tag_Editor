using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FLAC_Tag_Editor.Models;

namespace FLAC_Tag_Editor.Services.Interfaces;

/// <summary>
/// 独自タグ定義カタログ（<c>custom_tags.toml</c>）の読み込みおよび保存を抽象化するサービスインターフェース。
/// </summary>
public interface ICustomTagDefinitionService
{
    /// <summary>
    /// 独自タグ定義ファイルの保存先フルパス。
    /// </summary>
    string DefinitionsFilePath { get; }

    /// <summary>
    /// 独自タグ定義ファイルを読み込む。未作成時は空リストを返し、破損・I/Oエラー時は空リストとエラー情報を返す。
    /// </summary>
    Task<CustomTagDefinitionLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定された独自タグ定義一覧を永続化する。
    /// </summary>
    Task SaveAsync(IReadOnlyList<CustomTagDefinition> definitions, CancellationToken cancellationToken = default);
}
