using Xunit;
using yEdit.Core.Backup;

namespace yEdit.Core.Tests.Backup;

/// <summary>
/// HIGH-1: BackupRecord.Id の白リスト検証(GUID N 形式=32 桁 hex・区切りなし)を固定する。
/// %APPDATA%\yEdit\backups 配下に攻撃者が植えた不正 JSON の Id からトラバーサル入口を作らせないため、
/// BackupStore.Write / Delete が Id を Path.Combine に流す前段で拒否できる形にする。
/// BK-L-8 (2026-07-20 v0.11): lowercase 厳格化。大文字/mixed case は攻撃者 JSON 由来を疑い拒否する
/// (NTFS の case-insensitive path と id 文字列比較の齟齬による衝突リスクを潰す)。
/// </summary>
public class BackupIdValidatorTests
{
    [Theory]
    [InlineData("00000000000000000000000000000000")] // 全 0 GUID N
    [InlineData("abcdef0123456789abcdef0123456789")] // 通常
    public void IsValid_ReturnsTrue_ForCanonicalGuidN(string id) =>
        Assert.True(BackupIdValidator.IsValid(id));

    /// <summary>BK-L-8: 大文字/mixed case は GUID N として parse できても拒否する。</summary>
    [Theory]
    [InlineData("ABCDEF0123456789ABCDEF0123456789")] // 全大文字
    [InlineData("abcdef0123456789ABCDEF0123456789")] // mixed case
    public void IsValid_ReturnsFalse_ForUppercaseHex(string id) =>
        Assert.False(BackupIdValidator.IsValid(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abcdef0123456789abcdef012345678")] // 31 文字
    [InlineData("abcdef0123456789abcdef01234567890")] // 33 文字
    [InlineData("abcdef01-2345-6789-abcd-ef0123456789")] // ハイフン形式
    [InlineData(@"..\..\..\..\Windows\System32\evil")] // トラバーサル
    [InlineData(@"C:\Windows\Temp\rooted")] // ルート付き
    [InlineData("xyz injection")] // 制御文字類
    [InlineData("gggggggggggggggggggggggggggggggg")] // 非 hex
    [InlineData("aBcDeF0123456789abcdef0123456789")] // BK-L-8: mixed case (先頭のみ大文字混在)
    public void IsValid_ReturnsFalse_ForInvalidId(string? id) =>
        Assert.False(BackupIdValidator.IsValid(id));
}
