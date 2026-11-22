using System.Threading.Tasks;

namespace FLAC_Tag_Editor.ViewModels;

/// <summary>
/// ウィンドウ終了要求を ViewModel 側で非同期に検証・承認するためのインターフェース。
/// </summary>
public interface IWindowCloseGuard
{
    /// <summary>
    /// ウィンドウ終了時に確認または抑止処理が必要かどうか。
    /// </summary>
    bool ShouldInterceptClose { get; }

    /// <summary>
    /// ウィンドウ終了を許可してよいか非同期に確認する（true で終了許可）。
    /// </summary>
    Task<bool> ConfirmCloseAsync();
}
