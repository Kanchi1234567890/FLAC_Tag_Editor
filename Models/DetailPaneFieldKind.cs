using System;
using System.Collections.Generic;
using System.Linq;

namespace FLAC_Tag_Editor.Models;

/// <summary>
/// 右ペイン（詳細パネル）の各表示項目を論理的に識別する種別。
/// </summary>
public enum DetailPaneFieldKind
{
    None = 0,
    Artwork,
    Title,
    Artist,
    Album,
    AlbumArtist,
    Track,
    Disc,
    Year,
    Genre,
    Comment,
    UnregisteredCustomTags,
    FileName,
    FilePath,
    Duration,
    FileSize,
    AudioFormat
}

/// <summary>
/// 右ペイン表示項目のメタデータ定義（UI非依存）。
/// </summary>
public sealed record DetailPaneFieldDefinition(
    DetailPaneFieldKind Kind,
    string Key,
    string DisplayName,
    bool DefaultVisible);

/// <summary>
/// <see cref="DetailPaneFieldKind"/> に関する補助メソッドおよび既定定義。
/// </summary>
public static class DetailPaneFieldKindExtensions
{
    private static readonly DetailPaneFieldDefinition[] FieldDefinitions =
    [
        new(DetailPaneFieldKind.Artwork, "Artwork", "アートワーク", DefaultVisible: true),
        new(DetailPaneFieldKind.Title, "Title", "タイトル", DefaultVisible: true),
        new(DetailPaneFieldKind.Artist, "Artist", "アーティスト", DefaultVisible: true),
        new(DetailPaneFieldKind.Album, "Album", "アルバム", DefaultVisible: true),
        new(DetailPaneFieldKind.AlbumArtist, "AlbumArtist", "アルバムアーティスト", DefaultVisible: true),
        new(DetailPaneFieldKind.Track, "Track", "トラック (番号 / 総数)", DefaultVisible: true),
        new(DetailPaneFieldKind.Disc, "Disc", "ディスク (番号 / 総数)", DefaultVisible: true),
        new(DetailPaneFieldKind.Year, "Year", "年", DefaultVisible: true),
        new(DetailPaneFieldKind.Genre, "Genre", "ジャンル", DefaultVisible: true),
        new(DetailPaneFieldKind.Comment, "Comment", "コメント", DefaultVisible: true),
        new(DetailPaneFieldKind.UnregisteredCustomTags, "UnregisteredCustomTags", "未登録の独自タグ", DefaultVisible: true),
        new(DetailPaneFieldKind.FileName, "FileName", "ファイル名", DefaultVisible: true),
        new(DetailPaneFieldKind.FilePath, "FilePath", "フルパス", DefaultVisible: true),
        new(DetailPaneFieldKind.Duration, "Duration", "再生時間", DefaultVisible: true),
        new(DetailPaneFieldKind.FileSize, "FileSize", "ファイルサイズ", DefaultVisible: true),
        new(DetailPaneFieldKind.AudioFormat, "AudioFormat", "音声フォーマット", DefaultVisible: true)
    ];

    private static readonly Dictionary<DetailPaneFieldKind, DetailPaneFieldDefinition> ByKind =
        FieldDefinitions.ToDictionary(def => def.Kind);

    private static readonly Dictionary<string, DetailPaneFieldDefinition> ByKey =
        FieldDefinitions.ToDictionary(def => def.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// すべての右ペイン表示項目の既定定義一覧。
    /// </summary>
    public static IReadOnlyList<DetailPaneFieldDefinition> AllDefinitions => FieldDefinitions;

    public static DetailPaneFieldDefinition GetDefinition(this DetailPaneFieldKind kind)
    {
        return ByKind[kind];
    }

    public static bool TryGetDefinitionByKey(string? key, out DetailPaneFieldDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            definition = null!;
            return false;
        }

        return ByKey.TryGetValue(key.Trim(), out definition!);
    }
}
