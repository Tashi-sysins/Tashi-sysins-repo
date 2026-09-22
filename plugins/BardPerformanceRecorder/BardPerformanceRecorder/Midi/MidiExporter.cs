using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BardPerformanceRecorder.Recording;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace BardPerformanceRecorder.Midi;

/// <summary>
/// MIDI 書き出しの設定。
/// 推定に頼る部分は既定値を明示して、画面から変えられるようにする。
/// </summary>
public sealed class MidiExportOptions
{
    /// <summary>
    /// 書き出しに使う BPM。
    ///
    /// 重要：これは元の曲のテンポではない。パケットにテンポ情報は無い。
    /// 「実時間を MIDI の時間軸へ落とすための基準値」でしかない。
    /// </summary>
    public double BeatsPerMinute { get; set; } = 120.0;

    /// <summary>1拍あたりの分解能。</summary>
    public short TicksPerQuarterNote { get; set; } = 480;

    /// <summary>
    /// 消音を観測できなかった音に与える暫定の長さ（ミリ秒）。
    /// 推定値なので、出力にもその旨を残す。
    /// </summary>
    public int FallbackNoteLengthMs { get; set; } = 250;

    /// <summary>
    /// 音の最短長（ミリ秒）。0 長の音は編集ソフトで消えることがあるため下限を設ける。
    /// </summary>
    public int MinimumNoteLengthMs { get; set; } = 30;

    /// <summary>MIDI ベロシティ。演奏パケットに強弱は無いため固定値。</summary>
    public byte Velocity { get; set; } = 100;

    /// <summary>
    /// 演奏者ごとにトラックを分けるか。
    ///
    /// MIDI では、同じチャンネルで同じ音高が重なると区別できない。
    /// 2人が同じ音を重ねて弾くと、読み戻したときに
    /// 音長が入れ替わってしまう（実測で確認）。
    /// 演奏者を別トラック・別チャンネルに分けると防げる。
    /// </summary>
    public bool SeparateTrackPerPerformer { get; set; } = true;

    /// <summary>
    /// 記録に使ったプラグインの版（ビルド時刻）。
    ///
    /// 版を取り違えたまま検証して混乱するのを防ぐために残す。
    /// この層は Dalamud に依存させないので、外から渡してもらう。
    /// </summary>
    public string BuildStamp { get; set; }

    /// <summary>
    /// 演奏者 EntityId → キャラクター名。
    ///
    /// 名前はゲーム側から取るもので、この層は Dalamud に依存させないため
    /// 外から渡してもらう。入っていない演奏者は ID だけで書く。
    /// </summary>
    public IReadOnlyDictionary<uint, string> PerformerNames { get; set; }

    /// <summary>
    /// MIDI に含める演奏者を限定する。null または空なら全員。
    ///
    /// 近くで無関係な人がソロ演奏していると、その音も届いて混ざる
    /// （実測で確認：合奏していない3人の演奏が同じ記録に入った）。
    /// ここで絞ると、目的の合奏だけを取り出せる。
    ///
    /// 記録そのものは全員分を残したうえで、書き出しのときに選ぶ。
    /// こうすると、選び間違えても録り直さずに何度でもやり直せる。
    /// </summary>
    public IReadOnlySet<uint> IncludePerformers { get; set; }

    /// <summary>
    /// MIDI から除外する演奏者。null または空なら誰も除外しない。
    ///
    /// <see cref="IncludePerformers"/> とは逆向きの指定。
    /// 「この人以外を全部」より「この人だけ要らない」のほうが
    /// 早いときのために両方用意する。両方指定した場合は
    /// 含める指定を先に適用し、そのあと除外する。
    /// </summary>
    public IReadOnlySet<uint> ExcludePerformers { get; set; }

    /// <summary>
    /// トラック名に楽器名を入れるか。
    ///
    /// MidiBard2 はトラック名に含まれる楽器名を見て、
    /// そのトラックを弾くときに楽器を自動で持ち替える。
    /// 入れておくと、記録した MIDI をそのまま読み込ませるだけで
    /// 元と同じ編成で弾き直せる。
    /// </summary>
    public bool WriteInstrumentInTrackName { get; set; } = true;

