using System.Threading.Tasks;
using FLAC_Tag_Editor.Models;

namespace FLAC_Tag_Editor.Services.Interfaces;

/// <summary>
/// アプリケーション内の確認ダイアログ等を提供するサービスインターフェース。
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// 未保存変更がある状態での確認ダイアログをモーダル表示し、ユーザーの選択結果を返す。
    /// </summary>
    Task<UnsavedChangesAction> ConfirmUnsavedChangesAsync(UnsavedChangesPromptContext context);
}
