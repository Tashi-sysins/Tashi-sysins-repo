// このファイルの AgentPerformance の構造（各フィールドのオフセット）は、
// MidiBard プロジェクト (Copyright (C) 2022 akira0245) の
// Midibard/Managers/Agents/AgentPerformance.cs に由来します。
//
// MidiBard は GNU Affero General Public License v3.0 で配布されています。
// 原作者 akira0245 の表示と、MidiBard プロジェクトで使われていたものである旨を
// 明示することが、元のライセンスで求められています。
//
// 元ライセンス: https://github.com/akira0245/MidiBard/blob/master/LICENSE

using System;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace BardPerformanceRecorder.Game;

/// <summary>
/// 「自分の」演奏状態を読む。
///
/// 何に使うか：
/// 他人の演奏を記録するとき、答え合わせの材料が無いと
/// 「音高がずれているのか、そもそも読み方が違うのか」が切り分けられない。
/// 自分で弾けば「何を弾いたか」が確実に分かるので、
/// 受信パケットと突き合わせて読み方を検証できる。
///
/// 注意：ここで使うオフセットは MidiBard2 由来で、
/// 現行クライアントでの妥当性は未確認。そのため
/// <see cref="LooksValid"/> で値の妥当性を自己検査し、
/// おかしければ「取得できない」として扱う。
/// </summary>
public sealed unsafe class LocalPerformanceReader
{
    /// <summary>押鍵なしを表す番兵。</summary>
    public const int NoNotePressed = Recording.LocalPerformanceReaderConstants.NoNotePressed;

    private const int OffsetInPerformanceMode = 0x20;
    private const int OffsetPerformanceTimer1 = 0x38;
    private const int OffsetPerformanceTimer2 = 0x40;
    private const int OffsetNoteOffset = 0x5C;
    private const int OffsetCurrentPressingNote = 0x60;
    private const int OffsetOctaveOffset = 0xFC;
    private const int OffsetGroupTone = 0x1D8;

    /// <summary>AgentPerformanceMode の AgentId。</summary>
    private const AgentId PerformanceAgentId = AgentId.PerformanceMode;

    /// <summary>直近に読めた値。画面表示と記録に使う。</summary>
    public bool Available { get; private set; }
    public bool InPerformanceMode { get; private set; }
    public int CurrentPressingNote { get; private set; } = NoNotePressed;
    public int NoteOffset { get; private set; }
    public int OctaveOffset { get; private set; }
    public int GroupTone { get; private set; }
    public long PerformanceTimer1 { get; private set; }
    public long PerformanceTimer2 { get; private set; }

    /// <summary>Dalamud が公式に持っている「演奏中か」の判定。</summary>
    public bool IsPerformingByUiModule { get; private set; }

    /// <summary>自己検査に落ちた回数。オフセットが古い疑いの目安。</summary>
    public int ImplausibleCount { get; private set; }

    /// <summary>
    /// 読んだ値が想定範囲に収まっているか。
    /// false でも Available は true になりうる（生値は見せる）。
    /// </summary>
    public bool Plausible { get; private set; }

    // ---- 生の値（妥当性に関わらずそのまま。原因調査用） ----
    public int RawPressingNote { get; private set; }
    public int RawNoteOffset { get; private set; }
    public int RawOctaveOffset { get; private set; }
    public int RawGroupTone { get; private set; }
    public byte RawInPerformanceByte { get; private set; }
    public IntPtr AgentAddress { get; private set; }

    /// <summary>Agent そのものが取れたか。</summary>
    public bool AgentFound => AgentAddress != IntPtr.Zero;

    public string LastError { get; private set; }

    /// <summary>押鍵しているか。</summary>
    public bool NotePressed => Available && CurrentPressingNote != NoNotePressed;

