using System.Diagnostics;
using System.Text;
using yEdit.Core.Buffers;

// P1 TextBuffer 性能ゲート(設計書DoD): --mb <サイズ> 既定1024
// 目標未達があれば EXIT 1

int mb = 1024;
for (int i = 0; i + 1 < args.Length; i++)
    if (args[i] == "--mb" && int.TryParse(args[i + 1], out int m)) mb = m;

long targetBytes = (long)mb * 1024 * 1024;
var rnd = new Random(20260705);
var results = new List<(string Name, string Value, string Target, bool? Pass)>();
long sink = 0;   // 最適化防止

Console.WriteLine($"TextBuffer ベンチ開始 (--mb {mb})");

// ---- 1) 合成文書構築(日本語+ASCII+改行混合) ----
const string TemplateLine =
    "The quick brown fox jumps over 0123456789.\r\n日本語の行テキスト、あいうえお漢字カナ混在の内容です。\nもう一行😀絵文字と🈴記号付き\r\n";
byte[] template = Encoding.UTF8.GetBytes(TemplateLine);
byte[] block;
{
    var b = new byte[1 << 20];
    int w = 0;
    while (w + template.Length <= b.Length) { template.CopyTo(b, w); w += template.Length; }
    block = b[..w];   // テンプレ整数個(コード点/改行を割らない)
}

var swBuild = Stopwatch.StartNew();
var builder = new TextBufferBuilder();
long written = 0;
while (written < targetBytes)
{
    int len = (int)Math.Min(block.Length, targetBytes - written);
    builder.Add(block.AsSpan(0, len));
    written += len;
}
var buffer = builder.Build();
swBuild.Stop();

var snap = buffer.Current;
int charLen = snap.CharLength;
int lineCount = snap.LineCount;
int initialPieces = snap.PieceCount;
results.Add(("1 構築", $"{swBuild.Elapsed.TotalSeconds:F1}s / {charLen:N0}文字 / {lineCount:N0}行 / {initialPieces}ピース", "記録のみ", null));

// ---- 8) メモリ(構築直後) ----
long managed = GC.GetTotalMemory(forceFullCollection: true);
long workingSet = Environment.WorkingSet;
results.Add(("8 メモリ", $"managed {managed / 1048576.0:F0}MB / WorkingSet {workingSet / 1048576.0:F0}MB(文書 {mb}MB)", "記録のみ(文書+O(ピース))", null));

// ---- 3) Current 取得 1,000,000回(スナップショットO(1)実証) ----
for (int i = 0; i < 10_000; i++) sink += buffer.Current.CharLength;   // ウォームアップ
const int CurrentIters = 1_000_000;
var sw = Stopwatch.StartNew();
for (int i = 0; i < CurrentIters; i++) sink += buffer.Current.CharLength;
sw.Stop();
double currentNs = TicksToNs(sw.ElapsedTicks) / CurrentIters;
AddResult("3 Current取得", $"{currentNs:F1} ns/回", "O(1)(<1µs)", currentNs < 1000);

// ---- 4) ランダム行 → GetLineStart 100,000回 ----
const int QueryIters = 100_000;
for (int i = 0; i < 1000; i++) sink += snap.GetLineStart(rnd.Next(lineCount));
sw.Restart();
for (int i = 0; i < QueryIters; i++) sink += snap.GetLineStart(rnd.Next(lineCount));
sw.Stop();
double lineStartUs = TicksToUs(sw.ElapsedTicks) / QueryIters;
AddResult("4 GetLineStart", $"{lineStartUs:F1} µs/回", "平均<100µs", lineStartUs < 100);

// ---- 5) ランダムpos → GetLineIndexOfChar 100,000回 ----
for (int i = 0; i < 1000; i++) sink += snap.GetLineIndexOfChar(rnd.Next(charLen + 1));
sw.Restart();
for (int i = 0; i < QueryIters; i++) sink += snap.GetLineIndexOfChar(rnd.Next(charLen + 1));
sw.Stop();
double lineIdxUs = TicksToUs(sw.ElapsedTicks) / QueryIters;
AddResult("5 GetLineIndexOfChar", $"{lineIdxUs:F1} µs/回", "平均<100µs", lineIdxUs < 100);

