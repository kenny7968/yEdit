using System.Text;
using System.Text.RegularExpressions;

namespace yEdit.Core.Search;

/// <summary>
/// 単一ファイル検索・置換の照合エンジン。内部を .NET Regex に統一する
/// （リテラルは Regex.Escape、単語単位は \b 境界、大小無視は IgnoreCase）。
/// オフセットは UTF-16 文字位置。正規表現の不正は例外でなく IsValid/Error で返す。
/// </summary>
public sealed class TextSearcher
{
    private readonly Regex? _regex;
    private readonly bool _expand; // 正規表現モードのみ置換文字列の $ を展開

    /// <summary>照合条件が有効（正規表現を構築できた）か。</summary>
    public bool IsValid => _regex is not null;

    /// <summary>無効な場合の理由（空パターンや不正な正規表現）。有効なら null。</summary>
    public string? Error { get; }

    /// <summary>照合条件から照合エンジンを構築する。不正でも例外は投げず IsValid/Error で返す。</summary>
    public TextSearcher(SearchOptions options)
    {
        _expand = options.UseRegex;
        if (string.IsNullOrEmpty(options.Pattern))
        {
            Error = "検索文字列が空です。";
            return;
        }
        string body = options.UseRegex ? options.Pattern : Regex.Escape(options.Pattern);
        if (options.WholeWord) body = $@"\b(?:{body})\b";
        var opts = RegexOptions.CultureInvariant;
        if (!options.MatchCase) opts |= RegexOptions.IgnoreCase;
        try { _regex = new Regex(body, opts); }
        catch (ArgumentException ex) { Error = ex.Message; }
    }

    /// <summary>text 全体のヒット件数。無効なら 0。</summary>
    public int Count(string text) => _regex is null ? 0 : _regex.Matches(text).Count;

    /// <summary>from 以降で最初のヒット（折り返しなし）。</summary>
    public MatchSpan? FindNext(string text, int from)
    {
        if (_regex is null) return null;
        if (from < 0) from = 0;
        if (from > text.Length) return null;
        var m = _regex.Match(text, from);
        return m.Success ? new MatchSpan(m.Index, m.Length) : null;
    }

    /// <summary>before より厳密に前で最後のヒット（折り返しなし）。</summary>
    public MatchSpan? FindPrev(string text, int before)
    {
        if (_regex is null) return null;
        MatchSpan? last = null;
        foreach (Match m in _regex.Matches(text))
        {
            if (m.Index >= before) break;
            last = new MatchSpan(m.Index, m.Length);
        }
        return last;
    }

    /// <summary>span を全ヒット中の何件目か（1始まり, total）。span がヒットでなければ null。</summary>
    public (int Ordinal, int Total)? Locate(string text, MatchSpan span)
    {
        if (_regex is null) return null;
        int ordinal = 0, total = 0; bool found = false;
        foreach (Match m in _regex.Matches(text))
        {
            total++;
            if (m.Index == span.Start && m.Length == span.Length) { ordinal = total; found = true; }
        }
        return found ? (ordinal, total) : null;
    }

    /// <summary>span が実際のヒットなら置換文字列を返す（正規表現は $1 等展開・リテラルは素のまま）。違えば null。</summary>
    public string? ReplacementAt(string text, MatchSpan span, string replacement)
    {
        if (_regex is null) return null;
        if (span.Start < 0 || span.Start > text.Length) return null;
        var m = _regex.Match(text, span.Start);
        if (!m.Success || m.Index != span.Start || m.Length != span.Length) return null;
        return Expand(m, replacement);
    }

    /// <summary>全文置換。返すのは置換後の全文（=新断片）と件数。</summary>
    public (string Fragment, int Count) ReplaceAll(string text, string replacement)
        => ReplaceInRange(text, 0, text.Length, replacement);

    /// <summary>
    /// [start, start+length) に完全に収まるヒットだけ置換し、その範囲の置換後断片と件数を返す。
    /// 範囲外・境界をまたぐヒットは対象外。エディタはこの断片で当該文字範囲を差し替える。
    /// </summary>
    public (string Fragment, int Count) ReplaceInRange(string text, int start, int length, string replacement)
    {
        int s = Math.Clamp(start, 0, text.Length);
        int end = Math.Clamp(start + length, s, text.Length);
        if (_regex is null) return (text.Substring(s, end - s), 0);

        var sb = new StringBuilder();
        int count = 0, pos = s;
        foreach (Match m in _regex.Matches(text))
        {
            if (m.Index < s) continue;
            if (m.Index + m.Length > end) break;
            sb.Append(text, pos, m.Index - pos);
            sb.Append(Expand(m, replacement));
            pos = m.Index + m.Length;
            count++;
        }
        sb.Append(text, pos, end - pos);
        return (sb.ToString(), count);
    }

    private string Expand(Match m, string replacement) => _expand ? m.Result(replacement) : replacement;
}
