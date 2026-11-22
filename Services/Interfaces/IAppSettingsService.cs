using System;
using System.Threading;
using System.Threading.Tasks;
using FLAC_Tag_Editor.Models;

namespace FLAC_Tag_Editor.Services.Interfaces;

/// <summary>
/// アプリケーション設定の読み込みおよび保存を抽象化するサービスインターフェース。
/// </summary>
public interface IAppSettingsService
{
    /// <summary>
    /// 設定ファイルの保存先フルパス。
    /// </summary>
    string SettingsFilePath { get; }

    /// <summary>
    /// 設定ファイルを読み込む。未作成時は既定値を返し、破損・I/Oエラー時は既定値とエラー情報を返す。
    /// </summary>
    Task<AppSettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定された設定を永続化する。
    /// </summary>
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// 現在の設定スナップショットに対して部分更新を適用し、永続化する。
    /// 他セクションの設定を上書きせずに特定セクションのみを更新する場合に使用する。
    /// </summary>
    Task UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken = default);
}
