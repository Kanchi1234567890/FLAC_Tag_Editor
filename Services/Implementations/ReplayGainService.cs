using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FLAC_Tag_Editor.Models;
using FLAC_Tag_Editor.Services.Interfaces;

namespace FLAC_Tag_Editor.Services.Implementations;

/// <summary>
/// FFmpeg 外部プロセス（<c>-af replaygain</c>）を用いてトラックおよびアルバム単位の ReplayGain を計算するサービス実装。
/// </summary>
public sealed partial class ReplayGainService : IReplayGainService
{
    private const string FfmpegExecutableName = "ffmpeg.exe";
    private const string FfmpegSubFolderName = "ffmpeg";
    private const string FfmpegBinSubFolderName = "bin";
    private const int ValidationTimeoutMilliseconds = 5000;
    private const int FallbackSampleRateHz = 48000;
    private const int MinTracksForAlbumConcat = 2;
    private const int InlineFilterComplexMaxLength = 4000;
    private const string TempFilePrefix = "flactageditor_rg_";
    private const string SingleAlbumGroupKey = "__single_album__";
    private const string SingleAlbumGroupLabel = "単一アルバム";
    private const string UnnamedFolderGroupLabel = "不明なフォルダ";
    private const string NullOutputTarget = "-";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    [GeneratedRegex(@"track_gain\s*=\s*([+-]?\d+(?:\.\d+)?)\s*dB", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrackGainRegex();

    [GeneratedRegex(@"track_peak\s*=\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrackPeakRegex();

    public async Task<FfmpegValidationResult> ValidateFfmpegAsync(
        string? configuredPath,
        CancellationToken cancellationToken = default)
    {
        var resolution = ResolveFfmpegPath(configuredPath);
        if (!resolution.Found || string.IsNullOrWhiteSpace(resolution.ResolvedPath))
        {
            return new FfmpegValidationResult(
                IsAvailable: false,
                ResolvedPath: null,
                VersionText: null,
                ErrorMessage: resolution.ErrorMessage,
                IsAutoDetected: resolution.IsAutoDetected);
        }

        try
        {
            var processResult = await RunFfmpegAsync(
                resolution.ResolvedPath,
                ["-version"],
                cancellationToken,
                ValidationTimeoutMilliseconds).ConfigureAwait(false);

            if (processResult.ExitCode != 0)
            {
                return new FfmpegValidationResult(
                    IsAvailable: false,
                    ResolvedPath: resolution.ResolvedPath,
                    VersionText: null,
                    ErrorMessage: $"FFmpeg の実行確認に失敗しました（終了コード: {processResult.ExitCode}）。",
                    IsAutoDetected: resolution.IsAutoDetected);
            }

            var firstLine = ExtractFirstNonEmptyLine(processResult.StandardOutput)
                            ?? ExtractFirstNonEmptyLine(processResult.StandardError)
                            ?? "FFmpeg 検出済み";

            return new FfmpegValidationResult(
                IsAvailable: true,
                ResolvedPath: resolution.ResolvedPath,
                VersionText: firstLine,
                ErrorMessage: null,
                IsAutoDetected: resolution.IsAutoDetected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new FfmpegValidationResult(
                IsAvailable: false,
                ResolvedPath: resolution.ResolvedPath,
                VersionText: null,
                ErrorMessage: "FFmpeg のバージョン確認がタイムアウトしました。",
                IsAutoDetected: resolution.IsAutoDetected);
        }
        catch (Exception ex)
        {
            return new FfmpegValidationResult(
                IsAvailable: false,
                ResolvedPath: resolution.ResolvedPath,
                VersionText: null,
                ErrorMessage: $"FFmpeg の起動に失敗しました: {ex.Message}",
                IsAutoDetected: resolution.IsAutoDetected);
        }
    }

    public async Task<ReplayGainBatchResult> CalculateAsync(
        IReadOnlyList<ReplayGainTrackInput> tracks,
        ReplayGainOptions options,
        IProgress<ReplayGainProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(options);

        if (tracks.Count == 0)
        {
            return new ReplayGainBatchResult([], string.Empty, 0, 0, 0);
        }

        progress?.Report(new ReplayGainProgress(
            ReplayGainProgressPhase.ValidatingFfmpeg,
            0,
            tracks.Count,
            string.Empty,
            "FFmpeg を確認中..."));

        var validation = await ValidateFfmpegAsync(options.FfmpegPath, cancellationToken).ConfigureAwait(false);
        if (!validation.IsAvailable || string.IsNullOrWhiteSpace(validation.ResolvedPath))
        {
            throw new InvalidOperationException(
                validation.ErrorMessage ?? "FFmpeg が見つかりません。設定から ffmpeg.exe を指定してください。");
        }

        var ffmpegPath = validation.ResolvedPath;
        var albumGroups = options.Mode == ReplayGainCalculationMode.TrackAndAlbum
            ? BuildAlbumGroups(tracks, options.AlbumGrouping)
            : [];

        var multiTrackAlbumGroups = albumGroups
            .Where(g => g.TrackIndices.Count >= MinTracksForAlbumConcat)
            .ToList();

        var totalSteps = tracks.Count + multiTrackAlbumGroups.Count;
        var completedSteps = 0;

        var trackStates = new TrackAnalysisState[tracks.Count];
        var albumLabelByTrackIndex = new string[tracks.Count];

        if (options.Mode == ReplayGainCalculationMode.TrackAndAlbum)
        {
            foreach (var group in albumGroups)
            {
                foreach (var index in group.TrackIndices)
                {
                    albumLabelByTrackIndex[index] = group.DisplayLabel;
                }
            }
        }
        else
        {
            for (var i = 0; i < tracks.Count; i++)
            {
                albumLabelByTrackIndex[i] = string.Empty;
            }
        }

        // フェーズ 1: 各トラックの Track Gain / Track Peak 解析
        for (var i = 0; i < tracks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var track = tracks[i];
            progress?.Report(new ReplayGainProgress(
                ReplayGainProgressPhase.AnalyzingTracks,
                completedSteps,
                totalSteps,
                track.FileName,
                $"トラック解析中 ({i + 1}/{tracks.Count}): {track.FileName}"));

            trackStates[i] = await AnalyzeSingleTrackAsync(ffmpegPath, track, cancellationToken).ConfigureAwait(false);
            completedSteps++;

            progress?.Report(new ReplayGainProgress(
                ReplayGainProgressPhase.AnalyzingTracks,
                completedSteps,
                totalSteps,
                track.FileName,
                $"トラック解析中 ({i + 1}/{tracks.Count}): {track.FileName}"));
        }

        // フェーズ 2: アルバム単位の Album Gain / Album Peak 解析
        if (options.Mode == ReplayGainCalculationMode.TrackAndAlbum)
        {
            var multiGroupProcessed = 0;

            foreach (var group in albumGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var succeededIndices = group.TrackIndices
                    .Where(idx => trackStates[idx].Succeeded && trackStates[idx].TrackGainDb.HasValue && trackStates[idx].TrackPeak.HasValue)
                    .ToList();

                if (group.TrackIndices.Count < MinTracksForAlbumConcat)
                {
                    if (succeededIndices.Count == 1)
                    {
                        var singleIndex = succeededIndices[0];
                        trackStates[singleIndex].AlbumGainDb = trackStates[singleIndex].TrackGainDb;
                        trackStates[singleIndex].AlbumPeak = trackStates[singleIndex].TrackPeak;
                    }

                    continue;
                }

                multiGroupProcessed++;
                progress?.Report(new ReplayGainProgress(
                    ReplayGainProgressPhase.AnalyzingAlbums,
                    completedSteps,
                    totalSteps,
                    group.DisplayLabel,
                    $"アルバム解析中 ({multiGroupProcessed}/{multiTrackAlbumGroups.Count}): {group.DisplayLabel}"));

                if (succeededIndices.Count == 1)
                {
                    var singleIndex = succeededIndices[0];
                    trackStates[singleIndex].AlbumGainDb = trackStates[singleIndex].TrackGainDb;
                    trackStates[singleIndex].AlbumPeak = trackStates[singleIndex].TrackPeak;
                }
                else if (succeededIndices.Count >= MinTracksForAlbumConcat)
                {
                    var groupTracks = succeededIndices.Select(idx => tracks[idx]).ToList();
                    var albumPeak = succeededIndices.Max(idx => trackStates[idx].TrackPeak!.Value);

                    var albumResult = await AnalyzeAlbumGainAsync(
                        ffmpegPath,
                        groupTracks,
                        cancellationToken).ConfigureAwait(false);

                    foreach (var idx in succeededIndices)
                    {
                        if (albumResult.Succeeded && albumResult.GainDb.HasValue)
                        {
                            trackStates[idx].AlbumGainDb = albumResult.GainDb.Value;
                            trackStates[idx].AlbumPeak = albumPeak;
                        }
                        else
                        {
                            trackStates[idx].AlbumErrorMessage = albumResult.ErrorMessage ?? "アルバム解析に失敗しました。";
                        }
                    }
                }

                completedSteps++;
                progress?.Report(new ReplayGainProgress(
                    ReplayGainProgressPhase.AnalyzingAlbums,
                    completedSteps,
                    totalSteps,
                    group.DisplayLabel,
                    $"アルバム解析中 ({multiGroupProcessed}/{multiTrackAlbumGroups.Count}): {group.DisplayLabel}"));
            }
        }

        var results = new List<ReplayGainTrackResult>(tracks.Count);
        var succeededCount = 0;
        var failedCount = 0;

        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            var state = trackStates[i];
            var errorMessage = state.Succeeded ? state.AlbumErrorMessage : state.ErrorMessage;

            if (state.Succeeded)
            {
                succeededCount++;
            }
            else
            {
                failedCount++;
            }

            results.Add(new ReplayGainTrackResult(
                FilePath: track.FilePath,
                FileName: track.FileName,
                AlbumGroupLabel: albumLabelByTrackIndex[i],
                Succeeded: state.Succeeded,
                TrackGainDb: state.TrackGainDb,
                TrackPeak: state.TrackPeak,
                AlbumGainDb: state.AlbumGainDb,
                AlbumPeak: state.AlbumPeak,
                ErrorMessage: errorMessage));
        }

        progress?.Report(new ReplayGainProgress(
            ReplayGainProgressPhase.Completed,
            totalSteps,
            totalSteps,
            string.Empty,
            $"計算完了（成功 {succeededCount} 件 / 失敗 {failedCount} 件）"));

        return new ReplayGainBatchResult(
            TrackResults: results,
            ResolvedFfmpegPath: ffmpegPath,
            AlbumGroupCount: albumGroups.Count,
            SucceededCount: succeededCount,
            FailedCount: failedCount);
    }

    private static async Task<TrackAnalysisState> AnalyzeSingleTrackAsync(
        string ffmpegPath,
        ReplayGainTrackInput track,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(track.FilePath))
        {
            return TrackAnalysisState.Failed("FLACファイルが見つかりません。");
        }

        try
        {
            var arguments = new[]
            {
                "-hide_banner",
                "-nostdin",
                "-nostats",
                "-loglevel", "info",
                "-i", track.FilePath,
                "-vn",
                "-sn",
                "-dn",
                "-af", "replaygain",
                "-f", "null",
                NullOutputTarget
            };

            var processResult = await RunFfmpegAsync(ffmpegPath, arguments, cancellationToken).ConfigureAwait(false);
            if (TryParseReplayGainOutput(processResult.StandardError, processResult.StandardOutput, out var gainDb, out var peak))
            {
                return TrackAnalysisState.Success(gainDb, peak);
            }

            var detail = ExtractErrorDetail(processResult.StandardError, processResult.ExitCode);
            return TrackAnalysisState.Failed(detail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return TrackAnalysisState.Failed(ex.Message);
        }
    }

    private static async Task<AlbumAnalysisOutcome> AnalyzeAlbumGainAsync(
        string ffmpegPath,
        IReadOnlyList<ReplayGainTrackInput> groupTracks,
        CancellationToken cancellationToken)
    {
        if (HasUniformAudioFormat(groupTracks))
        {
            var concatOutcome = await RunAlbumConcatDemuxerAsync(ffmpegPath, groupTracks, cancellationToken).ConfigureAwait(false);
            if (concatOutcome.Succeeded)
            {
                return concatOutcome;
            }
        }

        return await RunAlbumFilterComplexAsync(ffmpegPath, groupTracks, cancellationToken).ConfigureAwait(false);
    }

    private static bool HasUniformAudioFormat(IReadOnlyList<ReplayGainTrackInput> tracks)
    {
        if (tracks.Count == 0)
        {
            return false;
        }

        var first = tracks[0];
        if (first.SampleRate <= 0 || first.Channels <= 0 || first.BitsPerSample <= 0)
        {
            return false;
        }

        for (var i = 1; i < tracks.Count; i++)
        {
            var current = tracks[i];
            if (current.SampleRate != first.SampleRate
                || current.Channels != first.Channels
                || current.BitsPerSample != first.BitsPerSample)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<AlbumAnalysisOutcome> RunAlbumConcatDemuxerAsync(
        string ffmpegPath,
        IReadOnlyList<ReplayGainTrackInput> tracks,
        CancellationToken cancellationToken)
    {
        var concatFilePath = CreateTempFilePath(".ffconcat");
        try
        {
            var builder = new StringBuilder();
            builder.AppendLine("ffconcat version 1.0");
            foreach (var track in tracks)
            {
                builder.Append("file '");
                builder.Append(EscapeForFfconcat(track.FilePath));
                builder.AppendLine("'");
            }

            await File.WriteAllTextAsync(concatFilePath, builder.ToString(), Utf8NoBom, cancellationToken).ConfigureAwait(false);

            var arguments = new[]
            {
                "-hide_banner",
                "-nostdin",
                "-nostats",
                "-loglevel", "info",
                "-f", "concat",
                "-safe", "0",
                "-i", concatFilePath,
                "-vn",
                "-sn",
                "-dn",
                "-af", "replaygain",
                "-f", "null",
                NullOutputTarget
            };

            var processResult = await RunFfmpegAsync(ffmpegPath, arguments, cancellationToken).ConfigureAwait(false);
            if (TryParseReplayGainOutput(processResult.StandardError, processResult.StandardOutput, out var gainDb, out _))
            {
                return AlbumAnalysisOutcome.Success(gainDb);
            }

            var detail = ExtractErrorDetail(processResult.StandardError, processResult.ExitCode);
            return AlbumAnalysisOutcome.Failed(detail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AlbumAnalysisOutcome.Failed(ex.Message);
        }
        finally
        {
            TryDeleteTempFile(concatFilePath);
        }
    }

    private static async Task<AlbumAnalysisOutcome> RunAlbumFilterComplexAsync(
        string ffmpegPath,
        IReadOnlyList<ReplayGainTrackInput> tracks,
        CancellationToken cancellationToken)
    {
        string? filterScriptPath = null;
        try
        {
            var filterBuilder = new StringBuilder();
            for (var i = 0; i < tracks.Count; i++)
            {
                filterBuilder.Append('[');
                filterBuilder.Append(i.ToString(CultureInfo.InvariantCulture));
                filterBuilder.Append(":a]aresample=");
                filterBuilder.Append(FallbackSampleRateHz.ToString(CultureInfo.InvariantCulture));
                filterBuilder.Append(",aformat=sample_fmts=fltp:channel_layouts=stereo[a");
                filterBuilder.Append(i.ToString(CultureInfo.InvariantCulture));
                filterBuilder.AppendLine("];");
            }

            for (var i = 0; i < tracks.Count; i++)
            {
                filterBuilder.Append("[a");
                filterBuilder.Append(i.ToString(CultureInfo.InvariantCulture));
                filterBuilder.Append(']');
            }

            filterBuilder.Append("concat=n=");
            filterBuilder.Append(tracks.Count.ToString(CultureInfo.InvariantCulture));
            filterBuilder.Append(":v=0:a=1,replaygain[out]");

            var filterGraph = filterBuilder.ToString();
            var useScriptFile = filterGraph.Length > InlineFilterComplexMaxLength;
            if (useScriptFile)
            {
                filterScriptPath = CreateTempFilePath(".filter");
                await File.WriteAllTextAsync(filterScriptPath, filterGraph, Utf8NoBom, cancellationToken).ConfigureAwait(false);
            }

            var arguments = new List<string>(tracks.Count * 2 + 16)
            {
                "-hide_banner",
                "-nostdin",
                "-nostats",
                "-loglevel", "info"
            };

            foreach (var track in tracks)
            {
                arguments.Add("-i");
                arguments.Add(track.FilePath);
            }

            arguments.Add("-vn");
            arguments.Add("-sn");
            arguments.Add("-dn");
            if (useScriptFile && filterScriptPath is not null)
            {
                arguments.Add("-filter_complex_script");
                arguments.Add(filterScriptPath);
            }
            else
            {
                arguments.Add("-filter_complex");
                arguments.Add(filterGraph);
            }
            arguments.Add("-map");
            arguments.Add("[out]");
            arguments.Add("-f");
            arguments.Add("null");
            arguments.Add(NullOutputTarget);

            var processResult = await RunFfmpegAsync(ffmpegPath, arguments, cancellationToken).ConfigureAwait(false);
            if (TryParseReplayGainOutput(processResult.StandardError, processResult.StandardOutput, out var gainDb, out _))
            {
                return AlbumAnalysisOutcome.Success(gainDb);
            }

            var detail = ExtractErrorDetail(processResult.StandardError, processResult.ExitCode);
            return AlbumAnalysisOutcome.Failed(detail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AlbumAnalysisOutcome.Failed(ex.Message);
        }
        finally
        {
            if (filterScriptPath is not null)
            {
                TryDeleteTempFile(filterScriptPath);
            }
        }
    }

    private static List<AlbumGroup> BuildAlbumGroups(
        IReadOnlyList<ReplayGainTrackInput> tracks,
        ReplayGainAlbumGroupingMode groupingMode)
    {
        var groupMap = new Dictionary<string, AlbumGroup>(StringComparer.OrdinalIgnoreCase);
        var orderedGroups = new List<AlbumGroup>();

        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            var (groupKey, displayLabel) = ResolveAlbumGroupKeyAndLabel(track, groupingMode);

            if (!groupMap.TryGetValue(groupKey, out var group))
            {
                group = new AlbumGroup(groupKey, displayLabel);
                groupMap[groupKey] = group;
                orderedGroups.Add(group);
            }

            group.TrackIndices.Add(i);
        }

        return orderedGroups;
    }

    private static (string Key, string Label) ResolveAlbumGroupKeyAndLabel(
        ReplayGainTrackInput track,
        ReplayGainAlbumGroupingMode groupingMode)
    {
        switch (groupingMode)
        {
            case ReplayGainAlbumGroupingMode.SingleAlbum:
                return (SingleAlbumGroupKey, SingleAlbumGroupLabel);

            case ReplayGainAlbumGroupingMode.ByFolder:
                return ResolveFolderGroup(track.DirectoryPath);

            case ReplayGainAlbumGroupingMode.ByAlbumTag:
            default:
                var album = track.Album?.Trim() ?? string.Empty;
                if (album.Length == 0)
                {
                    var folder = ResolveFolderGroup(track.DirectoryPath);
                    return (folder.Key, $"{folder.Label} (フォルダ)");
                }

                var albumArtist = track.AlbumArtist?.Trim() ?? string.Empty;
                var tagKey = $"tag:{albumArtist}|{album}";
                var tagLabel = albumArtist.Length > 0 ? $"{albumArtist} - {album}" : album;
                return (tagKey, tagLabel);
        }
    }

    private static (string Key, string Label) ResolveFolderGroup(string? directoryPath)
    {
        var normalizedDir = string.IsNullOrWhiteSpace(directoryPath)
            ? string.Empty
            : directoryPath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (normalizedDir.Length == 0)
        {
            return ("dir:", UnnamedFolderGroupLabel);
        }

        var folderName = Path.GetFileName(normalizedDir);
        var label = string.IsNullOrWhiteSpace(folderName) ? normalizedDir : folderName;
        return ($"dir:{normalizedDir}", label);
    }

    private static FfmpegPathResolution ResolveFfmpegPath(string? configuredPath)
    {
        var trimmed = TrimSurroundingQuotes(configuredPath);
        if (!string.IsNullOrWhiteSpace(trimmed))
        {
            var candidate = Directory.Exists(trimmed)
                ? Path.Combine(trimmed, FfmpegExecutableName)
                : trimmed;

            if (File.Exists(candidate))
            {
                return new FfmpegPathResolution(true, Path.GetFullPath(candidate), IsAutoDetected: false, null);
            }

            return new FfmpegPathResolution(
                Found: false,
                ResolvedPath: null,
                IsAutoDetected: false,
                ErrorMessage: $"指定されたパスに FFmpeg が見つかりません: {candidate}");
        }

        // 自動検出 1: アプリ実行フォルダ配下
        var baseDir = AppContext.BaseDirectory;
        var localCandidates = new[]
        {
            Path.Combine(baseDir, FfmpegExecutableName),
            Path.Combine(baseDir, FfmpegSubFolderName, FfmpegExecutableName),
            Path.Combine(baseDir, FfmpegSubFolderName, FfmpegBinSubFolderName, FfmpegExecutableName)
        };

        foreach (var localCandidate in localCandidates)
        {
            if (File.Exists(localCandidate))
            {
                return new FfmpegPathResolution(true, Path.GetFullPath(localCandidate), IsAutoDetected: true, null);
            }
        }

        // 自動検出 2: 環境変数 PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var rawDir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var dir = TrimSurroundingQuotes(rawDir);
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }

                try
                {
                    var candidate = Path.Combine(dir, FfmpegExecutableName);
                    if (File.Exists(candidate))
                    {
                        return new FfmpegPathResolution(true, Path.GetFullPath(candidate), IsAutoDetected: true, null);
                    }
                }
                catch
                {
                    // 無効な PATH エントリは無視
                }
            }
        }

        return new FfmpegPathResolution(
            Found: false,
            ResolvedPath: null,
            IsAutoDetected: true,
            ErrorMessage: "FFmpeg (ffmpeg.exe) が環境変数 PATH またはアプリフォルダ内に見つかりません。「参照...」から ffmpeg.exe を指定してください。");
    }

    private static async Task<ProcessExecutionResult> RunFfmpegAsync(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        int? timeoutMilliseconds = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("FFmpeg プロセスを開始できませんでした。");
        }

        using var timeoutCts = timeoutMilliseconds.HasValue
            ? new CancellationTokenSource(timeoutMilliseconds.Value)
            : null;
        using var linkedCts = timeoutCts is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token)
            : null;
        var effectiveToken = linkedCts?.Token ?? cancellationToken;

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        await using var killRegistration = effectiveToken.Register(() => TryKillProcessTree(process));

        try
        {
            await process.WaitForExitAsync(effectiveToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch
            {
                // 強制終了に伴うストリーム終了例外は無視
            }

            throw;
        }

        var standardOutput = await stdoutTask.ConfigureAwait(false);
        var standardError = await stderrTask.ConfigureAwait(false);
        return new ProcessExecutionResult(process.ExitCode, standardOutput, standardError);
    }

    private static bool TryParseReplayGainOutput(
        string standardError,
        string standardOutput,
        out double gainDb,
        out double peak)
    {
        if (TryParseReplayGainFromText(standardError, out gainDb, out peak))
        {
            return true;
        }

        return TryParseReplayGainFromText(standardOutput, out gainDb, out peak);
    }

    private static bool TryParseReplayGainFromText(string text, out double gainDb, out double peak)
    {
        gainDb = 0.0;
        peak = 0.0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var gainMatches = TrackGainRegex().Matches(text);
        var peakMatches = TrackPeakRegex().Matches(text);
        if (gainMatches.Count == 0 || peakMatches.Count == 0)
        {
            return false;
        }

        var lastGainText = gainMatches[^1].Groups[1].Value;
        var lastPeakText = peakMatches[^1].Groups[1].Value;

        if (!double.TryParse(lastGainText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedGain)
            || !double.IsFinite(parsedGain))
        {
            return false;
        }

        if (!double.TryParse(lastPeakText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedPeak)
            || !double.IsFinite(parsedPeak)
            || parsedPeak < 0.0)
        {
            return false;
        }

        gainDb = parsedGain;
        peak = parsedPeak;
        return true;
    }

    private static string EscapeForFfconcat(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath).Replace('\\', '/');
        return fullPath.Replace("'", @"'\''", StringComparison.Ordinal);
    }

    private static string CreateTempFilePath(string extension)
    {
        return Path.Combine(Path.GetTempPath(), $"{TempFilePrefix}{Guid.NewGuid():N}{extension}");
    }

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 一時ファイル削除失敗は無視
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // プロセス終了済み・アクセス競合時は無視
        }
    }

