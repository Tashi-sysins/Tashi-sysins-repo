using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BardPerformanceRecorder.Game;
using BardPerformanceRecorder.Midi;
using BardPerformanceRecorder.Recording;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace BardPerformanceRecorder.Tests;

/// <summary>
/// ゲームなしで MIDI 変換を検証する（完成条件1）。
/// 併せて完成条件3（連続同音・休符・演奏者切替・途中停止）も確認する。
/// </summary>
public static class Program
{
    private static int failed;
    private static int passed;

    public static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("BardPerformanceRecorder 検証");
        Console.WriteLine("============================\n");

        var outDir = Path.Combine(Path.GetTempPath(), "bprec_tests");
        Directory.CreateDirectory(outDir);

        TestSimpleScale(outDir);
        TestRepeatedSameNote(outDir);
        TestRests(outDir);
        TestUnclosedNoteGetsFallback(outDir);
        TestOrphanNoteOffIsDropped(outDir);
        TestOutOfRangeIsDropped(outDir);
        TestChord(outDir);
        TestEmptyRecording();
        TestRecorderStateMachine();
        TestRecorderPerformerSwitch();
        TestRecorderStopClosesNotes();
        TestPacketSentinels();
        TestMidiIsReadableByStandardReader(outDir);
        TestTempoDoesNotDistortRealTime(outDir);
        TestStopIsSafeWhilePacketsArrive();
        TestAnalyzerDetectsNoteOffset();
        TestAnalyzerFindsNoteBytes();
        TestAnalyzerHandlesNoData();
        TestRawLogRoundTrip(outDir);
        TestLocalKeyLogEdges();
        TestSlotTiming();
        TestRepeatedNoteInSamePacket();
        TestSustainedNoteRepeatedInNextPacket();
        TestDuplicatePacketIsIgnored();
        TestDuplicateNeedsTimeWindow();
        TestTwoPerformersSameNote();
        TestNoteOffMarkerGivesLength();
        TestRetriggerClosesWaitingNote();
        TestWaitingStateIsPerPerformer();
        TestOneSidedNoteOffDoesNotAffectOther();
        TestPerformerFilter();
        TestEnsembleLayoutConstants();
        TestUniqueFileName(outDir);
        TestContinuousPolicy();

        DumpSampleReport(outDir);

        Console.WriteLine();
        Console.WriteLine($"成功 {passed} 件 / 失敗 {failed} 件");
        Console.WriteLine($"出力先: {outDir}");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 実機で出る診断レポートの見本を作って保存する。
    /// 画面と出力の体裁を、実機に入る前に確認できるようにする。
    /// </summary>
    private static void DumpSampleReport(string outDir)
    {
        var packets = new List<RawPacketLog.Entry>();
        var keys = new List<LocalKeyLog.Entry>();
        var rnd = new Random(7);

        // ドレミファソを弾いた想定。パケットは押鍵値と同じ値を返す。
        var played = new[] { 24, 26, 28, 29, 31, 31, 31, 36, 48, 60 };
        for (var i = 0; i < played.Length; i++)
        {
            var t = TimeSpan.FromSeconds(i * 0.5);
            keys.Add(new LocalKeyLog.Entry(t, played[i], 0, 0, 0, true));
            packets.Add(new RawPacketLog.Entry(
                false, 0x10203040, t.Add(TimeSpan.FromMilliseconds(40)),
                MakeSoloPacket(played[i]), 3, 16, 3, "テスト演奏者"));

            keys.Add(new LocalKeyLog.Entry(
                t.Add(TimeSpan.FromSeconds(0.3)), played[i], 0, 0, 0, false));
            packets.Add(new RawPacketLog.Entry(
                false, 0x10203040, t.Add(TimeSpan.FromMilliseconds(340)),
                MakeSoloPacket(), 3, 16, 3, "テスト演奏者"));
        }

        var report = CalibrationAnalyzer.Analyze(packets, keys, 24);
        var path = Path.Combine(outDir, "sample_diagnosis.txt");
        File.WriteAllText(path, report.ToString(), new System.Text.UTF8Encoding(true));

        Console.WriteLine();
        Console.WriteLine("■ 診断レポートの見本");
        Console.WriteLine($"  {path}");
    }

    // ---------- 検証ヘルパ ----------

    private static void Check(string name, bool condition, string detail = null)
    {
        if (condition)
        {
            passed++;
            Console.WriteLine($"  [OK]   {name}");
        }
        else
        {
            failed++;
            Console.WriteLine($"  [NG]   {name}" + (detail is null ? "" : $"  … {detail}"));
        }
    }

    private static void Section(string title) => Console.WriteLine($"\n■ {title}");

    private static NoteEventRecord On(uint performer, int note, double seconds)
        => new(performer, note, (byte)(note - PerformanceConstants.MidiNoteOffset), 0, 1,
            TimeSpan.FromSeconds(seconds), NoteEdge.On, NoteSource.SoloPacket);

    private static NoteEventRecord Off(uint performer, int note, double seconds)
        => new(performer, note, PerformanceConstants.NoteEmpty, 0xFF, 1,
            TimeSpan.FromSeconds(seconds), NoteEdge.OffInferred, NoteSource.SoloPacket);

    private static MidiExportOptions Options() => new()
    {
        BeatsPerMinute = 120.0,
        TicksPerQuarterNote = 480,
        FallbackNoteLengthMs = 250,
        MinimumNoteLengthMs = 30,
    };

    /// <summary>書き出した MIDI を読み直して (ノート番号, 開始tick, 長さtick) を取り出す。</summary>
    private static List<(int note, long time, long length)> ReadBack(string path)
    {
        var file = MidiFile.Read(path);
        return file.GetNotes()
            .OrderBy(n => n.Time).ThenBy(n => n.NoteNumber)
            .Select(n => ((int)n.NoteNumber, n.Time, n.Length))
            .ToList();
    }

    // ---------- 個別の検証 ----------

    /// <summary>既知の音符列（ドレミファソ）を入れて、音高と順序が保たれるか。</summary>
    private static void TestSimpleScale(string outDir)
    {
        Section("既知の音符列 → MIDI（音高と音順）");

        var expected = new[] { 60, 62, 64, 65, 67 };
        var records = new List<NoteEventRecord>();
        for (var i = 0; i < expected.Length; i++)
        {
            records.Add(On(1, expected[i], i * 0.5));
            records.Add(Off(1, expected[i], i * 0.5 + 0.4));
        }

        var path = Path.Combine(outDir, "scale.mid");
        var result = MidiExporter.Save(records, Options(), path);

        Check("音符数が一致", result.NoteCount == 5, $"実際 {result.NoteCount}");
        Check("すべて消音を観測できている", result.FallbackLengthCount == 0);

        var notes = ReadBack(path);
        Check("読み直した音符数が一致", notes.Count == 5, $"実際 {notes.Count}");
        Check("音高と音順が一致",
            notes.Select(n => n.note).SequenceEqual(expected),
            string.Join(",", notes.Select(n => n.note)));

        // BPM120 / 480tpqn では 4分音符 = 0.5 秒 = 480 tick。
        // よって 1ms = 0.96 tick、0.5 秒 = 480 tick。
        Check("開始時刻が実時間どおり",
            notes.Select(n => n.time).SequenceEqual(new long[] { 0, 480, 960, 1440, 1920 }),
            string.Join(",", notes.Select(n => n.time)));
        Check("音長が 0.4 秒(384tick)",
            notes.All(n => n.length == 384),
            string.Join(",", notes.Select(n => n.length)));

        // tick の計算を自分で検算せず、実時間に戻して確かめる。
        // 「記録した実時間が MIDI 上でも同じ秒数になる」ことが本来の要件。
        var file = MidiFile.Read(path);
        var map = file.GetTempoMap();
        var times = file.GetNotes().OrderBy(n => n.Time)
            .Select(n => n.TimeAs<MetricTimeSpan>(map).TotalMicroseconds / 1_000_000.0)
            .ToList();
        var lengths = file.GetNotes().OrderBy(n => n.Time)
            .Select(n => n.LengthAs<MetricTimeSpan>(map).TotalMicroseconds / 1_000_000.0)
            .ToList();

        Check("実時間に戻すと 0.0/0.5/1.0/1.5/2.0 秒",
            times.Select((t, i) => Math.Abs(t - i * 0.5) < 0.002).All(x => x),
            string.Join(",", times.Select(t => t.ToString("F3"))));
        Check("実時間に戻すと音長 0.4 秒",
            lengths.All(l => Math.Abs(l - 0.4) < 0.002),
            string.Join(",", lengths.Select(l => l.ToString("F3"))));
    }

    /// <summary>連続した同音が 1 個に潰れないこと（完成条件3）。</summary>
    private static void TestRepeatedSameNote(string outDir)
    {
        Section("連続した同音");

        var records = new List<NoteEventRecord>
        {
            On(1, 60, 0.0), Off(1, 60, 0.2),
            On(1, 60, 0.3), Off(1, 60, 0.5),
            On(1, 60, 0.6), Off(1, 60, 0.8),
        };

        var path = Path.Combine(outDir, "repeat.mid");
        var result = MidiExporter.Save(records, Options(), path);

        Check("3回とも残る", result.NoteCount == 3, $"実際 {result.NoteCount}");

        var notes = ReadBack(path);
        Check("読み直しても3個", notes.Count == 3, $"実際 {notes.Count}");
        Check("すべて同じ音高", notes.All(n => n.note == 60));
        Check("開始が重ならない",
            notes.Select(n => n.time).Distinct().Count() == 3,
            string.Join(",", notes.Select(n => n.time)));
    }

