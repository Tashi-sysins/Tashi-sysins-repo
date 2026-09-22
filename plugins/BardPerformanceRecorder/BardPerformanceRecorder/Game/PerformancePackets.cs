// このファイルのパケット構造の定義は、MidiBard プロジェクト
// (Copyright (C) 2022 akira0245) の Midibard/Structs/SoloPerformanceIpc.cs および
// EnsemblePerformanceIpc.cs に由来します。
// （オフセットの実測値は 2026-09-20 に現行クライアントで再確認済み。docs/STAGE1.md 参照）
//
// MidiBard は GNU Affero General Public License v3.0 で配布されています。
// 原作者 akira0245 の表示と、MidiBard プロジェクトで使われていたものである旨を
// 明示することが、元のライセンスで求められています。
//
// 元ライセンス: https://github.com/akira0245/MidiBard/blob/master/LICENSE

using System;

namespace BardPerformanceRecorder.Game;

/// <summary>
/// 演奏パケットの共通定数。
///
/// レイアウトは 2026-09-20 に ffxiv_dx11.exe (ver 2026.09.15.0000.0000) を
/// 逆アセンブルして確認した実測値。詳細は docs/STAGE1.md を参照。
///
/// 元の構造体定義は MidiBard プロジェクト (akira0245, AGPL-3.0) に由来する。
/// </summary>
public static class PerformanceConstants
{
    /// <summary>音符バイトの番兵：発音していない。</summary>
    public const byte NoteEmpty = 0xFF;

    /// <summary>
    /// 音符バイトの番兵：その位置で音が終わった（離鍵）ことを示す。
    ///
    /// 【実測で確定・2026-09-21】
    /// 同じ音を 0.1/0.25/0.5/1.0/2.0/3.0 秒だけ保持させて記録したところ、
    /// 「実音が現れたスロット」と「0xFE が現れたスロット」の間隔が
    /// 元の音長と一致した（誤差 最大 27ms）。
    ///
    ///   元 0.5秒 → 0.482秒   元 1.0秒 → 0.973秒
    ///   元 2.0秒 → 2.011秒   元 3.0秒 → 3.001秒
    ///
    /// パケットをまたぐ長い音では**同じスロット位置**に現れ、
    /// 同一パケット内で終わる短い音では後ろのスロットに現れる。
    ///
    /// 当初は「保持」と解釈して発音から除外するだけだったが、
    /// それでは音長が受信間隔（約0.49秒）に量子化されてしまっていた。
    /// </summary>
    public const byte NoteOff = 0xFE;

    /// <summary>旧称。意味は <see cref="NoteOff"/> と同じ。</summary>
    public const byte NoteSustain = NoteOff;

    /// <summary>ソロパケットが1回で運ぶ音符数の上限（実機コードの cmp al, 0Ah）。</summary>
    public const int SoloNoteCount = 10;

    /// <summary>合奏パケットが1人あたり運ぶ音符数の上限（実機コードの cmp al, 3Ch）。</summary>
    public const int EnsembleNoteCount = 60;

    /// <summary>合奏パケットに入る演奏者の枠数。</summary>
    public const int EnsembleMemberCount = 8;

    /// <summary>合奏パケットの空き枠を示す CharacterId。</summary>
    public const uint NullActorId = 0xE000_0000;

    /// <summary>
    /// ゲーム内の音符番号を MIDI ノート番号へ変換するオフセット。
    ///
    /// 【実測で確定・2026-09-20】
    /// 他プレイヤーが全37鍵を順に弾いたパケットを記録したところ、
    /// 音符バイトは 24〜60 の連番（ちょうど37音）だった。
    /// FF14 の演奏音域は C3〜C6（MIDI 48〜84）なので、
    ///   24 → 48 (C3),  36 → 60 (C4),  48 → 72 (C5),  60 → 84 (C6)
    /// となる 24 が正解。
    ///
    /// 【重要】自分の押鍵（AgentPerformance +0x60）とは基準が違う。
    /// 押鍵側は 39〜75 で、そちらのずらし量は 9 になる。
    /// 両者を取り違えると 15 半音ずれるので、混同しないこと。
    /// </summary>
    public const int MidiNoteOffset = 24;

    /// <summary>パケット上のいちばん低い音の値（C3）。実測値。</summary>
    public const byte LowestNoteValue = 24;

    /// <summary>パケット上のいちばん高い音の値（C6）。実測値。</summary>
    public const byte HighestNoteValue = 60;

    /// <summary>
    /// 自分の押鍵（AgentPerformance）を MIDI にするずらし量。
    /// パケットとは基準が違うので別に持つ。実測で 39 → C3(48)。
    /// </summary>
    public const int LocalKeyMidiOffset = 9;

    /// <summary>自分の押鍵のいちばん低い値（C3）。実測値。</summary>
    public const int LocalKeyLowest = 39;

