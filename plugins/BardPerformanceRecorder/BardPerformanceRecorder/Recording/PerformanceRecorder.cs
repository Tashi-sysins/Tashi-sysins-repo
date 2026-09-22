using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BardPerformanceRecorder.Game;

namespace BardPerformanceRecorder.Recording;

/// <summary>
/// 記録の本体。
///
/// 設計上の取り決め：
/// - <see cref="Feed"/> はフック内（ゲームスレッド）から呼ばれる。
///   ここでは必要最小限のコピーとキュー追加しかしない。
/// - ファイル保存や MIDI 変換は <see cref="Drain"/> 以降の別処理で行う。
/// - 時刻は Stopwatch（単調増加）で測る。システム時計の変更に影響されないため。
/// </summary>
public sealed class PerformanceRecorder
{
    private readonly ConcurrentQueue<NoteEventRecord> queue = new();
    private readonly Stopwatch clock = new();

    /// <summary>
    /// 演奏者ごとの「いま鳴っている音」と、その音が入っていたスロット位置。
    ///
    /// スロット位置を覚えておく理由：
    /// 消音の時刻を決めるとき、発音に足したのと同じ量を足さないと
    /// 「消音が発音より前」になって対応が取れなくなる。
    /// </summary>
    private readonly Dictionary<uint, Dictionary<int, int>> sounding = new();

    /// <summary>
    /// 演奏者ごとの「直前に処理したパケットの中身」。
    ///
    /// 実機では同じパケットが 2 回届く（実測で全パケットが二重）。
    /// そのまま 2 回処理すると、同音連打の判定が誤作動して
    /// 音が余分に増えるため、同じ内容が続いたら無視する。
    /// </summary>
    private readonly Dictionary<uint, byte[]> lastPacket = new();

    /// <summary>直前のパケットを受け取った時刻。二重観測の判定に使う。</summary>
    private readonly Dictionary<uint, TimeSpan> lastPacketAt = new();

    /// <summary>
    /// ログ再生のときに外から与えられた受信時刻。
    /// null なら内部の時計を使う。
    /// </summary>
    private TimeSpan? injectedTime;

    /// <summary>いま使うべき受信時刻。</summary>
    private TimeSpan Now => injectedTime ?? clock.Elapsed;

    private readonly object gate = new();

    /// <summary>記録済みイベント（確定分）。</summary>
    private readonly List<NoteEventRecord> events = new();

    public bool IsRecording { get; private set; }

    /// <summary>
    /// 記録対象の演奏者。0 の場合は「最初に観測した演奏者を自動で選ぶ」。
    /// </summary>
    public uint TargetPerformerId { get; set; }

    /// <summary>自動選択が働いた結果、実際に記録している演奏者。</summary>
    public uint ActivePerformerId { get; private set; }

    /// <summary>
    /// 演奏者を絞らず、届いたものを全部記録するか。
    /// 人のいない場所での検証時に使う。合奏の記録にもそのまま使える。
    /// </summary>
    public bool RecordAllPerformers { get; set; }

    /// <summary>
    /// スロットの位置を時刻に反映するか。
    ///
    /// パケットは約 0.5 秒ごとに届き、その間に弾かれた音が
    /// 10 個のスロットに順番に入る。スロット番号は区間内の
    /// 時間位置を表すので、これを足すと受信間隔より細かく復元できる。
    ///
    /// 実測（ファミマ入店音・17音）では、
    /// 平均ずれが 179ms → 51ms に改善した。
    /// </summary>
    public bool UseSlotTiming { get; set; } = true;

    /// <summary>
    /// スロット 1 個ぶんの時間（ミリ秒）。
    /// 受信間隔 約0.49秒 ÷ 10スロット ≒ 50ms。
    /// </summary>
    public double SlotIntervalMs { get; set; } = 50.0;

    /// <summary>
    /// 0xFE を「離鍵の合図」として音長に使うか。
    ///
    /// 実測（2026-09-21）で、0xFE が現れたスロット位置と
    /// 実音のスロット位置の差が元の音長と一致することを確かめた。
    /// これを使わないと、音長が受信間隔（約0.49秒）に量子化される。
    /// </summary>
    public bool UseNoteOffMarker { get; set; } = true;