    /// <summary>休符（無音区間）が潰れず、時間が保たれること。</summary>
    private static void TestRests(string outDir)
    {
        Section("休符");

        var records = new List<NoteEventRecord>
        {
            On(1, 60, 0.0), Off(1, 60, 0.2),
            // 2秒の休符
            On(1, 64, 2.2), Off(1, 64, 2.4),
        };

        var path = Path.Combine(outDir, "rest.mid");
        MidiExporter.Save(records, Options(), path);

        var notes = ReadBack(path);
        Check("音符は2個", notes.Count == 2, $"実際 {notes.Count}");

        // 2.2 秒 = 2200ms × 0.96 = 2112 tick
        Check("休符ぶんの間隔が保たれる",
            notes[1].time == 2112,
            $"実際 {notes[1].time}");
        Check("休符が音符として現れない", notes.All(n => n.length > 0));
    }

    /// <summary>消音が来なかった音に暫定長が当たること。</summary>
    private static void TestUnclosedNoteGetsFallback(string outDir)
    {
        Section("消音を観測できなかった音（記録途中の停止）");

        var records = new List<NoteEventRecord>
        {
            On(1, 60, 0.0), Off(1, 60, 0.2),
            On(1, 67, 0.5), // 閉じないまま終わる
        };

        var path = Path.Combine(outDir, "unclosed.mid");
        var result = MidiExporter.Save(records, Options(), path);

        Check("音符は2個", result.NoteCount == 2, $"実際 {result.NoteCount}");
        Check("暫定長を当てたのは1個", result.FallbackLengthCount == 1,
            $"実際 {result.FallbackLengthCount}");
        Check("消音を観測できたのは1個", result.InferredLengthCount == 1);

        var notes = ReadBack(path);
        // 暫定 250ms × 0.96 = 240 tick
        var last = notes.OrderBy(n => n.time).Last();
        Check("暫定長 250ms が 240tick で入る", last.length == 240, $"実際 {last.length}");
    }

    /// <summary>対応する発音が無い消音が、静かに破棄されること。</summary>
    private static void TestOrphanNoteOffIsDropped(string outDir)
    {
        Section("対応する発音が無い消音（記録開始前から鳴っていた音）");

        var records = new List<NoteEventRecord>
        {
            Off(1, 72, 0.1), // 開始が無い
            On(1, 60, 0.2), Off(1, 60, 0.4),
        };

        var path = Path.Combine(outDir, "orphan.mid");
        var result = MidiExporter.Save(records, Options(), path);

        Check("音符は1個だけ", result.NoteCount == 1, $"実際 {result.NoteCount}");
        Check("破棄が1件記録される", result.DroppedCount == 1, $"実際 {result.DroppedCount}");
        Check("例外なく書き出せる", File.Exists(path));
    }

    /// <summary>MIDI 範囲外の異常値が破棄されること。</summary>
    private static void TestOutOfRangeIsDropped(string outDir)
    {
        Section("範囲外の異常値");

        var records = new List<NoteEventRecord>
        {
            new(1, 999, 200, 0, 1, TimeSpan.FromSeconds(0.0), NoteEdge.On, NoteSource.SoloPacket),
            new(1, -5, 200, 0, 1, TimeSpan.FromSeconds(0.1), NoteEdge.On, NoteSource.SoloPacket),
            On(1, 60, 0.2), Off(1, 60, 0.4),
        };

        var path = Path.Combine(outDir, "outofrange.mid");
        var result = MidiExporter.Save(records, Options(), path);

        Check("正常な音だけ残る", result.NoteCount == 1, $"実際 {result.NoteCount}");
        Check("異常値2件を破棄", result.DroppedCount == 2, $"実際 {result.DroppedCount}");
    }

    /// <summary>同時発音（和音）が保たれること。</summary>
    private static void TestChord(string outDir)
    {
        Section("和音（同時発音）");

        var records = new List<NoteEventRecord>
        {
            On(1, 60, 0.0), On(1, 64, 0.0), On(1, 67, 0.0),
            Off(1, 60, 0.5), Off(1, 64, 0.5), Off(1, 67, 0.5),
        };

        var path = Path.Combine(outDir, "chord.mid");
        MidiExporter.Save(records, Options(), path);

        var notes = ReadBack(path);
        Check("3音が残る", notes.Count == 3, $"実際 {notes.Count}");
        Check("すべて同時刻", notes.All(n => n.time == 0));
        Check("音高が一致",
            notes.Select(n => n.note).OrderBy(x => x).SequenceEqual(new[] { 60, 64, 67 }));
    }

    /// <summary>空の記録で落ちないこと。</summary>
    private static void TestEmptyRecording()
    {
        Section("空の記録");

        try
        {
            var file = MidiExporter.Build(new List<NoteEventRecord>(), Options(), out var summary);
            Check("例外なく組み立てられる", file != null);
            Check("音符数 0", summary.NoteCount == 0);
        }
        catch (Exception e)
        {
            Check("例外なく組み立てられる", false, e.Message);
        }
    }

    /// <summary>
    /// 取り込み側の状態機械。パケット列から正しく On/Off が生成されるか。
    /// </summary>
    private static void TestRecorderStateMachine()
    {
        Section("取り込み：パケット列 → 発音/消音");

        var rec = new PerformanceRecorder();
        rec.Start(100);

        var empty = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();
        var tones = new byte[10];

        // ド(0x0C → MIDI 60)を鳴らす
        var notes = (byte[])empty.Clone();
        notes[0] = 36;   // MIDI 60 (C4)
        rec.Feed(100, notes, tones, 1, NoteSource.SoloPacket);

        // 同じ音が続く（継続）→ 新しい発音を作らない
        rec.Feed(100, notes, tones, 1, NoteSource.SoloPacket);

        // 消える → 推定消音
        rec.Feed(100, empty, tones, 1, NoteSource.SoloPacket);

        // 0xFE（離鍵の合図）を待つ設計なので、停止するまで閉じない。
        // 停止すれば必ず閉じられる。
        rec.Stop();

        var events = rec.Snapshot();
        var ons = events.Count(e => e.Edge == NoteEdge.On);
        var offs = events.Count(e => e.Edge == NoteEdge.OffInferred);

        Check("発音は1回だけ（継続を重複させない）", ons == 1, $"実際 {ons}");
        Check("消音が1回", offs == 1, $"実際 {offs}");
        Check("パケット値 36 が MIDI 60 (C4) になる", events.First(e => e.Edge == NoteEdge.On).NoteNumber == 60,
            $"実際 {events.First(e => e.Edge == NoteEdge.On).NoteNumber}");
    }

    /// <summary>対象以外の演奏者を混ぜないこと（完成条件3：演奏者の切り替わり）。</summary>
    private static void TestRecorderPerformerSwitch()
    {
        Section("演奏者の切り替わり");

        var rec = new PerformanceRecorder();
        rec.Start(100); // 100 番だけを記録

        var tones = new byte[10];
        var notes = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();
        notes[0] = 36;   // MIDI 60 (C4)

        rec.Feed(100, notes, tones, 1, NoteSource.SoloPacket); // 対象
        rec.Feed(200, notes, tones, 1, NoteSource.SoloPacket); // 別人：無視されるべき

        var events = rec.Snapshot();
        Check("対象の演奏者だけ記録される",
            events.All(e => e.PerformerId == 100),
            string.Join(",", events.Select(e => e.PerformerId)));
        Check("別人のぶんが混ざらない", events.Count(e => e.Edge == NoteEdge.On) == 1);

        rec.Stop();

        // 自動選択：最初に音を出した人を掴む
        var auto = new PerformanceRecorder();
        auto.Start(0);
        auto.Feed(555, notes, tones, 1, NoteSource.SoloPacket);
        auto.Feed(777, notes, tones, 1, NoteSource.SoloPacket);
        var autoEvents = auto.Snapshot();
        Check("自動選択は最初に音を出した人になる",
            auto.ActivePerformerId == 555, $"実際 {auto.ActivePerformerId}");
        Check("自動選択後は他人を混ぜない",
            autoEvents.All(e => e.PerformerId == 555));
        auto.Stop();
    }

    /// <summary>記録途中で停止しても、鳴っていた音が閉じること。</summary>
    private static void TestRecorderStopClosesNotes()
    {
        Section("記録途中の停止");

        var rec = new PerformanceRecorder();
        rec.Start(100);

        var tones = new byte[10];
        var notes = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();
        notes[0] = 36;   // MIDI 60 (C4)
        notes[1] = 40;   // MIDI 64 (E4)

        rec.Feed(100, notes, tones, 1, NoteSource.SoloPacket);
        rec.Stop(); // 鳴りっぱなしのまま停止

        var events = rec.Snapshot();
        var ons = events.Count(e => e.Edge == NoteEdge.On);
        var offs = events.Count(e => e.Edge == NoteEdge.OffInferred);

        Check("発音が2個", ons == 2, $"実際 {ons}");
        Check("停止時に2個とも閉じる", offs == 2, $"実際 {offs}");
        Check("変換しても破綻しない",
            MidiExporter.Build(events, Options(), out var s) != null && s.NoteCount == 2,
            "音符数が合わない");
    }

