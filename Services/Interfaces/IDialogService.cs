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
    /// 独自タグ定義管理ダイアログをモーダル表示し、OK 確定時は結果（編集後の定義一覧とクリーンアップ指定）、キャンセル時は null を返す。
    /// </summary>
    Task<CustomTagManagerResult?> ShowCustomTagManagerAsync(
        IReadOnlyList<CustomTagDefinition> currentDefinitions,
        IReadOnlyList<string>? savedCustomColumnKeys = null);

    /// <summary>
    /// ReplayGain 計算ダイアログをモーダル表示し、適用確定時は計算結果と保存オプション、キャンセル時は null を返す。
    /// </summary>
    Task<ReplayGainDialogResult?> ShowReplayGainAsync(ReplayGainDialogContext context);
}
