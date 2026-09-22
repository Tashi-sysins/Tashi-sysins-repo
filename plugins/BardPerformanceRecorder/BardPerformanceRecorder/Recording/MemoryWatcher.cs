using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BardPerformanceRecorder.Recording;

/// <summary>
/// メモリ領域を監視して「どこが変化したか」を見つける。
///
/// 何のために使うか：
/// 「押鍵は +0x60 にある」という想定が現行クライアントで
/// 崩れている場合、決め打ちで読んでも永遠に取れない。
/// 実際に鍵を押した前後でメモリを比べ、変化した位置を洗い出せば、
/// 正しいオフセットを実測で突き止められる。
/// </summary>
public sealed class MemoryWatcher
{
    /// <summary>押鍵なしを表す番兵。</summary>
    private const int NoNotePressed = LocalPerformanceReaderConstants.NoNotePressed;

    /// <summary>基準となるスナップショット（何も押していないときの状態）。</summary>
    private byte[] baseline;

    /// <summary>各オフセットが変化した回数。</summary>
    private readonly Dictionary<int, int> changeCount = new();

    /// <summary>各オフセットで観測した値の集合。</summary>
    private readonly Dictionary<int, HashSet<int>> observedValues = new();

    /// <summary>比較した回数。</summary>
    public int SampleCount { get; private set; }

    public bool HasBaseline => baseline != null;

    public void Reset()
    {
        baseline = null;
        changeCount.Clear();
        observedValues.Clear();
        SampleCount = 0;
    }

    /// <summary>基準を取る。鍵を押していない状態で呼ぶ。</summary>
    public void SetBaseline(byte[] snapshot)
    {
        if (snapshot == null)
            return;

        baseline = (byte[])snapshot.Clone();
        changeCount.Clear();
        observedValues.Clear();
        SampleCount = 0;
    }

    /// <summary>
    /// 現在の状態を基準と比べる。鍵を押している間に呼ぶ。
    /// </summary>
    public void Compare(byte[] snapshot)
    {
        if (snapshot == null || baseline == null)
            return;

        SampleCount++;

        var len = Math.Min(snapshot.Length, baseline.Length);

        // 4バイト単位（int）でも見る。押鍵は int で入っている想定のため。
        for (var i = 0; i + 3 < len; i += 4)
        {
            var now = BitConverter.ToInt32(snapshot, i);
            var was = BitConverter.ToInt32(baseline, i);

            if (now == was)
                continue;

            changeCount.TryGetValue(i, out var c);
            changeCount[i] = c + 1;

            if (!observedValues.TryGetValue(i, out var set))
            {
                set = new HashSet<int>();
                observedValues[i] = set;
            }

            // 値の種類が増えすぎると意味がないので上限を設ける
            if (set.Count < 64)
                set.Add(now);
        }
    }