// ---- 6) ランダム窓 GetText(pos, 200) 100,000回 ----
for (int i = 0; i < 1000; i++) sink += snap.GetText(rnd.Next(charLen - 200), 200).Length;
sw.Restart();
for (int i = 0; i < QueryIters; i++) sink += snap.GetText(rnd.Next(charLen - 200), 200).Length;
sw.Stop();
double getTextUs = TicksToUs(sw.ElapsedTicks) / QueryIters;
AddResult("6 GetText(200)", $"{getTextUs:F1} µs/回", "平均<100µs", getTextUs < 100);

// ---- 2) ランダム位置 splice 10,000回(タイプ相当1〜3文字) ----
string[] typing = ["a", "あ", "xy", "漢字a", "e"];
for (int i = 0; i < 200; i++)   // ウォームアップ
    buffer.Insert(rnd.Next(buffer.Current.CharLength + 1), typing[rnd.Next(typing.Length)]);
const int SpliceIters = 10_000;
var spliceTicks = new long[SpliceIters];
for (int i = 0; i < SpliceIters; i++)
{
    int pos = rnd.Next(buffer.Current.CharLength + 1);
    string s = typing[rnd.Next(typing.Length)];
    long t0 = Stopwatch.GetTimestamp();
    buffer.Insert(pos, s);
    spliceTicks[i] = Stopwatch.GetTimestamp() - t0;
}
Array.Sort(spliceTicks);
double spliceAvgMs = TicksToMs(spliceTicks.Sum()) / SpliceIters;
double spliceP99Ms = TicksToMs(spliceTicks[(int)(SpliceIters * 0.99)]);
AddResult("2 splice 10,000回", $"平均 {spliceAvgMs * 1000:F1} µs / p99 {spliceP99Ms * 1000:F1} µs",
    "平均<1ms かつ p99<1ms", spliceAvgMs < 1.0 && spliceP99Ms < 1.0);

// ---- 7) 連続タイピング10,000字後の PieceCount ----
buffer.BreakUndoCoalescing();
int piecesBefore = buffer.Current.PieceCount;
int caret = buffer.Current.CharLength / 2;
if (caret > 0 && char.IsLowSurrogate(buffer.Current.GetChar(caret))) caret--;
for (int i = 0; i < 10_000; i++) { buffer.Insert(caret, "a"); caret++; }
int piecesAfter = buffer.Current.PieceCount;
int pieceDelta = piecesAfter - piecesBefore;
AddResult("7 連続タイピング断片化", $"before {piecesBefore} → after {piecesAfter}(Δ{pieceDelta})",
    "Δ≤50(断片化しない)", pieceDelta <= 50);

// ---- 結果表 ----
Console.WriteLine();
Console.WriteLine("| # シナリオ | 結果 | 目標 | 判定 |");
Console.WriteLine("|---|---|---|---|");
foreach (var r in results.OrderBy(r => r.Name))
    Console.WriteLine($"| {r.Name} | {r.Value} | {r.Target} | {(r.Pass is null ? "―" : r.Pass.Value ? "PASS" : "FAIL")} |");
Console.WriteLine($"(sink={sink})");

bool allPass = results.All(r => r.Pass is not false);
Console.WriteLine(allPass ? "全シナリオ目標達成 (EXIT 0)" : "目標未達あり (EXIT 1)");
return allPass ? 0 : 1;

void AddResult(string name, string value, string target, bool pass)
    => results.Add((name, value, target, pass));

static double TicksToNs(long ticks) => ticks * 1_000_000_000.0 / Stopwatch.Frequency;
static double TicksToUs(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;
static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
