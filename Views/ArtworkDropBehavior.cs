using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using FLAC_Tag_Editor.Models;

namespace FLAC_Tag_Editor.Views;

/// <summary>
/// アートワーク領域へのファイルおよびWeb画像URLのドラッグ＆ドロップをViewModelコマンドへ橋渡しする添付ビヘイビア。
/// </summary>
public static class ArtworkDropBehavior
{
    private const string DragOverClassName = "drag-over";

    private static readonly DataFormat<string> HtmlFormatString = DataFormat.CreateStringPlatformFormat("HTML Format");
    private static readonly DataFormat<byte[]> HtmlFormatBytes = DataFormat.CreateBytesPlatformFormat("HTML Format");
    private static readonly DataFormat<string> TextHtmlString = DataFormat.CreateStringPlatformFormat("text/html");
    private static readonly DataFormat<byte[]> TextHtmlBytes = DataFormat.CreateBytesPlatformFormat("text/html");
    private static readonly DataFormat<string> UrlWideString = DataFormat.CreateStringPlatformFormat("UniformResourceLocatorW");
    private static readonly DataFormat<byte[]> UrlWideBytes = DataFormat.CreateBytesPlatformFormat("UniformResourceLocatorW");
    private static readonly DataFormat<string> UrlAnsiString = DataFormat.CreateStringPlatformFormat("UniformResourceLocator");
    private static readonly DataFormat<byte[]> UrlAnsiBytes = DataFormat.CreateBytesPlatformFormat("UniformResourceLocator");
    private static readonly DataFormat<string> UriListString = DataFormat.CreateStringPlatformFormat("text/uri-list");
    private static readonly DataFormat<byte[]> UriListBytes = DataFormat.CreateBytesPlatformFormat("text/uri-list");

    public static readonly AttachedProperty<ICommand?> DropCommandProperty =
        AvaloniaProperty.RegisterAttached<Control, ICommand?>(
            "DropCommand",
            typeof(ArtworkDropBehavior));

    public static readonly AttachedProperty<bool> IsDragOverProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>(
            "IsDragOver",
            typeof(ArtworkDropBehavior),
            defaultValue: false);

    static ArtworkDropBehavior()
    {
        DropCommandProperty.Changed.AddClassHandler<Control>(OnDropCommandChanged);
    }

    public static ICommand? GetDropCommand(Control element) =>
        element.GetValue(DropCommandProperty);

    public static void SetDropCommand(Control element, ICommand? value) =>
        element.SetValue(DropCommandProperty, value);

    public static bool GetIsDragOver(Control element) =>
        element.GetValue(IsDragOverProperty);

    public static void SetIsDragOver(Control element, bool value) =>
        element.SetValue(IsDragOverProperty, value);

    private static void OnDropCommandChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        var hadCommand = e.OldValue is ICommand;
        var hasCommand = e.NewValue is ICommand;

