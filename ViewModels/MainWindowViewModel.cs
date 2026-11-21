using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FLAC_Tag_Editor.Models;
using FLAC_Tag_Editor.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace FLAC_Tag_Editor.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private const string OpenFolderDialogTitle = "FLACフォルダを選択";
    private const string OpenFilesDialogTitle = "FLACファイルを選択";
    private const string InitialStatusMessage = "フォルダまたはFLACファイルを選択してください";

    private readonly IFlacTagService? _flacTagService;
    private readonly IFileDialogService? _fileDialogService;
    private CancellationTokenSource? _loadCts;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTrack))]
    private TrackItemViewModel? _selectedTrack;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFilesCommand))]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = InitialStatusMessage;

    [ObservableProperty]
    private string _currentSourcePath = string.Empty;

    /// <summary>
    /// XAMLデザインプレビュー用コンストラクタ。
    /// </summary>
    public MainWindowViewModel()
    {
        var sampleTrack = new FlacTrack
        {
            FilePath = @"C:\Music\Sample Album\01 - Sample Track.flac",
            FileName = "01 - Sample Track.flac",
            FileSizeBytes = 38_420_112,
            Title = "Sample Track",
            Artist = "Sample Artist",
            Album = "Sample Album",
            AlbumArtist = "Sample Artist",
            Year = 2026,
            TrackNumber = 1,
            TrackCount = 10,
            DiscNumber = 1,
            DiscCount = 1,
            Genre = "Electronic",
            Comment = "Design-time preview track",
            Duration = TimeSpan.FromSeconds(245),
            SampleRate = 96000,
            BitsPerSample = 24,
            Channels = 2,
            Bitrate = 2890,
            CustomTags =
            [
                new CustomTagEntry("REPLAYGAIN_TRACK_GAIN", "-6.42 dB"),
                new CustomTagEntry("LABEL", "Sample Records")
            ]
        };

        Tracks.Add(new TrackItemViewModel(sampleTrack));
        SelectedTrack = Tracks[0];
        CurrentSourcePath = @"C:\Music\Sample Album";
        StatusMessage = "1 件のFLACファイルを読み込みました";
    }

    [ActivatorUtilitiesConstructor]
    public MainWindowViewModel(
        IFlacTagService flacTagService,
        IFileDialogService fileDialogService)
    {
        _flacTagService = flacTagService ?? throw new ArgumentNullException(nameof(flacTagService));
        _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
    }

    public ObservableCollection<TrackItemViewModel> Tracks { get; } = new();

    public bool HasTracks => Tracks.Count > 0;

    public bool HasSelectedTrack => SelectedTrack is not null;

    public string TrackCountText => $"{Tracks.Count} トラック";

    private bool CanOpen => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenFolderAsync()
    {
        if (_fileDialogService is null || _flacTagService is null)
        {
            return;
        }

        var folderPath = await _fileDialogService.OpenFolderAsync(OpenFolderDialogTitle);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        await LoadCoreAsync(
            ct => _flacTagService.LoadTracksFromFolderAsync(folderPath, includeSubdirectories: true, ct),
            folderPath);
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenFilesAsync()
    {
        if (_fileDialogService is null || _flacTagService is null)
        {
            return;
        }

        var filePaths = await _fileDialogService.OpenFlacFilesAsync(OpenFilesDialogTitle);
        if (filePaths.Count == 0)
        {
            return;
        }

        var sourceLabel = filePaths.Count == 1
            ? filePaths[0]
            : $"{Path.GetDirectoryName(filePaths[0])} ({filePaths.Count} ファイル選択)";

        await LoadCoreAsync(
            ct => _flacTagService.LoadTracksFromFilesAsync(filePaths, ct),
            sourceLabel);
    }

    private async Task LoadCoreAsync(
        Func<CancellationToken, Task<TrackLoadResult>> loadOperation,
        string sourceDescription)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var cancellationToken = _loadCts.Token;

        IsLoading = true;
        StatusMessage = "FLACファイルを読み込み中...";

        try
        {
            var result = await loadOperation(cancellationToken);
            ReplaceTracks(result, sourceDescription);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "読み込みをキャンセルしました";
        }
        catch (Exception ex)
        {
            StatusMessage = $"読み込みエラー: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 現在のトラック一覧を新しい読み込み結果で置き換える（将来的に追記モードも分離可能）。
    /// </summary>
    private void ReplaceTracks(TrackLoadResult result, string sourceDescription)
    {
        ClearTracks();

        foreach (var track in result.Tracks)
        {
            Tracks.Add(new TrackItemViewModel(track));
        }

        SelectedTrack = Tracks.FirstOrDefault();
        CurrentSourcePath = sourceDescription;

        OnPropertyChanged(nameof(HasTracks));
        OnPropertyChanged(nameof(TrackCountText));

        StatusMessage = BuildStatusMessage(result);
    }

    private void ClearTracks()
    {
        SelectedTrack = null;

        foreach (var item in Tracks)
        {
            item.Dispose();
        }

        Tracks.Clear();
    }

    private static string BuildStatusMessage(TrackLoadResult result)
    {
        if (result.Tracks.Count == 0 && result.Errors.Count == 0)
        {
            return "FLACファイルが見つかりませんでした";
        }

        if (result.Errors.Count > 0)
        {
            return $"{result.Tracks.Count} 件読み込み完了（{result.Errors.Count} 件エラー）";
        }

        return $"{result.Tracks.Count} 件のFLACファイルを読み込みました";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        ClearTracks();
    }
}
