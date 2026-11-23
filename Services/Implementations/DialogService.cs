using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using FLAC_Tag_Editor.Models;
using FLAC_Tag_Editor.Services.Interfaces;
using FLAC_Tag_Editor.ViewModels;
using FLAC_Tag_Editor.Views;

namespace FLAC_Tag_Editor.Services.Implementations;

/// <summary>
/// Avalonia のモーダルダイアログを表示するサービス実装。
/// </summary>
public sealed class DialogService : IDialogService
{
    private readonly IReplayGainService _replayGainService;
    private readonly IFileDialogService _fileDialogService;
    private readonly IAppSettingsService _appSettingsService;

    public DialogService(
        IReplayGainService replayGainService,
        IFileDialogService fileDialogService,
        IAppSettingsService appSettingsService)
    {
        _replayGainService = replayGainService ?? throw new ArgumentNullException(nameof(replayGainService));
        _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
        _appSettingsService = appSettingsService ?? throw new ArgumentNullException(nameof(appSettingsService));
    }

    public async Task<UnsavedChangesAction> ConfirmUnsavedChangesAsync(UnsavedChangesPromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var owner = GetMainWindow();
        if (owner is null)
        {
            return UnsavedChangesAction.Cancel;
        }

        var viewModel = new UnsavedChangesDialogViewModel(context);
        var dialog = new UnsavedChangesDialog
        {
            DataContext = viewModel
        };

        void OnCloseRequested(UnsavedChangesAction action)
        {
            dialog.Close(action);
        }

        viewModel.CloseRequested += OnCloseRequested;
        try
        {
            var result = await dialog.ShowDialog<UnsavedChangesAction?>(owner);
            return result ?? UnsavedChangesAction.Cancel;
        }
        finally
        {
            viewModel.CloseRequested -= OnCloseRequested;
        }
    }

    public async Task<CustomTagManagerResult?> ShowCustomTagManagerAsync(
        IReadOnlyList<CustomTagDefinition> currentDefinitions,
        IReadOnlyList<string>? savedCustomColumnKeys = null)
    {
        ArgumentNullException.ThrowIfNull(currentDefinitions);

        var owner = GetMainWindow();
        if (owner is null)
        {
            return null;
        }

        var viewModel = new CustomTagManagerDialogViewModel(currentDefinitions, savedCustomColumnKeys);
        var dialog = new CustomTagManagerDialog
        {
            DataContext = viewModel
        };

        void OnCloseRequested(CustomTagManagerResult? result)
        {
            dialog.Close(result);
        }

        viewModel.CloseRequested += OnCloseRequested;
        try
        {
            return await dialog.ShowDialog<CustomTagManagerResult?>(owner);
        }
        finally
        {
            viewModel.CloseRequested -= OnCloseRequested;
        }
    }

    public async Task<ReplayGainDialogResult?> ShowReplayGainAsync(ReplayGainDialogContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var owner = GetMainWindow();
        if (owner is null)
        {
            return null;
        }

        using var viewModel = new ReplayGainDialogViewModel(
            context,
            _replayGainService,
            _fileDialogService,
            _appSettingsService);
        var dialog = new ReplayGainDialog
        {
            DataContext = viewModel
        };

        void OnCloseRequested(ReplayGainDialogResult? result)
        {
            dialog.Close(result);
        }

        viewModel.CloseRequested += OnCloseRequested;
        try
        {
            return await dialog.ShowDialog<ReplayGainDialogResult?>(owner);
        }
        finally
        {
            viewModel.CloseRequested -= OnCloseRequested;
        }
    }

    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }

        return null;
    }
}