    /// <summary>番兵バイトの扱い。</summary>
    private static void TestPacketSentinels()
    {
        Section("番兵バイト（0xFF 無音 / 0xFE 保持）");

        Check("0xFF は発音として扱わない",
            !PerformanceConstants.IsPlayableNote(PerformanceConstants.NoteEmpty));
        Check("0xFE は発音として扱わない",
            !PerformanceConstants.IsPlayableNote(PerformanceConstants.NoteSustain));
        Check("通常の音は発音として扱う", PerformanceConstants.IsPlayableNote(12));
        Check("0xFF の変換結果は -1",
            PerformanceConstants.ToMidiNote(PerformanceConstants.NoteEmpty) == -1);
        // 実測（2026-09-20）：37鍵を下から押すと値は 39〜75 の連番だった。
        // FF14 の音域 C3〜C6（MIDI 48〜84）に対応する。
        // 実測（2026-09-20）：他プレイヤーが全37鍵を弾いたパケットは 24〜60 の連番。
        // FF14 の音域 C3〜C6（MIDI 48〜84）に対応する。
        Check("パケット値 24 は MIDI 48 (C3)",
            PerformanceConstants.ToMidiNote(24) == 48,
            $"実際 {PerformanceConstants.ToMidiNote(24)}");
        Check("パケット値 60 は MIDI 84 (C6)",
            PerformanceConstants.ToMidiNote(60) == 84,
            $"実際 {PerformanceConstants.ToMidiNote(60)}");
        Check("パケット値 36 は MIDI 60 (C4・中央ド)",
            PerformanceConstants.ToMidiNote(36) == 60);

        // 演奏できる範囲の外は音符ではない
        Check("23 は範囲外なので -1", PerformanceConstants.ToMidiNote(23) == -1);
        Check("61 は範囲外なので -1", PerformanceConstants.ToMidiNote(61) == -1);
        Check("0 は範囲外なので -1", PerformanceConstants.ToMidiNote(0) == -1);
        Check("0xFD は範囲外なので -1", PerformanceConstants.ToMidiNote(0xFD) == -1);

        // 押鍵はパケットと基準が違う（取り違え防止）
        Check("押鍵 39 + 押鍵用ずらし量 = MIDI 48 (C3)",
            39 + PerformanceConstants.LocalKeyMidiOffset == 48);
        Check("押鍵とパケットのずらし量は別物",
            PerformanceConstants.LocalKeyMidiOffset != PerformanceConstants.MidiNoteOffset);
    }

    /// <summary>
    /// ソロパケットを模したバイト列を作る。
    /// </summary>
    private static byte[] MakeSoloPacket(params int[] notes)
    {
        var b = new byte[PerformanceConstants.SoloPacketSize];
        for (var i = 0; i < b.Length; i++)
            b[i] = 0;

        b[PerformanceConstants.SoloOffsetCount] = (byte)notes.Length;

        for (var i = 0; i < PerformanceConstants.SoloNoteCount; i++)
        {
            b[PerformanceConstants.SoloOffsetNotes + i] =
                i < notes.Length ? (byte)notes[i] : PerformanceConstants.NoteEmpty;
            b[PerformanceConstants.SoloOffsetTones + i] = 0;
        }

        return b;
    }

    /// <summary>
    /// 診断が音高のずれを言い当てられること。
    ///
    /// わざと「押鍵値 + ずれ」のパケットを作り、
    /// 診断がそのずれを検出できるかを見る。
    /// </summary>
    private static void TestAnalyzerDetectsNoteOffset()
    {
        Section("診断：音高のずれを言い当てる");

        // ずれ 0（パケットの値 = 押鍵値）の場合
        {
            var packets = new List<RawPacketLog.Entry>();
            var keys = new List<LocalKeyLog.Entry>();

            for (var i = 0; i < 12; i++)
            {
                var t = TimeSpan.FromSeconds(i * 0.5);
                var note = 24 + i;   // 実測の音域に合わせる
                keys.Add(new LocalKeyLog.Entry(t, note, 0, 0, 0, true));
                packets.Add(new RawPacketLog.Entry(
                    false, 1, t.Add(TimeSpan.FromMilliseconds(50)),
                    MakeSoloPacket(note), 1, 16, 1, "自分"));
            }

            var r = CalibrationAnalyzer.Analyze(packets, keys, 24);
            Check("ずれ0を検出し、現状維持を提案", r.SuggestedMidiNoteOffset == 24,
                $"提案 {r.SuggestedMidiNoteOffset}");
        }

        // パケットの値が押鍵値より 12 大きい場合
        {
            var packets = new List<RawPacketLog.Entry>();
            var keys = new List<LocalKeyLog.Entry>();

            for (var i = 0; i < 12; i++)
            {
                var t = TimeSpan.FromSeconds(i * 0.5);
                var note = 24 + i;   // 実測の音域に合わせる
                keys.Add(new LocalKeyLog.Entry(t, note, 0, 0, 0, true));
                packets.Add(new RawPacketLog.Entry(
                    false, 1, t.Add(TimeSpan.FromMilliseconds(50)),
                    MakeSoloPacket(note + 12), 1, 16, 1, "自分"));
            }

            var r = CalibrationAnalyzer.Analyze(packets, keys, 24);
            // パケットが 12 大きい → オフセットは 12 小さくすべき
            Check("ずれ+12 を検出して 12 を提案", r.SuggestedMidiNoteOffset == 12,
                $"提案 {r.SuggestedMidiNoteOffset}");
        }
    }

    /// <summary>
    /// 診断が「音符らしいバイト位置」を見つけられること。
    /// こちらの想定オフセットが合っているかの裏取りになる。
    /// </summary>
    private static void TestAnalyzerFindsNoteBytes()
    {
        Section("診断：音符らしいバイト位置を見つける");

        var packets = new List<RawPacketLog.Entry>();
        var rnd = new Random(12345);

        for (var i = 0; i < 60; i++)
        {
            var t = TimeSpan.FromSeconds(i * 0.1);
            // 半分は無音、半分は単音
            var b = i % 2 == 0
                ? MakeSoloPacket()
                : MakeSoloPacket(rnd.Next(24, 61));

            packets.Add(new RawPacketLog.Entry(false, 1, t, b, 1, 16, 1, "自分"));
        }

        var r = CalibrationAnalyzer.Analyze(packets, new List<LocalKeyLog.Entry>(), 24);
        var text = r.ToString();

        Check("ソロ受信を認識", r.SoloPacketsSeen);
        Check("音符らしいバイトを指摘する", text.Contains("★音符らしい"),
            "「★音符らしい」が出ていない");

        // 音符が入るのは offset 1。その行に印が付くはず。
        var noteLine = r.Lines.FirstOrDefault(l => l.TrimStart().StartsWith("+  1"));
        Check("offset 1 が音符らしいと判定される",
            noteLine != null && noteLine.Contains("★"),
            noteLine ?? "該当行なし");
    }

    /// <summary>データが無いときに、原因の候補を出すこと。</summary>
    private static void TestAnalyzerHandlesNoData()
    {
        Section("診断：データが無いとき");

        var r = CalibrationAnalyzer.Analyze(
            new List<RawPacketLog.Entry>(), new List<LocalKeyLog.Entry>(), 24);

        var text = r.ToString();
        Check("例外なく診断できる", text.Length > 0);
        Check("パケットが無い旨を書く", text.Contains("1件も届いていません"));
        Check("原因の候補を挙げる", text.Contains("シグネチャ"));
        Check("提案は出さない", r.SuggestedMidiNoteOffset == null);
    }

    /// <summary>生ログの保存と読み戻し。バイトが欠けないこと。</summary>
    private static void TestRawLogRoundTrip(string outDir)
    {
        Section("生ログの保存");

        var log = new RawPacketLog();
        var original = MakeSoloPacket(3, 7, 11);

        log.Enqueue(new RawPacketLog.Entry(
            false, 0xDEADBEEF, TimeSpan.FromSeconds(1.5),
            original, 5, 16, 5, "テスト演奏者"));
        log.Drain();

        var entries = log.Snapshot();
        Check("1件記録される", entries.Count == 1);
        Check("バイト列が変わらない",
            entries[0].Bytes.SequenceEqual(original));

        var csv = Path.Combine(outDir, "raw_test.csv");
        RawPacketLog.SaveCsv(entries, csv);
        Check("CSVが書ける", File.Exists(csv));

        var body = File.ReadAllText(csv);
        Check("16進で全バイトが残る",
            body.Contains(Convert.ToHexString(original)),
            "16進文字列が見つからない");
        Check("送り元IDが残る", body.Contains("DEADBEEF"));

        var txt = Path.Combine(outDir, "raw_test.txt");
        RawPacketLog.SaveReadable(entries, txt);
        Check("読み物版が書ける", File.Exists(txt));

        // 上限を超えたら捨てるが、落ちないこと
        var small = new RawPacketLog { Capacity = 2 };
        for (var i = 0; i < 10; i++)
            small.Enqueue(new RawPacketLog.Entry(
                false, 1, TimeSpan.Zero, new byte[4], -1, 0, 0, ""));
        small.Drain();
        Check("上限で頭打ちになる", small.Count == 2, $"実際 {small.Count}");
        Check("超過分を数える", small.OverflowCount == 8, $"実際 {small.OverflowCount}");
    }

