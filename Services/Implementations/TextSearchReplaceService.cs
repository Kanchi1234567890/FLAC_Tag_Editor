using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using FLAC_Tag_Editor.Models;
using FLAC_Tag_Editor.Services.Interfaces;

namespace FLAC_Tag_Editor.Services.Implementations;

/// <summary>
/// 正規表現・通常テキストの検索および置換、FLAC ファイル名妥当性検証を提供するサービス実装。
/// </summary>
public sealed class TextSearchReplaceService : ITextSearchReplaceService
{
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(500);

    public string? ValidatePattern(SearchReplaceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!query.HasSearchPattern || !query.UseRegex)
        {
            return null;
        }

        try
        {
            _ = CreateRegex(query);
            return null;
        }
        catch (ArgumentException ex)
        {
            return $"正規表現エラー: {ex.Message}";
        }
    }

    public int CountMatches(string? input, SearchReplaceQuery query, bool isFileName = false)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!query.HasSearchPattern)
        {
            return 0;
        }

        var source = input ?? string.Empty;
        var (targetText, _) = SplitFileNameScope(source, isFileName && query.PreserveFileExtension);

        if (query.UseRegex)
        {
            var regex = CreateRegex(query);
            return regex.Matches(targetText).Count;
        }

        return CountPlainMatches(targetText, query.SearchPattern, query.IgnoreCase);
    }

    public TextReplaceResult Replace(string? input, SearchReplaceQuery query, bool isFileName = false)
    {
        ArgumentNullException.ThrowIfNull(query);

        var source = input ?? string.Empty;
        if (!query.HasSearchPattern)
        {
            return new TextReplaceResult(source, 0, false);
        }

        var (targetText, preservedExtension) = SplitFileNameScope(source, isFileName && query.PreserveFileExtension);
        var replacement = query.Replacement ?? string.Empty;

        string replacedTarget;
        int matchCount;

        if (query.UseRegex)
        {
            var regex = CreateRegex(query);
            matchCount = regex.Matches(targetText).Count;
            if (matchCount == 0)
            {
                return new TextReplaceResult(source, 0, false);
            }

            replacedTarget = regex.Replace(targetText, replacement);
        }
        else
        {
            (replacedTarget, matchCount) = ReplacePlainText(targetText, query.SearchPattern, replacement, query.IgnoreCase);
            if (matchCount == 0)
            {
                return new TextReplaceResult(source, 0, false);
            }
        }

        var finalText = preservedExtension.Length > 0
            ? replacedTarget + preservedExtension
            : replacedTarget;

        var isChanged = !string.Equals(source, finalText, StringComparison.Ordinal);
        return new TextReplaceResult(finalText, matchCount, isChanged);
    }

    public FileNameValidationResult ValidateFileName(string? fileName)
    {
        return FlacFileNameValidator.Validate(fileName);
    }

    private static Regex CreateRegex(SearchReplaceQuery query)
    {
        var options = RegexOptions.CultureInvariant;
        if (query.IgnoreCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        return new Regex(query.SearchPattern, options, RegexMatchTimeout);
    }

    private static (string TargetText, string PreservedExtension) SplitFileNameScope(string source, bool preserveExtension)
    {
        if (!preserveExtension || string.IsNullOrEmpty(source))
        {
            return (source, string.Empty);
        }

        var extension = Path.GetExtension(source);
        if (string.IsNullOrEmpty(extension) || extension.Length >= source.Length)
        {
            return (source, string.Empty);
        }

        return (source[..^extension.Length], extension);
    }

    private static int CountPlainMatches(string input, string searchPattern, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(searchPattern))
        {
            return 0;
        }

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var count = 0;
        var index = 0;

        while (index <= input.Length - searchPattern.Length)
        {
            var foundIndex = input.IndexOf(searchPattern, index, comparison);
            if (foundIndex < 0)
            {
                break;
            }

            count++;
            index = foundIndex + searchPattern.Length;
        }

        return count;
    }

    private static (string ReplacedText, int MatchCount) ReplacePlainText(
        string input,
        string searchPattern,
        string replacement,
        bool ignoreCase)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(searchPattern))
        {
            return (input, 0);
        }

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var firstIndex = input.IndexOf(searchPattern, 0, comparison);
        if (firstIndex < 0)
        {
            return (input, 0);
        }

        var builder = new StringBuilder(input.Length);
        var matchCount = 0;
        var currentIndex = 0;
        var searchIndex = firstIndex;

        while (searchIndex >= 0)
        {
            builder.Append(input, currentIndex, searchIndex - currentIndex);
            builder.Append(replacement);
            matchCount++;

            currentIndex = searchIndex + searchPattern.Length;
            searchIndex = currentIndex <= input.Length - searchPattern.Length
                ? input.IndexOf(searchPattern, currentIndex, comparison)
                : -1;
        }

        builder.Append(input, currentIndex, input.Length - currentIndex);
        return (builder.ToString(), matchCount);
    }
}
