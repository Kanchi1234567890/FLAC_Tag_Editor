using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FLAC_Tag_Editor.Models;
using FLAC_Tag_Editor.Services.Interfaces;
using SkiaSharp;

namespace FLAC_Tag_Editor.Services.Implementations;

/// <summary>
/// アートワーク画像のローカル読み込み・HttpClientによるWeb取得・D&Dペイロード解決およびJPEG/PNG正規化を行うサービス実装。
/// </summary>
public sealed partial class ArtworkService : IArtworkService, IDisposable
{
    private const long MaxImageSizeBytes = 20L * 1024 * 1024;
    private const int HttpTimeoutSeconds = 15;
    private const int DownloadBufferSize = 81920;
    private const int JpegEncodeQuality = 92;
    private const int PngEncodeQuality = 100;
    private const byte OpaqueAlphaValue = 255;

    private const string JpegMimeType = "image/jpeg";
    private const string PngMimeType = "image/png";
    private const string DataImagePrefix = "data:image/";
    private const string Base64Marker = ";base64,";
    private const string UserAgentHeaderValue = "FLAC-Tag-Editor/1.0 (+https://localhost)";

    private readonly HttpClient _httpClient;
    private bool _disposed;

    public ArtworkService()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = true
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(HttpTimeoutSeconds)
        };
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgentHeaderValue);
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "image/jpeg,image/png,image/webp,image/*,*/*;q=0.8");
    }

    public async Task<ArtworkImage> LoadFromFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("指定された画像ファイルが見つかりません。", filePath);
        }

        if (fileInfo.Length == 0)
        {
            throw new InvalidOperationException("画像ファイルが空です。");
        }

        if (fileInfo.Length > MaxImageSizeBytes)
        {
            throw new InvalidOperationException($"画像サイズが上限（{MaxImageSizeBytes / (1024 * 1024)} MB）を超えています。");
        }

        var rawBytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => NormalizeImageBytes(rawBytes), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArtworkImage> DownloadFromUrlAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageUrl);

        var trimmed = imageUrl.Trim();
        if (TryDecodeDataUri(trimmed, out var dataUriBytes))
        {
            return await Task.Run(() => NormalizeImageBytes(dataUriBytes), cancellationToken).ConfigureAwait(false);
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("対応していない画像 URL 形式です（http / https のみ対応）。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"画像のダウンロードに失敗しました (HTTP {(int)response.StatusCode})。");
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!string.IsNullOrWhiteSpace(contentType)
            && !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(contentType, "binary/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"指定された URL は画像データではありません ({contentType})。");
        }

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > MaxImageSizeBytes)
        {
            throw new InvalidOperationException($"画像サイズが上限（{MaxImageSizeBytes / (1024 * 1024)} MB）を超えています。");
        }

        await using var responseStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var rawBytes = await ReadStreamWithLimitAsync(responseStream, cancellationToken).ConfigureAwait(false);

        return await Task.Run(() => NormalizeImageBytes(rawBytes), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArtworkImage> ResolveFromDropAsync(
        ArtworkDropPayload payload,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(payload);

        Exception? lastError = null;

        // 1. ローカルファイル（エクスプローラーやブラウザの一時ファイル）
        foreach (var filePath in payload.FilePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                continue;
            }

            try
            {
                return await LoadFromFileAsync(filePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }
        }

        // 2. HTML断片内の <img src="...">（Webブラウザからのリンク付き画像D&Dを最優先で解決）
        if (!string.IsNullOrWhiteSpace(payload.HtmlContent))
        {
            var baseUri = ExtractSourceUriFromHtmlFormat(payload.HtmlContent);
            foreach (var imgSrc in ExtractImageUrlsFromHtml(payload.HtmlContent, baseUri))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return await ResolveStringCandidateAsync(imgSrc, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                }
            }
        }

        // 3. URI / プレーンテキスト候補（画像URL・Data URI・ローカルパス）
        var seenCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawText in payload.TextCandidates)
        {
            foreach (var candidate in ExtractUrlOrPathCandidates(rawText))
            {
                if (!seenCandidates.Add(candidate))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return await ResolveStringCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                }
            }
        }

        // 4. フォールバックの直接Bitmapデータ
        if (payload.FallbackBitmapBytes is { Length: > 0 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await Task.Run(
                    () => NormalizeImageBytes(payload.FallbackBitmapBytes),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new InvalidOperationException("ドロップされたデータから有効な画像を取得できませんでした。");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
    }

    private Task<ArtworkImage> ResolveStringCandidateAsync(string candidate, CancellationToken cancellationToken)
    {
        if (candidate.StartsWith(DataImagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return DownloadFromUrlAsync(candidate, cancellationToken);
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
            {
                return LoadFromFileAsync(uri.LocalPath, cancellationToken);
            }

            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            {
                return DownloadFromUrlAsync(uri.AbsoluteUri, cancellationToken);
            }
        }

        if (File.Exists(candidate))
        {
            return LoadFromFileAsync(candidate, cancellationToken);
        }

        throw new InvalidOperationException("有効な画像ファイルまたは画像 URL ではありません。");
    }

    private static ArtworkImage NormalizeImageBytes(byte[] rawBytes)
    {
        if (rawBytes is null || rawBytes.Length == 0)
        {
            throw new InvalidOperationException("画像データが空です。");
        }

        if (rawBytes.Length > MaxImageSizeBytes)
        {
            throw new InvalidOperationException($"画像サイズが上限（{MaxImageSizeBytes / (1024 * 1024)} MB）を超えています。");
        }

        using var skData = SKData.CreateCopy(rawBytes);
        using var codec = SKCodec.Create(skData);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
        {
            throw new InvalidOperationException("対応していない画像形式、または破損した画像データです。");
        }

        var encodedFormat = codec.EncodedFormat;
        if (encodedFormat == SKEncodedImageFormat.Jpeg)
        {
            return new ArtworkImage(rawBytes, JpegMimeType, WasConverted: false, OriginalFormatName: "JPEG");
        }

        if (encodedFormat == SKEncodedImageFormat.Png)
        {
            return new ArtworkImage(rawBytes, PngMimeType, WasConverted: false, OriginalFormatName: "PNG");
        }

        // WebP / BMP / GIF 等の非標準フォーマットは透過判定のうえ PNG または JPEG に変換
        var decodeInfo = new SKImageInfo(
            codec.Info.Width,
            codec.Info.Height,
            SKColorType.Bgra8888,
            codec.Info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul);

        using var bitmap = new SKBitmap(decodeInfo);
        var decodeResult = codec.GetPixels(decodeInfo, bitmap.GetPixels());
        if (decodeResult != SKCodecResult.Success && decodeResult != SKCodecResult.IncompleteInput)
        {
            throw new InvalidOperationException("画像のデコードに失敗しました。");
        }

        var hasTransparency = HasTransparency(bitmap);
        var targetFormat = hasTransparency ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
        var targetMimeType = hasTransparency ? PngMimeType : JpegMimeType;
        var encodeQuality = hasTransparency ? PngEncodeQuality : JpegEncodeQuality;

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(targetFormat, encodeQuality);
        if (encoded is null)
        {
            throw new InvalidOperationException("画像のフォーマット変換に失敗しました。");
        }

        return new ArtworkImage(
            encoded.ToArray(),
            targetMimeType,
            WasConverted: true,
            OriginalFormatName: encodedFormat.ToString().ToUpperInvariant());
    }

    private static bool HasTransparency(SKBitmap bitmap)
    {
        if (bitmap.AlphaType == SKAlphaType.Opaque)
        {
            return false;
        }

        var pixelBytes = bitmap.GetPixelSpan();
        // SKColorType.Bgra8888 のため 4 バイトごとに index+3 が Alpha チャンネル
        for (var i = 3; i < pixelBytes.Length; i += 4)
        {
            if (pixelBytes[i] < OpaqueAlphaValue)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<byte[]> ReadStreamWithLimitAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var memoryStream = new MemoryStream();
        var buffer = new byte[DownloadBufferSize];
        long totalRead = 0;

        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;
            if (totalRead > MaxImageSizeBytes)
            {
                throw new InvalidOperationException($"画像サイズが上限（{MaxImageSizeBytes / (1024 * 1024)} MB）を超えています。");
            }

            memoryStream.Write(buffer, 0, bytesRead);
        }

        if (memoryStream.Length == 0)
        {
            throw new InvalidOperationException("ダウンロードされた画像データが空です。");
        }

        return memoryStream.ToArray();
    }

    private static bool TryDecodeDataUri(string value, out byte[] bytes)
    {
        bytes = [];
        if (!value.StartsWith(DataImagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var markerIndex = value.IndexOf(Base64Marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var base64Part = value[(markerIndex + Base64Marker.Length)..].Trim();
        if (base64Part.Length == 0)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(base64Part);
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static Uri? ExtractSourceUriFromHtmlFormat(string htmlContent)
    {
        var match = SourceUrlRegex().Match(htmlContent);
        if (!match.Success)
        {
            return null;
        }

        var rawUrl = match.Groups[1].Value.Trim();
        return Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static IEnumerable<string> ExtractImageUrlsFromHtml(string htmlContent, Uri? baseUri)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in ImgSrcRegex().Matches(htmlContent))
        {
            var rawSrc = match.Groups[1].Success
                ? match.Groups[1].Value
                : match.Groups[2].Success
                    ? match.Groups[2].Value
                    : match.Groups[3].Value;

            if (string.IsNullOrWhiteSpace(rawSrc))
            {
                continue;
            }

            var decoded = WebUtility.HtmlDecode(rawSrc).Trim();
            if (decoded.Length == 0)
            {
                continue;
            }

            if (decoded.StartsWith(DataImagePrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (yielded.Add(decoded))
                {
                    yield return decoded;
                }

                continue;
            }

            if (Uri.TryCreate(decoded, UriKind.Absolute, out var absoluteUri))
            {
                if (yielded.Add(absoluteUri.AbsoluteUri))
                {
                    yield return absoluteUri.AbsoluteUri;
                }

                continue;
            }

            if (baseUri is not null && Uri.TryCreate(baseUri, decoded, out var resolvedUri))
            {
                if (yielded.Add(resolvedUri.AbsoluteUri))
                {
                    yield return resolvedUri.AbsoluteUri;
                }
            }
        }
    }

    private static IEnumerable<string> ExtractUrlOrPathCandidates(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            yield break;
        }

        var trimmed = rawText.Trim().Trim('\0');
        if (trimmed.StartsWith(DataImagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            yield return trimmed;
            yield break;
        }

        var lines = trimmed.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            var cleaned = line.Trim().Trim('"', '\'');
            if (cleaned.Length > 0)
            {
                yield return cleaned;
            }
        }
    }

    [GeneratedRegex(@"^SourceURL:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SourceUrlRegex();

    [GeneratedRegex(@"<img\b[^>]*?\bsrc\s*=\s*(?:""([^""]+)""|'([^']+)'|([^\s>]+))", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ImgSrcRegex();
}
