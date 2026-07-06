using System.Text;
using yEdit.Core.Buffers;

namespace yEdit.Core.Text;

/// <summary>
/// P6 Task 10: Stream I/O 経路の読込結果(<see cref="TextFileService.LoadAsBufferAuto"/> の返却型)。
/// 既存 <see cref="LoadedDocument"/> と対を成し、本文表現が <c>string</c> ではなく
/// <see cref="TextBuffer"/>(=チャンク木)である点が違い。1GB 級 UTF-8 の string 全文化を回避する。
/// </summary>
public sealed class LoadedBuffer
{
    /// <summary>読み込んだ本文(チャンク木構築済み)。</summary>
    public required TextBuffer Buffer { get; init; }

    /// <summary>確定した文字コード。</summary>
    public required Encoding Encoding { get; init; }

    /// <summary>UTF-8 BOM を持つファイルなら true(UTF-8 以外では常に false)。</summary>
    public required bool HasBom { get; init; }

    /// <summary>検出した改行種別(先頭サンプルから多数決)。</summary>
    public required LineEnding LineEnding { get; init; }

    /// <summary>デコード時に U+FFFD(置換文字)が出たか(文字コード取り違えの示唆)。</summary>
    public required bool HadReplacementChar { get; init; }
}
