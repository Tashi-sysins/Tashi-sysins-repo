using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BardPerformanceRecorder.Recording;

/// <summary>
/// 記録したデータから、読み方が正しいかを自動で診断する。
///
/// 狙い：
/// 実機で録ったあと、人間が16進を睨んで当たりをつける作業を減らす。
/// 「パケットのどのバイトが音符か」「音高のずらし量はいくつか」を
/// データ自身に答えさせる。
/// </summary>
public static class CalibrationAnalyzer
{
    public sealed class Report
    {
        public readonly List<string> Lines = new();
        public int? SuggestedMidiNoteOffset;
        public bool SoloPacketsSeen;
        public bool EnsemblePacketsSeen;
        public bool LocalKeysSeen;

        public void Add(string line) => Lines.Add(line);
        public override string ToString() => string.Join(Environment.NewLine, Lines);
    }

    /// <summary>
    /// 生パケットと自分の押鍵ログから診断を作る。
    /// </summary>
    public static Report Analyze(
        IReadOnlyList<RawPacketLog.Entry> packets,
        IReadOnlyList<LocalKeyLog.Entry> localKeys,
        int currentMidiNoteOffset)
    {
        var r = new Report();

        r.Add("記録の自動診断");
        r.Add("================");
        r.Add("");

        // ---- 1. 何が届いたか ----
        var solo = packets.Where(p => !p.IsEnsemble).ToList();
        var ens = packets.Where(p => p.IsEnsemble).ToList();
        r.SoloPacketsSeen = solo.Count > 0;
        r.EnsemblePacketsSeen = ens.Count > 0;
        r.LocalKeysSeen = localKeys.Count > 0;

        r.Add("■ 受信したパケット");
        r.Add($"  ソロ受信 : {solo.Count} 件");
        r.Add($"  合奏受信 : {ens.Count} 件");
        r.Add("");

        if (packets.Count == 0)
        {
            r.Add("  パケットが1件も届いていません。");
            r.Add("  考えられること：");
            r.Add("   - 演奏者が遠い／視界外");
            r.Add("   - この受信ハンドラは他人の演奏では呼ばれない");
            r.Add("   - シグネチャが現行クライアントと合っていない");
            r.Add("");
            return r;
        }

        // ---- 2. 送り元ごと ----
        r.Add("■ 送り元");
        foreach (var g in packets.GroupBy(p => p.SourceId).OrderByDescending(g => g.Count()))
        {
            var name = g.Select(x => x.PerformerName)
                .FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "(名前不明)";
            r.Add($"  0x{g.Key:X8}  {g.Count(),6} 件  {name}");
        }

        r.Add("");

        // ---- 3. バイトごとの振る舞い ----
        // どのオフセットが「音符らしく」動くかを、データから見る。
        // 音符バイトなら：値が頻繁に変わり、0xFF が多く、値域が狭い。
        AnalyzeByteBehaviour(r, solo, "ソロ");
        AnalyzeByteBehaviour(r, ens, "合奏");

        // ---- 4. 自分の押鍵との突き合わせ ----
        if (localKeys.Count == 0)
        {
            r.Add("■ 音高のずらし量（MidiNoteOffset）");
            r.Add("  自分の押鍵ログがないため、判定できません。");
            r.Add("  自分で演奏しながら記録すると、ここで自動判定できます。");
            r.Add("");
        }
        else
        {
            AnalyzeNoteOffset(r, solo, ens, localKeys, currentMidiNoteOffset);
        }

        return r;
    }

