namespace yEdit.Core.Backup;

/// <summary>
/// BackupRecord.Id が正規の GUID N 形式(32 桁 hex・区切りなし)かを検証する。
/// ここを通さない Id は、%APPDATA%\yEdit\backups 配下に攻撃者が植えた不正 JSON の
/// 可能性がある(パストラバーサル入口を BackupStore.Write / Delete で塞ぐための白リスト)。
/// </summary>
public static class BackupIdValidator
{
    public static bool IsValid(string? id) =>
        !string.IsNullOrEmpty(id) && Guid.TryParseExact(id, "N", out _);
}
