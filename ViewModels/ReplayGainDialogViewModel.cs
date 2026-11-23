using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FLAC_Tag_Editor.Models;
using FLAC_Tag_Editor.Services.Interfaces;

namespace FLAC_Tag_Editor.ViewModels;

/// <summary>
/// ReplayGain 計算ダイアログの ViewModel。
/// FFmpeg パス検証・計算対象／モード選択・非同期計算の進捗表示とキャンセル・結果プレビューおよび適用確定を担う。
/// </summary>
public sealed partial class ReplayGainDialogViewModel : ViewModelBase, IWindowCloseGuard, IDisposable
{
    private const string BrowseFfmpegDialogTitle = "FFmpeg 実行ファイル (ffmpeg.exe) を選択";
    private const string ApplyOnlyButtonText = "タグへ適用";
    private const string ApplyAndSaveButtonText = "適用して保存";
    private const string ClearOnlyButtonText = "タグをクリア";
    private const string ClearAndSaveButtonText = "クリアして保存";
    private const string DefaultStatusText = "計算対象とオプションを確認し、「計算開始」をクリックしてください";

    private readonly IReplayGainService? _replayGainService;
    private readonly IFileDialogService? _fileDialogService;
    private readonly IAppSettingsService? _appSettingsService;
    private readonly IReadOnlyList<ReplayGainTrackInput> _allTracks;
    private readonly IReadOnlyList<ReplayGainTrackInput> _selectedTracks;

    private CancellationTokenSource? _validationCts;
    private CancellationTokenSource? _calculationCts;
    private Task? _runningCalculationTask;
    private ReplayGainCalculationMode _lastCalculatedMode = ReplayGainCalculationMode.TrackAndAlbum;
    private bool _disposed;