    /// <summary>
    /// 各バイト位置が「音符らしいか」を、データの動きから見る。
    /// こちらの想定オフセットが合っているかの裏取りになる。
    /// </summary>
    private static void AnalyzeByteBehaviour(
        Report r, List<RawPacketLog.Entry> packets, string label)
    {
        if (packets.Count == 0)
            return;

        var len = packets.Max(p => p.Bytes.Length);
        if (len == 0)
            return;

        r.Add($"■ バイトごとの動き（{label}・全 {packets.Count} 件）");
        r.Add("   値が動くバイトほど「音符・音色」である可能性が高い。");
        r.Add("");
        r.Add("   位置  種類数  0xFF率  0xFE率  最小 最大  判定");

        var interesting = 0;
        var fixedBytes = 0;

        for (var i = 0; i < len; i++)
        {
            var vals = packets
                .Where(p => i < p.Bytes.Length)
                .Select(p => p.Bytes[i])
                .ToList();

            if (vals.Count == 0)
                continue;

            var distinct = vals.Distinct().Count();
            var ffRate = vals.Count(v => v == 0xFF) / (double)vals.Count;
            var feRate = vals.Count(v => v == 0xFE) / (double)vals.Count;

            // 実際に鳴っている音だけの値域
            var playable = vals.Where(v => v != 0xFF && v != 0xFE).ToList();
            var min = playable.Count > 0 ? playable.Min() : 0;
            var max = playable.Count > 0 ? playable.Max() : 0;

            // 判定：値が動き、0xFF が混ざり、値域が 37鍵に収まるなら音符らしい
            string verdict;
            if (distinct <= 1)
                verdict = "固定値";
            else if (ffRate > 0.05 && playable.Count > 0
                     && min >= Game.PerformanceConstants.LowestNoteValue
                     && max <= Game.PerformanceConstants.HighestNoteValue)
                verdict = "★音符らしい";
            else if (playable.Count > 0 && max <= 5 && distinct <= 8)
                verdict = "音色らしい";
            else
                verdict = "";

            if (distinct > 1)
            {
                interesting++;

                // 動きのあるバイトだけ出す。
                // 固定値の行まで出すと数十行になって、肝心の音符が埋もれる。
                r.Add($"   +{i,3:X}  {distinct,5}  {ffRate,6:P0}  {feRate,6:P0}  "
                      + $"{min,3} {max,4}  {verdict}");
            }
            else
            {
                fixedBytes++;
            }
        }

        if (interesting == 0)
            r.Add("   （動きのあるバイトがありません。演奏が録れていない可能性）");
        else if (fixedBytes > 0)
            r.Add($"   （ほか {fixedBytes} バイトは値が変化しないため省略）");

        r.Add("");
    }

    /// <summary>
    /// 「音符はこの位置に入る」という前提が正しいかを、総当たりで確かめる。
    ///
    /// 押鍵した値が実際にどのバイト位置に現れるかを数える。
    /// 想定どおりの位置に出るなら前提は正しい。
    /// 別の位置に集中していたら、読み方が違う。
    /// </summary>
    private static void VerifyNoteRegionAssumption(
        Report r,
        List<RawPacketLog.Entry> packets,
        List<LocalKeyLog.Entry> presses)
    {
        // ソロと合奏では音符の位置が違うので、混ぜて数えない。
        // （オフセット範囲が一部重なるため、混ぜると誤った注記が出る）
        AnalyzeHitPositions(r, packets.Where(p => !p.IsEnsemble).ToList(), presses,
            "ソロ",
            Game.PerformanceConstants.SoloOffsetNotes,
            Game.PerformanceConstants.SoloNoteCount);

        AnalyzeHitPositions(r, packets.Where(p => p.IsEnsemble).ToList(), presses,
            "合奏",
            Game.PerformanceConstants.EnsembleMemberOffsetNotes,
            Game.PerformanceConstants.EnsembleNoteCount);
    }