    /// <summary>
    /// General MIDI のプログラムチェンジを書くか。
    ///
    /// 一般的な MIDI 音源で聞いたときに、それらしい音で鳴るようにする。
    /// ゲーム内の音の再現ではない。
    /// </summary>
    public bool WriteGeneralMidiProgram { get; set; } = true;
}

/// <summary>
/// 記録したイベント列を標準 MIDI ファイルへ変換する。
///
/// ゲームに依存しないので、既知の音符列を入れれば単体で検証できる
/// （完成条件1）。Dalamud の型は一切使わないこと。
/// </summary>
public static class MidiExporter
{
    /// <summary>
    /// 変換結果の要約。画面と README に出すために、
    /// 「何を推定したか」を数で持ち帰る。
    /// </summary>
    public sealed class ExportResult
    {
        public int NoteCount { get; init; }
        public int InferredLengthCount { get; init; }
        public int FallbackLengthCount { get; init; }
        public int DroppedCount { get; init; }
        public string FilePath { get; init; }
        public TimeSpan Duration { get; init; }

        /// <summary>
        /// 書き出したトラックの一覧（順番は MIDI のトラック順）。
        /// MidiBard2 用の設定ファイルを作るのに使う。
        /// </summary>
        public IReadOnlyList<TrackInfo> Tracks { get; init; } = [];

        /// <summary>
        /// 絞り込みで除いた演奏者と、その音数。
        ///
        /// 「何を捨てたか」を必ず持ち帰る。黙って減らすと、
        /// 取りこぼしと区別がつかなくなるため。
        /// </summary>
        public IReadOnlyList<(uint PerformerId, string Name, int Events)> Excluded { get; init; } = [];
    }

    /// <summary>
    /// 書き出した 1 トラックの情報。
    /// 「どのトラックを誰が何の楽器で弾いたか」を持ち帰る。
    /// </summary>
    public sealed class TrackInfo
    {
        /// <summary>MIDI 内のトラック番号（0 起点）。</summary>
        public int Index { get; init; }

        /// <summary>トラック名（MidiBard2 はここから楽器を判別する）。</summary>
        public string Name { get; init; }

        /// <summary>演奏者の EntityId。</summary>
        public uint PerformerId { get; init; }

        /// <summary>演奏者のキャラクター名。取れなければ null。</summary>
        public string PerformerName { get; init; }

        /// <summary>観測した楽器 ID。取れなければ -1。</summary>
        public int InstrumentId { get; init; }

        /// <summary>楽器名。未知の ID なら null。</summary>
        public string InstrumentName { get; init; }

        /// <summary>このトラックの音符数。</summary>
        public int NoteCount { get; init; }

        /// <summary>
        /// 記録中に楽器が変わって見えた場合、その全 ID。
        /// 通常は 1 つ。2 つ以上あるときは観測が揺れている。
        /// </summary>
        public IReadOnlyList<int> ObservedInstrumentIds { get; init; } = [];
    }

