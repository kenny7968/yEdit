namespace yEdit.Core.Backup;

/// <summary>
/// 1 文書ぶんのバックアップ記録（サイドカー JSON の中身）。本文とともに、復元に必要な
/// メタ（元パス・文字コード・改行・無題連番）を持つ。Id はファイル名（&lt;Id&gt;.json）に使う GUID。
/// </summary>
public sealed record BackupRecord(
    string Id,
    string? OriginalPath,
    int UntitledNumber,
    int CodePage,
    bool HasBom,
    int LineEndingId,
    string Content,
    DateTime TimestampUtc
);
