using System.Text;

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
        var rows = new List<IReadOnlyList<CsvField>>();
        var row = new List<CsvField>();
        var sb = new StringBuilder();   // 現在フィールドの論理値
        int n = text.Length;
        int i = 0;
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

        while (i < n)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < n && text[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                    inQuotes = false; i++; continue;   // 閉じ引用符
                }
                sb.Append(c); i++; continue;           // 引用符内のカンマ・改行も literal
            }

            if (c == '"' && i == fieldStart) { inQuotes = true; i++; continue; } // 開き引用符（フィールド先頭のみ）
            if (c == ',') { EndField(i); i++; fieldStart = i; continue; }
            if (c == '\r' || c == '\n')
            {
                EndField(i);
                int lb = (c == '\r' && i + 1 < n && text[i + 1] == '\n') ? 2 : 1;
                EndRow();
                i += lb; fieldStart = i; continue;
            }
            sb.Append(c); i++;   // 通常文字（閉じ引用符後の余剰文字も寛容に literal 扱い）
        }

        if (inQuotes) ok = false;   // 引用符未終端

        // 末尾レコードの確定: 直近の改行後に内容が無い「末尾の空レコード」は捨てる。
        if (i > fieldStart || row.Count > 0)
        {
            EndField(n);
            EndRow();
        }

        return new CsvDocument(rows, ok);
    }
}
