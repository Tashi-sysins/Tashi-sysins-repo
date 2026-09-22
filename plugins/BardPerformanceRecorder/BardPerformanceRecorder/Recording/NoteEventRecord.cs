using System;

namespace BardPerformanceRecorder.Recording;

/// <summary>
/// 音符イベントの取得方法。出力にそのまま残すため、後から
/// 「その値がどこまで信用できるか」を判断できるようにしておく。
/// </summary>
public enum NoteSource
{
    /// <summary>ソロ演奏受信ハンドラ（SoloReceivedHandler）から取得。</summary>
    SoloPacket,

    /// <summary>合奏演奏受信ハンドラ（EnsembleReceivedHandler）から取得。</summary>
    EnsemblePacket,
}

/// <summary>
/// 音符の「開始」「終了」の区別。
/// 終了はパケットに含まれないため、必ず推定であることを持ち回る。
/// </summary>
public enum NoteEdge
{
    /// <summary>発音開始。パケットに音符番号が現れた瞬間。</summary>
    On,

    /// <summary>
    /// 消音。パケット上で音符が消えた（0xFF になった）ことから推定したもの。
    /// ゲームから「離鍵」という情報が来るわけではない。
    /// </summary>
    OffInferred,
}

/// <summary>
/// 記録1件。取得処理（フック内）はこの構造体を作ってキューに積むだけにし、
/// 重い処理は一切行わない。
/// </summary>
public readonly struct NoteEventRecord
{
    /// <summary>演奏者の EntityId（受信ハンドラの第1引数 sourceId）。</summary>
    public readonly uint PerformerId;

    /// <summary>MIDI ノート番号に変換済みの音高。</summary>
    public readonly int NoteNumber;

    /// <summary>
    /// パケット上の生の音符バイト。0xFF=無音, 0xFE=保持。
    /// 変換前の値を残しておき、後から検証できるようにする。
    /// </summary>
    public readonly byte RawNote;

    /// <summary>
    /// 音色バイト（ギター系のトーン 0-4 など）。取得できない場合は 0xFF。
    /// </summary>
    public readonly byte RawTone;

    /// <summary>
    /// 受信時点で観測した楽器 ID（Perform シート行 ID）。
    /// 取得できなかった場合は -1。
    /// </summary>
    public readonly int InstrumentId;

    /// <summary>
    /// 記録開始からの経過時間。Stopwatch（単調増加時計）で測る。
    /// これは「受信時刻」であって「演奏者が弾いた時刻」ではない。
    /// </summary>
    public readonly TimeSpan ReceivedAt;

    /// <summary>開始か、推定消音か。</summary>
    public readonly NoteEdge Edge;

    /// <summary>どの経路で取得したか。</summary>
    public readonly NoteSource Source;

    public NoteEventRecord(
        uint performerId,
        int noteNumber,
        byte rawNote,
        byte rawTone,
        int instrumentId,
        TimeSpan receivedAt,
        NoteEdge edge,
        NoteSource source)
    {
        PerformerId = performerId;
        NoteNumber = noteNumber;
        RawNote = rawNote;
        RawTone = rawTone;
        InstrumentId = instrumentId;
        ReceivedAt = receivedAt;
        Edge = edge;
        Source = source;
    }

    public override string ToString()
        => $"{ReceivedAt.TotalSeconds,8:F3}s {(Edge == NoteEdge.On ? "ON " : "off")} "
           + $"note={NoteNumber,3} raw=0x{RawNote:X2} tone=0x{RawTone:X2} "
           + $"perf={PerformerId:X8} inst={InstrumentId} src={Source}";
}