    private IntPtr GetAgent()
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null)
            return IntPtr.Zero;

        var agentModule = uiModule->GetAgentModule();
        if (agentModule == null)
            return IntPtr.Zero;

        var agent = agentModule->GetAgentByInternalId(PerformanceAgentId);
        return (IntPtr)agent;
    }

    /// <summary>
    /// 読み取った値がもっともらしいか確かめる。
    ///
    /// オフセットが現行クライアントでずれていると、
    /// でたらめな値を「自分が押した鍵」として記録してしまう。
    /// それは何も記録しないより有害なので、範囲で弾く。
    /// </summary>
    private static bool LooksValid(int pressingNote, int octaveOffset, int noteOffset)
    {
        // 押鍵の値は 0 起点ではない。
        // 実測（2026-09-20・ver 2026.09.15）では 39〜43 を観測した。
        // 0〜36 と決め打つと、押しているのに「押していない」と誤判定する。
        // ここでは MIDI ノート番号として成立する範囲まで広く許容する。
        var noteOk = pressingNote == NoNotePressed || pressingNote is >= 0 and <= 127;

        // オクターブ・音符のずらし量が極端な値なら、読み違えている。
        var octaveOk = octaveOffset is >= -5 and <= 5;
        var offsetOk = noteOffset is >= -64 and <= 64;

        return noteOk && octaveOk && offsetOk;
    }

    /// <summary>
    /// AgentPerformance の先頭からのメモリをそのまま写す。
    ///
    /// なぜ必要か：
    /// 「押鍵は +0x60」という想定が現行クライアントで崩れている場合、
    /// 決め打ちで読んでも永遠に取れない。実際のメモリを見て、
    /// 押した瞬間にどこが変化するかを突き止めるための材料にする。
    /// </summary>
    public byte[] DumpAgentMemory(int length = 0x200)
    {
        try
        {
            var agent = GetAgent();
            if (agent == IntPtr.Zero)
                return null;

            var p = (byte*)agent;
            var buf = new byte[length];
            for (var i = 0; i < length; i++)
                buf[i] = p[i];

            return buf;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 毎フレーム呼ぶ。読めなければ Available=false にするだけで、例外は出さない。
    /// </summary>
    public void Update()
    {
        try
        {
            var uiModule = UIModule.Instance();
            IsPerformingByUiModule = uiModule != null && uiModule->IsPerforming();

            var agent = GetAgent();
            if (agent == IntPtr.Zero)
            {
                Available = false;
                AgentAddress = IntPtr.Zero;
                LastError = "AgentPerformance を取得できません（演奏モードに入っていない可能性）。";
                return;
            }

            var p = (byte*)agent;

            var inMode = p[OffsetInPerformanceMode] != 0;
            var pressing = *(int*)(p + OffsetCurrentPressingNote);
            var noteOffset = *(int*)(p + OffsetNoteOffset);
            var octave = *(int*)(p + OffsetOctaveOffset);
            var tone = *(int*)(p + OffsetGroupTone);
            var t1 = *(long*)(p + OffsetPerformanceTimer1);
            var t2 = *(long*)(p + OffsetPerformanceTimer2);

            // 生の値は、妥当性に関わらず必ず残す。
            // 「想定外だから見せない」では原因調査ができなくなる。
            RawPressingNote = pressing;
            RawNoteOffset = noteOffset;
            RawOctaveOffset = octave;
            RawGroupTone = tone;
            RawInPerformanceByte = p[OffsetInPerformanceMode];
            AgentAddress = agent;

            if (!LooksValid(pressing, octave, noteOffset))
            {
                ImplausibleCount++;
                Plausible = false;
                LastError = "AgentPerformance から読んだ値が想定範囲外です。"
                            + "オフセットが現行クライアントと合っていない可能性があります。";
            }
            else
            {
                Plausible = true;
                LastError = null;
            }

            // Agent が取れている限り Available は true にする。
            // 値が妥当かどうかは Plausible で別に持つ。
            InPerformanceMode = inMode;
            CurrentPressingNote = pressing;
            NoteOffset = noteOffset;
            OctaveOffset = octave;
            GroupTone = tone;
            PerformanceTimer1 = t1;
            PerformanceTimer2 = t2;
            Available = true;
            LastError = null;
        }
        catch (Exception e)
        {
            Available = false;
            LastError = e.Message;
        }
    }
}