    /// <summary>
    /// 片方の種別について、押鍵値が現れたバイト位置を数える。
    /// </summary>
    private static void AnalyzeHitPositions(
        Report r,
        List<RawPacketLog.Entry> packets,
        List<LocalKeyLog.Entry> presses,
        string label,
        int noteStart,
        int noteCount)
    {
        if (packets.Count == 0)
            return;

        var hits = new Dictionary<int, int>();

        foreach (var press in presses)
        {
            var window = packets
                .Where(p => p.ReceivedAt >= press.At
                            && p.ReceivedAt <= press.At + TimeSpan.FromSeconds(1.0))
                .Take(8);

            foreach (var p in window)
            {
                for (var i = 0; i < p.Bytes.Length; i++)
                {
                    if (p.Bytes[i] != press.PressedNote)
                        continue;

                    hits.TryGetValue(i, out var c);
                    hits[i] = c + 1;
                }
            }
        }

        if (hits.Count == 0)
            return;

        var noteEnd = noteStart + noteCount - 1;

        r.Add($"  参考：押鍵した値が現れたバイト位置（{label}・上位6件）");
        r.Add($"        想定している音符域は +0x{noteStart:X3}〜+0x{noteEnd:X3}");

        foreach (var kv in hits.OrderByDescending(kv => kv.Value).Take(6))
        {
            var inNote = kv.Key >= noteStart && kv.Key <= noteEnd;
            r.Add($"     +0x{kv.Key:X3} : {kv.Value,6} 回  "
                  + (inNote ? "（想定の音符域内）" : "（想定外の位置）"));
        }

        var top = hits.OrderByDescending(kv => kv.Value).First().Key;
        if (top < noteStart || top > noteEnd)
        {
            r.Add("");
            r.Add($"  ※ {label}：最頻の位置が想定の音符域の外です。");
            r.Add("     パケットの読み方が現行クライアントと合っていない可能性があります。");
            r.Add("     生ログ（raw_*.txt）で実際のバイト並びを確認してください。");
        }

        r.Add("");
    }

    /// <summary>
    /// パケットから「音符が入っている範囲」のバイトだけを取り出す。
    ///
    /// 全バイトを見ると、音符数・音色・0埋めまで音符として数えてしまい、
    /// 判定が壊れる。ソロと合奏で範囲が違うので分けて扱う。
    /// </summary>
    private static IEnumerable<byte> EnumerateNoteBytes(RawPacketLog.Entry p)
    {
        var start = p.IsEnsemble
            ? Game.PerformanceConstants.EnsembleMemberOffsetNotes
            : Game.PerformanceConstants.SoloOffsetNotes;

        var count = p.IsEnsemble
            ? Game.PerformanceConstants.EnsembleNoteCount
            : Game.PerformanceConstants.SoloNoteCount;

        for (var i = 0; i < count; i++)
        {
            var idx = start + i;
            if (idx >= p.Bytes.Length)
                break;

            var b = p.Bytes[idx];

            // 番兵は鳴っていない
            if (b is 0xFF or 0xFE)
                continue;

            // 演奏できる範囲（実測 39〜75）の外は音符ではない
            if (b < Game.PerformanceConstants.LowestNoteValue
                || b > Game.PerformanceConstants.HighestNoteValue)
                continue;

            yield return b;
        }
    }

