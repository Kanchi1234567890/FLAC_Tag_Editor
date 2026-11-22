using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FLAC_Tag_Editor.Models;
using FLAC_Tag_Editor.Services.Interfaces;
using TomlSettings;
using TomlSettings.Exceptions;
using TomlSettings.Serialization;
using TomlSettings.Storage;

namespace FLAC_Tag_Editor.Services.Implementations;

/// <summary>
/// <c>TomlSettings</c> を用いて実行フォルダ直下の <c>custom_tags.toml</c> へ独自タグ定義を読み書きするサービス実装。
/// </summary>
public sealed class CustomTagDefinitionService : ICustomTagDefinitionService, IDisposable
{
    private const string DefinitionsFileName = "custom_tags.toml";

    private readonly ISettingsStore<CustomTagDefinitionSettings> _settingsStore;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private bool _disposed;

    public CustomTagDefinitionService()
        : this(Path.Combine(AppContext.BaseDirectory, DefinitionsFileName))
    {
    }

    public CustomTagDefinitionService(string definitionsFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionsFilePath);

        DefinitionsFilePath = definitionsFilePath;

        var serializerOptions = new TomlynSettingsSerializerOptions
        {
            AppendTrailingNewLine = true,
            UseInlineArrayOfTables = true
        };

        var serializer = new TomlynSettingsSerializer<CustomTagDefinitionSettings>(serializerOptions);
        var fileStoreOptions = new FileSettingsStoreOptions
        {
            CreateDirectoryIfNotExists = true,
            OverwriteExistingFile = true
        };

        var fileStore = new FileSettingsStore<CustomTagDefinitionSettings>(serializer, fileStoreOptions);
        _settingsStore = new FixedPathSettingsStore<CustomTagDefinitionSettings>(fileStore, DefinitionsFilePath);
    }

    public string DefinitionsFilePath { get; }

    public async Task<CustomTagDefinitionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _settingsStore.TryLoadAsync(cancellationToken).ConfigureAwait(false);
            if (result.Succeeded && result.Value is not null)
            {
                var normalized = CustomTagDefinitionRules.Normalize(result.Value, out var skippedCount);
                return new CustomTagDefinitionLoadResult(
                    Definitions: normalized,
                    IsLoadedFromFile: true,
                    HasLoadError: false,
                    SkippedEntryCount: skippedCount);
            }

            if (result.Exception is SettingsFileNotFoundException or SettingsNotFoundException or FileNotFoundException)
            {
                return new CustomTagDefinitionLoadResult(
                    Definitions: [],
                    IsLoadedFromFile: false,
                    HasLoadError: false,
                    SkippedEntryCount: 0);
            }

            return new CustomTagDefinitionLoadResult(
                Definitions: [],
                IsLoadedFromFile: false,
                HasLoadError: true,
                SkippedEntryCount: 0,
                ErrorMessage: BuildLoadErrorMessage(result.Exception));
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task SaveAsync(IReadOnlyList<CustomTagDefinition> definitions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var settings = CustomTagDefinitionRules.ToSettings(definitions);

        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ioGate.Dispose();
    }

    private static string BuildLoadErrorMessage(Exception? exception)
    {
        return exception switch
        {
            SettingsParseException parseEx when parseEx.Line.HasValue =>
                $"TOML構文エラー ({parseEx.Line.Value}行目): {parseEx.ParseErrorDetails}",
            SettingsParseException parseEx =>
                $"TOML構文エラー: {parseEx.ParseErrorDetails}",
            SettingsStorageException storageEx =>
                storageEx.InnerException?.Message ?? storageEx.Message,
            not null => exception.Message,
            _ => "不明な読み込みエラーが発生しました。"
        };
    }
}
