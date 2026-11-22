using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using FLAC_Tag_Editor.Models;

namespace FLAC_Tag_Editor.ViewModels;

/// <summary>
/// 各トラックが持つ「定義済み独自タグ」1 項目分の編集 ViewModel。
/// </summary>
public sealed partial class CustomTagFieldViewModel : ViewModelBase
{
    public const string MultiValueSeparator = "; ";

    private readonly Action _onChanged;
    private readonly IReadOnlyList<CustomTagEntry> _sourceEntries;
    private readonly string _initialJoinedValue;

    [ObservableProperty]
    private string _value;

    public CustomTagFieldViewModel(
        CustomTagDefinitionViewModel definition,
        IReadOnlyList<CustomTagEntry> sourceEntries,
        Action onChanged)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _sourceEntries = sourceEntries ?? [];
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));

        _initialJoinedValue = JoinValues(_sourceEntries);
        _value = _initialJoinedValue;
    }

    /// <summary>
    /// 対応する共有独自タグ定義 ViewModel。
    /// </summary>
    public CustomTagDefinitionViewModel Definition { get; }

    public string Key => Definition.Key;

    public string DisplayLabel => Definition.DisplayLabel;

    /// <summary>
    /// 元ファイルに同名キーの複数値が存在し、かつ未編集で保持されているかどうか。
    /// </summary>
    public bool HasPreservedMultipleValues =>
        _sourceEntries.Count > 1
        && string.Equals(Value, _initialJoinedValue, StringComparison.Ordinal);

    partial void OnValueChanged(string value)
    {
        if (value is null)
        {
            Value = string.Empty;
            return;
        }

        OnPropertyChanged(nameof(HasPreservedMultipleValues));
        _onChanged();
    }

    /// <summary>
    /// 保存・差分判定・再分割用に現在の編集状態を <see cref="CustomTagEntry"/> 列へ展開する。
    /// </summary>
    public IEnumerable<CustomTagEntry> Flatten()
    {
        if (_sourceEntries.Count > 0 && string.Equals(Value, _initialJoinedValue, StringComparison.Ordinal))
        {
            // 未編集（または元の連結値と同じ）なら複数値と元のキー表記をそのまま維持
            return _sourceEntries;
        }

        if (string.IsNullOrEmpty(Value))
        {
            // 空欄は書き込まない（既存値を空にした場合は削除）
            return [];
        }

        var keyToWrite = _sourceEntries.Count > 0 ? _sourceEntries[0].Key : Definition.Key;
        return [new CustomTagEntry(keyToWrite, Value)];
    }

    private static string JoinValues(IReadOnlyList<CustomTagEntry> entries)
    {
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        if (entries.Count == 1)
        {
            return entries[0].Value;
        }

        return string.Join(MultiValueSeparator, entries.Select(e => e.Value));
    }
}