    /// <summary>
    /// イベント列から MidiFile を組み立てる。
    /// </summary>
    public static MidiFile Build(
        IReadOnlyList<NoteEventRecord> records,
        MidiExportOptions options,
        out ExportResult summary)
    {
        if (records == null) throw new ArgumentNullException(nameof(records));
        if (options == null) throw new ArgumentNullException(nameof(options));

        var tpqn = options.TicksPerQuarterNote;
        if (tpqn <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "TicksPerQuarterNote は正の値が必要です。");

        var bpm = options.BeatsPerMinute;
        if (bpm <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "BeatsPerMinute は正の値が必要です。");

        // 実時間(ms) → tick への換算係数
        var msPerQuarter = 60000.0 / bpm;
        var ticksPerMs = tpqn / msPerQuarter;

        // ---- 演奏者の絞り込み ----
        //
        // 近くで無関係な人がソロ演奏していると、その音も届いて混ざる。
        // ここで対象外を取り除く。
        //
        // 何を捨てたかは必ず持ち帰る。黙って減らすと、
        // 取りこぼしと区別がつかなくなるため。
        var excluded = new List<(uint, string, int)>();
        var include = options.IncludePerformers;
        var exclude = options.ExcludePerformers;

        if ((include != null && include.Count > 0) || (exclude != null && exclude.Count > 0))
        {
            var kept = new List<NoteEventRecord>(records.Count);
            var removedCount = new Dictionary<uint, int>();

            foreach (var r in records)
            {
                var ok = include == null || include.Count == 0 || include.Contains(r.PerformerId);

                if (ok && exclude != null && exclude.Contains(r.PerformerId))
                    ok = false;

                if (ok)
                    kept.Add(r);
                else
                    removedCount[r.PerformerId] = removedCount.GetValueOrDefault(r.PerformerId) + 1;
            }

            foreach (var (id, count) in removedCount.OrderByDescending(x => x.Value))
            {
                string name = null;
                options.PerformerNames?.TryGetValue(id, out name);
                excluded.Add((id, name, count));
            }

            records = kept;
        }

        // 発音中の音を突き合わせて (開始, 終了) の組を作る。
        // 同音が連続する場合に備え、音高ごとにキューで管理する。
        // 発音待ちの音。
        //
        // キーは「演奏者 × 音高」。音高だけで突き合わせると、
        // 2人が同じ音を重ねて弾いたとき、片方の消音がもう片方の発音と
        // 組まれて音長が入れ替わる。
        var open = new Dictionary<(uint Performer, int Note), Queue<NoteEventRecord>>();
        var pairs = new List<(NoteEventRecord on, TimeSpan? off)>();
        var dropped = 0;

        // 並べ替えの決まり：
        //   1) 時刻順
        //   2) 同じ時刻なら「発音」を先に処理する
        //
        // スロット位置を時刻に反映すると、発音が受信時刻より後ろへずれ、
        // 次のパケットの消音と前後が入れ替わることがある。
        // 消音を先に処理してしまうと対応する発音が見つからず、
        // 閉じない音と行き場のない消音が大量に生まれる。
        foreach (var rec in records
                     .OrderBy(r => r.ReceivedAt)
                     .ThenBy(r => r.Edge == NoteEdge.On ? 0 : 1))
        {
            if (rec.NoteNumber is < 0 or > 127)
            {
                dropped++;
                continue;
            }

            var key = (rec.PerformerId, rec.NoteNumber);

            if (rec.Edge == NoteEdge.On)
            {
                if (!open.TryGetValue(key, out var q))
                {
                    q = new Queue<NoteEventRecord>();
                    open[key] = q;
                }

                q.Enqueue(rec);
            }
            else
            {
                if (open.TryGetValue(key, out var q) && q.Count > 0)
                {
                    var on = q.Dequeue();

                    // スロット位置のぶん、消音が発音より前に来ることがある。
                    // そのままだと負の長さになるので、発音時刻まで引き上げる。
                    // （長さは下の最短長で補われる）
                    var off = rec.ReceivedAt < on.ReceivedAt
                        ? on.ReceivedAt
                        : rec.ReceivedAt;

                    pairs.Add((on, off));
                }
                else
                {
                    // 対応する開始が無い消音。記録開始前から鳴っていた音など。
                    dropped++;
                }
            }
        }

        // 閉じなかった音は暫定長を当てる。
        var fallbackCount = 0;
        foreach (var q in open.Values)
        {
            while (q.Count > 0)
            {
                pairs.Add((q.Dequeue(), null));
                fallbackCount++;
            }
        }

        // 演奏者ごとにトラックを分ける。
        //
        // MIDI では同じチャンネルで同じ音高が重なると区別できないため、
        // 2人が同じ音を重ねて弾くと読み戻したときに音長が入れ替わる。
        // 演奏者を分ければ防げる。
        var performers = options.SeparateTrackPerPerformer
            ? pairs.Select(p => p.on.PerformerId).Distinct().OrderBy(x => x).ToList()
            : [0u];

        // 音が 1 つも無い場合でも、テンポと注記を書くトラックが要る。
        if (performers.Count == 0)
            performers.Add(0u);

        var trackOf = new Dictionary<uint, List<TimedEvent>>();
        var channelOf = new Dictionary<uint, FourBitNumber>();

        for (var i = 0; i < performers.Count; i++)
        {
            trackOf[performers[i]] = [];

            // チャンネル 9 は打楽器なので避ける。16人を超えたら巡回させる。
            var ch = i % 15;
            if (ch >= 9) ch++;
            channelOf[performers[i]] = (FourBitNumber)ch;
        }

        // テンポと注記は、音符を持たない専用トラックに分ける。
        //
        // 以前は先頭の演奏者トラックに混ぜていたが、
        // 演奏者が1人のときに書き出し側がテンポを別トラックへ切り出し、
        // トラック名だけがそちらへ付いて音符トラックが無名になった
        // （実際にソロ記録11件で発生）。
        // 最初から分けておけば、演奏者の数に関わらず
        // 「名前と音符が同じトラックにある」状態を保てる。
        var conductor = new List<TimedEvent>();

        // テンポは「実時間を写すための基準」。元データの値ではない。
        conductor.Add(new TimedEvent(
            new SetTempoEvent((long)Math.Round(msPerQuarter * 1000.0)), 0));

        // 推定であることをファイル自体にも残す（完成条件：出力結果に明示）。
        conductor.Add(new TimedEvent(new TextEvent(
            "Recorded by BardPerformanceRecorder. "
            + "Note lengths are INFERRED from packet observation, not from the original MIDI. "
            + $"Tempo {bpm:0.##} BPM is an export baseline, NOT the original song tempo."), 0));

        conductor.Add(new TimedEvent(
            new SequenceTrackNameEvent("Tempo / notes (no performance)"), 0));

        // 演奏者ごとの楽器を決める。
        //
        // 楽器 ID は 1 音ごとに記録してある。ゲームの仕様では
        // 曲の途中で持ち替えられないが、観測が揺れる可能性はあるので
        // 「最も多く観測された ID」を採り、揺れた事実も残す。
        var instrumentOf = new Dictionary<uint, int>();
        var observedOf = new Dictionary<uint, List<int>>();

        foreach (var performer in performers)
        {
            var ids = records
                .Where(r => (!options.SeparateTrackPerPerformer || r.PerformerId == performer)
                            && r.InstrumentId >= 0)
                .GroupBy(r => r.InstrumentId)
                .OrderByDescending(g => g.Count())
                .ToList();

            instrumentOf[performer] = ids.Count > 0 ? ids[0].Key : -1;
            observedOf[performer] = ids.Select(g => g.Key).OrderBy(x => x).ToList();
        }

        var trackInfos = new List<TrackInfo>();

        for (var i = 0; i < performers.Count; i++)
        {
            var performer = performers[i];
            var instrumentId = instrumentOf[performer];
            var instrumentName = InstrumentNames.TryGet(instrumentId);

            string performerName = null;
            options.PerformerNames?.TryGetValue(performer, out performerName);

            // トラック名の組み立て。
            //
            // MidiBard2 は名前に含まれる楽器名を見て楽器を持ち替えるので、
            // 楽器名を必ず入れる。読む人のために演奏者名も添える。
            // MIDI のトラック名は latin-1 しか書けないため、
            // 日本語混じりの名前は ID にする。
            // 楽器名は必ず先頭に置く。
            //
            // .json が失われても、トラック名だけで
            // 「どのトラックを何の楽器で弾くか」が分かるようにするため。
            // 名前が引けないときも、黙って省かずに理由を残す
            // （「楽器が無い曲」と「取得できなかった」は別物なので）。
            var parts = new List<string>();

            if (options.WriteInstrumentInTrackName)
            {
                if (instrumentName != null)
                    parts.Add(instrumentName);
                else if (instrumentId >= 0)
                    parts.Add($"Instrument{instrumentId}");
                else
                    parts.Add("UnknownInstrument");
            }

            if (options.SeparateTrackPerPerformer)
            {
                var who = IsLatin1(performerName) ? performerName : null;
                parts.Add(who ?? $"performer {performer:X8}");
            }

            if (parts.Count == 0)
                parts.Add("Observed performance");

            var name = string.Join(" - ", parts) + " (observed)";

            trackOf[performer].Add(new TimedEvent(
                new SequenceTrackNameEvent(name), 0));

            // 一般的な MIDI 音源で聞いたときに、それらしい音で鳴るようにする。
            if (options.WriteGeneralMidiProgram)
            {
                var gm = InstrumentNames.TryGetGeneralMidiProgram(instrumentId);
                if (gm.HasValue)
                {
                    trackOf[performer].Add(new TimedEvent(
                        new ProgramChangeEvent((SevenBitNumber)gm.Value)
                        {
                            Channel = channelOf[performer],
                        }, 0));
                }
            }

            trackInfos.Add(new TrackInfo
            {
                // 先頭にテンポ用トラックを置くので 1 つ後ろへずれる。
                // ここがずれると .json の割り当てが 1 つ手前の
                // トラックを指してしまう。
                Index = i + 1,
                Name = name,
                PerformerId = performer,
                PerformerName = performerName,
                InstrumentId = instrumentId,
                InstrumentName = instrumentName,
                ObservedInstrumentIds = observedOf[performer],
            });
        }

        var inferredCount = 0;

        foreach (var (on, off) in pairs.OrderBy(p => p.on.ReceivedAt))
        {
            var startMs = on.ReceivedAt.TotalMilliseconds;
            double endMs;

            if (off.HasValue)
            {
                endMs = off.Value.TotalMilliseconds;
                inferredCount++;
            }
            else
            {
                endMs = startMs + options.FallbackNoteLengthMs;
            }

            if (endMs - startMs < options.MinimumNoteLengthMs)
                endMs = startMs + options.MinimumNoteLengthMs;

            var startTick = (long)Math.Round(startMs * ticksPerMs);
            var endTick = (long)Math.Round(endMs * ticksPerMs);
            if (endTick <= startTick)
                endTick = startTick + 1;

            // 念のためもう一度範囲を確認する（取り込み時にも弾いているが、
            // ここで例外を出すと書き出し全体が落ちるため）。
            if (on.NoteNumber < SevenBitNumber.MinValue || on.NoteNumber > SevenBitNumber.MaxValue)
            {
                dropped++;
                continue;
            }

            var note = (SevenBitNumber)on.NoteNumber;
            var velocity = (SevenBitNumber)Math.Clamp(
                (int)options.Velocity, SevenBitNumber.MinValue, SevenBitNumber.MaxValue);

            var owner = options.SeparateTrackPerPerformer ? on.PerformerId : 0u;
            var events = trackOf[owner];
            var channel = channelOf[owner];

            events.Add(new TimedEvent(
                new NoteOnEvent(note, velocity) { Channel = channel }, startTick));
            events.Add(new TimedEvent(
                new NoteOffEvent(note, SevenBitNumber.MinValue) { Channel = channel }, endTick));
        }

        var chunks = new List<TrackChunk>();

        // テンポ用トラックを先頭に置く。
        var conductorChunk = new TrackChunk();
        conductorChunk.AddObjects(conductor);
        chunks.Add(conductorChunk);

        foreach (var performer in performers)
        {
            var c = new TrackChunk();
            c.AddObjects(trackOf[performer]);
            chunks.Add(c);
        }

        var file = new MidiFile(chunks);
        file.TimeDivision = new TicksPerQuarterNoteTimeDivision(tpqn);

        var duration = pairs.Count == 0
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(
                pairs.Max(p => (p.off ?? p.on.ReceivedAt.Add(
                    TimeSpan.FromMilliseconds(options.FallbackNoteLengthMs))).TotalMilliseconds));

        // トラックごとの音符数を数えて詰め直す。
        var noteCountOf = pairs
            .GroupBy(p => options.SeparateTrackPerPerformer ? p.on.PerformerId : 0u)
            .ToDictionary(g => g.Key, g => g.Count());

        var tracksWithCounts = trackInfos.Select(t => new TrackInfo
        {
            Index = t.Index,
            Name = t.Name,
            PerformerId = t.PerformerId,
            PerformerName = t.PerformerName,
            InstrumentId = t.InstrumentId,
            InstrumentName = t.InstrumentName,
            ObservedInstrumentIds = t.ObservedInstrumentIds,
            NoteCount = noteCountOf.TryGetValue(t.PerformerId, out var n) ? n : 0,
        }).ToList();

        summary = new ExportResult
        {
            NoteCount = pairs.Count,
            InferredLengthCount = inferredCount,
            FallbackLengthCount = fallbackCount,
            DroppedCount = dropped,
            Duration = duration,
            Tracks = tracksWithCounts,
            Excluded = excluded,
        };

        return file;
    }