    public event Action<ReplayGainDialogResult?>? CloseRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomFfmpegPath))]
    [NotifyCanExecuteChangedFor(nameof(ClearFfmpegPathCommand))]
    private string _ffmpegPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FfmpegStatusBadgeText))]
    private bool _isFfmpegAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ValidateFfmpegCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseFfmpegCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearFfmpegPathCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartCalculationCommand))]
    private bool _isValidatingFfmpeg;

    [ObservableProperty]
    private string _ffmpegStatusDetailText = "FFmpeg を確認中...";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveTargetTrackCount))]
    [NotifyPropertyChangedFor(nameof(ActiveTargetReplayGainTrackCount))]
    [NotifyCanExecuteChangedFor(nameof(StartCalculationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearTargetTagsCommand))]
    private bool _useSelectedTracks;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveTargetTrackCount))]
    [NotifyPropertyChangedFor(nameof(ActiveTargetReplayGainTrackCount))]
    [NotifyCanExecuteChangedFor(nameof(StartCalculationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearTargetTagsCommand))]
    private bool _useAllTracks = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAlbumGroupingEnabled))]
    private bool _isTrackAndAlbumMode = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAlbumGroupingEnabled))]
    private bool _isTrackOnlyMode;

    [ObservableProperty]
    private ReplayGainAlbumGroupingOption _selectedAlbumGroupingOption = ReplayGainAlbumGroupingOption.AllOptions[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditOptions))]
    [NotifyPropertyChangedFor(nameof(CanSelectTargetSelectedTracks))]
    [NotifyPropertyChangedFor(nameof(IsAlbumGroupingEnabled))]
    [NotifyCanExecuteChangedFor(nameof(BrowseFfmpegCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearFfmpegPathCommand))]
    [NotifyCanExecuteChangedFor(nameof(ValidateFfmpegCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartCalculationCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCalculationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearTargetTagsCommand))]
    private bool _isCalculating;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _progressCountText = string.Empty;

    [ObservableProperty]
    private string _currentStatusText = DefaultStatusText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSucceededResults))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private int _succeededCount;

    [ObservableProperty]
    private int _failedCount;

    [ObservableProperty]
    private string _resultSummaryText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApplyButtonText))]
    [NotifyPropertyChangedFor(nameof(ClearTagsButtonText))]
    private bool _autoSaveAfterApply;

    /// <summary>
    /// XAML デザインプレビュー用コンストラクタ。
    /// </summary>
    public ReplayGainDialogViewModel()
    {
        _allTracks =
        [
            new ReplayGainTrackInput(@"C:\Music\Album\01.flac", "01 - Opening.flac", @"C:\Music\Album", "Sample Album", "Sample Artist", 96000, 24, 2),
            new ReplayGainTrackInput(@"C:\Music\Album\02.flac", "02 - Finale.flac", @"C:\Music\Album", "Sample Album", "Sample Artist", 96000, 24, 2)
        ];
        _selectedTracks = [_allTracks[0]];

        IsFfmpegAvailable = true;
        FfmpegStatusDetailText = @"自動検出: C:\ffmpeg\bin\ffmpeg.exe (ffmpeg version 7.1)";
        Results.Add(new ReplayGainTrackResult(
            _allTracks[0].FilePath,
            _allTracks[0].FileName,
            "Sample Artist - Sample Album",
            Succeeded: true,
            TrackGainDb: -6.42,
            TrackPeak: 0.987654,
            AlbumGainDb: -6.15,
            AlbumPeak: 0.992100));
        SucceededCount = 1;
        ResultSummaryText = "計算完了: 成功 1 件 / 失敗 0 件（アルバム 1 グループ）";
    }

    public ReplayGainDialogViewModel(
        ReplayGainDialogContext context,
        IReplayGainService replayGainService,
        IFileDialogService fileDialogService,
        IAppSettingsService appSettingsService)
    {
        ArgumentNullException.ThrowIfNull(context);
        _replayGainService = replayGainService ?? throw new ArgumentNullException(nameof(replayGainService));
        _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
        _appSettingsService = appSettingsService ?? throw new ArgumentNullException(nameof(appSettingsService));

        _allTracks = context.AllTracks;
        _selectedTracks = context.SelectedTracks;

        FfmpegPath = context.InitialFfmpegPath ?? string.Empty;

        var startWithSelected = context.PreferSelectedTracks && _selectedTracks.Count > 0;
        UseSelectedTracks = startWithSelected;
        UseAllTracks = !startWithSelected;

        _ = ValidateFfmpegInternalAsync(savePathToSettings: false);
    }

    public ObservableCollection<ReplayGainTrackResult> Results { get; } = new();

    public IReadOnlyList<ReplayGainAlbumGroupingOption> AlbumGroupingOptions => ReplayGainAlbumGroupingOption.AllOptions;

    public bool HasResults => Results.Count > 0;

    public bool HasSucceededResults => SucceededCount > 0;

    public bool HasCustomFfmpegPath => !string.IsNullOrWhiteSpace(FfmpegPath);

    public bool CanUseSelectedTracks => _selectedTracks.Count > 0;

    public bool CanEditOptions => !IsCalculating;

    public bool CanSelectTargetSelectedTracks => !IsCalculating && _selectedTracks.Count > 0;

    public bool IsAlbumGroupingEnabled => !IsCalculating && IsTrackAndAlbumMode;

    public int ActiveTargetTrackCount => UseSelectedTracks ? _selectedTracks.Count : _allTracks.Count;

    public int ActiveTargetReplayGainTrackCount =>
        (UseSelectedTracks ? _selectedTracks : _allTracks).Count(t => t.HasReplayGainTags);

    public string SelectedTracksOptionText => $"選択中のトラック ({_selectedTracks.Count} 件)";

    public string AllTracksOptionText => $"読み込み済みの全トラック ({_allTracks.Count} 件)";

    public string FfmpegStatusBadgeText => IsValidatingFfmpeg
        ? "確認中"
        : (IsFfmpegAvailable ? "利用可能" : "未検出");

    public string ApplyButtonText => AutoSaveAfterApply
        ? ApplyAndSaveButtonText
        : ApplyOnlyButtonText;

    public string ClearTagsButtonText => AutoSaveAfterApply
        ? ClearAndSaveButtonText
        : ClearOnlyButtonText;

    public bool ShouldInterceptClose => IsCalculating;

    private bool CanBrowseOrValidateFfmpeg => !IsCalculating && !IsValidatingFfmpeg;

    private bool CanClearFfmpegPath => !IsCalculating && !IsValidatingFfmpeg && HasCustomFfmpegPath;

    private bool CanStartCalculation => !IsCalculating && !IsValidatingFfmpeg && ActiveTargetTrackCount > 0;

    private bool CanCancelCalculation => IsCalculating;

    private bool CanApply => !IsCalculating && HasSucceededResults;

    private bool CanClearTargetTags => !IsCalculating && ActiveTargetReplayGainTrackCount > 0;

    partial void OnIsValidatingFfmpegChanged(bool value)
    {
        OnPropertyChanged(nameof(FfmpegStatusBadgeText));
    }

    public async Task<bool> ConfirmCloseAsync()
    {
        if (!IsCalculating)
        {
            return true;
        }

        CancelCalculation();
        var runningTask = _runningCalculationTask;
        if (runningTask is not null)
        {
            try
            {
                await runningTask;
            }
            catch
            {
                // キャンセル終了例外は無視
            }
        }

        return true;
    }

    [RelayCommand(CanExecute = nameof(CanBrowseOrValidateFfmpeg))]
    private async Task BrowseFfmpegAsync()
    {
        if (_fileDialogService is null)
        {
            return;
        }

        var selectedPath = await _fileDialogService.OpenFfmpegExecutableAsync(BrowseFfmpegDialogTitle);
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        FfmpegPath = selectedPath;
        await ValidateFfmpegInternalAsync(savePathToSettings: true);
    }

    [RelayCommand(CanExecute = nameof(CanClearFfmpegPath))]
    private async Task ClearFfmpegPathAsync()
    {
        FfmpegPath = string.Empty;
        await ValidateFfmpegInternalAsync(savePathToSettings: true);
    }

    [RelayCommand(CanExecute = nameof(CanBrowseOrValidateFfmpeg))]
    private async Task ValidateFfmpegAsync()
    {
        await ValidateFfmpegInternalAsync(savePathToSettings: true);
    }

    [RelayCommand(CanExecute = nameof(CanStartCalculation))]
    private async Task StartCalculationAsync()
    {
        if (_replayGainService is null || IsCalculating)
        {
            return;
        }

        var targets = UseSelectedTracks ? _selectedTracks : _allTracks;
        if (targets.Count == 0)
        {
            CurrentStatusText = "計算対象のトラックがありません";
            return;
        }

        var calculationMode = IsTrackAndAlbumMode
            ? ReplayGainCalculationMode.TrackAndAlbum
            : ReplayGainCalculationMode.TrackOnly;
        var groupingMode = SelectedAlbumGroupingOption?.Mode ?? ReplayGainAlbumGroupingMode.ByAlbumTag;

        Results.Clear();
        OnPropertyChanged(nameof(HasResults));
        SucceededCount = 0;
        FailedCount = 0;
        ResultSummaryText = string.Empty;
        ProgressPercent = 0;
        ProgressCountText = string.Empty;

        _calculationCts?.Cancel();
        _calculationCts?.Dispose();
        _calculationCts = new CancellationTokenSource();
        var cancellationToken = _calculationCts.Token;

        IsCalculating = true;
        CurrentStatusText = "FFmpeg を確認中...";

        var task = RunCalculationCoreAsync(targets, calculationMode, groupingMode, cancellationToken);
        _runningCalculationTask = task;

        try
        {
            await task;
        }
        finally
        {
            if (ReferenceEquals(_runningCalculationTask, task))
            {
                _runningCalculationTask = null;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelCalculation))]
    private void CancelCalculation()
    {
        if (!IsCalculating)
        {
            return;
        }

        CurrentStatusText = "計算をキャンセルしています...";
        _calculationCts?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (!CanApply)
        {
            return;
        }

        var succeededResults = Results.Where(r => r.Succeeded).ToList();
        if (succeededResults.Count == 0)
        {
            return;
        }

        CloseRequested?.Invoke(new ReplayGainDialogResult(
            Action: ReplayGainDialogAction.ApplyCalculated,
            TrackResults: succeededResults,
            CalculationMode: _lastCalculatedMode,
            AutoSaveAfterApply: AutoSaveAfterApply));
    }

    [RelayCommand(CanExecute = nameof(CanClearTargetTags))]
    private void ClearTargetTags()
    {
        if (!CanClearTargetTags)
        {
            return;
        }

        var targets = UseSelectedTracks ? _selectedTracks : _allTracks;
        var targetPaths = targets
            .Where(t => t.HasReplayGainTags)
            .Select(t => t.FilePath)
            .ToList();
        if (targetPaths.Count == 0)
        {
            return;
        }

        CloseRequested?.Invoke(new ReplayGainDialogResult(
            Action: ReplayGainDialogAction.ClearTags,
            TrackResults: [],
            CalculationMode: _lastCalculatedMode,
            AutoSaveAfterApply: AutoSaveAfterApply,
            ClearTargetFilePaths: targetPaths));
    }

    [RelayCommand]
    private async Task CloseAsync()
    {
        if (IsCalculating)
        {
            await ConfirmCloseAsync();
        }

        CloseRequested?.Invoke(null);
    }

    private async Task RunCalculationCoreAsync(
        IReadOnlyList<ReplayGainTrackInput> targets,
        ReplayGainCalculationMode mode,
        ReplayGainAlbumGroupingMode groupingMode,
        CancellationToken cancellationToken)
    {
        try
        {
            var progress = new Progress<ReplayGainProgress>(OnProgressReported);
            var options = new ReplayGainOptions(
                FfmpegPath: FfmpegPath.Trim(),
                Mode: mode,
                AlbumGrouping: groupingMode);

            var batchResult = await _replayGainService!.CalculateAsync(
                targets,
                options,
                progress,
                cancellationToken);

            _lastCalculatedMode = mode;

            IsFfmpegAvailable = true;
            if (!string.IsNullOrWhiteSpace(batchResult.ResolvedFfmpegPath))
            {
                await SaveFfmpegPathIfNeededAsync(FfmpegPath.Trim());
            }

            foreach (var item in batchResult.TrackResults)
            {
                Results.Add(item);
            }

            OnPropertyChanged(nameof(HasResults));
            SucceededCount = batchResult.SucceededCount;
            FailedCount = batchResult.FailedCount;
            ProgressPercent = 100.0;

            ResultSummaryText = mode == ReplayGainCalculationMode.TrackAndAlbum
                ? $"計算完了: 成功 {batchResult.SucceededCount} 件 / 失敗 {batchResult.FailedCount} 件（アルバム {batchResult.AlbumGroupCount} グループ）"
                : $"計算完了: 成功 {batchResult.SucceededCount} 件 / 失敗 {batchResult.FailedCount} 件（トラックのみ）";

            CurrentStatusText = batchResult.SucceededCount > 0
                ? "結果を確認し、下部の適用ボタンをクリックしてタグへ反映してください"
                : "すべてのトラックで ReplayGain の計算に失敗しました";
        }
        catch (OperationCanceledException)
        {
            CurrentStatusText = "ReplayGain の計算をキャンセルしました";
            ResultSummaryText = "計算はキャンセルされました";
        }
        catch (Exception ex)
        {
            IsFfmpegAvailable = false;
            FfmpegStatusDetailText = ex.Message;
            CurrentStatusText = $"計算エラー: {ex.Message}";
            ResultSummaryText = string.Empty;
        }
        finally
        {
            IsCalculating = false;
        }
    }

    private void OnProgressReported(ReplayGainProgress progress)
    {
        if (_disposed || !IsCalculating || progress.Phase == ReplayGainProgressPhase.Completed)
        {
            return;
        }

        ProgressPercent = progress.ProgressPercent;
        ProgressCountText = progress.TotalSteps > 0
            ? $"{progress.CompletedSteps} / {progress.TotalSteps} ({progress.ProgressPercent:0}%)"
            : string.Empty;
        CurrentStatusText = progress.StatusText;
    }

    private async Task ValidateFfmpegInternalAsync(bool savePathToSettings)
    {
        if (_replayGainService is null || _disposed)
        {
            return;
        }

        _validationCts?.Cancel();
        _validationCts?.Dispose();
        _validationCts = new CancellationTokenSource();
        var cancellationToken = _validationCts.Token;

        var targetPath = FfmpegPath.Trim();
        IsValidatingFfmpeg = true;
        FfmpegStatusDetailText = "FFmpeg を確認中...";

        try
        {
            var result = await _replayGainService.ValidateFfmpegAsync(targetPath, cancellationToken);
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            IsFfmpegAvailable = result.IsAvailable;
            if (result.IsAvailable)
            {
                var sourceLabel = result.IsAutoDetected ? "自動検出" : "指定パス";
                var versionSuffix = string.IsNullOrWhiteSpace(result.VersionText)
                    ? string.Empty
                    : $" ({result.VersionText})";
                FfmpegStatusDetailText = $"{sourceLabel}: {result.ResolvedPath}{versionSuffix}";
            }
            else
            {
                FfmpegStatusDetailText = result.ErrorMessage ?? "FFmpeg が見つかりません。";
            }

            if (savePathToSettings)
            {
                await SaveFfmpegPathIfNeededAsync(targetPath);
            }
        }
        catch (OperationCanceledException)
        {
            // 後続の検証リクエストに置き換わった場合は無視
        }
        catch (Exception ex)
        {
            IsFfmpegAvailable = false;
            FfmpegStatusDetailText = $"FFmpeg 確認エラー: {ex.Message}";
        }
        finally
        {
            if (!_disposed && !cancellationToken.IsCancellationRequested)
            {
                IsValidatingFfmpeg = false;
            }
        }
    }

    private async Task SaveFfmpegPathIfNeededAsync(string pathToSave)
    {
        if (_appSettingsService is null || _disposed)
        {
            return;
        }

        try
        {
            await _appSettingsService.UpdateAsync(settings =>
            {
                settings.ReplayGain ??= ReplayGainSettings.CreateDefault();
                settings.ReplayGain.FfmpegPath = pathToSave;
            });
        }
        catch
        {
            // 設定保存失敗は計算実行自体を妨げない
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _validationCts?.Cancel();
        _validationCts?.Dispose();
        _validationCts = null;

        _calculationCts?.Cancel();
        _calculationCts?.Dispose();
        _calculationCts = null;
    }
}
