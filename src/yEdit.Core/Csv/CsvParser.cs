using System.Text;
using yEdit.Core.Buffers;

namespace yEdit.Core.Csv;

/// <summary>
/// RFC 4180 準拠の CSV パーサ（区切りはカンマ固定）。各フィールドの元テキスト上の
/// UTF-16 スパン（引用符込み）を保持する。引用符内のカンマ・改行・"" エスケープに対応。
/// 引用符が閉じないまま EOF に達した場合のみ Ok=false。
/// </summary>
public static class CsvParser
{
    public static CsvDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var reader = new StringReader(text);
        return ParseCore(reader);
    }

    /// <summary>
    /// TextSnapshot 全文をチャンク供給で読みながらパースする。全文 string 実体化を経由せず、
    /// 1GB 級 CSV でもピーク使用量が O(chunk + パース中間) に収まる。
    /// </summary>
    public static CsvDocument Parse(TextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var reader = snapshot.CreateReader();
        return ParseCore(reader);
    }

    /// <summary>Parse(string) と Parse(TextSnapshot) の共通実装。挙動は
    /// 元の char index ベース実装と等価(text[i]→Read()、text[i+1]→Peek()、i+=2→Read() 追加消費)。</summary>
    private static CsvDocument ParseCore(TextReader reader)
    {
        var rows = new List<IReadOnlyList<CsvField>>();
        var row = new List<CsvField>();
        var sb = new StringBuilder();   // 現在フィールドの論理値
        int pos = 0;
        int fieldStart = 0;
        bool inQuotes = false;
        bool ok = true;

        void EndField(int endExclusive)
        {
            row.Add(new CsvField(fieldStart, endExclusive - fieldStart, sb.ToString()));
            sb.Clear();
        }
        void EndRow()
        {
            rows.Add(row);
            row = new List<CsvField>();
        }

        int ci;
        while ((ci = reader.Read()) != -1)
        {
            char c = (char)ci;
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"') { sb.Append('"'); reader.Read(); pos += 2; continue; }
                    inQuotes = false; pos++; continue;   // 閉じ引用符
                }
                sb.Append(c); pos++; continue;           // 引用符内のカンマ・改行も literal
            }

            if (c == '"' && pos == fieldStart) { inQuotes = true; pos++; continue; } // 開き引用符（フィールド先頭のみ）
            if (c == ',') { EndField(pos); pos++; fieldStart = pos; continue; }
            if (c == '\r' || c == '\n')
            {
                EndField(pos);
                int lb = 1;
                if (c == '\r' && reader.Peek() == '\n') { reader.Read(); lb = 2; }
                EndRow();
                pos += lb; fieldStart = pos; continue;
            }
            sb.Append(c); pos++;   // 通常文字（閉じ引用符後の余剰文字も寛容に literal 扱い）
        }

        if (inQuotes) ok = false;   // 引用符未終端

        // 末尾レコードの確定: 直近の改行後に内容が無い「末尾の空レコード」は捨てる。
        if (pos > fieldStart || row.Count > 0)
        {
            EndField(pos);
            EndRow();
        }

        return new CsvDocument(rows, ok);
    }
}