    /// <summary>
    /// MIDI のトラック名に書ける文字だけでできているか。
    ///
    /// 標準 MIDI のテキストは latin-1 の範囲しか安全に書けない。
    /// 日本語のキャラクター名をそのまま入れると文字化けするため、
    /// 書ける場合だけ使う。
    /// </summary>
    private static bool IsLatin1(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return false;

        foreach (var c in s)
        {
            if (c > 0xFF)
                return false;
        }

        return true;
    }

    /// <summary>
    /// ファイルへ書き出す。付随して、推定の内訳を書いたテキストも隣に置く。
    /// </summary>
    public static ExportResult Save(
        IReadOnlyList<NoteEventRecord> records,
        MidiExportOptions options,
        string filePath)
    {
        var file = Build(records, options, out var summary);

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        file.Write(filePath, overwriteFile: true,
            settings: new WritingSettings { });

        summary = new ExportResult
        {
            NoteCount = summary.NoteCount,
            InferredLengthCount = summary.InferredLengthCount,
            FallbackLengthCount = summary.FallbackLengthCount,
            DroppedCount = summary.DroppedCount,
            Duration = summary.Duration,
            Tracks = summary.Tracks,
            Excluded = summary.Excluded,
            FilePath = filePath,
        };

        WriteSidecar(records, options, summary, filePath);
        WriteMidiBardSettings(summary, filePath);
        return summary;
    }

