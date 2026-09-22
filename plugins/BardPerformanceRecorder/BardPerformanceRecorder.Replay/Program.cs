using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BardPerformanceRecorder.Game;
using BardPerformanceRecorder.Midi;
using BardPerformanceRecorder.Recording;

namespace BardPerformanceRecorder.Replay;

/// <summary>
/// 保存済みの生ログを、本番と同じ記録処理に流して MIDI を作り直す。
///
/// なぜ必要か：
/// 実機の問題を調べるとき、別実装で再現しようとすると
/// 前処理の違いで問題が隠れる（実際に、重複パケットを
/// 勝手に除去して再生したために原因を取り違えた）。
///
/// ここでは元の CSV の順番・時刻・重複・番兵をそのまま保ち、
/// `PerformanceRecorder` と `MidiExporter` という本番と同じ経路を通す。
/// ユーザーに録り直しを頼む前に、手元で何度でも検証できる。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length < 1)
        {
            Console.WriteLine("使い方:");
            Console.WriteLine("  dotnet run -c Release -- <raw_日時.csv> [出力.mid] [オプション]");
            Console.WriteLine();
            Console.WriteLine("保存済みの生ログを本番と同じ処理で再生し、MIDI を作り直します。");
            Console.WriteLine();
            Console.WriteLine("オプション:");
            Console.WriteLine("  --list                 演奏者の一覧だけ出す（書き出さない）");
            Console.WriteLine("  --only 名前,名前,...   その人だけを含める");
            Console.WriteLine("  --skip 名前,名前,...   その人を除く");
            Console.WriteLine();
            Console.WriteLine("  名前は部分一致・大小文字を区別しません。");
            Console.WriteLine("  EntityId（16進8桁）でも指定できます。");
            return 1;
        }

        // オプションを先に取り出す。
        // 残りを位置引数（生ログ・出力先）として扱う。
        var only = new List<string>();
        var skip = new List<string>();
        var listOnly = false;
        var positional = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--list":
                    listOnly = true;
                    break;

                case "--only" when i + 1 < args.Length:
                    only.AddRange(args[++i].Split(',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;

                case "--skip" when i + 1 < args.Length:
                    skip.AddRange(args[++i].Split(',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;

                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        if (positional.Count == 0)
        {
            Console.WriteLine("生ログを指定してください。");
            return 1;
        }

        args = positional.ToArray();
        var csvPath = args[0];

        // 出力先を指定しない場合は、本番と同じ場所へ置く。
        //
        // 本番は設定で D:\MIDI\作成Midi を指している。
        // ここでも同じ場所を既定にして、再生成したものが
        // 実機の出力と別々の場所に散らばらないようにする。
        // そのドライブが無い環境では、生ログの隣へ落とす。
        const string PreferredDir = @"D:\MIDI\作成Midi";

        var fallbackDir = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(csvPath)) ?? ".", "作成Midi");

        var baseDir = args.Length > 1
            ? null
            : (Directory.Exists(Path.GetPathRoot(PreferredDir)) ? PreferredDir : fallbackDir);

        var outPath = args.Length > 1
            ? args[1]
            : Path.Combine(
                baseDir,
                Path.GetFileNameWithoutExtension(csvPath).Replace("raw_", "performance_") + ".mid");

        var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(outDir))
            Directory.CreateDirectory(outDir);

        // 出力先を明示していないときは、既存を上書きしない。
        // 明示された場合は、そこへ書くのが意図なのでそのまま従う。
        if (args.Length <= 1 && File.Exists(outPath))
        {
            var stem = Path.Combine(outDir ?? ".", Path.GetFileNameWithoutExtension(outPath));
            for (var i = 2; i < 1000; i++)
            {
                var candidate = $"{stem}_{i}.mid";
                if (!File.Exists(candidate))
                {
                    Console.WriteLine($"既存ファイルがあるため {Path.GetFileName(candidate)} に保存します。");
                    outPath = candidate;
                    break;
                }
            }
        }

        if (!File.Exists(csvPath))
        {
            Console.WriteLine($"生ログが見つかりません: {csvPath}");
            return 1;
        }

        var rows = ReadCsv(csvPath);
        if (rows.Count == 0)
        {
            Console.WriteLine("生ログに行がありません。");
            return 1;
        }

        var recorder = new PerformanceRecorder
        {
            UseSlotTiming = true,
            SlotIntervalMs = 50.0,
            RecordAllPerformers = true,
        };

        recorder.Start(0);

        // 合奏は 60 スロット、ソロは 10 スロット。大きいほうに合わせて確保する。
        var tones = new byte[PerformanceConstants.EnsembleNoteCount];
        var notes = new byte[PerformanceConstants.EnsembleNoteCount];
        var lastSeconds = 0.0;

        foreach (var row in rows)
        {
            lastSeconds = row.Seconds;

            // ソロと合奏でレイアウトが違う（実測で確認）。
            //
            //   ソロ : +0x00 音符数 / +0x01 音符×10 / +0x0B 音色×10
            //   合奏 : +0x00 CharacterId / +0x04 音符数
            //          +0x05 音符×60 / +0x41 音色×60
            //
            // 経路に合わせて読み分けないと、合奏ログを
            // ソロとして読んで音が欠ける。
            var slotCount = row.IsEnsemble
                ? PerformanceConstants.EnsembleNoteCount
                : PerformanceConstants.SoloNoteCount;

            // 生ログは通常、1人分の中身が +0x00 から始まる。
            //
            // ただし 2026-09-21 の一時的な不具合で、
            // 8 バイトずれて保存されたログが少数ある（120 バイト）。
            // そのログも読めるよう、中身の位置を確かめてから読む。
            var bodyOffset = 0;

            if (row.IsEnsemble
                && !PerformanceConstants.LooksLikeEnsembleBody(row.Bytes, 0)
                && PerformanceConstants.LooksLikeEnsembleBody(
                    row.Bytes, PerformanceConstants.EnsembleLegacyHeaderSize))
            {
                bodyOffset = PerformanceConstants.EnsembleLegacyHeaderSize;
            }

            var noteOffset = bodyOffset + (row.IsEnsemble
                ? PerformanceConstants.EnsembleMemberOffsetNotes
                : PerformanceConstants.SoloOffsetNotes);

            var toneOffset = bodyOffset + (row.IsEnsemble
                ? PerformanceConstants.EnsembleMemberOffsetTones
                : PerformanceConstants.SoloOffsetTones);

            // 音符さえ読めれば記録できる。
            //
            // 形B のログは先頭 8 バイトぶん後ろにずれているため、
            // 音色の最後まで届かないことがある（実測 5 バイト不足）。
            // 以前はここで丸ごと飛ばしていて、
            // **音符は読めるのに 1 音も記録されなかった**。
            // 音色はギターの音色にしか使わないので、
            // 足りなければ「不明」で埋めて音符だけ活かす。
            if (row.Bytes.Length < noteOffset + slotCount)
                continue;

            // 前のパケットの値が残らないよう、使う範囲を毎回埋める。
            for (var i = 0; i < slotCount; i++)
            {
                notes[i] = row.Bytes[noteOffset + i];

                var t = toneOffset + i;
                tones[i] = t < row.Bytes.Length ? row.Bytes[t] : (byte)0xFF;
            }

            // 元の受信時刻をそのまま渡す。重複行も除かずに流す。
            //
            // スロット数ぶんだけ渡す。配列全体を渡すと、
            // ソロのときに使っていない後ろのスロットまで読まれてしまう。
            // 形B では送り元 ID が EntityId ではなかった（実測 0x00001477）。
            // 中身の CharacterId が使えるならそちらを使う。
            var performerId = row.SourceId;

            if (bodyOffset > 0
                && row.Bytes.Length >= bodyOffset + 4)
            {
                var inner = BitConverter.ToUInt32(row.Bytes, bodyOffset);
                if (inner != 0 && inner != PerformanceConstants.NullActorId)
                    performerId = inner;
            }

            recorder.FeedAt(
                TimeSpan.FromSeconds(row.Seconds),
                performerId,
                notes.AsSpan(0, slotCount),
                tones.AsSpan(0, slotCount),
                row.Instrument,
                row.IsEnsemble ? NoteSource.EnsemblePacket : NoteSource.SoloPacket);
        }

        recorder.Stop(TimeSpan.FromSeconds(lastSeconds + 0.5));

        var events = recorder.Snapshot();

        // 生ログに残っている演奏者名を拾って、トラック名に載せる。
        var names = new Dictionary<uint, string>();
        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.Performer))
                names[row.SourceId] = row.Performer;
        }

        // --list なら一覧だけ出して終わる。
        // 誰を選ぶか決めてから書き出せるようにするため。
        if (listOnly)
        {
            Console.WriteLine($"生ログ: {csvPath}");
            Console.WriteLine();
            Console.WriteLine("EntityId  名前                        発音数  楽器");
            Console.WriteLine(new string('-', 62));

            foreach (var g in events
                         .Where(e => e.Edge == NoteEdge.On)
                         .GroupBy(e => e.PerformerId)
                         .OrderByDescending(g => g.Count()))
            {
                names.TryGetValue(g.Key, out var nm);

                var inst = g.Where(e => e.InstrumentId >= 0)
                    .GroupBy(e => e.InstrumentId)
                    .OrderByDescending(x => x.Count())
                    .Select(x => x.Key)
                    .FirstOrDefault(-1);

                var instName = InstrumentNames.TryGet(inst)
                               ?? (inst >= 0 ? $"ID{inst}" : "不明");

                Console.WriteLine($"{g.Key:X8}  {nm ?? "(名前不明)",-26} {g.Count(),6}  {instName}");
            }

            return 0;
        }

        // --only / --skip を EntityId に解決する。
        // 名前は部分一致。どれにも当たらない指定は黙って無視せず知らせる。
        var includeIds = ResolvePerformers(only, names, "--only");
        var excludeIds = ResolvePerformers(skip, names, "--skip");

        var options = new MidiExportOptions
        {
            BeatsPerMinute = 120.0,
            TicksPerQuarterNote = 480,
            FallbackNoteLengthMs = 250,
            MinimumNoteLengthMs = 30,
            BuildStamp = "replay",
            PerformerNames = names,
            IncludePerformers = includeIds.Count > 0 ? includeIds : null,
            ExcludePerformers = excludeIds.Count > 0 ? excludeIds : null,
        };

        var summary = MidiExporter.Save(events, options, outPath);

        Console.WriteLine($"生ログ     : {csvPath}");
        Console.WriteLine($"読んだ行   : {rows.Count}");
        Console.WriteLine($"重複除外   : {recorder.DuplicatePacketCount}");
        Console.WriteLine($"発音       : {events.Count(e => e.Edge == NoteEdge.On)}");
        Console.WriteLine($"消音       : {events.Count(e => e.Edge == NoteEdge.OffInferred)}");
        foreach (var (id, nm, cnt) in summary.Excluded)
            Console.WriteLine($"除外       : {nm ?? $"0x{id:X8}"} ({cnt} 件)");

        Console.WriteLine($"MIDI 音符数: {summary.NoteCount}");
        Console.WriteLine($"暫定長     : {summary.FallbackLengthCount}");
        Console.WriteLine($"破棄       : {summary.DroppedCount}");
        Console.WriteLine($"出力       : {outPath}");
        return 0;
    }

    /// <summary>
    /// 名前または EntityId の指定を、実際の EntityId に解決する。
    ///
    /// 名前は部分一致・大小文字を区別しない。
    /// 当たらなかった指定は黙って無視せず知らせる。
    /// 打ち間違いに気づかないまま「フィルターが効かない」と
    /// 悩むことを防ぐため。
    /// </summary>
    private static HashSet<uint> ResolvePerformers(
        List<string> wanted, Dictionary<uint, string> names, string optionName)
    {
        var result = new HashSet<uint>();

        foreach (var w in wanted)
        {
            var hit = false;

            // EntityId 直接指定（16進8桁）
            if (w.Length == 8 &&
                uint.TryParse(w, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var id))
            {
                result.Add(id);
                hit = true;
            }

            foreach (var kv in names)
            {
                if (kv.Value != null &&
                    kv.Value.Contains(w, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(kv.Key);
                    hit = true;
                }
            }

            if (!hit)
                Console.WriteLine($"警告: {optionName} の「{w}」は誰にも当たりませんでした。");
        }

        return result;
    }

    private readonly record struct Row(
        double Seconds, bool IsEnsemble, uint SourceId, string Performer,
        int Instrument, byte[] Bytes);

    /// <summary>
    /// 生ログの CSV を読む。
    /// 演奏者名にカンマが混ざっても壊れないよう、引用符を扱う。
    /// </summary>
    private static List<Row> ReadCsv(string path)
    {
        var rows = new List<Row>();
        var lines = File.ReadAllLines(path);

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var cols = SplitCsv(line);
            if (cols.Count < 9)
                continue;

            if (!double.TryParse(cols[0], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var sec))
                continue;

            if (!uint.TryParse(cols[2], NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var src))
                continue;

            int.TryParse(cols[6], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var inst);

            byte[] bytes;
            try
            {
                bytes = Convert.FromHexString(cols[8].Trim());
            }
            catch
            {
                continue;
            }

            rows.Add(new Row(sec, cols[1] == "ensemble", src, cols[3], inst, bytes));
        }

        return rows;
    }

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(current.ToString());
        return result;
    }
}
