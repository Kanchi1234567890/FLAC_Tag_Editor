using System;
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
/// <c>TomlSettings</c> を用いて実行フォルダ直下の <c>settings.toml</c> へ設定を読み書きするサービス実装。
/// </summary>
public sealed class AppSettingsService : IAppSettingsService, IDisposable
{
    private const string SettingsFileName = "settings.toml";

    private readonly ISettingsStore<AppSettings> _settingsStore;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private bool _disposed;

    public AppSettingsService()
        : this(Path.Combine(AppContext.BaseDirectory, SettingsFileName))
    {
    }

    public AppSettingsService(string settingsFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFilePath);

        SettingsFilePath = settingsFilePath;

        var serializerOptions = new TomlynSettingsSerializerOptions
        {
            AppendTrailingNewLine = true,
            UseInlineArrayOfTables = true
        };

        var serializer = new TomlynSettingsSerializer<AppSettings>(serializerOptions);
        var fileStoreOptions = new FileSettingsStoreOptions
        {
            CreateDirectoryIfNotExists = true,
            OverwriteExistingFile = true
        };

        var fileStore = new FileSettingsStore<AppSettings>(serializer, fileStoreOptions);
        _settingsStore = new FixedPathSettingsStore<AppSettings>(fileStore, SettingsFilePath);
    }

    public string SettingsFilePath { get; }

    public async Task<AppSettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _settingsStore.TryLoadAsync(cancellationToken).ConfigureAwait(false);
            if (result.Succeeded && result.Value is not null)
            {
                return new AppSettingsLoadResult(
                    Settings: result.Value,
                    IsLoadedFromFile: true,
                    HasLoadError: false);
            }

            if (result.Exception is SettingsFileNotFoundException or SettingsNotFoundException or FileNotFoundException)
            {
                return new AppSettingsLoadResult(
                    Settings: AppSettings.CreateDefault(),
                    IsLoadedFromFile: false,
                    HasLoadError: false);
            }

            return new AppSettingsLoadResult(
                Settings: AppSettings.CreateDefault(),
                IsLoadedFromFile: false,
                HasLoadError: true,
                ErrorMessage: BuildLoadErrorMessage(result.Exception));
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);

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
