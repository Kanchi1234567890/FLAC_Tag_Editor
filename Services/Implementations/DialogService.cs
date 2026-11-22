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

    public async Task<IReadOnlyList<CustomTagDefinition>?> ShowCustomTagManagerAsync(
        IReadOnlyList<CustomTagDefinition> currentDefinitions)
    {
        ArgumentNullException.ThrowIfNull(currentDefinitions);

        var owner = GetMainWindow();
        if (owner is null)
        {
            return null;
        }

        var viewModel = new CustomTagManagerDialogViewModel(currentDefinitions);
        var dialog = new CustomTagManagerDialog
        {
            DataContext = viewModel
        };

        void OnCloseRequested(IReadOnlyList<CustomTagDefinition>? definitions)
        {
            dialog.Close(definitions);
        }

        viewModel.CloseRequested += OnCloseRequested;
        try
        {
            return await dialog.ShowDialog<IReadOnlyList<CustomTagDefinition>?>(owner);
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
