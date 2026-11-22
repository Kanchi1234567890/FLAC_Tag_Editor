using System;
using CommunityToolkit.Mvvm.Input;
using FLAC_Tag_Editor.Models;

namespace FLAC_Tag_Editor.ViewModels;

/// <summary>
/// 未保存変更確認ダイアログ用 ViewModel。
/// </summary>
public partial class UnsavedChangesDialogViewModel : ViewModelBase
{
    public UnsavedChangesDialogViewModel()
        : this(new UnsavedChangesPromptContext(
            Title: "未保存の変更があります",
            Message: "未保存の変更が 3 件あります。続行する前に保存しますか？",
            WarningMessage: null,
            SaveButtonText: "保存して続行",
            DiscardButtonText: "破棄して続行",
            CancelButtonText: "キャンセル",
            CanSave: true))
    {
    }

    public UnsavedChangesDialogViewModel(UnsavedChangesPromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Title = context.Title;
        Message = context.Message;
        WarningMessage = context.WarningMessage ?? string.Empty;
        HasWarningMessage = !string.IsNullOrWhiteSpace(context.WarningMessage);
        SaveButtonText = context.SaveButtonText;
        DiscardButtonText = context.DiscardButtonText;
        CancelButtonText = context.CancelButtonText;
        CanSave = context.CanSave;
    }

    public event Action<UnsavedChangesAction>? CloseRequested;

    public string Title { get; }

    public string Message { get; }

    public string WarningMessage { get; }

    public bool HasWarningMessage { get; }

    public string SaveButtonText { get; }

    public string DiscardButtonText { get; }

    public string CancelButtonText { get; }

    public bool CanSave { get; }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void SaveAndContinue()
    {
        CloseRequested?.Invoke(UnsavedChangesAction.SaveAndContinue);
    }

    [RelayCommand]
    private void DiscardAndContinue()
    {
        CloseRequested?.Invoke(UnsavedChangesAction.DiscardAndContinue);
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseRequested?.Invoke(UnsavedChangesAction.Cancel);
    }
}
