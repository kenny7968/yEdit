using yEdit.Core.Layout;

namespace yEdit.Editor;

/// <summary>
/// TextRenderer(GDI)ベースの <see cref="ICharMetrics"/> 実装(UI スレッド専用)。
/// ASCII(0..127)の 1 文字幅を構築時に前計算してキャッシュし、ホットパス(<see cref="MeasureRun"/> は
/// 1000 文字行なら 1000 回呼ばれる)ではキャッシュ加算で完結させる。カーニングは無視する。
/// TAB は半角スペース幅として扱う(タブ揃えの本実装は入力側 P3 に配置)。
/// </summary>
public sealed class GdiCharMetrics : ICharMetrics
{
    private static readonly Size MaxSize = new(int.MaxValue, int.MaxValue);
    private const TextFormatFlags MeasureFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    private readonly Font _font;
    private readonly int[] _asciiWidths;

    public GdiCharMetrics(Font font)
    {
        ArgumentNullException.ThrowIfNull(font);
        _font = font;
        LineHeightPx = TextRenderer.MeasureText("Mg", font, MaxSize, MeasureFlags).Height;
        _asciiWidths = new int[128];
        for (int c = 0; c < 128; c++)
        {
            _asciiWidths[c] = TextRenderer
                .MeasureText(((char)c).ToString(), font, MaxSize, MeasureFlags)
                .Width;
        }
        _asciiWidths['\t'] = _asciiWidths[' '];
    }

    public int LineHeightPx { get; }

    public int MeasureRun(ReadOnlySpan<char> text)
    {
        int px = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c >= 128)
                return TextRenderer
                    .MeasureText(text.ToString(), _font, MaxSize, MeasureFlags)
                    .Width;
            px += _asciiWidths[c];
        }
        return px;
    }
}
