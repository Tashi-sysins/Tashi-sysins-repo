using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BardPerformanceRecorder.Recording;

/// <summary>
/// 受信したパケットの生バイトをそのまま残すための記録。
///
/// なぜ生で残すのか：
/// こちらの解釈（オフセットや番兵の意味）が間違っていた場合、
/// 解釈後のデータしか残っていないと録り直すしかない。
/// 生バイトさえ残っていれば、あとから何度でも読み直せる。
///
/// フックの中ではここへ「写して積む」だけを行う。
/// </summary>
public sealed class RawPacketLog
{
    /// <summary>1件の生パケット。</summary>
    public readonly struct Entry
    {
        /// <summary>ソロ受信か合奏受信か。</summary>
        public readonly bool IsEnsemble;

        /// <summary>受信ハンドラの第1引数（sourceId）。</summary>
        public readonly uint SourceId;

        /// <summary>記録開始からの経過時間。</summary>
        public readonly TimeSpan ReceivedAt;

        /// <summary>生バイト列（そのままのコピー）。</summary>
        public readonly byte[] Bytes;

        /// <summary>
        /// 受信時点で観測できた周辺情報。
        /// あとから突き合わせるために一緒に残す。
        /// </summary>
        public readonly int InstrumentId;
        public readonly byte Mode;
        public readonly byte ModeParam;
        public readonly string PerformerName;

        public Entry(
            bool isEnsemble, uint sourceId, TimeSpan receivedAt, byte[] bytes,
            int instrumentId, byte mode, byte modeParam, string performerName)
        {
            IsEnsemble = isEnsemble;
            SourceId = sourceId;
            ReceivedAt = receivedAt;
            Bytes = bytes;
            InstrumentId = instrumentId;
            Mode = mode;
            ModeParam = modeParam;
            PerformerName = performerName;
        }
    }

    private readonly ConcurrentQueue<Entry> queue = new();
    private readonly List<Entry> entries = new();
    private readonly object gate = new();

    /// <summary>
    /// 溜めこむ上限。狭い場所で数分録る想定だが、
    /// 万一パケットが大量に来ても際限なく太らないようにする。
    /// </summary>
    public int Capacity { get; set; } = 200_000;

    public bool Enabled { get; set; } = true;

    /// <summary>上限に達して捨てた件数。</summary>
    public int OverflowCount { get; private set; }

    public int Count
    {
        get { lock (gate) return entries.Count; }
    }

    public void Clear()
    {
        while (queue.TryDequeue(out _)) { }
        lock (gate)
        {
            entries.Clear();
            OverflowCount = 0;
        }
    }

    /// <summary>フックから呼ぶ。キューに積むだけ。</summary>
    public void Enqueue(in Entry entry)
    {
        if (!Enabled)
            return;

        queue.Enqueue(entry);
    }

    /// <summary>キューを確定リストへ移す。フレーム処理から呼ぶ。</summary>
    public void Drain()
    {
        while (queue.TryDequeue(out var e))
        {
            lock (gate)
            {
                if (entries.Count >= Capacity)
                {
                    OverflowCount++;
                    continue;
                }

                entries.Add(e);
            }
        }
    }

    public List<Entry> Snapshot()
    {
        Drain();
        lock (gate)
            return new List<Entry>(entries);
    }

    /// <summary>
    /// 生ログを CSV で書き出す。
    /// バイト列は16進の文字列にして、全バイトをそのまま残す。
    /// </summary>
    public static void SaveCsv(IReadOnlyList<Entry> entries, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        w.WriteLine("seconds,kind,source_id,performer,mode,mode_param,instrument,byte_count,bytes_hex");

        foreach (var e in entries)
        {
            var hex = Convert.ToHexString(e.Bytes);
            var name = (e.PerformerName ?? string.Empty).Replace(",", " ");
            w.WriteLine(string.Join(",",
                e.ReceivedAt.TotalSeconds.ToString("F4", CultureInfo.InvariantCulture),
                e.IsEnsemble ? "ensemble" : "solo",
                e.SourceId.ToString("X8"),
                name,
                e.Mode.ToString(CultureInfo.InvariantCulture),
                e.ModeParam.ToString(CultureInfo.InvariantCulture),
                e.InstrumentId.ToString(CultureInfo.InvariantCulture),
                e.Bytes.Length.ToString(CultureInfo.InvariantCulture),
                hex));
        }
    }

    /// <summary>
    /// 人が読むための一覧。バイトの意味の当たりをつけるために、
    /// 解釈せずに「並べて見せる」ことを重視する。
    /// </summary>
    public static void SaveReadable(IReadOnlyList<Entry> entries, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var w = new StreamWriter(path, false, new UTF8Encoding(true));

        w.WriteLine("生パケット一覧");
        w.WriteLine("==============");
        w.WriteLine();
        w.WriteLine("※ ここに出るのは解釈前の生バイトです。");
        w.WriteLine("   こちらの読み方が間違っていても、この記録から読み直せます。");
        w.WriteLine();

        var solo = entries.Count(e => !e.IsEnsemble);
        var ens = entries.Count(e => e.IsEnsemble);
        w.WriteLine($"ソロ受信 : {solo} 件");
        w.WriteLine($"合奏受信 : {ens} 件");
        w.WriteLine();

        // どの sourceId から来たか
        w.WriteLine("■ 送り元ごとの件数");
        foreach (var g in entries.GroupBy(e => e.SourceId).OrderByDescending(g => g.Count()))
        {
            var name = g.Select(x => x.PerformerName)
                .FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "(名前不明)";
            var modes = g.Select(x => x.ModeParam).Distinct().OrderBy(x => x);
            w.WriteLine($"  0x{g.Key:X8}  {g.Count(),6} 件  {name}  "
                        + $"ModeParam={string.Join("/", modes)}");
        }

        w.WriteLine();
        w.WriteLine("■ 受信の中身（先頭 2000 件）");
        w.WriteLine();

        foreach (var e in entries.Take(2000))
        {
            var kind = e.IsEnsemble ? "ENS" : "SOLO";
            w.WriteLine($"[{e.ReceivedAt.TotalSeconds,9:F4}s] {kind} src=0x{e.SourceId:X8} "
                        + $"mode={e.Mode} param={e.ModeParam} inst={e.InstrumentId} "
                        + $"len={e.Bytes.Length}");

            // 16バイトずつ、16進と10進を並べる。
            // 音符の値は10進のほうが当たりをつけやすい。
            for (var i = 0; i < e.Bytes.Length; i += 16)
            {
                var len = Math.Min(16, e.Bytes.Length - i);
                var hex = new StringBuilder();
                var dec = new StringBuilder();

                for (var j = 0; j < len; j++)
                {
                    var b = e.Bytes[i + j];
                    hex.Append(b.ToString("X2")).Append(' ');
                    dec.Append(b == 0xFF ? "  ." : b == 0xFE ? "  -" : $"{b,3}").Append(' ');
                }

                w.WriteLine($"    +{i:X3}  {hex,-48}  |{dec}");
            }

            w.WriteLine();
        }

        if (entries.Count > 2000)
            w.WriteLine($"... 他 {entries.Count - 2000} 件（全件は CSV を参照）");
    }
}
