using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BardPerformanceRecorder.Game;
using BardPerformanceRecorder.Midi;
using BardPerformanceRecorder.Recording;
using BardPerformanceRecorder.UI;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace BardPerformanceRecorder;

public sealed class Plugin : IDalamudPlugin
{
    public const string CommandName = "/bprec";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; }
    [PluginService] internal static ICommandManager CommandManager { get; private set; }
    [PluginService] internal static ISigScanner SigScanner { get; private set; }
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; }
    [PluginService] internal static IObjectTable ObjectTable { get; private set; }
    [PluginService] internal static IClientState ClientState { get; private set; }
    [PluginService] internal static IFramework Framework { get; private set; }
    [PluginService] internal static IPluginLog Log { get; private set; }

    internal Configuration Config { get; }
    internal PerformanceRecorder Recorder { get; }
    internal PerformanceHookManager Hooks { get; }
    internal PerformanceSignatures Signatures { get; }

    /// <summary>生パケットの記録。解釈を間違えても読み直せるようにするため。</summary>
    internal RawPacketLog RawLog { get; } = new();

    /// <summary>自分の演奏状態の読み取り。照合の正解データを作る。</summary>
    internal LocalPerformanceReader LocalPerformance { get; } = new();

    /// <summary>自分が押した鍵の時系列。</summary>
    internal LocalKeyLog LocalKeys { get; } = new();

    /// <summary>押鍵オフセットを実測で探すための監視。</summary>
    internal MemoryWatcher MemoryWatcher { get; } = new();

    private readonly WindowSystem windowSystem = new("BardPerformanceRecorder");
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        if (string.IsNullOrWhiteSpace(Config.OutputDirectory))
            Config.OutputDirectory = DefaultOutputDirectory();

        Recorder = new PerformanceRecorder();

        Signatures = new PerformanceSignatures();
        Signatures.Resolve(SigScanner, Log);

        Hooks = new PerformanceHookManager(Recorder, Log, ObjectTable, RawLog)
        {
            ShouldRecordEnsemble = () => Config.RecordEnsemblePackets,
            ShouldLogRaw = () => Config.LogRawPackets && Recorder.IsRecording,
            Clock = () => Recorder.Elapsed,
            ShouldWatchOpcodes = () => Config.UseDispatcherHook,
            ShouldLogHeader = () => Config.LogPacketHeader,
            CaptureBytes = Config.CaptureBytes,
            PerformanceOpcode = Config.PerformanceOpcode,
        };
        Hooks.Install(Signatures, GameInterop);

        mainWindow = new MainWindow(this);
        windowSystem.AddWindow(mainWindow);

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi += OpenMain;

        Framework.Update += OnFrameworkUpdate;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "演奏記録ウィンドウを開きます。",
        });
    }

    /// <summary>
    /// いま動いている DLL のビルド時刻。
    ///
    /// Dalamud はプラグインをメモリ上に読み込むため
    /// Assembly.Location は空になる。Dalamud が持っている
    /// 実ファイルの場所から取る。
    ///
    /// DLL を差し替えても読み込み直さないと反映されないので、
    /// 「新しい版のつもりで古い版を使っていた」を防ぐために表示する。
    /// </summary>
    internal static string BuildStamp
    {
        get
        {
            if (buildStamp != null)
                return buildStamp;

            try
            {
                var file = PluginInterface?.AssemblyLocation;
                buildStamp = file is { Exists: true }
                    ? file.LastWriteTime.ToString("MM/dd HH:mm:ss")
                    : "不明";
            }
            catch
            {
                buildStamp = "不明";
            }

            return buildStamp;
        }
    }

    private static string buildStamp;

    internal static string DefaultOutputDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "BardPerformanceRecorder");

    /// <summary>
    /// 完成した MIDI と、その設定ファイル（.json）を置く場所。
    ///
    /// 生ログや診断は調べるためのものなので親フォルダーに残し、
    /// 「弾き直せる成果物」だけをここへ分ける。
    ///
    /// 設定で別のドライブを指せるようにしてある。
    /// ただし外付けや別ドライブが外れていることがあるので、
    /// 作れなかったときは黙って諦めず、生ログの隣へ落とす。
    /// 記録そのものを失わせないため。
    /// </summary>
    internal string ResolveMidiDirectory(string baseDir)
    {
        var fallback = Path.Combine(baseDir, "作成Midi");
        var wanted = Config.MidiOutputDirectory;

        if (string.IsNullOrWhiteSpace(wanted))
            wanted = fallback;

        try
        {
            Directory.CreateDirectory(wanted);
            return wanted;
        }
        catch (Exception e)
        {
            // 指定先が使えない（ドライブ未接続・権限不足など）。
            // 事実を伝えたうえで、保存できる場所へ回す。
            Log.Warning(e, $"MIDI の保存先を用意できませんでした: {wanted}");
            Recorder.SetError(
                $"MIDI の保存先 {wanted} が使えないため、{fallback} に保存しました。");

            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // 自分の演奏状態は常に見ておく（画面に出すため）。
        LocalPerformance.Update();

        // 連続記録の面倒を見る。
        // 記録していないとき（待機中）も動く必要があるので、
        // 下の IsRecording チェックより前に置く。
        UpdateContinuous();

        // 重い処理をフックから追い出すための受け皿。
        // フックが積んだキューをここで確定リストへ移す。
        if (!Recorder.IsRecording)
            return;

        Recorder.Drain();
        RawLog.Drain();

        // 自分が押している鍵を追う。これが照合の正解データになる。
        if (Config.LogLocalKeys && LocalPerformance.Available)
            LocalKeys.Observe(
                Recorder.Elapsed,
                LocalPerformance.CurrentPressingNote,
                LocalPerformance.OctaveOffset,
                LocalPerformance.NoteOffset,
                LocalPerformance.GroupTone);
    }

    // ---- 連続記録モード ----
    //
    // 「ずっと記録する」を押してから、演奏を待ち続けて
    // 曲ごとに自動で区切って保存する。
    //
    // 流れ：
    //   待機 → パケットが来たら記録開始
    //        → パケットが途切れて一定時間たったら「曲が終わった」とみなし
    //          書き出して、また待機に戻る
    //   ずっと来なければ、時間切れで自分から止まる

    /// <summary>連続記録モードの状態。</summary>
    internal enum ContinuousState
    {
        /// <summary>動いていない。</summary>
        Off,

        /// <summary>演奏が始まるのを待っている。</summary>
        Waiting,

        /// <summary>演奏を記録している。</summary>
        Recording,
    }

    internal ContinuousState Continuous { get; private set; } = ContinuousState.Off;

    /// <summary>連続記録で保存した曲の数。</summary>
    internal int ContinuousSavedCount { get; private set; }

    /// <summary>
    /// 連続記録で保存した曲の一覧（新しい順）。
    ///
    /// `LastSavedFiles` は書き出すたびに消えるので、
    /// 連続記録では別に持っておく。
    /// 何時間か回しっぱなしにしたあと、
    /// 何が録れたか一目で分かるようにするため。
    /// </summary>
    internal List<(DateTime At, string Path, int Notes, int Performers)> ContinuousSongs { get; }
        = new();

    /// <summary>連続記録を始めた時刻。時間切れの判定に使う。</summary>
    private DateTime continuousStartedAt;

    /// <summary>最後に演奏パケットを見た時刻。</summary>
    private DateTime lastPacketAt;

    /// <summary>最後に見たパケット数。増えていれば受信したと分かる。</summary>
    private long lastPacketTotal;

    /// <summary>連続記録の最後の出来事。画面に出す。</summary>
    internal string ContinuousMessage { get; private set; } = string.Empty;

    /// <summary>演奏パケットの総数。増減を見て受信を判断する。</summary>
    private long PacketTotal
        => Hooks.SoloPacketCount + Hooks.EnsemblePacketCount + Hooks.PerformanceOpcodeHits;

    /// <summary>連続記録を始める。</summary>
    internal void StartContinuous()
    {
        Continuous = ContinuousState.Waiting;
        ContinuousSavedCount = 0;
        continuousStartedAt = DateTime.Now;
        lastPacketAt = DateTime.Now;
        lastPacketTotal = PacketTotal;

        ContinuousMessage = "演奏が始まるのを待っています。";
        Log.Information("連続記録を開始しました。");
    }

    /// <summary>
    /// 連続記録をやめる。記録中だった分は保存する。
    ///
    /// 途中で止めても、そこまでの演奏を捨てないため。
    /// </summary>
    internal void StopContinuous()
    {
        if (Continuous == ContinuousState.Recording && Recorder.IsRecording)
        {
            var r = StopAndExport();
            if (r != null && r.NoteCount > 0)
            {
                ContinuousSavedCount++;
                RememberSong(r);
                ContinuousMessage = $"{ContinuousSavedCount} 曲目を保存して停止しました。";
            }
            else
            {
                ContinuousMessage = "停止しました（書き出すものがありませんでした）。";
            }
        }
        else
        {
            ContinuousMessage = "停止しました。";
        }

        Continuous = ContinuousState.Off;
        Log.Information("連続記録を停止しました。");
    }

    /// <summary>保存した曲を一覧に残す。多くなりすぎたら古いものから捨てる。</summary>
    private void RememberSong(MidiExporter.ExportResult result)
    {
        ContinuousSongs.Insert(0, (
            DateTime.Now,
            result.FilePath,
            result.NoteCount,
            result.Tracks?.Count ?? 0));

        // 画面に出すためのものなので、際限なく増やさない。
        while (ContinuousSongs.Count > 50)
            ContinuousSongs.RemoveAt(ContinuousSongs.Count - 1);
    }

    /// <summary>
    /// 連続記録の面倒を見る。毎フレーム呼ばれる。
    ///
    /// ここでは時間を見るだけにして、
    /// 重い処理（書き出し）は区切りのときだけ行う。
    /// </summary>
    private void UpdateContinuous()
    {
        if (Continuous == ContinuousState.Off)
            return;

        var now = DateTime.Now;

        // パケットが増えていれば「今も演奏が続いている」。
        var total = PacketTotal;
        var gotPacket = total != lastPacketTotal;

        if (gotPacket)
        {
            lastPacketTotal = total;
            lastPacketAt = now;
        }

        // 何をするかの判断は ContinuousPolicy に任せる。
        // 時間まわりの決まりを実機なしで検証できるようにするため。
        var action = ContinuousPolicy.Decide(
            Continuous == ContinuousState.Recording,
            gotPacket,
            (now - lastPacketAt).TotalSeconds,
            Config.ContinuousSongGapSeconds,
            Config.ContinuousIdleTimeoutMinutes);

        switch (action)
        {
            case ContinuousPolicy.Action.None:
                return;

            case ContinuousPolicy.Action.StartRecording:
                // 演奏が始まった。
                //
                // 全員を記録する。連続記録は「その場の演奏を残す」用途なので、
                // 誰かひとりに絞ると取りこぼす。
                // 誰を残すかは、あとから選び直せる。
                Config.RecordAllPerformers = true;
                StartRecording(0);

                Continuous = ContinuousState.Recording;
                ContinuousMessage = $"{ContinuousSavedCount + 1} 曲目を記録しています。";
                Log.Information("演奏を検出したので記録を開始しました。");
                return;

            case ContinuousPolicy.Action.GiveUp:
                Continuous = ContinuousState.Off;
                ContinuousMessage =
                    $"{Config.ContinuousIdleTimeoutMinutes:0} 分待っても演奏が無かったので停止しました"
                    + $"（保存 {ContinuousSavedCount} 曲）。";
                Log.Information("演奏が無いまま時間切れになりました。");
                return;
        }

        // ---- 曲の終わり。書き出して待機に戻る ----
        var result = StopAndExport();

        if (result != null && result.NoteCount > 0)
        {
            ContinuousSavedCount++;
            RememberSong(result);

            ContinuousMessage =
                $"{ContinuousSavedCount} 曲目を保存しました（{result.NoteCount} 音）。"
                + "次の演奏を待っています。";
            Log.Information($"曲の区切りを検出して保存しました: {result.NoteCount} 音");
        }
        else
        {
            // 音が取れなかった場合も、待機には戻る。
            // 生ログと診断は StopAndExport が残している。
            ContinuousMessage = "音を取り出せませんでした。次の演奏を待っています。";
        }

        // 次の曲に備える。
        //
        // 待ち時間の起点を今にしておかないと、
        // 直前の無音ぶんがそのまま時間切れに数えられてしまう。
        Continuous = ContinuousState.Waiting;
        lastPacketAt = now;
        lastPacketTotal = PacketTotal;
    }

    private void OnCommand(string command, string args) => OpenMain();

    private void OpenMain() => mainWindow.IsOpen = true;

    /// <summary>記録を開始する。付随するログも一緒に初期化する。</summary>
    internal void StartRecording(uint targetPerformerId)
    {
        RawLog.Clear();
        LocalKeys.Clear();

        Recorder.RecordAllPerformers = Config.RecordAllPerformers;
        Recorder.UseSlotTiming = Config.UseSlotTiming;
        Recorder.SlotIntervalMs = Config.SlotIntervalMs;
        Recorder.Start(targetPerformerId);
    }

    /// <summary>
    /// 既存のファイルと衝突しない日時文字列を作る。
    ///
    /// 秒単位の日時だけだと、1秒以内に2回記録を止めたときに
    /// 同じ名前になり、**前の記録を黙って上書きしてしまう**。
    /// 録り直しのきかないデータなので、必ず別名にする。
    ///
    /// 生ログ側と MIDI 側の両方を見る。
    /// 片方だけ見ると、もう片方で上書きが起きる。
    /// </summary>
    private string MakeUniqueStamp(string rawDir)
    {
        var baseStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var midiDir = ResolveMidiDirectory(rawDir);

        bool Taken(string s)
        {
            try
            {
                return File.Exists(Path.Combine(rawDir, $"raw_{s}.csv"))
                       || File.Exists(Path.Combine(rawDir, $"diagnosis_{s}.txt"))
                       || File.Exists(Path.Combine(midiDir, $"performance_{s}.mid"));
            }
            catch
            {
                // 調べられないときは「使われている」とみなす。
                // 上書きするより、名前を変えるほうが安全。
                return true;
            }
        }

        if (!Taken(baseStamp))
            return baseStamp;

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseStamp}_{i}";
            if (!Taken(candidate))
            {
                Log.Information($"同じ日時のファイルがあったため {candidate} で保存します。");
                return candidate;
            }
        }

        // ここまで来ることはまずないが、
        // 最後の手段としてミリ秒まで付ける。
        return DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
    }

    /// <summary>直近の書き出しで作ったファイルの一覧。画面に出す。</summary>
    internal List<string> LastSavedFiles { get; } = new();

    /// <summary>
    /// 直近の記録の内容。停止後も保持しておく。
    ///
    /// 「誰を残すか」を後から選び直して書き出し直せるようにするため。
    /// 録り直しを頼まずに済む。
    /// </summary>
    internal IReadOnlyList<NoteEventRecord> LastRecords { get; private set; } = [];

    /// <summary>直近の記録に含まれる演奏者（EntityId → 名前）。</summary>
    internal Dictionary<uint, string> LastPerformerNames { get; } = new();

    /// <summary>直近の記録の保存先（書き出し直すときに使う）。</summary>
    internal string LastStamp { get; private set; }

    /// <summary>直近の記録で、書き出しに含める演奏者。空なら全員。</summary>
    internal HashSet<uint> SelectedPerformers { get; } = new();

    /// <summary>
    /// 直近の記録に含まれる演奏者の一覧を、音数の多い順に返す。
    /// 画面でチェックを付けてもらうために使う。
    /// </summary>
    internal List<(uint Id, string Name, int Notes, int Instrument)> LastPerformerSummary()
    {
        var list = new List<(uint, string, int, int)>();

        foreach (var g in LastRecords
                     .Where(r => r.Edge == NoteEdge.On)
                     .GroupBy(r => r.PerformerId))
        {
            LastPerformerNames.TryGetValue(g.Key, out var name);

            var inst = g.Where(r => r.InstrumentId >= 0)
                .GroupBy(r => r.InstrumentId)
                .OrderByDescending(x => x.Count())
                .Select(x => x.Key)
                .FirstOrDefault(-1);

            list.Add((g.Key, name, g.Count(), inst));
        }

        return list.OrderByDescending(x => x.Item3).ToList();
    }

    /// <summary>
    /// 選び直した演奏者で MIDI を書き出し直す。
    ///
    /// 生ログもキューも触らないので、何度でもやり直せる。
    /// 元のファイルは消さず、別名で保存する。
    /// </summary>
    internal MidiExporter.ExportResult ReExport()
    {
        if (LastRecords.Count == 0)
        {
            Recorder.SetError("書き出し直せる記録がありません。先に記録してください。");
            return null;
        }

        var dir = string.IsNullOrWhiteSpace(Config.OutputDirectory)
            ? DefaultOutputDirectory()
            : Config.OutputDirectory;

        var midiDir = ResolveMidiDirectory(dir);

        // 元のファイルを消さないよう、選び直したぶんは別名にする。
        var suffix = SelectedPerformers.Count > 0 ? $"_選択{SelectedPerformers.Count}人" : "_全員";
        var path = Path.Combine(midiDir, $"performance_{LastStamp}{suffix}.mid");

        // 同じ人数で選び直すと名前が同じになる。
        // 前の書き出しを消さないよう、空いている名前まで番号を進める。
        for (var i = 2; File.Exists(path) && i < 1000; i++)
            path = Path.Combine(midiDir, $"performance_{LastStamp}{suffix}_{i}.mid");

        var options = new MidiExportOptions
        {
            BeatsPerMinute = Config.BeatsPerMinute,
            FallbackNoteLengthMs = Config.FallbackNoteLengthMs,
            MinimumNoteLengthMs = Config.MinimumNoteLengthMs,
            BuildStamp = BuildStamp,
            PerformerNames = LastPerformerNames,
            IncludePerformers = SelectedPerformers.Count > 0 ? SelectedPerformers : null,
        };

        try
        {
            var result = MidiExporter.Save(LastRecords, options, path);

            LastSavedFiles.Clear();
            LastSavedFiles.Add(path);
            LastSavedFiles.Add(Path.ChangeExtension(path, ".txt"));
            LastSavedFiles.Add(Path.ChangeExtension(path, ".json"));

            Log.Information($"選び直して書き出しました: {path}");
            return result;
        }
        catch (Exception e)
        {
            Recorder.SetError($"書き出し直しに失敗しました: {e.Message}");
            Log.Error(e, "書き出し直しに失敗しました。");
            return null;
        }
    }

    /// <summary>
    /// 記録を止めて、取れたものを全部書き出す。
    ///
    /// MIDI が作れない場合でも、生ログと診断は必ず残す。
    /// 「録れなかった」という事実こそ、次の手を決める材料になるため。
    /// </summary>
    internal MidiExporter.ExportResult StopAndExport()
    {
        Recorder.Stop();
        RawLog.Drain();

        LastSavedFiles.Clear();

        var dir = string.IsNullOrWhiteSpace(Config.OutputDirectory)
            ? DefaultOutputDirectory()
            : Config.OutputDirectory;

        // 秒単位だと、1秒以内に2回止めたときに衝突して
        // 前の記録を上書きしてしまう。
        // 既存のファイルがあれば連番を足して必ず別名にする。
        var stamp = MakeUniqueStamp(dir);
        var records = Recorder.Snapshot();
        var packets = RawLog.Snapshot();
        var keys = LocalKeys.Snapshot();

        // ---- 生ログと診断は、MIDI が作れなくても必ず出す ----
        try
        {
            if (packets.Count > 0)
            {
                var csv = Path.Combine(dir, $"raw_{stamp}.csv");
                RawPacketLog.SaveCsv(packets, csv);
                LastSavedFiles.Add(csv);

                var txt = Path.Combine(dir, $"raw_{stamp}.txt");
                RawPacketLog.SaveReadable(packets, txt);
                LastSavedFiles.Add(txt);
            }

            if (keys.Count > 0)
            {
                var keyCsv = Path.Combine(dir, $"localkeys_{stamp}.csv");
                LocalKeyLog.SaveCsv(keys, keyCsv);
                LastSavedFiles.Add(keyCsv);
            }

            var report = CalibrationAnalyzer.Analyze(
                packets, keys, PerformanceConstants.MidiNoteOffset);

            var reportPath = Path.Combine(dir, $"diagnosis_{stamp}.txt");
            Directory.CreateDirectory(dir);
            File.WriteAllText(reportPath, report.ToString(),
                new System.Text.UTF8Encoding(true));
            LastSavedFiles.Add(reportPath);

            LastDiagnosis = report;
        }
        catch (Exception e)
        {
            Recorder.SetError($"生ログの書き出しに失敗しました: {e.Message}");
            Log.Error(e, "生ログの書き出しに失敗しました。");
        }

        // ---- MIDI ----
        if (records.Count == 0)
        {
            // 受信数そのものは記録中かどうかに関わらず数えている。
            // 「届いていない」と「記録していなかった」は別物なので、
            // 取り違えないよう区別して伝える。
            if (packets.Count > 0)
                Recorder.SetError("音符を取り出せませんでした。生ログと診断は保存しています。");
            else if (Hooks.PerformanceOpcodeHits > 0)
                Recorder.SetError(
                    $"演奏パケットは {Hooks.PerformanceOpcodeHits} 件届いていますが、"
                    + "記録中ではなかったため保存されていません。"
                    + "「記録を開始」を押してから演奏させてください。");
            else
                Recorder.SetError("パケットが1件も届きませんでした。診断を確認してください。");

            return null;
        }

        // 生ログに残っている演奏者名を拾って、MIDI のトラック名に載せる。
        //
        // 名前の取得はゲーム側（オブジェクトテーブル）に依存するため、
        // MIDI 書き出し層には持ち込まず、ここで辞書にしてから渡す。
        var performerNames = new Dictionary<uint, string>();
        foreach (var p in packets)
        {
            if (!string.IsNullOrWhiteSpace(p.PerformerName))
                performerNames[p.SourceId] = p.PerformerName;
        }

        // 後から「誰を残すか」を選び直せるよう、記録内容を持っておく。
        LastRecords = records;
        LastStamp = stamp;
        LastPerformerNames.Clear();
        foreach (var kv in performerNames)
            LastPerformerNames[kv.Key] = kv.Value;

        // 前回の選択は引き継がない。人が入れ替わっていることがあるため。
        SelectedPerformers.Clear();

        var options = new MidiExportOptions
        {
            BeatsPerMinute = Config.BeatsPerMinute,
            FallbackNoteLengthMs = Config.FallbackNoteLengthMs,
            MinimumNoteLengthMs = Config.MinimumNoteLengthMs,
            BuildStamp = BuildStamp,
            PerformerNames = performerNames,
        };

        // 成果物（MIDI と .json と説明）は「作成Midi」へ分けて置く。
        var midiDir = ResolveMidiDirectory(dir);

        var path = Path.Combine(midiDir, $"performance_{stamp}.mid");

        try
        {
            var result = MidiExporter.Save(records, options, path);
            LastSavedFiles.Add(path);
            LastSavedFiles.Add(Path.ChangeExtension(path, ".txt"));
            LastSavedFiles.Add(Path.ChangeExtension(path, ".json"));
            Log.Information($"MIDI を書き出しました: {path}");
            return result;
        }
        catch (Exception e)
        {
            Recorder.SetError($"MIDI の書き出しに失敗しました: {e.Message}");
            Log.Error(e, "MIDI の書き出しに失敗しました。");
            return null;
        }
    }

    /// <summary>直近の診断結果。画面にそのまま出す。</summary>
    internal CalibrationAnalyzer.Report LastDiagnosis { get; private set; }

    internal void SetDiagnosis(CalibrationAnalyzer.Report report) => LastDiagnosis = report;

    internal void SaveConfig() => PluginInterface.SavePluginConfig(Config);

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandName);

        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMain;

        windowSystem.RemoveAllWindows();

        Hooks?.Dispose();
    }
}