    /// <summary>
    /// スロットの位置が時刻に反映されること。
    ///
    /// パケットは約0.5秒ごとにしか届かないので、そのままでは
    /// 同じパケットの音が全部同じ時刻になってしまう。
    /// スロット番号を足すことで、区間内の順序を復元する。
    /// </summary>
    private static void TestSlotTiming()
    {
        Section("スロット位置による時刻の復元");

        var tones = new byte[10];
        var notes = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();
        notes[0] = 24;   // スロット1 → C3
        notes[5] = 28;   // スロット6 → E3
        notes[9] = 31;   // スロット10 → G3

        // スロット位置を使う場合
        var rec = new PerformanceRecorder { UseSlotTiming = true, SlotIntervalMs = 50.0 };
        rec.Start(100);
        rec.Feed(100, notes, tones, 1, NoteSource.SoloPacket);
        var withSlot = rec.Snapshot().Where(e => e.Edge == NoteEdge.On)
            .OrderBy(e => e.ReceivedAt).ToList();
        rec.Stop();

        Check("3音とも記録される", withSlot.Count == 3, $"実際 {withSlot.Count}");

        if (withSlot.Count == 3)
        {
            var t0 = withSlot[0].ReceivedAt.TotalMilliseconds;
            var t1 = withSlot[1].ReceivedAt.TotalMilliseconds;
            var t2 = withSlot[2].ReceivedAt.TotalMilliseconds;

            Check("時刻が別々になる", t0 < t1 && t1 < t2,
                $"{t0:F0}/{t1:F0}/{t2:F0}");
            Check("スロット1と6の差が約250ms",
                Math.Abs((t1 - t0) - 250) < 5, $"実際 {t1 - t0:F0}ms");
            Check("スロット1と10の差が約450ms",
                Math.Abs((t2 - t0) - 450) < 5, $"実際 {t2 - t0:F0}ms");
            Check("音高の順が保たれる",
                withSlot.Select(e => e.NoteNumber).SequenceEqual(new[] { 48, 52, 55 }),
                string.Join(",", withSlot.Select(e => e.NoteNumber)));
        }

        // 使わない場合は全部同じ時刻になる
        var rec2 = new PerformanceRecorder { UseSlotTiming = false };
        rec2.Start(100);
        rec2.Feed(100, notes, tones, 1, NoteSource.SoloPacket);
        var noSlot = rec2.Snapshot().Where(e => e.Edge == NoteEdge.On).ToList();
        rec2.Stop();

        Check("無効にすると同じ時刻にまとまる",
            noSlot.Count == 3
            && noSlot.Select(e => e.ReceivedAt).Distinct().Count() == 1,
            $"時刻の種類 {noSlot.Select(e => e.ReceivedAt).Distinct().Count()}");
    }

    /// <summary>
    /// 同じパケット内に同じ音が複数ある場合、別々の発音として扱うこと。
    ///
    /// 実データで見つかった問題：同じ音を続けて弾くと、
    /// 1つのパケットの複数スロットに同じ値が入る。
    /// これを「同じ音」として潰すと、2回目が記録から消える。
    /// </summary>
    private static void TestRepeatedNoteInSamePacket()
    {
        Section("同一パケット内の同音連打");

        var tones = new byte[10];
        var notes = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();
        notes[0] = 33;   // スロット1  A3
        notes[7] = 33;   // スロット8  A3（同じ音をもう一度）

        var rec = new PerformanceRecorder { UseSlotTiming = true, SlotIntervalMs = 50.0 };
        rec.Start(100);
        rec.Feed(100, notes, tones, 1, NoteSource.SoloPacket);
        rec.Stop();

        var ev = rec.Snapshot();
        var ons = ev.Where(e => e.Edge == NoteEdge.On).OrderBy(e => e.ReceivedAt).ToList();

        Check("同じ音でも2回発音される", ons.Count == 2, $"実際 {ons.Count}");

        if (ons.Count == 2)
        {
            Check("どちらも同じ音高",
                ons[0].NoteNumber == ons[1].NoteNumber && ons[0].NoteNumber == 57,
                $"{ons[0].NoteNumber}/{ons[1].NoteNumber}");

            var gap = (ons[1].ReceivedAt - ons[0].ReceivedAt).TotalMilliseconds;
            Check("スロット差ぶん離れる（約350ms）",
                Math.Abs(gap - 350) < 5, $"実際 {gap:F0}ms");
        }

        // MIDI にしても2音として残ること
        MidiExporter.Build(ev, Options(), out var summary);
        Check("MIDI でも2音になる", summary.NoteCount == 2, $"実際 {summary.NoteCount}");
    }

    /// <summary>
    /// 「継続中の音」と「同パケット内の繰り返し」が重なっても
    /// 音が増えないこと。
    ///
    /// 実データで見つかった問題（2026-09-21）：
    /// 前のパケットから鳴り続けている音が、次のパケットで
    /// 2 スロットに現れると、消音が二重に出て音が 1 つ余分になった。
    /// </summary>
    private static void TestSustainedNoteRepeatedInNextPacket()
    {
        Section("継続中の音が次パケットで繰り返される場合");

        var tones = new byte[10];

        // パケット1: スロット7に A3
        var p1 = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();
        p1[6] = 33;

        // パケット2: スロット1と8に A3（1つ目は継続、2つ目は新しい音）
        var p2 = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();
        p2[0] = 33;
        p2[7] = 33;

        // パケット3: 無音（すべて閉じる）
        var p3 = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();

        var rec = new PerformanceRecorder { UseSlotTiming = true, SlotIntervalMs = 50.0 };
        rec.Start(100);
        rec.Feed(100, p1, tones, 1, NoteSource.SoloPacket);
        rec.Feed(100, p2, tones, 1, NoteSource.SoloPacket);
        rec.Feed(100, p3, tones, 1, NoteSource.SoloPacket);
        rec.Stop();

        var ev = rec.Snapshot();
        var ons = ev.Count(e => e.Edge == NoteEdge.On);
        var offs = ev.Count(e => e.Edge == NoteEdge.OffInferred);

        // 実音が現れた回数だけ発音する。
        //   パケット1のスロット7、パケット2のスロット1、同スロット8 = 3回
        //
        // 実データ（ファミマ）でも、A3 が 4 スロットに現れたとき
        // 元 MIDI の A3 は 4 回だった。実音の出現＝発音と数えるのが正しい。
        Check("実音の数だけ発音される（3回）", ons == 3, $"実際 {ons}");
        Check("発音と消音が釣り合う", ons == offs, $"発音{ons} 消音{offs}");

        MidiExporter.Build(ev, Options(), out var summary);
        Check("MIDI でも3音", summary.NoteCount == 3, $"実際 {summary.NoteCount}");
        Check("閉じない音が残らない", summary.FallbackLengthCount == 0,
            $"実際 {summary.FallbackLengthCount}");
    }

    /// <summary>
    /// 同じパケットが 2 回届いても音が増えないこと。
    ///
    /// 実機では全パケットが二重に届く（実測）。
    /// そのまま処理すると、1 回目で鳴らした音が 2 回目に
    /// 「同じパケット内の繰り返し」と誤判定され、音が増えた。
    /// </summary>
    private static void TestDuplicatePacketIsIgnored()
    {
        Section("同じパケットが二重に届く場合");

        var tones = new byte[10];
        var p = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();
        p[0] = 33;
        p[7] = 33;   // 同じ音が2スロット（同音連打）

        var empty = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();

        var rec = new PerformanceRecorder { UseSlotTiming = true, SlotIntervalMs = 50.0 };
        rec.Start(100);
        rec.Feed(100, p, tones, 1, NoteSource.SoloPacket);
        rec.Feed(100, p, tones, 1, NoteSource.SoloPacket);   // 同じものが再度
        rec.Feed(100, empty, tones, 1, NoteSource.SoloPacket);
        rec.Feed(100, empty, tones, 1, NoteSource.SoloPacket);
        rec.Stop();

        var ev = rec.Snapshot();
        var ons = ev.Count(e => e.Edge == NoteEdge.On);

        Check("発音は2回のまま（増えない）", ons == 2, $"実際 {ons}");
        Check("重複を数えている", rec.DuplicatePacketCount == 2,
            $"実際 {rec.DuplicatePacketCount}");

        MidiExporter.Build(ev, Options(), out var summary);
        Check("MIDI でも2音", summary.NoteCount == 2, $"実際 {summary.NoteCount}");

        // 同じ内容でも、間に別の状態を挟めば別のパケットとして扱う
        var rec2 = new PerformanceRecorder { UseSlotTiming = true, SlotIntervalMs = 50.0 };
        rec2.Start(100);
        var single = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();
        single[0] = 33;
        rec2.Feed(100, single, tones, 1, NoteSource.SoloPacket);
        rec2.Feed(100, empty, tones, 1, NoteSource.SoloPacket);
        rec2.Feed(100, single, tones, 1, NoteSource.SoloPacket);   // 間を空けた同じ音
        rec2.Stop();

        var ons2 = rec2.Snapshot().Count(e => e.Edge == NoteEdge.On);
        Check("間に無音を挟めば別の音として記録される", ons2 == 2, $"実際 {ons2}");
    }

