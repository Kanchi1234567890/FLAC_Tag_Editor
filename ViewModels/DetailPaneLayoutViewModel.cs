using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using FLAC_Tag_Editor.Models;
using FLAC_Tag_Editor.Services.Interfaces;

namespace FLAC_Tag_Editor.ViewModels;

/// <summary>
/// 右ペイン（詳細パネル）の表示項目の状態管理と <c>settings.toml</c> への永続化を担う ViewModel。
/// </summary>
public sealed partial class DetailPaneLayoutViewModel : ViewModelBase, IDisposable
{
    private const string ResetStatusMessage = "右ペインの表示項目を既定に戻しました";

    private readonly IAppSettingsService? _appSettingsService;
    private readonly Action<string>? _reportStatus;
    private readonly Dictionary<DetailPaneFieldKind, DetailPaneFieldOptionViewModel> _fieldsByKind;
    private readonly object _saveLock = new();

    private Task _saveTask = Task.CompletedTask;
    private bool _disposed;

    public DetailPaneLayoutViewModel(
        IAppSettingsService? appSettingsService = null,
        Action<string>? reportStatus = null)
    {
        _appSettingsService = appSettingsService;
        _reportStatus = reportStatus;

        var fields = DetailPaneFieldKindExtensions.AllDefinitions
            .Select(def => new DetailPaneFieldOptionViewModel(def, OnFieldVisibilityChangedByUser))
            .ToList();

        Fields = fields;
        _fieldsByKind = fields.ToDictionary(f => f.Kind);

        Artwork = _fieldsByKind[DetailPaneFieldKind.Artwork];
        Title = _fieldsByKind[DetailPaneFieldKind.Title];
        Artist = _fieldsByKind[DetailPaneFieldKind.Artist];
        Album = _fieldsByKind[DetailPaneFieldKind.Album];
        AlbumArtist = _fieldsByKind[DetailPaneFieldKind.AlbumArtist];
        Track = _fieldsByKind[DetailPaneFieldKind.Track];
        Disc = _fieldsByKind[DetailPaneFieldKind.Disc];
        Year = _fieldsByKind[DetailPaneFieldKind.Year];
        Genre = _fieldsByKind[DetailPaneFieldKind.Genre];
        Comment = _fieldsByKind[DetailPaneFieldKind.Comment];
        UnregisteredCustomTags = _fieldsByKind[DetailPaneFieldKind.UnregisteredCustomTags];
        FileName = _fieldsByKind[DetailPaneFieldKind.FileName];
        FilePath = _fieldsByKind[DetailPaneFieldKind.FilePath];
        Duration = _fieldsByKind[DetailPaneFieldKind.Duration];
        FileSize = _fieldsByKind[DetailPaneFieldKind.FileSize];
        AudioFormat = _fieldsByKind[DetailPaneFieldKind.AudioFormat];
    }

    /// <summary>
    /// Flyout に一覧表示するすべての基本表示項目。
    /// </summary>
    public IReadOnlyList<DetailPaneFieldOptionViewModel> Fields { get; }

    public DetailPaneFieldOptionViewModel Artwork { get; }
    public DetailPaneFieldOptionViewModel Title { get; }
    public DetailPaneFieldOptionViewModel Artist { get; }
    public DetailPaneFieldOptionViewModel Album { get; }
    public DetailPaneFieldOptionViewModel AlbumArtist { get; }
    public DetailPaneFieldOptionViewModel Track { get; }
    public DetailPaneFieldOptionViewModel Disc { get; }
    public DetailPaneFieldOptionViewModel Year { get; }
    public DetailPaneFieldOptionViewModel Genre { get; }
    public DetailPaneFieldOptionViewModel Comment { get; }
    public DetailPaneFieldOptionViewModel UnregisteredCustomTags { get; }
    public DetailPaneFieldOptionViewModel FileName { get; }
    public DetailPaneFieldOptionViewModel FilePath { get; }
    public DetailPaneFieldOptionViewModel Duration { get; }
    public DetailPaneFieldOptionViewModel FileSize { get; }
    public DetailPaneFieldOptionViewModel AudioFormat { get; }

    /// <summary>
    /// 「トラック」または「ディスク」の少なくとも一方が表示状態かどうか。
    /// </summary>
    public bool IsTrackOrDiscVisible => Track.IsVisible || Disc.IsVisible;

    /// <summary>
    /// 「年」または「ジャンル」の少なくとも一方が表示状態かどうか。
    /// </summary>
    public bool IsYearOrGenreVisible => Year.IsVisible || Genre.IsVisible;

    /// <summary>
    /// 「再生時間」または「ファイルサイズ」の少なくとも一方が表示状態かどうか。
    /// </summary>
    public bool IsDurationOrFileSizeVisible => Duration.IsVisible || FileSize.IsVisible;

    /// <summary>
    /// 基本タグセクション内の項目が 1 つ以上表示状態かどうか。
    /// </summary>
    public bool HasVisibleBasicTags =>
        Title.IsVisible
        || Artist.IsVisible
        || Album.IsVisible
        || AlbumArtist.IsVisible
        || Track.IsVisible
        || Disc.IsVisible
        || Year.IsVisible
        || Genre.IsVisible
        || Comment.IsVisible;

