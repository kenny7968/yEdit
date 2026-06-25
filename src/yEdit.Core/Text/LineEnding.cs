namespace yEdit.Core.Text;

public enum LineEnding { Crlf, Lf, Cr }

public static class LineEndingDetector
{
    /// <summary>本文中で最も多い改行種別を返す。改行が無ければ CRLF（Windows 既定）。</summary>
    public static LineEnding Detect(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else if (c == '\n') lf++;
        }
        if (crlf == 0 && lf == 0 && cr == 0) return LineEnding.Crlf;
        if (crlf >= lf && crlf >= cr) return LineEnding.Crlf;
        return lf >= cr ? LineEnding.Lf : LineEnding.Cr;
    }
}
