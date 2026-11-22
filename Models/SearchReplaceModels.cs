using System;
using System.Collections.Generic;
using System.IO;

namespace FLAC_Tag_Editor.Models;

/// <summary>
/// 検索および一括置換の対象フィールド種別。
/// </summary>
public enum SearchReplaceTarget
{
    All = 0,
    FileName,
    Title,
    Artist,
    Album,
    AlbumArtist,
    Genre,
    Comment,
    CustomTags
}

/// <summary>
/// UI の対象フィールド選択用オプション。
/// </summary>
public sealed record SearchReplaceTargetOption(SearchReplaceTarget Target, string DisplayName);

/// <summary>
/// 検索・置換の条件パラメータ。
/// </summary>
public sealed record SearchReplaceQuery
{
    public string SearchPattern { get; init; } = string.Empty;
    public string Replacement { get; init; } = string.Empty;
    public SearchReplaceTarget Target { get; init; } = SearchReplaceTarget.All;
    public bool UseRegex { get; init; }
    public bool IgnoreCase { get; init; } = true;
    public bool PreserveFileExtension { get; init; } = true;

    public bool HasSearchPattern => !string.IsNullOrEmpty(SearchPattern);
}

/// <summary>
/// 単一テキストに対する置換結果。
/// </summary>
public sealed record TextReplaceResult(string ReplacedText, int MatchCount, bool IsChanged);

/// <summary>
/// ファイル名妥当性検証の結果。
/// </summary>
public sealed record FileNameValidationResult(bool IsValid, string? ErrorMessage)
{
    public static FileNameValidationResult Valid { get; } = new(true, null);

    public static FileNameValidationResult Invalid(string errorMessage) => new(false, errorMessage);
}

/// <summary>
/// Windows 環境の FLAC ファイル名に関するドメイン検証ルール。
/// </summary>
public static class FlacFileNameValidator
{
    private const string RequiredFlacExtension = ".flac";
    private const int MaxFileNameLength = 255;

    private static readonly char[] AdditionalInvalidFileNameChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    private static readonly HashSet<string> ReservedWindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static FileNameValidationResult Validate(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return FileNameValidationResult.Invalid("ファイル名が空です。");
        }

        if (fileName.Length > MaxFileNameLength)
        {
            return FileNameValidationResult.Invalid($"ファイル名が長すぎます（最大 {MaxFileNameLength} 文字）。");
        }

        if (fileName.EndsWith('.') || fileName.EndsWith(' '))
        {
            return FileNameValidationResult.Invalid("ファイル名の末尾にピリオドや空白は使用できません。");
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        foreach (var ch in fileName)
        {
            if (char.IsControl(ch)
                || Array.IndexOf(invalidChars, ch) >= 0
                || Array.IndexOf(AdditionalInvalidFileNameChars, ch) >= 0)
            {
                return FileNameValidationResult.Invalid("ファイル名に使用できない文字が含まれています。");
            }
        }

        var extension = Path.GetExtension(fileName);
        if (!string.Equals(extension, RequiredFlacExtension, StringComparison.OrdinalIgnoreCase))
        {
            return FileNameValidationResult.Invalid($"拡張子は {RequiredFlacExtension} である必要があります。");
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return FileNameValidationResult.Invalid("拡張子以外のファイル名が空です。");
        }

        var normalizedBaseName = baseName.TrimEnd('.', ' ');
        if (ReservedWindowsDeviceNames.Contains(normalizedBaseName))
        {
            return FileNameValidationResult.Invalid($"「{normalizedBaseName}」は Windows の予約名のため使用できません。");
        }

        return FileNameValidationResult.Valid;
    }
}

/// <summary>
/// 置換一覧プレビューの1変更項目。
/// </summary>
public sealed record SearchReplacePreviewItem
{
    public required string TrackFilePath { get; init; }
    public required string TrackDisplayName { get; init; }
    public required SearchReplaceTarget FieldTarget { get; init; }
    public required string FieldDisplayName { get; init; }
    public required string BeforeValue { get; init; }
    public required string AfterValue { get; init; }
    public int MatchCount { get; init; }
    public string? WarningMessage { get; init; }

    public bool HasWarning => !string.IsNullOrEmpty(WarningMessage);

    public string StatusDisplay => HasWarning ? WarningMessage! : "置換可";
}