    /// <summary>
    /// 離鍵の合図（0xFE）をどれだけ待つか（ミリ秒）。
    ///
    /// 【実測で分かったこと・2026-09-21】
    /// 0xFE は必ず出るわけではない。演奏が続いている間は現れず、
    /// 最後の音が終わったときだけ出る（和音の曲では17音に1件だけだった）。
    ///
    /// そのため、合図が来ない音はこの時間で打ち切る。
    ///   短くする → 和音・速い曲の音長が実態に近づく
    ///   長くする → 単音を長く伸ばしたときの音長が正確になる
    /// 両立しないので、中間の値にしている。
    ///
    /// なお「待っている最中に同じ音が鳴り直した」場合は、
    /// この時間を待たずにその時点で閉じる。
    /// </summary>
    public double NoteOffWaitMs { get; set; } = 1000.0;

    /// <summary>
    /// 離鍵の合図が来たとき、どの音を閉じるか。
    ///
    /// true  … 直前に鳴らした音を閉じる
    /// false … いちばん古くから鳴っている音を閉じる
    ///
    /// 実測（FF4 リディアのテーマ・472音）では true のほうが
    /// 元の音長に近かった。
    /// </summary>
    public bool CloseNewestFirst { get; set; } = true;

    /// <summary>
    /// 離鍵の合図を待っている音と、待ち始めた時刻。
    ///
    /// キーは「演奏者 × 音高」。音高だけにすると、
    /// 2人が同じ音を弾いたときに待ち状態が共有され、
    /// 片方の終了処理がもう片方の音長を変えてしまう
    /// （実測で A の音長が 250ms → 750ms になった）。
    /// </summary>
    private readonly Dictionary<(uint Performer, int Note), TimeSpan> waitingForOff = new();

    /// <summary>これまでに観測した演奏者の一覧（画面の選択肢用）。</summary>
    public IReadOnlyDictionary<uint, DateTime> SeenPerformers => seenPerformers;
    private readonly ConcurrentDictionary<uint, DateTime> seenPerformers = new();

    /// <summary>取り込んだイベント総数。</summary>
    public int EventCount
    {
        get { lock (gate) return events.Count; }
    }

    /// <summary>発音開始イベントの数（画面に出す「音符数」）。</summary>
    public int NoteOnCount { get; private set; }

    /// <summary>破棄した異常データの数。</summary>
    public int DiscardedCount { get; private set; }

    /// <summary>直近のエラー。画面に出す。</summary>
    public string LastError { get; private set; }

    public TimeSpan Elapsed => clock.Elapsed;

    public void Start(uint targetPerformerId)
    {
        lock (gate)
        {
            events.Clear();
            sounding.Clear();
            lastPacket.Clear();
            lastPacketAt.Clear();
            waitingForOff.Clear();
            DuplicatePacketCount = 0;
            while (queue.TryDequeue(out _)) { }

            TargetPerformerId = targetPerformerId;
            ActivePerformerId = targetPerformerId;
            NoteOnCount = 0;
            DiscardedCount = 0;
            LastError = null;

            injectedTime = null;
            clock.Restart();
            IsRecording = true;
        }
    }

    /// <summary>
    /// 記録を止める。
    /// </summary>
    /// <param name="at">
    /// 停止時刻。ログ再生のときに指定する。null なら内部の時計を使う。
    /// </param>
    public void Stop(TimeSpan? at = null)
    {
        injectedTime = at;

        // 停止と「鳴っている音を閉じる」は、間に Feed が割り込むと
        // 閉じたあとに音が復活してしまうため、ひとつのロックの中で行う。
        lock (gate)
        {
            IsRecording = false;
            clock.Stop();

            // 停止時の消音も、その音が居たスロット位置に合わせる。
            // そうしないと発音より前になり、対応が取れなくなる。
            var stopAt = Now;

            foreach (var (performerId, notes) in sounding)
            {
                foreach (var (midi, slot) in notes)
                {
                    var offAt = UseSlotTiming
                        ? stopAt + TimeSpan.FromMilliseconds(slot * SlotIntervalMs)
                        : stopAt;

                    queue.Enqueue(new NoteEventRecord(
                        performerId, midi, PerformanceConstants.NoteEmpty, 0xFF,
                        -1, offAt, NoteEdge.OffInferred, NoteSource.SoloPacket));
                }
            }

            sounding.Clear();
        }

        Drain();
    }