    /// <summary>
    /// MidiBard2 用の設定ファイル（.json）を MIDI の隣に置く。
    ///
    /// MidiBard2 は MIDI を読み込むとき、同じ名前の .json があれば
    /// トラックの有効・無効や楽器の割り当てをそこから読む。
    /// 記録した編成をそのまま書いておけば、
    /// 読み込むだけで元と同じ楽器で弾き直せる。
    ///
    /// AssignedCids（誰が弾くか）は空にする。
    /// あれは永続的な ContentId だが、こちらが観測できるのは
    /// セッション限りの EntityId で、両者は対応付けられない。
    /// 埋めたふりをすると別人に割り当たるため、空のまま渡す。
    /// </summary>
    private static void WriteMidiBardSettings(ExportResult summary, string midiPath)
    {
        var jsonPath = Path.ChangeExtension(midiPath, ".json");
        using var w = new StreamWriter(jsonPath, false, new System.Text.UTF8Encoding(false));

        w.WriteLine("{");
        w.WriteLine("  \"Tracks\": [");

        for (var i = 0; i < summary.Tracks.Count; i++)
        {
            var t = summary.Tracks[i];
            var comma = i < summary.Tracks.Count - 1 ? "," : "";

            w.WriteLine("    {");
            w.WriteLine($"      \"Index\": {t.Index},");
            w.WriteLine("      \"Enabled\": true,");
            w.WriteLine($"      \"Name\": \"{JsonEscape(t.Name)}\",");
            w.WriteLine("      \"Transpose\": 0,");
            w.WriteLine($"      \"Instrument\": {(t.InstrumentId > 0 ? t.InstrumentId : 0)},");
            w.WriteLine("      \"AssignedCids\": []");
            w.WriteLine($"    }}{comma}");
        }

        w.WriteLine("  ],");
        w.WriteLine("  \"ToneMode\": 0,");
        w.WriteLine("  \"AdaptNotes\": true,");
        w.WriteLine("  \"Speed\": 1.0");
        w.WriteLine("}");
    }

