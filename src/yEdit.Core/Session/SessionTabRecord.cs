namespace yEdit.Core.Session;

/// <summary>
/// タブ 1 個の永続表現。通常終了時に AppSettings.LastSession の一部として settings.json へ保存し、
/// 次回起動時に前回タブ列を復元する。無題タブの本文本体は BufferKey で LastSessionBuffersStore を参照する
/// (=大きな本文で settings.json が肥大化するのを避ける)。
/// </summary>
public sealed record SessionTabRecord(
    string? Path,
    int UntitledNumber,
    string? BufferKey,
    bool IsActive,
    int CaretLine,
    int CaretColumn
);
