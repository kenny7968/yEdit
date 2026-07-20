namespace yEdit.Core.Csv;

/// <summary>CSV フィールドの直列化（RFC 4180・区切りはカンマ固定）。F2 編集確定時に使う。</summary>
public static class CsvWriter
{
    /// <summary>OWASP CSV Injection 対策で apostrophe 前置の対象となる先頭文字集合。
    /// Excel / Sheets は先頭が = + - @ TAB CR のセルを数式/コマンド起動として解釈するため、
    /// 該当する場合は先頭に apostrophe (') を付けて文字列扱いに落とす。</summary>
    internal const string FormulaPrefixChars = "=+-@\t\r";

    /// <summary>論理値を CSV フィールド文字列へ直列化する。カンマ・二重引用符・CR・LF を含む場合のみ
    /// 二重引用符で囲み、内部の " を "" にエスケープする。それ以外は素通し。
    /// ただし先頭が <see cref="FormulaPrefixChars"/> のいずれかなら OWASP CSV Injection 対策として
    /// apostrophe (') を先頭付加してから既存の quote 判定に流す。</summary>
    public static string EscapeField(string value)
    {
        // formula injection 対策: 先頭文字が危険なら apostrophe を前置。
        // 空文字は index 0 アクセス不要のため素通し。
        if (value.Length > 0 && FormulaPrefixChars.Contains(value[0]))
        {
            value = "'" + value;
        }

        bool needsQuote =
            value.Contains(',', System.StringComparison.Ordinal)
            || value.Contains('"', System.StringComparison.Ordinal)
            || value.Contains('\r', System.StringComparison.Ordinal)
            || value.Contains('\n', System.StringComparison.Ordinal);
        return needsQuote ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