    /// <summary>
    /// フックから呼ばれる取り込み口。ここは短時間で返すこと。
    /// </summary>
    /// <param name="performerId">演奏者の EntityId。</param>
    /// <param name="notes">音符バイト列（呼び出し元でコピー済みのもの）。</param>
    /// <param name="tones">音色バイト列。長さは notes と同じであること。</param>
    /// <param name="instrumentId">観測できた楽器 ID。不明なら -1。</param>
    /// <param name="source">取得経路。</param>
    public void Feed(
        uint performerId,
        ReadOnlySpan<byte> notes,
        ReadOnlySpan<byte> tones,
        int instrumentId,
        NoteSource source)
        => FeedAt(null, performerId, notes, tones, instrumentId, source);

    /// <summary>
    /// 受信時刻を指定して取り込む。保存済みログの再生に使う。
    ///
    /// なぜ必要か：
    /// 通常の <see cref="Feed"/> は内部の時計を読むため、
    /// 記録済みログを流し込むと全部の音が一瞬に潰れてしまう。
    /// 本番と同じ処理を通しつつ、元の時刻・順序・重複をそのまま
    /// 再現できる入口を用意する。
    /// </summary>
    /// <param name="at">記録開始からの経過時間。null なら内部の時計を使う。</param>
    public void FeedAt(
        TimeSpan? at,
        uint performerId,
        ReadOnlySpan<byte> notes,
        ReadOnlySpan<byte> tones,
        int instrumentId,
        NoteSource source)
    {
        if (!IsRecording)
            return;

        injectedTime = at;

        seenPerformers[performerId] = DateTime.Now;

        // 全員記録するなら、絞り込みを一切しない。
        // （人のいない場所での検証や、合奏の記録に使う）
        if (RecordAllPerformers)
        {
            FeedCore(performerId, notes, tones, instrumentId, source);
            return;
        }

        // 対象が未指定なら、最初に音を出した演奏者を掴む。
        if (ActivePerformerId == 0)
        {
            // 実際に鳴っている音があるパケットだけを自動選択の根拠にする。
            var hasNote = false;
            foreach (var b in notes)
            {
                if (PerformanceConstants.IsPlayableNote(b))
                {
                    hasNote = true;
                    break;
                }
            }

            if (!hasNote)
                return;

            ActivePerformerId = performerId;
        }

        if (performerId != ActivePerformerId)
            return;

        FeedCore(performerId, notes, tones, instrumentId, source);
    }

    /// <summary>
    /// 同じ受信を二重に観測したものかどうか。
    ///
    /// 判定の根拠は「内容が同じ」だけでは足りない。
    /// 同じフレーズが繰り返される曲では、別の区間の正当な演奏でも
    /// 音符配列が一致するため、それまで捨ててしまう。
    ///
    /// 実測（2026-09-21）では、同じ内容が連続した間隔は
    ///   二重観測  : 0.0〜5.0ms（16件）
    ///   別の区間  : 461〜535ms（4件）
    /// と、はっきり二極化していた。あいだの値は現れない。
    /// そこで「ごく短い時間内に同じ内容が来たもの」だけを
    /// 二重観測とみなす。
    ///
    /// 必ずロックの中から呼ぶこと。
    /// </summary>
    private bool IsDuplicateObservation(
        uint performerId, ReadOnlySpan<byte> notes, TimeSpan now)
    {
        var isSameContent =
            lastPacket.TryGetValue(performerId, out var prevBytes)
            && prevBytes.Length == notes.Length
            && notes.SequenceEqual(prevBytes);

        if (isSameContent
            && lastPacketAt.TryGetValue(performerId, out var prevAt)
            && (now - prevAt).TotalMilliseconds <= DuplicateWindowMs)
        {
            DuplicatePacketCount++;

            // 時刻は更新しない。
            // 3 回以上続けて届いた場合も、最初の受信からの経過で測る。
            return true;
        }

        lastPacket[performerId] = notes.ToArray();
        lastPacketAt[performerId] = now;
        return false;
    }

    /// <summary>
    /// 二重観測とみなす時間の幅（ミリ秒）。
    ///
    /// 実測では二重観測は 5ms 以内、次の区間は 461ms 以上だったので、
    /// あいだの余裕をとって 50ms を既定とする。
    /// </summary>
    public double DuplicateWindowMs { get; set; } = 50.0;

    /// <summary>
    /// 同じ内容が続いたために無視したパケット数。
    /// 実機では全体のほぼ半分になる（毎回二重に届くため）。
    /// </summary>
    public int DuplicatePacketCount { get; private set; }