    private static string JsonEscape(string s)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;

        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>
    /// 何を推定したかを書いた説明ファイル。MIDI だけ見ても
    /// 「どこが観測でどこが推定か」が分かるようにする。
    /// </summary>
    private static void WriteSidecar(
        IReadOnlyList<NoteEventRecord> records,
        MidiExportOptions options,
        ExportResult summary,
        string midiPath)
    {
        var txtPath = Path.ChangeExtension(midiPath, ".txt");
        using var w = new StreamWriter(txtPath, false, new System.Text.UTF8Encoding(true));

        w.WriteLine("BardPerformanceRecorder 記録メモ");
        w.WriteLine("=================================");
        w.WriteLine();
        w.WriteLine($"書き出し日時 : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        if (!string.IsNullOrEmpty(options.BuildStamp))
            w.WriteLine($"記録した版   : {options.BuildStamp}");

        w.WriteLine($"MIDIファイル : {midiPath}");
        w.WriteLine($"音符数       : {summary.NoteCount}");
        w.WriteLine($"記録長       : {summary.Duration.TotalSeconds:F3} 秒");
        w.WriteLine();
        w.WriteLine("■ この記録の性質（重要）");
        w.WriteLine("  - これは近くの演奏を自分のクライアントで観測した結果であり、");
        w.WriteLine("    元の MIDI ファイルの復元ではありません。");
        w.WriteLine("  - 時刻はすべて「パケット受信時刻」です。演奏者が実際に弾いた");
        w.WriteLine("    時刻とは、通信の遅れのぶんだけずれています。");
        w.WriteLine($"  - テンポ {options.BeatsPerMinute:0.##} BPM は書き出しの基準値であり、");
        w.WriteLine("    元の曲のテンポではありません（パケットにテンポ情報はありません）。");
        w.WriteLine("  - 強弱（ベロシティ）はパケットに含まれないため、");
        w.WriteLine($"    全音符を固定値 {options.Velocity} で書き出しています。");
        w.WriteLine();
        w.WriteLine("■ 音の長さの内訳");
        w.WriteLine($"  - 消音を観測できた音   : {summary.InferredLengthCount} 個");
        w.WriteLine("      （パケット上で音が消えたことから長さを推定。");
        w.WriteLine("        ゲームから「離鍵」の通知が来るわけではありません）");
        w.WriteLine($"  - 暫定長を当てた音     : {summary.FallbackLengthCount} 個");
        w.WriteLine($"      （固定 {options.FallbackNoteLengthMs} ms。記録停止時に鳴っていた音など）");
        w.WriteLine($"  - 破棄した異常データ   : {summary.DroppedCount} 件");
        w.WriteLine();

        if (summary.Excluded.Count > 0)
        {
            w.WriteLine("■ 絞り込みで除いた演奏者");
            w.WriteLine();
            foreach (var (id, name, events) in summary.Excluded)
            {
                var who = string.IsNullOrEmpty(name) ? $"0x{id:X8}" : $"{name} (0x{id:X8})";
                w.WriteLine($"  - {who}  … {events} 件のイベントを除外");
            }

            w.WriteLine();
            w.WriteLine("  ※ この MIDI には上の演奏者は含まれていません。");
            w.WriteLine("     生ログには全員分が残っているので、");
            w.WriteLine("     選び直して作り直すことができます。");
            w.WriteLine();
        }

        w.WriteLine("■ トラックと楽器の割り当て");
        w.WriteLine();
        w.WriteLine("  Trk  楽器                       音符数  演奏者");
        w.WriteLine("  ---  -------------------------  ------  --------------------------------");

        foreach (var t in summary.Tracks)
        {
            var inst = t.InstrumentName
                       ?? (t.InstrumentId >= 0 ? $"(ID {t.InstrumentId} 不明)" : "(取得できず)");

            var who = string.IsNullOrEmpty(t.PerformerName)
                ? $"0x{t.PerformerId:X8}"
                : $"{t.PerformerName} (0x{t.PerformerId:X8})";

            w.WriteLine($"  {t.Index,3}  {inst,-25}  {t.NoteCount,6}  {who}");

            // 楽器が途中で変わって見えた場合は、観測が揺れている。
            // ゲームの仕様では曲中の持ち替えはできないので、
            // 黙って最頻値を採ったことを分かるようにしておく。
            if (t.ObservedInstrumentIds.Count > 1)
            {
                w.WriteLine("       ※ 記録中に複数の楽器 ID が観測されました: "
                            + string.Join(", ", t.ObservedInstrumentIds));
                w.WriteLine($"          最も多かった {t.InstrumentId} を採用しています。");
            }
        }

        w.WriteLine();
        w.WriteLine("  ※ トラック名には MidiBard2 が認識する楽器名を入れてあります。");
        w.WriteLine("     隣の .json と一緒に読み込ませると、同じ編成で弾き直せます。");
        w.WriteLine("     ただし「誰が弾くか」（AssignedCids）は空にしてあります。");
        w.WriteLine("     観測できるのはセッション限りの EntityId で、");
        w.WriteLine("     設定ファイルが使う永続 ContentId とは対応付けられないためです。");
        w.WriteLine();

        w.WriteLine("■ 生イベント一覧（先頭 500 件）");
        foreach (var r in records.Take(500))
            w.WriteLine("  " + r.ToString());

        if (records.Count > 500)
            w.WriteLine($"  ... 他 {records.Count - 500} 件");
    }
}
