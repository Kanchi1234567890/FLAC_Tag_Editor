namespace FLAC_Tag_Editor.Models;

/// <summary>
/// 未保存変更がある状態での確認ダイアログにおけるユーザー選択結果。
/// </summary>
public enum UnsavedChangesAction
{
    SaveAndContinue,
    DiscardAndContinue,
    Cancel
}

/// <summary>
/// 未保存変更確認ダイアログの表示内容。
/// </summary>
public sealed record UnsavedChangesPromptContext(
    string Title,
    string Message,
    string? WarningMessage,
    string SaveButtonText,
    string DiscardButtonText,
    string CancelButtonText,
    bool CanSave);