    /// <summary>
    /// 発音・消音の判定本体。絞り込みを通ったものだけが来る。
    /// </summary>
    private void FeedCore(
        uint performerId,
        ReadOnlySpan<byte> notes,
        ReadOnlySpan<byte> tones,
        int instrumentId,
        NoteSource source)
    {
        // ログ再生のときは、外から与えられた受信時刻を使う。
        var now = Now;

        lock (gate)
        {
            // Stop() と競合した場合、ここで抜ける。
            // ロックの外の IsRecording 判定だけでは、停止処理の途中に
            // 割り込んだパケットが「閉じたはずの音」を復活させてしまう。
            // （この行を消すと「競合」検証が 30回中 28回 落ちる）
            if (!IsRecording)
                return;

            // 同じ受信を二重に観測した場合は無視する。
            //
            // 実機では同じ内容がごく短い間隔で 2 回届く（実測）。
            // 2 回処理すると、1 回目で鳴らした音が 2 回目に
            // 「同じパケット内の繰り返し」と誤判定され、音が増える。
            if (IsDuplicateObservation(performerId, notes, now))
                return;

            if (!sounding.TryGetValue(performerId, out var prev))
            {
                prev = new Dictionary<int, int>();
                sounding[performerId] = prev;
            }

            // ---- スロットを順に見ていく ----
            //
            // パケットは「その約0.5秒のあいだに起きたこと」を
            // 10 個のスロットに時間順で並べたもの（実測で確認）。
            //
            //   実音   … そのスロット位置で音が鳴り始めた
            //   0xFE   … そのスロット位置で音が終わった（離鍵）
            //   0xFF   … 何も起きていない
            //
            // 連打では 0xFE と実音が交互に現れる。
            //   例: . 0xFE G4 . 0xFE G4 . 0xFE . G4
            //       （閉じる→鳴らす→閉じる→鳴らす…）
            //
            // そのため「先に全部閉じてから、後で全部鳴らす」と
            // 対応が崩れる。スロット順に 1 回で処理する。

            // いま鳴っている音と、鳴り始めたスロット位置。
            var live = new Dictionary<int, int>(prev);

            // 鳴り始めた順。離鍵はいちばん古い音から閉じる。
            var order = new List<int>(prev.Keys);

            for (var i = 0; i < notes.Length; i++)
            {
                var raw = notes[i];

                TimeSpan At(int slot) => UseSlotTiming
                    ? now + TimeSpan.FromMilliseconds(slot * SlotIntervalMs)
                    : now;

                // ---- 離鍵 ----
                if (raw == PerformanceConstants.NoteOff)
                {
                    if (!UseNoteOffMarker || order.Count == 0)
                        continue;

                    // どの音を閉じるか。
                    //
                    // 実測（FF4 リディアのテーマ・472音）で、
                    // 「いちばん古い音」より「直前に鳴らした音」を閉じるほうが
                    // 元の音長に合うことを確かめた。
                    //
                    //   例: スロット 9,16,24 に音、23 と 52 に 0xFE
                    //       古い順 → 9 の音を閉じる（0.7秒）
                    //       直前   → 16 の音を閉じる（0.35秒）← 元と一致
                    //
                    // 0xFE は「その位置で終わった音」を指すので、
                    // 直前に鳴り始めた音と対応させるのが自然。
                    var midiToClose = CloseNewestFirst
                        ? order[^1]
                        : order[0];

                    order.Remove(midiToClose);
                    live.Remove(midiToClose);
                    waitingForOff.Remove((performerId, midiToClose));

                    queue.Enqueue(new NoteEventRecord(
                        performerId, midiToClose, PerformanceConstants.NoteOff, 0xFF,
                        instrumentId, At(i), NoteEdge.OffInferred, source));

                    continue;
                }

                // ---- 実音（発音） ----
                if (!PerformanceConstants.IsPlayableNote(raw))
                    continue;

                var midi = PerformanceConstants.ToMidiNote(raw);
                if (midi < 0)
                {
                    DiscardedCount++;
                    continue;
                }

                // 同じ音がまだ鳴っているなら、いったん閉じてから鳴らし直す。
                // （閉じずに重ねると、MIDI 上で対応が取れなくなる）
                if (live.ContainsKey(midi))
                {
                    live.Remove(midi);
                    order.Remove(midi);
                    waitingForOff.Remove((performerId, midi));

                    queue.Enqueue(new NoteEventRecord(
                        performerId, midi, PerformanceConstants.NoteOff, 0xFF,
                        instrumentId, At(i), NoteEdge.OffInferred, source));
                }

                var tone = i < tones.Length ? tones[i] : (byte)0xFF;

                queue.Enqueue(new NoteEventRecord(
                    performerId, midi, raw, tone,
                    instrumentId, At(i), NoteEdge.On, source));

                live[midi] = i;
                order.Add(midi);
            }

            // ---- 離鍵の合図が来ないまま時間が経った音を閉じる ----
            //
            // 0xFE は必ず出るわけではない。和音の曲では
            // 17音に対し 1 件しか出なかった（実測）。
            // 待ち続けると音が閉じず、次の発音が欠落する。
            if (UseNoteOffMarker)
            {
                foreach (var midi in new List<int>(live.Keys))
                {
                    // このパケットで鳴らした音は、まだ待ち始めていない。
                    if (!prev.ContainsKey(midi))
                        continue;

                    if (!waitingForOff.TryGetValue((performerId, midi), out var since))
                    {
                        waitingForOff[(performerId, midi)] = now;
                        continue;
                    }

                    if ((now - since).TotalMilliseconds <= NoteOffWaitMs)
                        continue;

                    var slot = live[midi];
                    live.Remove(midi);
                    order.Remove(midi);
                    waitingForOff.Remove((performerId, midi));

                    queue.Enqueue(new NoteEventRecord(
                        performerId, midi, PerformanceConstants.NoteEmpty, 0xFF,
                        instrumentId,
                        UseSlotTiming
                            ? now + TimeSpan.FromMilliseconds(slot * SlotIntervalMs)
                            : now,
                        NoteEdge.OffInferred, source));
                }
            }
            else
            {
                // 合図を使わない設定では、実音が消えた時点で閉じる。
                foreach (var (midi, slot) in prev)
                {
                    if (live.ContainsKey(midi) && !IsInPacket(notes, midi))
                    {
                        live.Remove(midi);
                        order.Remove(midi);

                        queue.Enqueue(new NoteEventRecord(
                            performerId, midi, PerformanceConstants.NoteEmpty, 0xFF,
                            instrumentId,
                            UseSlotTiming
                                ? now + TimeSpan.FromMilliseconds(slot * SlotIntervalMs)
                                : now,
                            NoteEdge.OffInferred, source));
                    }
                }
            }

            sounding[performerId] = live;
        }
    }

