using System.Text;
using yEdit.Core.Text;

namespace yEdit.App;

/// <summary>
/// 現在開いているドキュメントの状態。v0.1 は単一だが、将来 DocumentManager で
/// タブ毎に持てるよう独立クラスにしておく（design M2 への布石）。
/// </summary>
public sealed class DocumentState
{
    public string? Path { get; set; }              // 未保存なら null
    public Encoding Encoding { get; set; } = EncodingCatalog.Get(65001);
    public bool HasBom { get; set; }
    public LineEnding LineEnding { get; set; } = LineEnding.Crlf;

    public string DisplayName => Path is null ? "無題" : System.IO.Path.GetFileName(Path);
}