    // ---- ソロパケット (SoloReceivedHandler の第2引数が指す先) ----
    // offset 0      : byte  NoteCount        (上限 10)
    // offset 1..10  : byte  NoteNumbers[10]
    // offset 11..20 : byte  NoteTones[10]
    public const int SoloOffsetCount = 0;
    public const int SoloOffsetNotes = 1;
    public const int SoloOffsetTones = 1 + SoloNoteCount; // = 0x0B
    public const int SoloPacketSize = 24;                 // 宣言サイズ（21バイト＋パディング）

    // ---- 合奏パケット (EnsembleReceivedHandler の第2引数が指す先) ----
    //
    // offset 0      : ヘッダ 8バイト
    // offset 8 から : EnsembleCharacterData が 0x80 バイト刻みで 8 個
    //   +0x00 : uint  CharacterId (空き枠は 0xE0000000)
    //   +0x04 : byte  NoteCount   (上限 60)
    //   +0x05 : byte  NoteNumbers[60]
    //   +0x41 : byte  ToneNumbers[60]
    //
    // 【2026-09-21 の誤りと訂正・重要】
    // 生ログで「1人1エントリ・128バイト」に見えたことから
    // **「1パケット＝1人分」と誤認して 8 人分のループを削除した。**
    // その結果、**合奏で1人分しか記録されなくなった**（実機で確認）。
    //
    // 実際は元の実装が「1人分(0x80B)ずつ別エントリで保存」していたため、
    // 8人いれば8エントリ出ていただけ。
    // 同一時刻の8エントリは、**1つのパケットから8人分を展開した結果**であって、
    // 8個のパケットが届いていたのではなかった。
    //
    // 教訓：生ログの見た目（1件あたりの長さ・件数）は
    // **保存側の都合**であって、パケットの構造ではない。
    // 構造を判断するなら保存前の生データか逆アセンブルを見ること。
    public const int EnsembleOffsetMembers = 8;
    public const int EnsembleMemberStride = 0x80;
    public const int EnsembleMemberOffsetId = 0x00;
    public const int EnsembleMemberOffsetCount = 0x04;
    public const int EnsembleMemberOffsetNotes = 0x05;
    public const int EnsembleMemberOffsetTones = 0x41;

    /// <summary>
    /// 生ログを読み直すときに使う、ずれ量の候補。
    ///
    /// 2026-09-21 の一時的な不具合で、
    /// 一部の生ログが 8 バイトずれた状態（120 バイト）で
    /// 保存されてしまった。その分を読み直せるようにするためだけの定数。
    ///
    /// 現在の記録には使わない（保存は常に正しい位置から行う）。
    /// </summary>
    public const int EnsembleLegacyHeaderSize = 8;

    /// <summary>
    /// 合奏の中身が、その位置から始まっているように見えるか。
    ///
    /// 判定は「音符数がちょうど 60」で行う。
    /// これは実データで確認した固定値で、
    /// ずれた位置を読むと、まず 60 にはならない。
    ///
    /// **過去の生ログを読み直すためだけに使う。**
    /// 上記の不具合でずれて保存されたログが残っているため。
    /// </summary>
    public static bool LooksLikeEnsembleBody(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + EnsembleMemberOffsetCount >= data.Length)
            return false;

        return data[offset + EnsembleMemberOffsetCount] == EnsembleNoteCount;
    }

    /// <summary>
    /// 合奏パケット全体の想定サイズ。
    /// ヘッダ 8 バイト ＋ 0x80 バイト × 8 人分。
    /// </summary>
    public const int EnsemblePacketSize =
        EnsembleOffsetMembers + EnsembleMemberStride * EnsembleMemberCount; // = 0x408

    /// <summary>
    /// 読み取りに最低限必要なバイト数。
    ///
    /// 音色の最後のバイト (+0x41 + 59) までが 0x7C。
    /// ここに届かないパケットは形式が違うので触らない。
    /// </summary>
    public const int EnsembleRequiredBytes =
        EnsembleMemberOffsetTones + EnsembleNoteCount; // = 0x7D

    /// <summary>
    /// ソロパケットの読み取りに最低限必要なバイト数。
    /// 音色の最後 (+0x0B + 9) までが 0x14。
    /// </summary>
    public const int SoloRequiredBytes =
        SoloOffsetTones + SoloNoteCount; // = 0x15

    /// <summary>
    /// 生の音符バイトが「実際に鳴っている音」かどうか。
    /// 番兵（0xFF/0xFE）を弾く。
    /// </summary>
    public static bool IsPlayableNote(byte raw)
        => raw != NoteEmpty && raw != NoteSustain;

    /// <summary>
    /// 生の音符バイトを MIDI ノート番号へ。
    /// 変換できない場合は -1 を返す（呼び出し側で捨てる）。
    /// </summary>
    public static int ToMidiNote(byte raw)
    {
        if (!IsPlayableNote(raw))
            return -1;

        // 演奏できるのは 37 鍵（実測で値 39〜75）。
        // この範囲の外は音符ではないので破棄する。
        if (raw < LowestNoteValue || raw > HighestNoteValue)
            return -1;

        var midi = raw + MidiNoteOffset;

        // 念のため MIDI の有効範囲も確認する。
        if (midi is < 0 or > 127)
            return -1;

        return midi;
    }
}
