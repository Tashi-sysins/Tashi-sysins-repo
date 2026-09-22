using System;
using Dalamud.Configuration;

namespace BardPerformanceRecorder;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>生ログ・診断の保存先フォルダ。空なら既定の場所を使う。</summary>
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>
    /// 完成した MIDI と .json の保存先フォルダ。
    ///
    /// 生ログとは別に持つ。生ログは調べるためのもので量が多く、
    /// 成果物は手持ちの MIDI と一緒に置きたいことがあるため
    /// （既定は D:\MIDI\作成Midi）。
    /// 空なら生ログの隣の「作成Midi」を使う。
    /// </summary>
    public string MidiOutputDirectory { get; set; } = @"D:\MIDI\作成Midi";

    /// <summary>書き出し基準の BPM（元曲のテンポではない）。</summary>
    public double BeatsPerMinute { get; set; } = 120.0;

    /// <summary>消音を観測できなかった音に当てる暫定長（ミリ秒）。</summary>
    public int FallbackNoteLengthMs { get; set; } = 250;

    /// <summary>音の最短長（ミリ秒）。</summary>
    public int MinimumNoteLengthMs { get; set; } = 30;

    /// <summary>合奏パケットも記録対象にするか。既定はソロのみ。</summary>
    public bool RecordEnsemblePackets { get; set; } = false;

    /// <summary>記録停止時に自動で MIDI を書き出すか。</summary>
    public bool AutoExportOnStop { get; set; } = true;

    /// <summary>
    /// 曲が終わったと判断するまでの無音時間（秒）。
    ///
    /// 合奏パケットは約3秒ごとに届く。演奏が止まると届かなくなるので、
    /// この時間だけ途切れたら「曲が終わった」とみなして書き出す。
    ///
    /// 短すぎると曲の途中の静かな部分で切れてしまう。
    /// 長すぎると次の曲と1つにつながる。
    /// </summary>
    public double ContinuousSongGapSeconds { get; set; } = 15.0;

    /// <summary>
    /// 連続記録をあきらめるまでの待ち時間（分）。
    ///
    /// これだけ待っても演奏が始まらなければ、待機をやめる。
    /// 押しっぱなしで放置しても、いつまでも動き続けないようにするため。
    /// </summary>
    public double ContinuousIdleTimeoutMinutes { get; set; } = 15.0;

    /// <summary>
    /// 生パケットを残すか。既定で有効。
    /// こちらの解釈が間違っていても、生さえ残っていれば後から読み直せる。
    /// </summary>
    public bool LogRawPackets { get; set; } = true;

    /// <summary>
    /// 自分が押した鍵を記録するか。既定で有効。
    /// 自分で弾いたものは「何を弾いたか」が確実なので、
    /// 受信パケットと突き合わせて読み方を検証できる。
    /// </summary>
    public bool LogLocalKeys { get; set; } = true;

    /// <summary>
    /// 記録対象の演奏者を絞らず、届いたものを全部記録するか。
    /// 人のいない場所で検証するときに有効。
    /// </summary>
    public bool RecordAllPerformers { get; set; } = false;

    /// <summary>
    /// スロットの位置を時刻に反映するか。
    /// パケット内の10スロットは区間内の時間位置を表すため、
    /// これを使うとタイミングの精度が上がる（実測で 179ms → 51ms）。
    /// </summary>
    public bool UseSlotTiming { get; set; } = true;

    /// <summary>スロット1個ぶんの時間（ミリ秒）。</summary>
    public double SlotIntervalMs { get; set; } = 50.0;

    /// <summary>
    /// パケットディスパッチャのフックを使うか。
    ///
    /// 演奏ハンドラ個別のフックが呼ばれない環境でも、
    /// ここなら opcode で直接拾える。既定で有効。
    /// </summary>
    public bool UseDispatcherHook { get; set; } = true;

    /// <summary>
    /// 生ログに残すペイロードのバイト数。
    /// 合奏の形式を調べるときに広げる。
    /// </summary>
    public int CaptureBytes { get; set; } = 24;

    /// <summary>
    /// IPC ヘッダも生ログに残すか。合奏調査用。
    /// </summary>
    public bool LogPacketHeader { get; set; } = false;

    /// <summary>
    /// 演奏パケットの opcode。
    /// 2026-09-20 に実機で観測した値が 0x337。
    /// ゲーム更新で変わるので、画面から変えられるようにしておく。
    /// </summary>
    public int PerformanceOpcode { get; set; } = 0x337;
}