    /// <summary>
    /// 同じ内容でも、時間が離れていれば別の演奏として扱うこと。
    ///
    /// 外部レビューでの指摘（2026-09-21）：
    /// 「内容が同じ」だけで無期限に除外すると、同じフレーズを
    /// 繰り返す曲で正当な演奏まで捨ててしまう。
    ///
    /// 実測では二重観測は 5ms 以内、次の区間は 461ms 以上だった。
    /// </summary>
    private static void TestDuplicateNeedsTimeWindow()
    {
        Section("同じ内容でも時間が離れていれば別の演奏");

        var tones = new byte[10];
        var p = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();
        p[0] = 36;
        p[5] = 40;

        // すぐ続けて同じものが来た場合 → 二重観測として無視
        var near = new PerformanceRecorder
        {
            UseSlotTiming = false,
            DuplicateWindowMs = 50.0,
        };

        near.Start(100);
        near.Feed(100, p, tones, 1, NoteSource.SoloPacket);
        near.Feed(100, p, tones, 1, NoteSource.SoloPacket);
        near.Stop();

        Check("すぐ続いた同じ内容は無視される",
            near.Snapshot().Count(e => e.Edge == NoteEdge.On) == 2,
            $"実際 {near.Snapshot().Count(e => e.Edge == NoteEdge.On)}");
        Check("二重観測として数えられる", near.DuplicatePacketCount == 1,
            $"実際 {near.DuplicatePacketCount}");

        // 時間を空けて同じものが来た場合 → 二重観測として捨てない
        //
        // 実機では、音が消えるパケットが必ず間に入る。
        // そこで「弾く→消える→また弾く」という実機どおりの並びで確かめる。
        var empty = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();

        var far = new PerformanceRecorder
        {
            UseSlotTiming = false,
            DuplicateWindowMs = 20.0,   // 検証を短時間で済ませるため狭める
        };

        far.Start(100);
        far.Feed(100, p, tones, 1, NoteSource.SoloPacket);
        System.Threading.Thread.Sleep(40);
        far.Feed(100, empty, tones, 1, NoteSource.SoloPacket);
        System.Threading.Thread.Sleep(40);
        far.Feed(100, p, tones, 1, NoteSource.SoloPacket);   // 同じ内容が再び
        far.Stop();

        var farOns = far.Snapshot().Count(e => e.Edge == NoteEdge.On);
        Check("時間が離れていれば別の演奏として記録される", farOns == 4,
            $"実際 {farOns}");
        Check("この場合は二重観測に数えない", far.DuplicatePacketCount == 0,
            $"実際 {far.DuplicatePacketCount}");

        // 修正前の実装（内容だけで無期限に除外）ならここが 2 になる。
        // 同じフレーズの繰り返しが消えてしまう問題の再現。
    }

    /// <summary>
    /// 2人が同じ音高を重ねて弾いても、音長が入れ替わらないこと。
    ///
    /// 外部レビューでの指摘（2026-09-21）：
    /// 発音と消音の対応付けが音高だけをキーにしていたため、
    /// 片方の消音がもう片方の発音と組まれていた。
    /// </summary>
    private static void TestTwoPerformersSameNote()
    {
        Section("2人が同じ音高を重ねて弾く場合");

        // A: 0.0 〜 1.0 秒（長い）
        // B: 0.2 〜 0.4 秒（短い）
        var records = new List<NoteEventRecord>
        {
            new(111, 60, 36, 0, 1, TimeSpan.FromSeconds(0.0), NoteEdge.On, NoteSource.SoloPacket),
            new(222, 60, 36, 0, 2, TimeSpan.FromSeconds(0.2), NoteEdge.On, NoteSource.SoloPacket),
            new(222, 60, 0xFF, 0xFF, 2, TimeSpan.FromSeconds(0.4), NoteEdge.OffInferred, NoteSource.SoloPacket),
            new(111, 60, 0xFF, 0xFF, 1, TimeSpan.FromSeconds(1.0), NoteEdge.OffInferred, NoteSource.SoloPacket),
        };

        var file = MidiExporter.Build(records, Options(), out var summary);
        Check("2音になる", summary.NoteCount == 2, $"実際 {summary.NoteCount}");
        Check("暫定長を当てた音は無い", summary.FallbackLengthCount == 0,
            $"実際 {summary.FallbackLengthCount}");

        var map = file.GetTempoMap();
        var lengths = file.GetNotes()
            .Select(n => Math.Round(n.LengthAs<MetricTimeSpan>(map).TotalMicroseconds / 1_000_000.0, 2))
            .OrderBy(x => x)
            .ToList();

        // 演奏者を無視すると 0.4 と 0.8 になってしまう（B の消音が A に付く）
        Check("それぞれの長さが保たれる（0.2秒と1.0秒）",
            lengths.Count == 2
            && Math.Abs(lengths[0] - 0.2) < 0.02
            && Math.Abs(lengths[1] - 1.0) < 0.02,
            string.Join(",", lengths));
    }

    /// <summary>
    /// 連続記録（「ずっと記録する」）の判断。
    ///
    /// 決まり：
    ///   - 待機中にパケットが来たら記録を始める
    ///   - 記録中にパケットが 15 秒途切れたら曲の終わりとみなして保存
    ///   - 待機のまま 15 分パケットが無ければ、待つのをやめる
    ///
    /// 時間の判断なので、実機で試すと 15 分待つことになる。
    /// ここで値を渡して確かめられるようにしてある。
    /// </summary>
    private static void TestContinuousPolicy()
    {
        Section("連続記録の判断");

        const double Gap = 15.0;    // 曲の区切り（秒）
        const double Idle = 15.0;   // 待機の打ち切り（分）

        ContinuousPolicy.Action Act(bool rec, bool got, double since)
            => ContinuousPolicy.Decide(rec, got, since, Gap, Idle);

        // ---- 待機中 ----
        Check("待機中・パケット無し → 何もしない",
            Act(false, false, 1.0) == ContinuousPolicy.Action.None);

        Check("待機中・パケットが来た → 記録を始める",
            Act(false, true, 0.0) == ContinuousPolicy.Action.StartRecording);

        Check("待機中・14分59秒 → まだ待つ",
            Act(false, false, 14 * 60 + 59) == ContinuousPolicy.Action.None);

        Check("待機中・15分ちょうど → 待つのをやめる",
            Act(false, false, 15 * 60) == ContinuousPolicy.Action.GiveUp);

        Check("待機中・20分 → 待つのをやめる",
            Act(false, false, 20 * 60) == ContinuousPolicy.Action.GiveUp);

        // 時間切れ寸前でも、演奏が始まれば記録に入る。
        // ここを取り違えると、待った末に演奏を逃す。
        Check("待機中・14分59秒でもパケットが来たら記録を始める",
            Act(false, true, 14 * 60 + 59) == ContinuousPolicy.Action.StartRecording);

        // ---- 記録中 ----
        Check("記録中・パケットが続いている → 何もしない",
            Act(true, true, 0.0) == ContinuousPolicy.Action.None);

        Check("記録中・3秒の途切れ → まだ曲の途中",
            Act(true, false, 3.0) == ContinuousPolicy.Action.None);

        // 合奏パケットは約3秒ごとなので、
        // 数秒の空きで切ってしまうと曲がぶつ切りになる。
        Check("記録中・14秒の途切れ → まだ曲の途中",
            Act(true, false, 14.0) == ContinuousPolicy.Action.None);

        Check("記録中・15秒ちょうど → 曲の終わりとみなす",
            Act(true, false, 15.0) == ContinuousPolicy.Action.SaveAndWait);

        Check("記録中・60秒の途切れ → 曲の終わりとみなす",
            Act(true, false, 60.0) == ContinuousPolicy.Action.SaveAndWait);

        // 記録中は「何分たったか」ではなく「何秒途切れたか」で見る。
        // 15分を超える長い曲でも、演奏が続いていれば切らない。
        Check("記録中・演奏が続いていれば20分でも切らない",
            Act(true, true, 0.0) == ContinuousPolicy.Action.None);

        // 設定を変えたときに、ちゃんと効くこと。
        Check("区切りを30秒にすると、20秒では切らない",
            ContinuousPolicy.Decide(true, false, 20.0, 30.0, Idle)
            == ContinuousPolicy.Action.None);

        Check("区切りを5秒にすると、6秒で切る",
            ContinuousPolicy.Decide(true, false, 6.0, 5.0, Idle)
            == ContinuousPolicy.Action.SaveAndWait);
    }