    private static string TrimSurroundingQuotes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return raw.Trim().Trim('"').Trim();
    }

    private static string? ExtractFirstNonEmptyLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return null;
    }

    private static string ExtractErrorDetail(string standardError, int exitCode)
    {
        if (!string.IsNullOrWhiteSpace(standardError))
        {
            var lines = standardError
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (!line.StartsWith("[Parsed_replaygain", StringComparison.OrdinalIgnoreCase))
                {
                    return line;
                }
            }
        }

        return $"ReplayGain 値を取得できませんでした（FFmpeg 終了コード: {exitCode}）。";
    }

    private sealed class AlbumGroup(string key, string displayLabel)
    {
        public string Key { get; } = key;
        public string DisplayLabel { get; } = displayLabel;
        public List<int> TrackIndices { get; } = [];
    }

    private sealed class TrackAnalysisState
    {
        public bool Succeeded { get; private init; }
        public double? TrackGainDb { get; private init; }
        public double? TrackPeak { get; private init; }
        public double? AlbumGainDb { get; set; }
        public double? AlbumPeak { get; set; }
        public string? ErrorMessage { get; private init; }
        public string? AlbumErrorMessage { get; set; }

        public static TrackAnalysisState Success(double trackGainDb, double trackPeak) => new()
        {
            Succeeded = true,
            TrackGainDb = trackGainDb,
            TrackPeak = trackPeak
        };

        public static TrackAnalysisState Failed(string errorMessage) => new()
        {
            Succeeded = false,
            ErrorMessage = errorMessage
        };
    }

    private sealed record AlbumAnalysisOutcome(bool Succeeded, double? GainDb, string? ErrorMessage)
    {
        public static AlbumAnalysisOutcome Success(double gainDb) => new(true, gainDb, null);
        public static AlbumAnalysisOutcome Failed(string errorMessage) => new(false, null, errorMessage);
    }

    private sealed record FfmpegPathResolution(
        bool Found,
        string? ResolvedPath,
        bool IsAutoDetected,
        string? ErrorMessage);

    private sealed record ProcessExecutionResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