    /// <summary>
    /// ファイル・音声情報セクション内の項目が 1 つ以上表示状態かどうか。
    /// </summary>
    public bool HasVisibleFileInfo =>
        FileName.IsVisible
        || FilePath.IsVisible
        || Duration.IsVisible
        || FileSize.IsVisible
        || AudioFormat.IsVisible;

    /// <summary>
    /// 起動時に設定ファイルを読み込み、右ペインの表示項目へ反映する。
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _appSettingsService is null)
        {
            return;
        }

        try
        {
            var loadResult = await _appSettingsService.LoadAsync(cancellationToken);
            if (_disposed || loadResult.HasLoadError)
            {
                return;
            }

            if (loadResult.IsLoadedFromFile)
            {
                ApplySettings(loadResult.Settings);
            }
        }
        catch (OperationCanceledException)
        {
            // 終了キャンセル時は何もしない
        }
        catch
        {
            // エラー通知は TrackGridColumnSettingsViewModel 側で行われるため既定値を維持
        }
    }

    /// <summary>
    /// 起動時に読み込まれた <see cref="AppSettings"/> から右ペイン設定を反映する。
    /// </summary>
    public void ApplySettings(AppSettings? settings)
    {
        if (_disposed)
        {
            return;
        }

        var savedByKind = new Dictionary<DetailPaneFieldKind, bool>();
        if (settings?.DetailPane?.Fields is { Count: > 0 } savedFields)
        {
            foreach (var entry in savedFields)
            {
                if (entry is null)
                {
                    continue;
                }

                if (DetailPaneFieldKindExtensions.TryGetDefinitionByKey(entry.Key, out var def))
                {
                    savedByKind.TryAdd(def.Kind, entry.IsVisible);
                }
            }
        }

        foreach (var field in Fields)
        {
            var isVisible = savedByKind.TryGetValue(field.Kind, out var savedVisible)
                ? savedVisible
                : field.Definition.DefaultVisible;
            field.SetIsVisibleSilently(isVisible);
        }

        NotifyGroupVisibilityChanged();
    }

    [RelayCommand]
    private void ResetFields()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var field in Fields)
        {
            field.SetIsVisibleSilently(field.Definition.DefaultVisible);
        }

        NotifyGroupVisibilityChanged();
        EnqueueSave(BuildDetailPaneSettingsSnapshot());
        _reportStatus?.Invoke(ResetStatusMessage);
    }

    public DetailPaneSettings BuildDetailPaneSettingsSnapshot()
    {
        return new DetailPaneSettings
        {
            Fields = Fields.Select(f => f.ToSettings()).ToList()
        };
    }

    public void FlushPendingSaveSync()
    {
        Task pending;
        lock (_saveLock)
        {
            pending = _saveTask;
        }

        try
        {
            if (!pending.IsCompleted)
            {
                pending.Wait(TimeSpan.FromSeconds(5));
            }
        }
        catch
        {
            // 終了時の設定保存失敗でアプリ終了を妨げない
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        FlushPendingSaveSync();
        _disposed = true;
    }

    private void OnFieldVisibilityChangedByUser(DetailPaneFieldOptionViewModel changedField)
    {
        if (_disposed)
        {
            return;
        }

        NotifyGroupVisibilityChanged();
        EnqueueSave(BuildDetailPaneSettingsSnapshot());
    }

    private void NotifyGroupVisibilityChanged()
    {
        OnPropertyChanged(nameof(IsTrackOrDiscVisible));
        OnPropertyChanged(nameof(IsYearOrGenreVisible));
        OnPropertyChanged(nameof(IsDurationOrFileSizeVisible));
        OnPropertyChanged(nameof(HasVisibleBasicTags));
        OnPropertyChanged(nameof(HasVisibleFileInfo));
    }

    private void EnqueueSave(DetailPaneSettings snapshot)
    {
        if (_disposed || _appSettingsService is null)
        {
            return;
        }

        var syncContext = SynchronizationContext.Current;
        lock (_saveLock)
        {
            var previous = _saveTask;
            _saveTask = SaveChainAsync(previous, snapshot, syncContext);
        }
    }

    private async Task SaveChainAsync(
        Task previousTask,
        DetailPaneSettings snapshot,
        SynchronizationContext? syncContext)
    {
        try
        {
            await previousTask.ConfigureAwait(false);
        }
        catch
        {
            // 直前の保存エラーは握りつぶし、最新のスナップショット保存を試みる
        }

        if (_appSettingsService is null)
        {
            return;
        }

        try
        {
            await _appSettingsService
                .UpdateAsync(settings => settings.DetailPane = snapshot, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                var errorMessage = $"設定ファイルの保存に失敗しました: {ex.Message}";
                if (syncContext is not null)
                {
                    syncContext.Post(_ => _reportStatus?.Invoke(errorMessage), null);
                }
                else
                {
                    _reportStatus?.Invoke(errorMessage);
                }
            }
        }
    }
}