    /// <summary>
    /// 自分の押鍵と、受信したパケットの音符バイトを突き合わせ、
    /// MIDI へのずらし量を実測で求める。
    /// </summary>
    private static void AnalyzeNoteOffset(
        Report r,
        List<RawPacketLog.Entry> solo,
        List<RawPacketLog.Entry> ens,
        IReadOnlyList<LocalKeyLog.Entry> localKeys,
        int currentMidiNoteOffset)
    {
        r.Add("■ 音高のずらし量（MidiNoteOffset）の実測");
        r.Add("");

        var presses = localKeys.Where(k => k.IsPress).ToList();
        r.Add($"  自分の押鍵 : {presses.Count} 回");

        if (presses.Count == 0)
        {
            r.Add("  押鍵が記録されていないため判定できません。");
            r.Add("");
            return;
        }

        // 押鍵の値域（0〜36 のはず）
        r.Add($"  押鍵の値域 : {presses.Min(p => p.PressedNote)} 〜 {presses.Max(p => p.PressedNote)}");

        var octaves = presses.Select(p => p.OctaveOffset).Distinct().OrderBy(x => x).ToList();
        r.Add($"  オクターブずらし : {string.Join(", ", octaves)}");
        r.Add("");

        // 押鍵の直後に届いたパケットの音符バイトを集め、差を取る。
        var all = solo.Concat(ens).OrderBy(p => p.ReceivedAt).ToList();
        if (all.Count == 0)
        {
            r.Add("  突き合わせるパケットがありません。");
            r.Add("");
            return;
        }

        var diffs = new Dictionary<int, int>();
        var matched = 0;

        foreach (var press in presses)
        {
            // 押鍵からおおよそ 1 秒以内に届いたパケットを見る。
            var window = all
                .Where(p => p.ReceivedAt >= press.At
                            && p.ReceivedAt <= press.At + TimeSpan.FromSeconds(1.0))
                .Take(8)
                .ToList();

            if (window.Count == 0)
                continue;

            // そのパケットに現れた「鳴っている音」を候補にする。
            //
            // 注意：パケット全体を走査してはいけない。
            // 音符数のバイトや、未使用の 0 埋め、音色の 0 まで
            // 「音符」として数えてしまい、本物の差が埋もれる。
            // 音符が入る範囲だけを見る。
            foreach (var p in window)
            {
                foreach (var b in EnumerateNoteBytes(p))
                {
                    var d = b - press.PressedNote;
                    diffs.TryGetValue(d, out var c);
                    diffs[d] = c + 1;
                }
            }

            matched++;
        }

        r.Add($"  突き合わせできた押鍵 : {matched} 回");
        r.Add("");

        if (diffs.Count == 0)
        {
            r.Add("  押鍵に対応する音符バイトが見つかりませんでした。");
            r.Add("  → 受信しているのが自分以外の演奏か、読み方が違う可能性。");
            r.Add("");
            return;
        }

        r.Add("  「パケットの音符値 − 自分の押鍵値」の分布：");
        foreach (var kv in diffs.OrderByDescending(kv => kv.Value).Take(6))
            r.Add($"     差 {kv.Key,4} : {kv.Value,6} 回");

        r.Add("");

        var best = diffs.OrderByDescending(kv => kv.Value).First();
        var total = diffs.Values.Sum();
        var share = best.Value / (double)total;

        // 念のため、想定した音符位置が本当に正しいかも確かめる。
        // 上の判定は「音符はここに入る」という前提に乗っているため、
        // その前提が崩れていたときに気づけるようにしておく。
        VerifyNoteRegionAssumption(r, all, presses);

        if (best.Key == 0 && share > 0.5)
        {
            r.Add($"  → パケットの音符値と自分の押鍵値が一致しています（{share:P0}）。");
            r.Add($"     つまり現在の MidiNoteOffset = {currentMidiNoteOffset} は、");
            r.Add("     「押鍵0 を MIDI のどの音にするか」を決めているだけです。");
            r.Add("");
            r.Add("     実際の音高を確かめるには、弾いた音の実音名が必要です。");
            r.Add("     ゲーム内でいちばん低い鍵（C3）を弾いた記録があれば、");
            r.Add($"     MidiNoteOffset = 48 で C3 になります（現在の設定と同じ）。");
            r.SuggestedMidiNoteOffset = currentMidiNoteOffset;
        }
        else if (share > 0.5)
        {
            r.Add($"  → 最頻の差は {best.Key}（{share:P0}）。");
            r.Add("     パケットの値と押鍵の値がずれています。");
            r.Add($"     MidiNoteOffset を {currentMidiNoteOffset - best.Key} にすると辻褄が合います。");
            r.SuggestedMidiNoteOffset = currentMidiNoteOffset - best.Key;
        }
        else
        {
            r.Add("  → 差が散らばっていて、はっきりしません。");
            r.Add("     和音や、他人の演奏が混ざっている可能性があります。");
            r.Add("     単音をゆっくり弾いて録り直すと判定しやすくなります。");
        }

        r.Add("");
    }
}
