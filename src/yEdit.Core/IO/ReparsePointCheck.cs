namespace yEdit.Core.IO;

/// <summary>
/// path が Windows の reparse point (symbolic link / directory junction) を指しているかを
/// 判定する述語。CSV-L-7 (v0.11): <see cref="AtomicFile.Write(string, byte[])"/> /
/// <see cref="AtomicFile.Write(string, System.Action{System.IO.Stream})"/> の
/// <see cref="File.Replace(string, string, string?)"/> は reparse point を follow して
/// 権限外の実体を上書きし得るため、書込直前に dest を本 check で拒否する用途。
///
/// silent fallback ポリシー: missing path / 属性取得失敗
/// (<see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>) は false
/// (=非 reparse point 扱い=通常経路へ) を返す。理由:
///  - <see cref="AtomicFile.Write(string, byte[])"/> は「新規作成」も正常経路のため missing を
///    true 扱いにすると全新規保存が拒否される。
///  - 属性取得失敗は「攻撃者もその要素をバイパスに使えない」= 無害扱いで進める
///    (Backup.OriginalPathValidator の parent walk と同方針)。
///
/// leaf 属性のみを確認する (parent chain は歩かない)。junction dir 配下の regular file
/// への書込は本 check では検出できないが、それは AtomicFile 側の責務ではなく
/// <see cref="Backup.OriginalPathValidator"/> の parent walk (BK-M-1) が担う。
/// AtomicFile は「dest 自体が symlink/junction のとき follow を止める」ことだけを保証する。
/// </summary>
public static class ReparsePointCheck
{
    /// <summary>
    /// path が存在し、かつ <see cref="FileAttributes.ReparsePoint"/> bit を持つなら true。
    /// null / 空文字 / 不存在 / 属性取得失敗はすべて false (silent fallback)。
    /// </summary>
    public static bool IsReparsePoint(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        // File.Exists / Directory.Exists は例外を投げず bool を返す契約
        // (不正パス文字・存在しないボリューム等でも false)。missing 判定はここで確定する。
        if (!File.Exists(path) && !Directory.Exists(path))
            return false;
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 属性取得できない要素はバイパスに使えない=silent fallback (false=通常経路)。
            return false;
        }
    }
}