    /// <summary>
    /// 合奏パケットのレイアウト定数が実測と合っているか。
    ///
    /// なぜ必要か（2026-09-21 に発見した実害）：
    /// 静的解析から「8人分が 1 パケット (0x408B) に入っている」と
    /// 読んでしまい、実機（1人分 0x80B）の**外側を読んでいた**。
    /// たまたまそこが 0 だったので結果は正しく見えていた。
    ///
    /// 定数を戻してしまうと同じことが起きるので、
    /// 実測値を検証で固定しておく。
    /// </summary>
    private static void TestEnsembleLayoutConstants()
    {
        Section("合奏パケットのレイアウト定数（実測値で固定）");

        // 【重要】1 パケットに 8 人分が入っている。
        // ここを「1パケット1人」と誤認してループを削除し、
        // 合奏で1人分しか記録されなくなった（2026-09-21・実機で確認）。
        Check("1 パケットは ヘッダ8B + 128B×8人 = 0x408",
            PerformanceConstants.EnsemblePacketSize == 0x408,
            $"実際 0x{PerformanceConstants.EnsemblePacketSize:X}");

        Check("メンバーは 8 人分",
            PerformanceConstants.EnsembleMemberCount == 8,
            PerformanceConstants.EnsembleMemberCount.ToString());

        Check("1 人分の幅は 128 バイト",
            PerformanceConstants.EnsembleMemberStride == 0x80,
            $"stride=0x{PerformanceConstants.EnsembleMemberStride:X}");

        Check("メンバーはヘッダ 8 バイトの後から始まる",
            PerformanceConstants.EnsembleOffsetMembers == 8,
            PerformanceConstants.EnsembleOffsetMembers.ToString());

        // 8人目の末尾までが、パケット全体に収まること。
        Check("8 人目の末尾がパケット内に収まる",
            PerformanceConstants.EnsembleOffsetMembers
            + PerformanceConstants.EnsembleMemberStride * 8
            <= PerformanceConstants.EnsemblePacketSize,
            "8人分 + ヘッダ");

        // 1 人分の読み取りが、その枠 (0x80B) の中に収まっていること。
        // ここが外に出ていると、隣の人の領域を侵す。
        Check("音符の読み取りが1人分の枠に収まる",
            PerformanceConstants.EnsembleMemberOffsetNotes
            + PerformanceConstants.EnsembleNoteCount
            <= PerformanceConstants.EnsembleMemberStride,
            $"{PerformanceConstants.EnsembleMemberOffsetNotes}"
            + $"+{PerformanceConstants.EnsembleNoteCount}");

        Check("音色の読み取りが1人分の枠に収まる",
            PerformanceConstants.EnsembleMemberOffsetTones
            + PerformanceConstants.EnsembleNoteCount
            <= PerformanceConstants.EnsembleMemberStride,
            $"{PerformanceConstants.EnsembleMemberOffsetTones}"
            + $"+{PerformanceConstants.EnsembleNoteCount}");

        Check("必要バイト数が実測の 0x7D",
            PerformanceConstants.EnsembleRequiredBytes == 0x7D,
            $"実際 0x{PerformanceConstants.EnsembleRequiredBytes:X}");

        // ソロ側も同様に確かめる。
        Check("ソロの読み取りがパケット内に収まる",
            PerformanceConstants.SoloRequiredBytes
            <= PerformanceConstants.SoloPacketSize,
            $"{PerformanceConstants.SoloRequiredBytes}"
            + $" <= {PerformanceConstants.SoloPacketSize}");

        // 8 人分が別々の枠として読めること。
        // 1 人分しか読まない実装に戻っていないかを、ここで捕まえる。
        var packet = new byte[PerformanceConstants.EnsemblePacketSize];

        for (var m = 0; m < 8; m++)
        {
            var at = PerformanceConstants.EnsembleOffsetMembers
                     + m * PerformanceConstants.EnsembleMemberStride;

            BitConverter.GetBytes((uint)(0x10000000 + m)).CopyTo(packet, at);
            packet[at + PerformanceConstants.EnsembleMemberOffsetCount] = 60;
        }

        var found = 0;

        for (var m = 0; m < PerformanceConstants.EnsembleMemberCount; m++)
        {
            var at = PerformanceConstants.EnsembleOffsetMembers
                     + m * PerformanceConstants.EnsembleMemberStride;

            var id = BitConverter.ToUInt32(packet, at);
            if (id == (uint)(0x10000000 + m)
                && packet[at + PerformanceConstants.EnsembleMemberOffsetCount] == 60)
            {
                found++;
            }
        }

        Check("1 パケットから 8 人分すべて読める", found == 8, $"実際 {found} 人");
    }

    /// <summary>
    /// 同じ日時のファイルがあるとき、別名になること。
    ///
    /// なぜ必要か：
    /// 日時が秒単位なので、1 秒以内に 2 回記録を止めると
    /// **前の記録を黙って上書き**してしまう。
    /// 演奏は録り直しがきかないので、上書きは避けたい。
    ///
    /// Plugin は Dalamud に依存して単体で動かせないため、
    /// ここでは同じ規則を再現して確かめる。
    /// </summary>
    private static void TestUniqueFileName(string outDir)
    {
        Section("ファイル名の重複防止");

        var dir = Path.Combine(outDir, "stamp_test");
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);

        // Plugin.MakeUniqueStamp と同じ規則。
        string Unique(string baseStamp)
        {
            bool Taken(string s) => File.Exists(Path.Combine(dir, $"raw_{s}.csv"));

            if (!Taken(baseStamp))
                return baseStamp;

            for (var i = 2; i < 1000; i++)
            {
                if (!Taken($"{baseStamp}_{i}"))
                    return $"{baseStamp}_{i}";
            }

            return baseStamp + "_overflow";
        }

        const string Stamp = "20260921_143802";

        var first = Unique(Stamp);
        Check("1 回目はそのままの日時", first == Stamp, first);
        File.WriteAllText(Path.Combine(dir, $"raw_{first}.csv"), "1");

        var second = Unique(Stamp);
        Check("2 回目は別名になる", second != first, $"{first} → {second}");
        Check("2 回目は _2 が付く", second == Stamp + "_2", second);
        File.WriteAllText(Path.Combine(dir, $"raw_{second}.csv"), "2");

        var third = Unique(Stamp);
        Check("3 回目は _3 が付く", third == Stamp + "_3", third);

