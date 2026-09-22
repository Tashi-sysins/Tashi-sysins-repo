using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BardPerformanceRecorder.Recording;

/// <summary>
/// 「自分が押した鍵」の時系列。
///
/// 何に使うか：
/// 自分で弾いたものは、何を弾いたかが確実に分かる。
/// これを受信パケットと突き合わせれば、
/// 「パケットの値 → 実際の音高」の対応を実測で確かめられる。
/// 音高のずらし量（MidiNoteOffset）を決めるための正解データになる。
/// </summary>
public sealed class LocalKeyLog
{
    public readonly struct Entry
    {
        /// <summary>記録開始からの経過時間。</summary>
        public readonly TimeSpan At;

        /// <summary>AgentPerformance が返す押鍵番号（0〜36）。</summary>
        public readonly int PressedNote;

        /// <summary>そのときのオクターブずらし量。</summary>
        public readonly int OctaveOffset;

        /// <summary>そのときの音符ずらし量。</summary>
        public readonly int NoteOffset;

        /// <summary>そのときの音色。</summary>
        public readonly int GroupTone;

        /// <summary>押したか離したか。</summary>
        public readonly bool IsPress;

        public Entry(TimeSpan at, int pressedNote, int octaveOffset,
            int noteOffset, int groupTone, bool isPress)
        {
            At = at;
            PressedNote = pressedNote;
            OctaveOffset = octaveOffset;
            NoteOffset = noteOffset;
            GroupTone = groupTone;
            IsPress = isPress;
        }
    }

    private readonly List<Entry> entries = new();
    private readonly object gate = new();

    private int lastNote = LocalPerformanceReaderConstants.NoNotePressed;

    public int Count
    {
        get { lock (gate) return entries.Count; }
    }

    /// <summary>押鍵の総数（離鍵を除く）。</summary>
    public int PressCount
    {
        get { lock (gate) return entries.Count(e => e.IsPress); }
    }

    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            lastNote = LocalPerformanceReaderConstants.NoNotePressed;
        }
    }

    /// <summary>
    /// 毎フレーム、現在の押鍵状態を渡す。変化したときだけ記録する。
    /// </summary>
    public void Observe(TimeSpan at, int currentNote, int octaveOffset,
        int noteOffset, int groupTone)
    {
        lock (gate)
        {
            if (currentNote == lastNote)
                return;

            var none = LocalPerformanceReaderConstants.NoNotePressed;

            // 直前に押していた鍵を離した
            if (lastNote != none)
                entries.Add(new Entry(at, lastNote, octaveOffset, noteOffset, groupTone, false));

            // 新しく押した鍵
            if (currentNote != none)
                entries.Add(new Entry(at, currentNote, octaveOffset, noteOffset, groupTone, true));

            lastNote = currentNote;
        }
    }

    public List<Entry> Snapshot()
    {
        lock (gate) return new List<Entry>(entries);
    }

    public static void SaveCsv(IReadOnlyList<Entry> entries, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        w.WriteLine("seconds,event,pressed_note,octave_offset,note_offset,group_tone");

        foreach (var e in entries)
            w.WriteLine(string.Join(",",
                e.At.TotalSeconds.ToString("F4", CultureInfo.InvariantCulture),
                e.IsPress ? "press" : "release",
                e.PressedNote.ToString(CultureInfo.InvariantCulture),
                e.OctaveOffset.ToString(CultureInfo.InvariantCulture),
                e.NoteOffset.ToString(CultureInfo.InvariantCulture),
                e.GroupTone.ToString(CultureInfo.InvariantCulture)));
    }
}

/// <summary>
/// LocalKeyLog が Game 層に依存しないようにするための定数置き場。
/// （検証プロジェクトから Dalamud 抜きで使えるようにするため）
/// </summary>
public static class LocalPerformanceReaderConstants
{
    public const int NoNotePressed = -100;
}