    /// <summary>その音がこのパケットの実音として現れているか。</summary>
    private static bool IsInPacket(ReadOnlySpan<byte> notes, int midi)
    {
        foreach (var raw in notes)
        {
            if (PerformanceConstants.IsPlayableNote(raw)
                && PerformanceConstants.ToMidiNote(raw) == midi)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 鳴っている音をすべて閉じる。停止時と演奏者切り替え時に呼ぶ。
    /// </summary>
    public void CloseAllSounding(TimeSpan at)
    {
        lock (gate)
        {
            foreach (var (performerId, notes) in sounding)
            {
                foreach (var (midi, slot) in notes)
                {
                    var offAt = UseSlotTiming
                        ? at + TimeSpan.FromMilliseconds(slot * SlotIntervalMs)
                        : at;

                    queue.Enqueue(new NoteEventRecord(
                        performerId, midi, PerformanceConstants.NoteEmpty, 0xFF,
                        -1, offAt, NoteEdge.OffInferred, NoteSource.SoloPacket));
                }
            }

            sounding.Clear();
        }
    }

    /// <summary>
    /// キューを確定リストへ移す。フレーム処理から定期的に呼ぶ。
    /// </summary>
    public void Drain()
    {
        while (queue.TryDequeue(out var record))
        {
            lock (gate)
            {
                events.Add(record);
                if (record.Edge == NoteEdge.On)
                    NoteOnCount++;
            }
        }
    }

    /// <summary>確定済みイベントのコピーを返す。</summary>
    public List<NoteEventRecord> Snapshot()
    {
        Drain();
        lock (gate)
            return new List<NoteEventRecord>(events);
    }

    public void SetError(string message) => LastError = message;

    public void ClearSeenPerformers() => seenPerformers.Clear();
}