    /// <summary>
    /// 「押鍵らしい」候補を探す。
    ///
    /// 押鍵の値は次の性質を持つはず：
    /// - 押すと変化する
    /// - 値が 0〜36（37鍵）の範囲か、離鍵の番兵
    /// - 値の種類がそこそこある（押した鍵の数だけ）
    /// </summary>
    public List<string> BuildReport(int currentAssumedOffset)
    {
        var lines = new List<string>();

        lines.Add("押鍵オフセットの探索");
        lines.Add("====================");
        lines.Add("");

        if (baseline == null)
        {
            lines.Add("基準がまだ取れていません。");
            lines.Add("鍵を押していない状態で「基準を取る」を押してください。");
            return lines;
        }

        lines.Add($"比較回数 : {SampleCount}");
        lines.Add($"現在の想定オフセット : +0x{currentAssumedOffset:X2}");
        lines.Add("");

        if (changeCount.Count == 0)
        {
            lines.Add("変化したバイトがありません。");
            lines.Add("鍵を押しながら「比較する」を押してください。");
            return lines;
        }

        // 押鍵らしさで並べる
        var candidates = new List<(int offset, int changes, HashSet<int> values, int score)>();

        foreach (var kv in changeCount)
        {
            var vals = observedValues.TryGetValue(kv.Key, out var v) ? v : new HashSet<int>();

            // 押鍵の値が 0 起点とは限らない（実測では 39〜43 だった）。
            // 「37鍵ぶんの狭い幅に収まっているか」で見る。
            var notes = vals.Where(x => x != NoNotePressed && x is > -1000 and < 1000).ToList();
            var sentinel = vals.Count(x => x == NoNotePressed);

            var score = 0;

            if (notes.Count > 0)
            {
                var span = notes.Max() - notes.Min();

                // 演奏できるのは37鍵。押した鍵の値はこの幅に収まるはず。
                if (span <= 36)
                    score += notes.Count * 10;

                // 値が小さくまとまっているほど押鍵らしい
                // （ポインタやタイマーは巨大な値になる）
                if (notes.All(x => x is >= 0 and < 128))
                    score += 20;
            }

            // 離鍵の番兵が見えていれば、かなり確度が高い
            score += sentinel * 15;

            candidates.Add((kv.Key, kv.Value, vals, score));
        }

        lines.Add("■ 押鍵らしい候補（上位10件）");
        lines.Add("   オフセット  変化回数  観測した値");
        lines.Add("");

        foreach (var c in candidates
                     .OrderByDescending(x => x.score)
                     .ThenByDescending(x => x.changes)
                     .Take(10))
        {
            var sample = string.Join(", ",
                c.values.OrderBy(x => x).Take(12));

            if (c.values.Count > 12)
                sample += ", ...";

            var mark = c.offset == currentAssumedOffset ? " ★現在の想定" : "";
            var likely = c.score > 0 ? "" : "  （押鍵ではなさそう）";

            lines.Add($"   +0x{c.offset:X3}  {c.changes,7}  [{sample}]{mark}{likely}");
        }

        lines.Add("");

        var best = candidates
            .Where(c => c.score > 0)
            .OrderByDescending(c => c.score)
            .ThenByDescending(c => c.changes)
            .FirstOrDefault();

        if (best.score > 0)
        {
            if (best.offset == currentAssumedOffset)
            {
                lines.Add($"→ 現在の想定 +0x{currentAssumedOffset:X2} が最有力です。");
                lines.Add("   オフセットは合っています。");
            }
            else
            {
                lines.Add($"→ 最有力は +0x{best.offset:X2} です"
                          + $"（現在の想定は +0x{currentAssumedOffset:X2}）。");
                lines.Add("   LocalPerformanceReader.cs の");
                lines.Add($"   OffsetCurrentPressingNote を 0x{best.offset:X2} に変えてください。");
            }
        }
        else
        {
            lines.Add("→ 押鍵らしい候補が見つかりませんでした。");
            lines.Add("   鍵を押した状態で比較できていない可能性があります。");
        }

        lines.Add("");
        lines.Add("■ 変化したすべてのオフセット（参考）");
        lines.Add("   " + string.Join(", ",
            changeCount.Keys.OrderBy(x => x).Select(x => $"+0x{x:X3}")));

        return lines;
    }

    /// <summary>
    /// 現在のスナップショットを16進で並べる。目視確認用。
    /// </summary>
    public static List<string> HexDump(byte[] data, int length = 0x100)
    {
        var lines = new List<string>();
        if (data == null)
        {
            lines.Add("（メモリを取得できません）");
            return lines;
        }

        var len = Math.Min(length, data.Length);

        for (var i = 0; i < len; i += 16)
        {
            var n = Math.Min(16, len - i);
            var hex = new StringBuilder();

            for (var j = 0; j < n; j++)
                hex.Append(data[i + j].ToString("X2")).Append(' ');

            // int として読んだ値も並べる（押鍵は int のため）
            var ints = new StringBuilder();
            for (var j = 0; j + 3 < n; j += 4)
                ints.Append($"{BitConverter.ToInt32(data, i + j),12}");

            lines.Add($"+0x{i:X3}  {hex,-48} |{ints}");
        }

        return lines;
    }
}