        if (!hadCommand && hasCommand)
        {
            DragDrop.SetAllowDrop(control, true);
            control.AddHandler(DragDrop.DragEnterEvent, OnDragOver);
            control.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            control.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
            control.AddHandler(DragDrop.DropEvent, OnDrop);
        }
        else if (hadCommand && !hasCommand)
        {
            control.RemoveHandler(DragDrop.DragEnterEvent, OnDragOver);
            control.RemoveHandler(DragDrop.DragOverEvent, OnDragOver);
            control.RemoveHandler(DragDrop.DragLeaveEvent, OnDragLeave);
            control.RemoveHandler(DragDrop.DropEvent, OnDrop);
            SetDragOverState(control, false);
        }
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Control control)
        {
            return;
        }

        var command = GetDropCommand(control);
        var canDrop = command is not null
                      && command.CanExecute(null)
                      && HasSupportedData(e.DataTransfer);

        e.DragEffects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        SetDragOverState(control, canDrop);
        e.Handled = true;
    }

    private static void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is Control control)
        {
            SetDragOverState(control, false);
        }
    }

    private static void OnDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Control control)
        {
            return;
        }

        SetDragOverState(control, false);

        var command = GetDropCommand(control);
        if (command is null)
        {
            return;
        }

        var payload = ExtractPayload(e.DataTransfer);
        if (command.CanExecute(payload))
        {
            command.Execute(payload);
        }

        e.Handled = true;
    }

    private static void SetDragOverState(Control control, bool isDragOver)
    {
        control.SetValue(IsDragOverProperty, isDragOver);
        control.Classes.Set(DragOverClassName, isDragOver);
    }

    private static bool HasSupportedData(IDataTransfer dataTransfer)
    {
        if (dataTransfer.Contains(DataFormat.File)
            || dataTransfer.Contains(DataFormat.Text)
            || dataTransfer.Contains(DataFormat.Bitmap))
        {
            return true;
        }

        foreach (var format in dataTransfer.Formats)
        {
            var id = format.Identifier;
            if (IsHtmlFormatIdentifier(id) || IsUrlFormatIdentifier(id))
            {
                return true;
            }
        }

        return false;
    }

    private static ArtworkDropPayload ExtractPayload(IDataTransfer dataTransfer)
    {
        var filePaths = new List<string>();
        var textCandidates = new List<string>();
        string? htmlContent = null;

        var files = TryGet(() => dataTransfer.TryGetFiles());
        if (files is { Length: > 0 })
        {
            foreach (var storageItem in files)
            {
                var localPath = TryGet(() => storageItem.TryGetLocalPath());
                if (!string.IsNullOrWhiteSpace(localPath))
                {
                    filePaths.Add(localPath);
                }
            }
        }

        // アイテムが公開しているフォーマットを走査して HTML / URL を抽出
        foreach (var item in dataTransfer.Items)
        {
            foreach (var format in item.Formats)
            {
                var id = format.Identifier;
                if (htmlContent is null && IsHtmlFormatIdentifier(id))
                {
                    var extracted = TryExtractRawString(item, format, isUtf16: false);
                    if (!string.IsNullOrWhiteSpace(extracted))
                    {
                        htmlContent = extracted;
                    }
                }
                else if (IsUrlFormatIdentifier(id))
                {
                    var isUtf16 = string.Equals(id, "UniformResourceLocatorW", StringComparison.OrdinalIgnoreCase);
                    var extracted = TryExtractRawString(item, format, isUtf16);
                    if (!string.IsNullOrWhiteSpace(extracted))
                    {
                        textCandidates.Add(extracted);
                    }
                }
            }
        }

        // 明示的なプラットフォームフォーマット経由でもフォールバック取得
        htmlContent ??= TryGetPlatformString(dataTransfer, HtmlFormatString, HtmlFormatBytes, isUtf16: false)
                        ?? TryGetPlatformString(dataTransfer, TextHtmlString, TextHtmlBytes, isUtf16: false);

        AddIfNotWhiteSpace(textCandidates, TryGetPlatformString(dataTransfer, UrlWideString, UrlWideBytes, isUtf16: true));
        AddIfNotWhiteSpace(textCandidates, TryGetPlatformString(dataTransfer, UrlAnsiString, UrlAnsiBytes, isUtf16: false));
        AddIfNotWhiteSpace(textCandidates, TryGetPlatformString(dataTransfer, UriListString, UriListBytes, isUtf16: false));
        AddIfNotWhiteSpace(textCandidates, TryGet(() => dataTransfer.TryGetText()));

        byte[]? fallbackBitmapBytes = null;
        if (filePaths.Count == 0 && string.IsNullOrWhiteSpace(htmlContent) && textCandidates.Count == 0)
        {
            fallbackBitmapBytes = TryExtractFallbackBitmapBytes(dataTransfer);
        }
        else if (dataTransfer.Contains(DataFormat.Bitmap))
        {
            // URL取得に失敗した場合のフォールバック用にも保持
            fallbackBitmapBytes = TryExtractFallbackBitmapBytes(dataTransfer);
        }

        return new ArtworkDropPayload
        {
            FilePaths = filePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            HtmlContent = htmlContent,
            TextCandidates = textCandidates.Distinct(StringComparer.Ordinal).ToList(),
            FallbackBitmapBytes = fallbackBitmapBytes
        };
    }

    private static byte[]? TryExtractFallbackBitmapBytes(IDataTransfer dataTransfer)
    {
        return TryGet(() =>
        {
            using var bitmap = dataTransfer.TryGetBitmap();
            if (bitmap is null)
            {
                return null;
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream);
            return stream.Length > 0 ? stream.ToArray() : null;
        });
    }

    private static string? TryGetPlatformString(
        IDataTransfer dataTransfer,
        DataFormat<string> stringFormat,
        DataFormat<byte[]> bytesFormat,
        bool isUtf16)
    {
        var str = TryGet(() => dataTransfer.TryGetValue(stringFormat));
        if (!string.IsNullOrWhiteSpace(str))
        {
            return str.TrimEnd('\0');
        }

        var bytes = TryGet(() => dataTransfer.TryGetValue(bytesFormat));
        return DecodeBytes(bytes, isUtf16);
    }

    private static string? TryExtractRawString(IDataTransferItem item, DataFormat format, bool isUtf16)
    {
        var raw = TryGet(() => item.TryGetRaw(format));
        return raw switch
        {
            string s => s.TrimEnd('\0'),
            byte[] bytes => DecodeBytes(bytes, isUtf16),
            _ => null
        };
    }

    private static string? DecodeBytes(byte[]? bytes, bool isUtf16)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        var encoding = isUtf16 ? Encoding.Unicode : Encoding.UTF8;
        var decoded = encoding.GetString(bytes);
        var nullIndex = decoded.IndexOf('\0');
        return nullIndex >= 0 ? decoded[..nullIndex] : decoded;
    }

    private static bool IsHtmlFormatIdentifier(string? identifier)
    {
        return string.Equals(identifier, "HTML Format", StringComparison.OrdinalIgnoreCase)
               || string.Equals(identifier, "text/html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUrlFormatIdentifier(string? identifier)
    {
        return string.Equals(identifier, "UniformResourceLocatorW", StringComparison.OrdinalIgnoreCase)
               || string.Equals(identifier, "UniformResourceLocator", StringComparison.OrdinalIgnoreCase)
               || string.Equals(identifier, "text/uri-list", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddIfNotWhiteSpace(List<string> list, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            list.Add(value);
        }
    }

    private static T? TryGet<T>(Func<T?> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return default;
        }
    }
}
