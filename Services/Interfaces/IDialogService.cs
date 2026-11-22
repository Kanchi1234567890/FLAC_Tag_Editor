using System.Collections.Generic;
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

    /// <summary>
    /// 独自タグ定義管理ダイアログをモーダル表示し、OK 確定時は編集後の定義一覧、キャンセル時は null を返す。
    /// </summary>
    Task<IReadOnlyList<CustomTagDefinition>?> ShowCustomTagManagerAsync(
        IReadOnlyList<CustomTagDefinition> currentDefinitions);
}