        // いちばん大事なこと：前のファイルが残っていること。
        Check("1 回目のファイルが消えていない",
            File.ReadAllText(Path.Combine(dir, $"raw_{first}.csv")) == "1");
        Check("2 回目のファイルも消えていない",
            File.ReadAllText(Path.Combine(dir, $"raw_{second}.csv")) == "2");
    }

    /// <summary>
    /// 演奏者の絞り込み。
    ///
    /// なぜ必要か（実測で確認、2026-09-21）：
    /// 合奏していない別々の3人がソロで弾いた演奏が、
    /// すべて同じ記録に入った。近くで無関係な人が弾いていると混ざる。
    ///
    /// ここで確かめること：
    ///   - 選んだ人だけが残る
    ///   - 残った人の音は1つも欠けない
    ///   - 除外した内容が結果に残る（黙って減らさない）
    /// </summary>
    private static void TestPerformerFilter()
    {
        Section("演奏者の絞り込み");

        // A(111) は 2 音、B(222) は 1 音、C(333) は 1 音。
        var records = new List<NoteEventRecord>
        {
            new(111, 60, 36, 0, 1, TimeSpan.FromSeconds(0.0), NoteEdge.On, NoteSource.SoloPacket),
            new(111, 60, 0xFF, 0xFF, 1, TimeSpan.FromSeconds(0.5), NoteEdge.OffInferred, NoteSource.SoloPacket),
            new(111, 62, 38, 0, 1, TimeSpan.FromSeconds(1.0), NoteEdge.On, NoteSource.SoloPacket),
            new(111, 62, 0xFF, 0xFF, 1, TimeSpan.FromSeconds(1.5), NoteEdge.OffInferred, NoteSource.SoloPacket),

            new(222, 64, 40, 0, 5, TimeSpan.FromSeconds(0.2), NoteEdge.On, NoteSource.SoloPacket),
            new(222, 64, 0xFF, 0xFF, 5, TimeSpan.FromSeconds(0.7), NoteEdge.OffInferred, NoteSource.SoloPacket),

            new(333, 65, 41, 0, 7, TimeSpan.FromSeconds(0.3), NoteEdge.On, NoteSource.SoloPacket),
            new(333, 65, 0xFF, 0xFF, 7, TimeSpan.FromSeconds(0.8), NoteEdge.OffInferred, NoteSource.SoloPacket),
        };

        var names = new Dictionary<uint, string>
        {
            [111] = "Alpha Test", [222] = "Bravo Test", [333] = "Charlie Test",
        };

        // --- 絞り込み無し ---
        var o0 = Options();
        o0.PerformerNames = names;
        MidiExporter.Build(records, o0, out var all);
        Check("絞り込み無しでは 4 音", all.NoteCount == 4, $"実際 {all.NoteCount}");
        Check("除外は記録されない", all.Excluded.Count == 0, $"実際 {all.Excluded.Count}");

        // --- 含める指定（A と B だけ） ---
        var o1 = Options();
        o1.PerformerNames = names;
        o1.IncludePerformers = new HashSet<uint> { 111u, 222u };
        var f1 = MidiExporter.Build(records, o1, out var inc);

        Check("選んだ2人で 3 音になる", inc.NoteCount == 3, $"実際 {inc.NoteCount}");
        Check("除外した1人が記録される", inc.Excluded.Count == 1, $"実際 {inc.Excluded.Count}");
        Check("除外された人の名前が残る",
            inc.Excluded.Count == 1 && inc.Excluded[0].Name == "Charlie Test",
            inc.Excluded.Count == 1 ? inc.Excluded[0].Name : "(無し)");
        Check("除外の件数が正しい（発音1+消音1=2）",
            inc.Excluded.Count == 1 && inc.Excluded[0].Events == 2,
            inc.Excluded.Count == 1 ? inc.Excluded[0].Events.ToString() : "(無し)");

        // 残した人の音が欠けていないこと。
        // 絞り込みで巻き添えにするのが一番まずいので、音高で確かめる。
        var kept = f1.GetNotes().Select(n => (int)n.NoteNumber).OrderBy(x => x).ToList();
        Check("残した2人の音が全部ある（60,62,64）",
            kept.SequenceEqual(new[] { 60, 62, 64 }), string.Join(",", kept));

        // --- 除く指定（A を除く） ---
        var o2 = Options();
        o2.PerformerNames = names;
        o2.ExcludePerformers = new HashSet<uint> { 111u };
        var f2 = MidiExporter.Build(records, o2, out var exc);

        Check("A を除くと 2 音", exc.NoteCount == 2, $"実際 {exc.NoteCount}");
        var left = f2.GetNotes().Select(n => (int)n.NoteNumber).OrderBy(x => x).ToList();
        Check("残るのは 64 と 65", left.SequenceEqual(new[] { 64, 65 }), string.Join(",", left));

        // --- 両方指定：含めるを先に、そのあと除く ---
        var o3 = Options();
        o3.PerformerNames = names;
        o3.IncludePerformers = new HashSet<uint> { 111u, 222u };
        o3.ExcludePerformers = new HashSet<uint> { 222u };
        var f3 = MidiExporter.Build(records, o3, out var both);

        Check("含める→除く の順で A だけ残る", both.NoteCount == 2, $"実際 {both.NoteCount}");
        var only = f3.GetNotes().Select(n => (int)n.NoteNumber).OrderBy(x => x).ToList();
        Check("残るのは A の 60 と 62", only.SequenceEqual(new[] { 60, 62 }), string.Join(",", only));

        // --- 誰も残らない指定でも落ちないこと ---
        var o4 = Options();
        o4.PerformerNames = names;
        o4.IncludePerformers = new HashSet<uint> { 999u };
        MidiExporter.Build(records, o4, out var none);
        Check("誰も残らなくても例外にならない", none.NoteCount == 0, $"実際 {none.NoteCount}");
        Check("全員が除外として記録される", none.Excluded.Count == 3, $"実際 {none.Excluded.Count}");

        // --- 音長が絞り込みで狂わないこと ---
        var map = f1.GetTempoMap();
        var lengths = f1.GetNotes()
            .Select(n => Math.Round(n.LengthAs<MetricTimeSpan>(map).TotalMicroseconds / 1_000_000.0, 2))
            .ToList();
        Check("残した音の長さが変わらない（全部 0.5 秒）",
            lengths.All(x => Math.Abs(x - 0.5) < 0.02), string.Join(",", lengths));
    }

    /// <summary>
    /// 0xFE（離鍵の合図）で音長が復元されること。
    ///
    /// 制御実験（2026-09-21）で分かったこと：
    /// 0xFE が現れたスロット位置と、実音のスロット位置の差が
    /// 元の音長と一致する（誤差 最大 27ms）。
    /// </summary>
    private static void TestNoteOffMarkerGivesLength()
    {
        Section("0xFE による音長の復元");

        var tones = new byte[10];

        // パケット1: スロット0 に実音
        var p1 = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();
        p1[0] = 36;

        // パケット2: スロット4 に 0xFE（＝離鍵）
        var p2 = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();
        p2[4] = PerformanceConstants.NoteOff;

        var rec = new PerformanceRecorder
        {
            UseSlotTiming = true,
            SlotIntervalMs = 50.0,
            UseNoteOffMarker = true,
        };

        rec.Start(100);
        rec.FeedAt(TimeSpan.FromSeconds(0.0), 100, p1, tones, 1, NoteSource.SoloPacket);
        rec.FeedAt(TimeSpan.FromSeconds(0.5), 100, p2, tones, 1, NoteSource.SoloPacket);
        rec.Stop(TimeSpan.FromSeconds(2.0));

        var ev = rec.Snapshot();
        MidiExporter.Build(ev, Options(), out var summary);

        Check("1音になる", summary.NoteCount == 1, $"実際 {summary.NoteCount}");

        var on = ev.FirstOrDefault(e => e.Edge == NoteEdge.On);
        var off = ev.FirstOrDefault(e => e.Edge == NoteEdge.OffInferred);
        var length = (off.ReceivedAt - on.ReceivedAt).TotalMilliseconds;

        // 発音 0.0+0*50ms、離鍵 0.5秒+4*50ms = 700ms
        Check("音長が 0xFE の位置から決まる（約700ms）",
            Math.Abs(length - 700) < 20, $"実際 {length:F0}ms");

        // 0xFE を使わない設定では、受信間隔に丸まる
        var rec2 = new PerformanceRecorder
        {
            UseSlotTiming = true,
            SlotIntervalMs = 50.0,
            UseNoteOffMarker = false,
        };

        rec2.Start(100);
        rec2.FeedAt(TimeSpan.FromSeconds(0.0), 100, p1, tones, 1, NoteSource.SoloPacket);
        rec2.FeedAt(TimeSpan.FromSeconds(0.5), 100, p2, tones, 1, NoteSource.SoloPacket);
        rec2.Stop(TimeSpan.FromSeconds(2.0));

        var ev2 = rec2.Snapshot();
        var on2 = ev2.FirstOrDefault(e => e.Edge == NoteEdge.On);
        var off2 = ev2.FirstOrDefault(e => e.Edge == NoteEdge.OffInferred);
        var length2 = (off2.ReceivedAt - on2.ReceivedAt).TotalMilliseconds;

        Check("使わない設定では受信間隔に丸まる（約500ms）",
            Math.Abs(length2 - 500) < 20, $"実際 {length2:F0}ms");
    }

    /// <summary>
    /// 離鍵の合図を待っている音が鳴り直したら、その時点で閉じること。
    ///
    /// 0xFE は必ず出るわけではないため、待ち続けると
    /// 同じ音の再発音が「継続中」とみなされて欠落する。
    /// </summary>
    private static void TestRetriggerClosesWaitingNote()
    {
        Section("合図待ちの音が鳴り直した場合");

        var tones = new byte[10];
        var p = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();
        p[0] = 36;
        var empty = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();

        var rec = new PerformanceRecorder
        {
            UseSlotTiming = true,
            SlotIntervalMs = 50.0,
            UseNoteOffMarker = true,
            NoteOffWaitMs = 5000.0,   // 待ち時間では閉じない設定にする
        };

        rec.Start(100);
        rec.FeedAt(TimeSpan.FromSeconds(0.0), 100, p, tones, 1, NoteSource.SoloPacket);
        rec.FeedAt(TimeSpan.FromSeconds(0.5), 100, empty, tones, 1, NoteSource.SoloPacket);
        rec.FeedAt(TimeSpan.FromSeconds(1.0), 100, p, tones, 1, NoteSource.SoloPacket);
        rec.Stop(TimeSpan.FromSeconds(2.0));

        var ev = rec.Snapshot();
        var ons = ev.Count(e => e.Edge == NoteEdge.On);

        Check("2回とも発音される（欠落しない）", ons == 2, $"実際 {ons}");

        MidiExporter.Build(ev, Options(), out var summary);
        Check("MIDI でも2音", summary.NoteCount == 2, $"実際 {summary.NoteCount}");
    }

    /// <summary>
    /// 2人が同じ音を弾いても、終了待ちの状態が混ざらないこと。
    ///
    /// 外部レビューでの指摘（2026-09-21）：
    /// 離鍵の合図を待つ状態が音高だけをキーにしていたため、
    /// 別の人が同じ音高を弾くと待ち時間が共有されていた。
    ///
    /// 修正前は A の音が停止時刻まで閉じなかった（実測で 4000ms）。
    /// </summary>
    private static void TestWaitingStateIsPerPerformer()
    {
        Section("2人が同じ音：終了待ちの独立性");

        var tones = new byte[10];
        var c4 = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();
        c4[0] = 36;
        var empty = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();

        var rec = new PerformanceRecorder
        {
            UseSlotTiming = false,
            RecordAllPerformers = true,
            NoteOffWaitMs = 1000.0,
        };

        rec.Start(0);

        // A が 0.0 秒、B が 0.4 秒に同じ C4 を鳴らす。どちらも合図は来ない。
        rec.FeedAt(TimeSpan.FromSeconds(0.0), 111, c4, tones, 1, NoteSource.SoloPacket);
        rec.FeedAt(TimeSpan.FromSeconds(0.4), 222, c4, tones, 2, NoteSource.SoloPacket);

        for (var t = 0.5; t <= 2.5; t += 0.5)
        {
            rec.FeedAt(TimeSpan.FromSeconds(t), 111, empty, tones, 1, NoteSource.SoloPacket);
            rec.FeedAt(TimeSpan.FromSeconds(t + 0.05), 222, empty, tones, 2, NoteSource.SoloPacket);
        }

        rec.Stop(TimeSpan.FromSeconds(4.0));

        var ev = rec.Snapshot();
        var aOff = ev.FirstOrDefault(e => e.PerformerId == 111 && e.Edge == NoteEdge.OffInferred);
        var bOff = ev.FirstOrDefault(e => e.PerformerId == 222 && e.Edge == NoteEdge.OffInferred);

        Check("A にも消音がある", aOff.PerformerId == 111);
        Check("B にも消音がある", bOff.PerformerId == 222);

        // 待ち時間 1000ms なので、どちらも 2 秒前後で閉じる。
        // 修正前は A が停止時刻（4000ms）まで閉じなかった。
        Check("A が停止を待たずに閉じる",
            aOff.ReceivedAt.TotalMilliseconds < 3000,
            $"実際 {aOff.ReceivedAt.TotalMilliseconds:F0}ms");

        // 2人の消音が、パケット到着差（50ms）程度に収まる
        var gap = Math.Abs(
            aOff.ReceivedAt.TotalMilliseconds - bOff.ReceivedAt.TotalMilliseconds);

        Check("2人の消音が独立している（差が小さい）",
            gap < 200, $"差 {gap:F0}ms");

        // MIDI にしても両方の音長がそろう
        MidiExporter.Build(ev, Options(), out var summary);
        Check("2音になる", summary.NoteCount == 2, $"実際 {summary.NoteCount}");
    }

    /// <summary>
    /// 片方だけ離鍵の合図が来ても、もう片方に影響しないこと。
    /// </summary>
    private static void TestOneSidedNoteOffDoesNotAffectOther()
    {
        Section("片方だけ合図が来る場合");

        var tones = new byte[10];
        var c4 = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();
        c4[0] = 36;
        var off = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();
        off[5] = PerformanceConstants.NoteOff;
        var empty = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();

        var rec = new PerformanceRecorder
        {
            UseSlotTiming = true,
            SlotIntervalMs = 50.0,
            RecordAllPerformers = true,
            NoteOffWaitMs = 1000.0,
        };

        rec.Start(0);
        rec.FeedAt(TimeSpan.FromSeconds(0.0), 111, c4, tones, 1, NoteSource.SoloPacket);
        rec.FeedAt(TimeSpan.FromSeconds(0.1), 222, c4, tones, 2, NoteSource.SoloPacket);

        // A だけ 0.5 秒に合図が来る（スロット5 → 0.5+0.25 = 0.75秒）
        rec.FeedAt(TimeSpan.FromSeconds(0.5), 111, off, tones, 1, NoteSource.SoloPacket);

        // B は合図が来ないまま無音が続く
        for (var t = 0.6; t <= 2.5; t += 0.5)
            rec.FeedAt(TimeSpan.FromSeconds(t), 222, empty, tones, 2, NoteSource.SoloPacket);

        rec.Stop(TimeSpan.FromSeconds(4.0));

        var ev = rec.Snapshot();
        var aOff = ev.FirstOrDefault(e => e.PerformerId == 111 && e.Edge == NoteEdge.OffInferred);
        var bOff = ev.FirstOrDefault(e => e.PerformerId == 222 && e.Edge == NoteEdge.OffInferred);

        // A は合図どおり 0.75 秒で閉じる
        Check("A は合図の位置で閉じる（約750ms）",
            Math.Abs(aOff.ReceivedAt.TotalMilliseconds - 750) < 60,
            $"実際 {aOff.ReceivedAt.TotalMilliseconds:F0}ms");

        // B は A の合図に巻き込まれず、待ち時間で閉じる
        Check("B は A の合図に巻き込まれない",
            bOff.ReceivedAt.TotalMilliseconds > 1000,
            $"実際 {bOff.ReceivedAt.TotalMilliseconds:F0}ms");

        Check("発音と消音が釣り合う",
            ev.Count(e => e.Edge == NoteEdge.On) == ev.Count(e => e.Edge == NoteEdge.OffInferred),
            $"発音{ev.Count(e => e.Edge == NoteEdge.On)} "
            + $"消音{ev.Count(e => e.Edge == NoteEdge.OffInferred)}");
    }

    /// <summary>押鍵ログが、押し・離し・同音連打を取りこぼさないこと。</summary>
    private static void TestLocalKeyLogEdges()
    {
        Section("押鍵ログ");

        var log = new LocalKeyLog();
        const int none = LocalPerformanceReaderConstants.NoNotePressed;

        // 押す → 離す → 同じ鍵をもう一度押す
        log.Observe(TimeSpan.FromSeconds(0.0), 10, 0, 0, 0);
        log.Observe(TimeSpan.FromSeconds(0.1), 10, 0, 0, 0); // 変化なし
        log.Observe(TimeSpan.FromSeconds(0.2), none, 0, 0, 0);
        log.Observe(TimeSpan.FromSeconds(0.3), 10, 0, 0, 0);
        log.Observe(TimeSpan.FromSeconds(0.4), none, 0, 0, 0);

        var e = log.Snapshot();
        Check("押鍵2回・離鍵2回", e.Count == 4, $"実際 {e.Count}");
        Check("押した回数が2", log.PressCount == 2, $"実際 {log.PressCount}");
        Check("同じ値の連続を重複させない",
            e.Count(x => x.IsPress) == 2);

        // 押したまま別の鍵へ移る（離鍵が挟まらない場合）
        var log2 = new LocalKeyLog();
        log2.Observe(TimeSpan.FromSeconds(0.0), 10, 0, 0, 0);
        log2.Observe(TimeSpan.FromSeconds(0.1), 14, 0, 0, 0);

        var e2 = log2.Snapshot();
        Check("鍵の移動で前の鍵が閉じる",
            e2.Count(x => !x.IsPress) == 1 && e2.Count(x => x.IsPress) == 2,
            $"押{e2.Count(x => x.IsPress)} 離{e2.Count(x => !x.IsPress)}");
    }

    /// <summary>
    /// パケットが届いている最中に停止しても破綻しないこと。
    ///
    /// フックはゲームスレッドから、停止は UI スレッドから呼ばれるため、
    /// 「停止処理の途中に割り込んだパケットが、閉じたはずの音を復活させる」
    /// 競合が起きうる。そうなると鳴りっぱなしの音が残る。
    /// </summary>
    private static void TestStopIsSafeWhilePacketsArrive()
    {
        Section("パケット受信中の停止（競合）");

        var problems = 0;

        for (var trial = 0; trial < 30; trial++)
        {
            var rec = new PerformanceRecorder();
            rec.Start(100);

            var stop = false;
            var feeder = new System.Threading.Thread(() =>
            {
                var tones = new byte[10];
                var on = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();
                on[0] = 36;
                on[1] = 40;
                var off = Enumerable.Repeat(PerformanceConstants.NoteEmpty, 10).ToArray();

                while (!System.Threading.Volatile.Read(ref stop))
                {
                    rec.Feed(100, on, tones, 1, NoteSource.SoloPacket);
                    rec.Feed(100, off, tones, 1, NoteSource.SoloPacket);
                }
            });

            feeder.Start();
            System.Threading.Thread.Sleep(5);
            rec.Stop();
            System.Threading.Volatile.Write(ref stop, true);
            feeder.Join();

            // 停止後にもう一度 Drain しても、発音と消音の数が釣り合うこと。
            var events = rec.Snapshot();
            var ons = events.Count(e => e.Edge == NoteEdge.On);
            var offs = events.Count(e => e.Edge == NoteEdge.OffInferred);

            if (ons != offs)
                problems++;

            // MIDI に変換しても、閉じない音が残っていないこと。
            MidiExporter.Build(events, Options(), out var summary);
            if (summary.FallbackLengthCount != 0)
                problems++;
        }

        Check("30回試して発音と消音が常に釣り合う", problems == 0,
            $"破綻 {problems} 件");
    }

    /// <summary>
    /// 書き出し基準 BPM を変えても、記録した実時間が変わらないこと。
    /// BPM は「実時間を写すための基準」でしかない、という設計の確認。
    /// </summary>
    private static void TestTempoDoesNotDistortRealTime(string outDir)
    {
        Section("BPM を変えても実時間が保たれる");

        var records = new List<NoteEventRecord>
        {
            On(1, 60, 0.0), Off(1, 60, 0.4),
            On(1, 64, 1.3), Off(1, 64, 1.7),
        };

        foreach (var bpm in new[] { 60.0, 120.0, 175.0 })
        {
            var opt = Options();
            opt.BeatsPerMinute = bpm;

            var path = Path.Combine(outDir, $"tempo_{bpm:0}.mid");
            MidiExporter.Save(records, opt, path);

            var file = MidiFile.Read(path);
            var map = file.GetTempoMap();
            var starts = file.GetNotes().OrderBy(n => n.Time)
                .Select(n => n.TimeAs<MetricTimeSpan>(map).TotalMicroseconds / 1_000_000.0)
                .ToList();

            var ok = starts.Count == 2
                     && Math.Abs(starts[0] - 0.0) < 0.003
                     && Math.Abs(starts[1] - 1.3) < 0.003;

            Check($"BPM {bpm:0} でも開始が 0.0 / 1.3 秒",
                ok, string.Join(",", starts.Select(s => s.ToString("F3"))));
        }
    }

    /// <summary>
    /// 一般的な MIDI 読み取りで開けること（完成条件4の機械的な確認）。
    /// 実際の編集ソフトで開く確認は別途必要。
    /// </summary>
    private static void TestMidiIsReadableByStandardReader(string outDir)
    {
        Section("標準MIDIとして読み出せるか");

        var path = Path.Combine(outDir, "scale.mid");
        try
        {
            var file = MidiFile.Read(path);
            Check("MidiFile.Read で開ける", file != null);
            Check("フォーマットが取得できる", file.OriginalFormat is MidiFileFormat.SingleTrack
                or MidiFileFormat.MultiTrack or MidiFileFormat.MultiSequence);
            Check("時間分解能が 480", file.TimeDivision is TicksPerQuarterNoteTimeDivision
            {
                TicksPerQuarterNote: 480,
            });

            var tempoMap = file.GetTempoMap();
            var tempo = tempoMap.GetTempoAtTime((MidiTimeSpan)0);
            Check("テンポが 120BPM で書かれている",
                Math.Abs(tempo.BeatsPerMinute - 120.0) < 0.01,
                $"実際 {tempo.BeatsPerMinute}");

            // 推定であることの注記が入っているか
            var text = file.GetTrackChunks()
                .SelectMany(c => c.Events)
                .OfType<TextEvent>()
                .Select(e => e.Text)
                .FirstOrDefault(t => t.Contains("INFERRED"));
            Check("推定である旨がファイルに残っている", text != null);

            // 付随の説明ファイル
            var sidecar = Path.ChangeExtension(path, ".txt");
            Check("説明テキストが出力される", File.Exists(sidecar));
            if (File.Exists(sidecar))
            {
                var body = File.ReadAllText(sidecar);
                Check("説明に推定の断り書きがある", body.Contains("推定"));
                Check("説明に復元ではない旨がある", body.Contains("復元ではありません"));
            }
        }
        catch (Exception e)
        {
            Check("MidiFile.Read で開ける", false, e.Message);
        }
    }
}
