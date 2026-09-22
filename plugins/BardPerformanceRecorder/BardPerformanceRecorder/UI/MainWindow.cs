using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using BardPerformanceRecorder.Game;
using BardPerformanceRecorder.Midi;
using BardPerformanceRecorder.Recording;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace BardPerformanceRecorder.UI;

/// <summary>
/// 最小限の操作画面。
/// 「何が観測値で、何が推定値か」を画面上でも分かるようにする。
/// </summary>
public sealed unsafe class MainWindow : Window
{
    private readonly Plugin plugin;
    private MidiExporter.ExportResult lastResult;
    private string lastSavedPath;

    private uint selectedPerformer;

    public MainWindow(Plugin plugin)
        : base("演奏記録 (Bard Performance Recorder)###BardPerformanceRecorderMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 420),
            MaximumSize = new Vector2(1400, 1200),
        };
    }

    public override void Draw()
    {
        DrawStatus();
        ImGui.Separator();

        if (ImGui.BeginTabBar("##bprec_tabs"))
        {
            if (ImGui.BeginTabItem("記録"))
            {
                DrawRecordTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("設定"))
            {
                DrawSettingsTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("診断"))
            {
                DrawAnalysisTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("調査"))
            {
                DrawInvestigationTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("取得可否"))
            {
                DrawDiagnosticsTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawStatus()
    {
        var hooks = plugin.Hooks;

        ImGui.TextDisabled($"動作中のビルド: {Plugin.BuildStamp}");

        if (!hooks.SoloHooked && !hooks.EnsembleHooked)
        {
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f),
                "受信フックが有効になっていません。記録できません。");
            if (!string.IsNullOrEmpty(hooks.LastHookError))
                ImGui.TextWrapped($"理由: {hooks.LastHookError}");
            if (!string.IsNullOrEmpty(plugin.Signatures.SoloError))
                ImGui.TextWrapped($"ソロ: {plugin.Signatures.SoloError}");
            return;
        }

        ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f),
            $"フック有効  ソロ:{(hooks.SoloHooked ? "○" : "×")} "
            + $"合奏:{(hooks.EnsembleHooked ? "○" : "×")}");

        ImGui.SameLine();
        var ensembleNote = plugin.Config.RecordEnsemblePackets ? "" : "・記録対象外";
        ImGui.TextDisabled(
            $"(受信 ソロ{hooks.SoloPacketCount} / 合奏{hooks.EnsemblePacketCount}{ensembleNote})");
    }

    /// <summary>
    /// 連続記録。押しておくと、演奏を待って曲ごとに勝手に保存する。
    ///
    /// 演奏会のように何曲も続けて弾かれる場では、
    /// 曲ごとに開始・停止を押すのが現実的でないため。
    /// </summary>
    private void DrawContinuous()
    {
        var state = plugin.Continuous;

        if (state == Plugin.ContinuousState.Off)
        {
            if (ImGui.Button("ずっと記録する", new Vector2(180, 34)))
            {
                plugin.StartContinuous();
                lastResult = null;
                lastSavedPath = null;
            }

            ImGui.SameLine();
            ImGui.TextDisabled("演奏を待って、曲ごとに自動で保存します");

            ImGui.TextDisabled(
                $"  曲の区切り: 演奏が {plugin.Config.ContinuousSongGapSeconds:0} 秒 途切れたら1曲とみなす");
            ImGui.TextDisabled(
                $"  自動終了  : {plugin.Config.ContinuousIdleTimeoutMinutes:0} 分 演奏が無ければ待機をやめる");
            return;
        }

        // 動作中。
        var recording = state == Plugin.ContinuousState.Recording;

        ImGui.TextColored(
            recording ? new Vector4(0.4f, 1f, 0.4f, 1f) : new Vector4(1f, 0.85f, 0.3f, 1f),
            recording ? "● 連続記録：記録中" : "○ 連続記録：演奏を待っています");

        if (ImGui.Button("連続記録をやめる", new Vector2(180, 30)))
            plugin.StopContinuous();

        ImGui.SameLine();
        ImGui.Text($"保存した曲: {plugin.ContinuousSavedCount}");

        if (!string.IsNullOrEmpty(plugin.ContinuousMessage))
            ImGui.TextWrapped(plugin.ContinuousMessage);

        // 録れた曲を並べる。回しっぱなしにしたあと、
        // 何が録れたか一目で分かるように。
        if (plugin.ContinuousSongs.Count > 0
            && ImGui.CollapsingHeader($"録れた曲（{plugin.ContinuousSongs.Count}）"))
        {
            foreach (var (at, path, notes, performers) in plugin.ContinuousSongs)
            {
                ImGui.BulletText(
                    $"{at:HH:mm:ss}  {notes} 音 / {performers} 人  {Path.GetFileName(path)}");
            }

            var dir = Path.GetDirectoryName(plugin.ContinuousSongs[0].Path);
            if (!string.IsNullOrEmpty(dir))
            {
                ImGui.TextWrapped($"場所: {dir}");

                if (ImGui.Button("フォルダを開く##cont"))
                {
                    try
                    {
                        if (Directory.Exists(dir))
                            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"")
                            {
                                UseShellExecute = true,
                            });
                    }
                    catch (Exception e)
                    {
                        plugin.Recorder.SetError($"フォルダを開けませんでした: {e.Message}");
                    }
                }
            }
        }
    }

    private void DrawRecordTab()
    {
        var rec = plugin.Recorder;

        ImGui.Spacing();
        DrawContinuous();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 連続記録が動いている間は、手動の操作を出さない。
        // 両方から止めたり始めたりできると、どちらの状態か分からなくなる。
        if (plugin.Continuous != Plugin.ContinuousState.Off)
        {
            DrawLiveState();
            return;
        }

        DrawPerformerPicker();
        ImGui.Spacing();

        if (!rec.IsRecording)
        {
            if (ImGui.Button("記録を開始", new Vector2(140, 30)))
            {
                plugin.StartRecording(selectedPerformer);
                lastResult = null;
                lastSavedPath = null;
            }

            if (selectedPerformer == 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(演奏者未選択：最初に音を出した人を自動で掴みます)");
            }

            // 記録していない間に演奏が届いていたら知らせる。
            // これが無いと「開始し忘れ」に気づけない。
            if (plugin.Hooks.PerformanceOpcodeHits > 0)
                ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f),
                    $"演奏パケットを {plugin.Hooks.PerformanceOpcodeHits} 件受信しています。"
                    + "記録するには「記録を開始」を押してください。");
        }
        else
        {
            if (ImGui.Button("記録を停止して書き出し", new Vector2(200, 30)))
            {
                lastResult = plugin.StopAndExport();
                lastSavedPath = lastResult?.FilePath;
            }

            ImGui.SameLine();
            if (ImGui.Button("破棄して停止", new Vector2(120, 30)))
            {
                rec.Stop();
                lastResult = null;
            }
        }

        ImGui.Spacing();
        ImGui.Separator();

        ImGui.Text($"状態       : {(rec.IsRecording ? "記録中" : "停止")}");
        ImGui.Text($"経過時間   : {rec.Elapsed.TotalSeconds:F1} 秒");
        ImGui.Text($"音符数     : {rec.NoteOnCount}");
        ImGui.Text($"イベント数 : {rec.EventCount}  (発音+推定消音)");

        if (rec.ActivePerformerId != 0)
            ImGui.Text($"記録対象   : EntityId 0x{rec.ActivePerformerId:X8}");

        ImGui.Text($"生パケット : {plugin.RawLog.Count} 件"
                   + (plugin.RawLog.OverflowCount > 0
                       ? $"（上限超過 {plugin.RawLog.OverflowCount} 件を破棄）" : ""));

        if (plugin.Config.LogLocalKeys)
            ImGui.Text($"自分の押鍵 : {plugin.LocalKeys.PressCount} 回");

        if (rec.DiscardedCount > 0 || plugin.Hooks.RejectedPacketCount > 0)
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f),
                $"破棄した異常データ: 音符{rec.DiscardedCount} / パケット{plugin.Hooks.RejectedPacketCount}");

        DrawLiveState();

        if (!string.IsNullOrEmpty(rec.LastError))
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), $"エラー: {rec.LastError}");

        ImGui.Spacing();
        ImGui.Separator();
        DrawResult();
    }

    /// <summary>
    /// いま自分が何を押しているかを出す。
    /// 実機で「フックは効いているのか」「値は妥当か」を
    /// その場で判断するための表示。
    /// </summary>
    private void DrawLiveState()
    {
        var local = plugin.LocalPerformance;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("自分の演奏状態（照合用）");

        if (!local.Available)
        {
            ImGui.TextDisabled("  取得できていません。演奏モードに入ると取得できます。");
            return;
        }

        if (!local.Plausible)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f),
                $"  値が想定範囲外です（{local.ImplausibleCount} 回）。"
                + "オフセットが古い可能性があります。");
            ImGui.TextDisabled("  →「調査」タブで実測できます。");
        }

        var pressed = local.NotePressed
            ? $"{local.CurrentPressingNote}"
            : $"なし (生値 {local.RawPressingNote})";

        ImGui.Text($"  演奏モード : {(local.InPerformanceMode ? "入っている" : "入っていない")}");
        ImGui.Text($"  押している鍵 : {pressed}   オクターブ: {local.OctaveOffset}   音色: {local.GroupTone}");

        if (local.NotePressed)
        {
            // 押鍵はパケットと基準が違うので、押鍵用のずらし量を使う。
            var midi = local.CurrentPressingNote + PerformanceConstants.LocalKeyMidiOffset;
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f),
                $"  → MIDI {midi} ({NoteName(midi)}) として書き出されます");
        }
    }

    private void DrawResult()
    {
        if (lastResult == null && plugin.LastSavedFiles.Count == 0)
            return;

        if (lastResult == null)
        {
            // MIDI は作れなかったが、生ログは残っている場合。
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f),
                "MIDI は作れませんでしたが、生ログと診断は保存しました。");
            DrawSavedFiles();
            return;
        }

        ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "書き出しました。");
        ImGui.Text($"音符数 : {lastResult.NoteCount}");
        ImGui.Text($"長さ   : {lastResult.Duration.TotalSeconds:F2} 秒");

        ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f),
            "※ 音の長さはすべて推定値です（パケットに音長は含まれません）");
        ImGui.BulletText($"消音を観測できた音: {lastResult.InferredLengthCount} 個");
        ImGui.BulletText($"暫定長を当てた音  : {lastResult.FallbackLengthCount} 個");
        if (lastResult.DroppedCount > 0)
            ImGui.BulletText($"破棄したデータ    : {lastResult.DroppedCount} 件");

        if (lastResult.Excluded.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), "絞り込みで除いた演奏者");
            foreach (var (id, name, events) in lastResult.Excluded)
            {
                var who = string.IsNullOrEmpty(name) ? $"0x{id:X8}" : name;
                ImGui.BulletText($"{who}（{events} 件）");
            }
        }

        ImGui.Spacing();
        DrawSavedFiles();

        ImGui.Spacing();
        ImGui.Separator();
        DrawReExport();
    }

    /// <summary>
    /// 記録した演奏者を選び直して、MIDI を作り直す。
    ///
    /// 近くで無関係な人がソロ演奏していると、その音も届いて混ざる
    /// （実測で確認済み）。録り直しを頼まずに済むよう、
    /// 記録は全員ぶん残したまま、書き出しのときだけ絞る。
    /// </summary>
    private void DrawReExport()
    {
        var performers = plugin.LastPerformerSummary();
        if (performers.Count == 0)
            return;

        if (!ImGui.CollapsingHeader($"演奏者を選んで作り直す（{performers.Count}人）"))
            return;

        ImGui.TextWrapped(
            "記録には全員ぶん残っています。ここで選んだ人だけの MIDI を"
            + "別名で作り直せます。元のファイルは消えません。");

        ImGui.Spacing();

        if (ImGui.Button("全員を選ぶ"))
        {
            plugin.SelectedPerformers.Clear();
            foreach (var p in performers)
                plugin.SelectedPerformers.Add(p.Id);
        }

        ImGui.SameLine();
        if (ImGui.Button("全部はずす"))
            plugin.SelectedPerformers.Clear();

        ImGui.SameLine();
        if (ImGui.Button("反転"))
        {
            foreach (var p in performers)
            {
                if (!plugin.SelectedPerformers.Remove(p.Id))
                    plugin.SelectedPerformers.Add(p.Id);
            }
        }

        ImGui.Spacing();

        foreach (var (id, name, notes, instrument) in performers)
        {
            var on = plugin.SelectedPerformers.Contains(id);
            var who = string.IsNullOrEmpty(name) ? $"0x{id:X8}" : name;
            var inst = InstrumentNames.TryGet(instrument)
                       ?? (instrument >= 0 ? $"ID{instrument}" : "楽器不明");

            if (ImGui.Checkbox($"{who}　{inst}　{notes} 音##sel{id:X8}", ref on))
            {
                if (on) plugin.SelectedPerformers.Add(id);
                else plugin.SelectedPerformers.Remove(id);
            }
        }

        ImGui.Spacing();

        var count = plugin.SelectedPerformers.Count;
        var label = count == 0
            ? "全員で作り直す"
            : $"選んだ {count} 人で作り直す";

        if (ImGui.Button(label))
        {
            var r = plugin.ReExport();
            if (r != null)
                lastResult = r;
        }

        if (count == 0)
            ImGui.TextDisabled("  誰も選んでいないときは全員ぶんを書き出します。");
    }

    /// <summary>保存したファイルを全部並べる。絶対パスで出す。</summary>
    private void DrawSavedFiles()
    {
        if (plugin.LastSavedFiles.Count == 0)
            return;

        ImGui.Spacing();
        ImGui.Text("保存したファイル");
        foreach (var f in plugin.LastSavedFiles)
            ImGui.BulletText(Path.GetFileName(f));

        var dir = Path.GetDirectoryName(plugin.LastSavedFiles[0]);
        ImGui.TextWrapped($"場所: {dir}");

        if (ImGui.Button("フォルダを開く"))
        {
            try
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"")
                    {
                        UseShellExecute = true,
                    });
            }
            catch (Exception e)
            {
                plugin.Recorder.SetError($"フォルダを開けませんでした: {e.Message}");
            }
        }
    }

    /// <summary>
    /// 演奏者の選択。いま演奏モードに入っている近くのプレイヤーを並べる。
    /// </summary>
    private void DrawPerformerPicker()
    {
        var candidates = new List<(uint id, string name, int instrument)>();

        try
        {
            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc)
                    continue;

                var native = (Character*)obj.Address;
                if (native == null)
                    continue;

                if (native->Mode != CharacterModes.Performance)
                    continue;

                candidates.Add((obj.EntityId, obj.Name.TextValue, native->ModeParam));
            }
        }
        catch (Exception e)
        {
            ImGui.TextDisabled($"演奏者一覧を取得できません: {e.Message}");
        }

        var label = selectedPerformer == 0
            ? "(自動：最初に音を出した人)"
            : candidates.FirstOrDefault(c => c.id == selectedPerformer).name
              ?? $"0x{selectedPerformer:X8}";

        ImGui.SetNextItemWidth(320);
        if (ImGui.BeginCombo("記録する演奏者", label))
        {
            if (ImGui.Selectable("(自動：最初に音を出した人)", selectedPerformer == 0))
                selectedPerformer = 0;

            foreach (var (id, name, instrument) in candidates)
            {
                var text = $"{name}  [楽器ID {instrument}]";
                if (ImGui.Selectable(text, selectedPerformer == id))
                    selectedPerformer = id;
            }

            ImGui.EndCombo();
        }

        if (candidates.Count == 0)
            ImGui.TextDisabled("いま演奏モードのプレイヤーは見つかりません。");
        else
            ImGui.TextDisabled($"演奏モードのプレイヤー: {candidates.Count} 人");
    }

    private void DrawSettingsTab()
    {
        var cfg = plugin.Config;
        var changed = false;

        ImGui.Spacing();
        ImGui.TextWrapped("MIDI の保存先（完成した .mid と .json）");
        var midiDir = cfg.MidiOutputDirectory ?? string.Empty;
        ImGui.SetNextItemWidth(420);
        if (ImGui.InputText("##mididir", ref midiDir, 512))
        {
            cfg.MidiOutputDirectory = midiDir;
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("既定に戻す##midi"))
        {
            cfg.MidiOutputDirectory = @"D:\MIDI\作成Midi";
            changed = true;
        }

        ImGui.TextDisabled("  ドライブが外れているなど使えないときは、生ログの隣に保存します。");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextWrapped("連続記録（「ずっと記録する」）");

        var gap = (float)cfg.ContinuousSongGapSeconds;
        ImGui.SetNextItemWidth(160);
        if (ImGui.DragFloat("曲の区切りとみなす無音（秒）", ref gap, 0.5f, 3f, 120f, "%.0f"))
        {
            cfg.ContinuousSongGapSeconds = gap;
            changed = true;
        }

        ImGui.TextDisabled("  合奏パケットは約3秒ごとに届きます。短くしすぎると曲の途中で切れます。");

        var idle = (float)cfg.ContinuousIdleTimeoutMinutes;
        ImGui.SetNextItemWidth(160);
        if (ImGui.DragFloat("演奏が無いとき待つ時間（分）", ref idle, 1f, 1f, 600f, "%.0f"))
        {
            cfg.ContinuousIdleTimeoutMinutes = idle;
            changed = true;
        }

        ImGui.TextDisabled("  この時間を過ぎると待機をやめます。押しっぱなしの放置に備えたものです。");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextWrapped("生ログ・診断の保存先");
        var dir = cfg.OutputDirectory ?? string.Empty;
        ImGui.SetNextItemWidth(420);
        if (ImGui.InputText("##outdir", ref dir, 512))
        {
            cfg.OutputDirectory = dir;
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("既定に戻す##raw"))
        {
            cfg.OutputDirectory = Plugin.DefaultOutputDirectory();
            changed = true;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), "以下はすべて推定のための設定です");
        ImGui.TextWrapped(
            "元の演奏データにテンポ・音長・強弱は含まれません。"
            + "ここで決めるのは「観測した実時間を MIDI に写すときの基準値」です。");

        ImGui.Spacing();

        var bpm = (float)cfg.BeatsPerMinute;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("書き出し基準 BPM", ref bpm, 40f, 240f, "%.0f"))
        {
            cfg.BeatsPerMinute = bpm;
            changed = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("元曲のテンポではありません。実時間を保ったまま MIDI の時間軸へ変換します。");

        var fallback = cfg.FallbackNoteLengthMs;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("暫定の音長 (ms)", ref fallback, 30, 2000))
        {
            cfg.FallbackNoteLengthMs = fallback;
            changed = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("消音を観測できなかった音に当てる長さ。記録停止時に鳴っていた音など。");

        ImGui.Spacing();
        var slotTiming = cfg.UseSlotTiming;
        if (ImGui.Checkbox("スロット位置で時刻を細かくする（推奨）", ref slotTiming))
        {
            cfg.UseSlotTiming = slotTiming;
            plugin.Recorder.UseSlotTiming = slotTiming;
            changed = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "パケットは約0.5秒ごとに届き、その間の音が10スロットに順に入ります。\n"
                + "スロット番号を時刻に反映すると、受信間隔より細かく復元できます。\n"
                + "実測ではタイミングのずれが 179ms → 51ms に改善しました。");

        if (cfg.UseSlotTiming)
        {
            var slotMs = (float)cfg.SlotIntervalMs;
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderFloat("スロット間隔 (ms)", ref slotMs, 10f, 100f, "%.0f"))
            {
                cfg.SlotIntervalMs = slotMs;
                plugin.Recorder.SlotIntervalMs = slotMs;
                changed = true;
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("受信間隔(約490ms) ÷ 10スロット ≒ 50ms");
        }

        ImGui.Spacing();
        var minimum = cfg.MinimumNoteLengthMs;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("最短の音長 (ms)", ref minimum, 1, 500))
        {
            cfg.MinimumNoteLengthMs = minimum;
            changed = true;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("取得する情報");
        ImGui.Spacing();

        var ensemble = cfg.RecordEnsemblePackets;
        if (ImGui.Checkbox("合奏パケットも記録対象にする", ref ensemble))
        {
            cfg.RecordEnsemblePackets = ensemble;
            changed = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("他人の演奏が合奏側に届いている場合、これを入れないと記録できません。");

        var raw = cfg.LogRawPackets;
        if (ImGui.Checkbox("生パケットを残す（推奨）", ref raw))
        {
            cfg.LogRawPackets = raw;
            changed = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "解釈前のバイト列をそのまま保存します。\n"
                + "読み方が間違っていても、録り直さずに後から読み直せます。");

        var keys = cfg.LogLocalKeys;
        if (ImGui.Checkbox("自分が押した鍵を記録する（推奨）", ref keys))
        {
            cfg.LogLocalKeys = keys;
            changed = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "自分で弾くと「何を弾いたか」が確実に分かります。\n"
                + "受信データと突き合わせて、音高のずれを自動判定できます。");

        var all = cfg.RecordAllPerformers;
        if (ImGui.Checkbox("演奏者を絞らず全部記録する", ref all))
        {
            cfg.RecordAllPerformers = all;
            changed = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("人のいない場所での検証向け。取りこぼしがなくなります。");

        ImGui.Spacing();
        var dispatcher = cfg.UseDispatcherHook;
        if (ImGui.Checkbox("ディスパッチャ経由で取得する（推奨）", ref dispatcher))
        {
            cfg.UseDispatcherHook = dispatcher;
            plugin.Hooks.ShouldWatchOpcodes = () => cfg.UseDispatcherHook;
            changed = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "個別の演奏ハンドラが呼ばれない場合でも、\n"
                + "全パケットの入口から opcode で直接拾います。");

        var opcode = cfg.PerformanceOpcode;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("演奏パケットの opcode (10進)", ref opcode, 1, 16))
        {
            cfg.PerformanceOpcode = opcode;
            plugin.Hooks.PerformanceOpcode = opcode;
            changed = true;
        }

        ImGui.TextDisabled($"  現在 0x{cfg.PerformanceOpcode:X}（10進 {cfg.PerformanceOpcode}）");
        ImGui.TextDisabled("  ゲーム更新で変わります。「調査」タブで確認できます。");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("合奏の調査用");
        ImGui.TextWrapped(
            "合奏パケットの形式を調べるときだけ使います。"
            + "ふだんは触らなくて構いません。");
        ImGui.Spacing();

        var header = cfg.LogPacketHeader;
        if (ImGui.Checkbox("IPCヘッダも生ログに残す", ref header))
        {
            cfg.LogPacketHeader = header;
            plugin.Hooks.ShouldLogHeader = () => cfg.LogPacketHeader;
            changed = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "ペイロードの手前 16 バイトも保存します。\n"
                + "パケットの形式そのものを調べたいときだけ使います。\n\n"
                + "※ 入れると生ログ全体が 16 バイトずれるため、\n"
                + "　 ログ再生（Replay）と解析ツールが正しく読めなくなります。\n"
                + "　 ふだんの記録では外しておいてください。");

        if (cfg.LogPacketHeader)
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f),
                "  ※ 入れている間は、ログ再生と解析ツールが使えません。");

        var capture = cfg.CaptureBytes;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("残すバイト数", ref capture, 8, 64))
        {
            cfg.CaptureBytes = Math.Clamp(capture, 24, 2048);
            plugin.Hooks.CaptureBytes = cfg.CaptureBytes;
            changed = true;
        }

        ImGui.TextDisabled($"  現在 {cfg.CaptureBytes} バイト（ソロは 24 で足ります）");
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f),
            "  ※ 安全に読める長さは確認できていません。");
        ImGui.TextWrapped(
            "   ゲームから長さが渡されないため、大きくしすぎると"
            + "関係ないメモリを読む可能性があります。調査時のみ広げてください。");

        if (changed)
            plugin.SaveConfig();
    }

    /// <summary>
    /// 記録したデータの自動診断。録ったその場で結果が読めるようにする。
    /// </summary>
    private void DrawAnalysisTab()
    {
        ImGui.Spacing();
        ImGui.TextWrapped(
            "記録したデータから、読み方が正しいかを自動で判定します。"
            + "停止して書き出すと、ここに結果が出ます。");
        ImGui.Spacing();

        if (ImGui.Button("いま溜まっているデータで診断する", new Vector2(280, 28)))
        {
            plugin.RawLog.Drain();
            RunAnalysisNow();
        }

        ImGui.Spacing();
        ImGui.Separator();

        var report = plugin.LastDiagnosis;
        if (report == null)
        {
            ImGui.TextDisabled("まだ診断結果がありません。");
            return;
        }

        if (report.SuggestedMidiNoteOffset is { } suggested
            && suggested != PerformanceConstants.MidiNoteOffset)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f),
                $"提案: MidiNoteOffset を {PerformanceConstants.MidiNoteOffset} → {suggested} に変更");
            ImGui.TextWrapped("  Game\\PerformancePackets.cs の MidiNoteOffset を書き換えてください。");
            ImGui.Separator();
        }

        ImGui.BeginChild("##report", new Vector2(0, 0), true,
            ImGuiWindowFlags.HorizontalScrollbar);

        foreach (var line in report.Lines)
            ImGui.TextUnformatted(line);

        ImGui.EndChild();
    }

    /// <summary>
    /// 取れないときの原因を突き止めるためのタブ。
    /// 生の値をそのまま出し、押鍵オフセットを実測で探せるようにする。
    /// </summary>
    private void DrawInvestigationTab()
    {
        var local = plugin.LocalPerformance;
        var hooks = plugin.Hooks;

        ImGui.Spacing();

        // ---- フックが呼ばれているか ----
        ImGui.Text("■ フックの発火状況");
        ImGui.Spacing();

        ImGui.Text($"  ソロ受信 : {hooks.SoloPacketCount} 回");
        if (hooks.SoloPacketCount > 0)
            ImGui.Text($"     最後 : {hooks.LastSoloHitAt:HH:mm:ss}  送り元 0x{hooks.LastSoloSourceId:X8}");

        ImGui.Text($"  合奏受信 : {hooks.EnsemblePacketCount} 回");
        if (hooks.EnsemblePacketCount > 0)
            ImGui.Text($"     最後 : {hooks.LastEnsembleHitAt:HH:mm:ss}  送り元 0x{hooks.LastEnsembleSourceId:X8}");

        if (hooks.SoloPacketCount == 0 && hooks.EnsemblePacketCount == 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f),
                "  個別ハンドラは呼ばれていません（ディスパッチャ経由で取得します）。");
        }

        ImGui.Spacing();
        ImGui.Text($"  ディスパッチャ : {(hooks.DispatcherHooked ? "有効" : "無効")}  "
                   + $"通過 {hooks.DispatcherCallCount} 回");
        ImGui.Text($"  演奏 opcode 0x{plugin.Config.PerformanceOpcode:X} の一致 : "
                   + $"{hooks.PerformanceOpcodeHits} 件");

        // 観測した opcode の上位。演奏中に何が流れているかを見る。
        if (hooks.OpcodeCounts.Count > 0)
        {
            ImGui.Spacing();
            ImGui.Text("  観測した opcode（件数の多い順・上位12件）");

            foreach (var kv in hooks.OpcodeCounts
                         .OrderByDescending(k => k.Value)
                         .Take(12))
            {
                var mark = kv.Key == plugin.Config.PerformanceOpcode ? "  ★演奏" : "";
                ImGui.Text($"     0x{kv.Key:X3} ({kv.Key,5})  {kv.Value,7} 件{mark}");
            }

            if (ImGui.Button("opcode の集計を消す", new Vector2(180, 24)))
                hooks.OpcodeCounts.Clear();
        }

        ImGui.Spacing();
        ImGui.Separator();

        // ---- 自分の演奏状態の生値 ----
        ImGui.Text("■ AgentPerformance の生の値");
        ImGui.Spacing();

        if (!local.AgentFound)
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f),
                "  Agent を取得できていません。");
            ImGui.TextWrapped($"  {local.LastError}");
        }
        else
        {
            ImGui.Text($"  Agent アドレス : 0x{local.AgentAddress:X}");
            ImGui.Text($"  +0x20 演奏中フラグ : {local.RawInPerformanceByte}");
            ImGui.Text($"  +0x60 押鍵(想定)   : {local.RawPressingNote}");
            ImGui.Text($"  +0x5C 音符ずらし   : {local.RawNoteOffset}");
            ImGui.Text($"  +0xFC オクターブ   : {local.RawOctaveOffset}");
            ImGui.Text($"  +0x1D8 音色        : {local.RawGroupTone}");

            if (!local.Plausible)
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f),
                    $"  ※ 値が想定範囲外です（{local.ImplausibleCount} 回）");
        }

        ImGui.Spacing();
        ImGui.Separator();

        // ---- 押した鍵の値の履歴 ----
        ImGui.Text("■ 押した鍵の値（音高の対応を確かめる）");
        ImGui.TextWrapped(
            "演奏モードで鍵を押すと、ここに値が溜まります。"
            + "いちばん低い鍵から順に押して、値の並びを確認してください。");
        ImGui.Spacing();

        if (ImGui.Button("履歴を消す", new Vector2(120, 24)))
            pressHistory.Clear();

        ImGui.SameLine();
        ImGui.TextDisabled($"{pressHistory.Count} 件");

        // 押鍵を検出して履歴に積む
        if (local.AgentFound
            && local.RawPressingNote != LocalPerformanceReaderConstants.NoNotePressed
            && local.RawPressingNote != lastSeenPress)
        {
            pressHistory.Add(local.RawPressingNote);
            if (pressHistory.Count > 60)
                pressHistory.RemoveAt(0);
        }

        lastSeenPress = local.RawPressingNote;

        if (pressHistory.Count > 0)
        {
            ImGui.TextUnformatted("  押した順: "
                                  + string.Join(", ", pressHistory));

            var min = pressHistory.Min();
            var max = pressHistory.Max();
            ImGui.Text($"  最小 {min} / 最大 {max} / 幅 {max - min}");

            if (max - min > 36)
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f),
                    "  ※ 37鍵の幅を超えています。オクターブ変更が混ざっている可能性。");

            ImGui.Spacing();

            var midiMin = min + PerformanceConstants.LocalKeyMidiOffset;
            var midiMax = max + PerformanceConstants.LocalKeyMidiOffset;

            ImGui.TextWrapped(
                $"  値 {min} → MIDI {midiMin} ({NoteName(midiMin)}) / "
                + $"値 {max} → MIDI {midiMax} ({NoteName(midiMax)})");

            if (min == PerformanceConstants.LocalKeyLowest && midiMin == 48)
                ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f),
                    "  いちばん低い鍵が C3 になっています。対応は正しいです。");
        }

        ImGui.Spacing();
        ImGui.Separator();

        // ---- 押鍵オフセットの探索 ----
        ImGui.Text("■ 押鍵オフセットの探索");
        ImGui.TextWrapped(
            "「+0x60 が押鍵」という想定が今のゲームで合っているかを実測します。");
        ImGui.Spacing();

        ImGui.TextWrapped("手順：演奏モードに入り、鍵を押していない状態で①、"
                          + "鍵を押したまま②を何度か押してください。");
        ImGui.Spacing();

        if (ImGui.Button("① 基準を取る（何も押していない状態で）", new Vector2(320, 26)))
        {
            var snap = plugin.LocalPerformance.DumpAgentMemory();
            if (snap == null)
                plugin.Recorder.SetError("メモリを取得できません。演奏モードに入ってください。");
            else
                plugin.MemoryWatcher.SetBaseline(snap);
        }

        ImGui.SameLine();
        ImGui.TextDisabled(plugin.MemoryWatcher.HasBaseline ? "取得済み" : "未取得");

        if (ImGui.Button("② 比較する（鍵を押しながら）", new Vector2(320, 26)))
        {
            var snap = plugin.LocalPerformance.DumpAgentMemory();
            if (snap == null)
                plugin.Recorder.SetError("メモリを取得できません。");
            else
                plugin.MemoryWatcher.Compare(snap);
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"比較 {plugin.MemoryWatcher.SampleCount} 回");

        if (ImGui.Button("結果を見る", new Vector2(150, 26)))
            watcherReport = plugin.MemoryWatcher.BuildReport(0x60);

        ImGui.SameLine();
        if (ImGui.Button("やり直す", new Vector2(120, 26)))
        {
            plugin.MemoryWatcher.Reset();
            watcherReport = null;
        }

        ImGui.Spacing();

        if (watcherReport != null)
        {
            ImGui.BeginChild("##watcher", new Vector2(0, 0), true,
                ImGuiWindowFlags.HorizontalScrollbar);

            foreach (var line in watcherReport)
                ImGui.TextUnformatted(line);

            ImGui.EndChild();
        }
    }

    private List<string> watcherReport;

    /// <summary>MIDI ノート番号を音名にする。照合を目で確かめるため。</summary>
    private static string NoteName(int midi)
    {
        if (midi is < 0 or > 127)
            return "?";

        string[] names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        return $"{names[midi % 12]}{midi / 12 - 1}";
    }

    /// <summary>押した鍵の値の履歴。音高の対応を目で確かめるため。</summary>
    private readonly List<int> pressHistory = new();
    private int lastSeenPress = LocalPerformanceReaderConstants.NoNotePressed;

    private void RunAnalysisNow()
    {
        try
        {
            var packets = plugin.RawLog.Snapshot();
            var keys = plugin.LocalKeys.Snapshot();
            plugin.SetDiagnosis(CalibrationAnalyzer.Analyze(
                packets, keys, PerformanceConstants.MidiNoteOffset));
        }
        catch (Exception e)
        {
            plugin.Recorder.SetError($"診断に失敗しました: {e.Message}");
        }
    }

    /// <summary>
    /// 第1段階の「取得できた／推定できる／取得できない」を画面で確認できるようにする。
    /// </summary>
    private void DrawDiagnosticsTab()
    {
        ImGui.Spacing();
        ImGui.TextWrapped(
            "このプラグインが何を取得できて、何を取得できないかの一覧です。"
            + "出力の信頼度を判断するために確認してください。");
        ImGui.Spacing();

        if (ImGui.BeginTable("##diag", 3,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("項目");
            ImGui.TableSetupColumn("判定");
            ImGui.TableSetupColumn("根拠 / 注意");
            ImGui.TableHeadersRow();

            Row("演奏者ID", "取得できた", "受信ハンドラの第1引数 (EntityId)");
            Row("音符番号", "取得できた", "パケット内の音符バイト列");
            Row("音色 (tone)", "取得できた", "パケット内の音色バイト列。ギター系の音色切替など");
            Row("楽器", "推定できる", "Character.ModeParam を受信時に別途読む。視界外だと取れない");
            Row("音の開始時刻", "推定できる", "パケット受信時刻。演奏者が弾いた時刻とは通信の遅れぶんずれる");
            Row("音の終了 / 長さ", "取得できない", "パケットに音長は無い。次のパケットで音が消えたことから推定");
            Row("テンポ", "取得できない", "パケットに無い。書き出し基準値を使う");
            Row("強弱 (ベロシティ)", "取得できない", "パケットに無い。固定値で書き出す");
            Row("合奏時の各演奏者", "取得できた", "合奏パケットは演奏者ごとに枠が分かれている");

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("シグネチャ解決状況");
        ImGui.BulletText($"ソロ受信   : {(plugin.Signatures.SoloResolved ? $"0x{plugin.Signatures.SoloAddress:X}" : "解決できず")}");
        ImGui.BulletText($"合奏受信   : {(plugin.Signatures.EnsembleResolved ? $"0x{plugin.Signatures.EnsembleAddress:X}" : "解決できず")}");
        ImGui.BulletText($"ディスパッチャ : {(plugin.Signatures.DispatcherResolved ? $"0x{plugin.Signatures.DispatcherAddress:X}" : "解決できず")}");

        ImGui.Spacing();
        ImGui.TextWrapped(
            "シグネチャが解決できない場合、ゲームの更新でパターンが変わった可能性があります。"
            + "README の「シグネチャが壊れたとき」を参照してください。");
        return;

        void Row(string item, string verdict, string note)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item);
            ImGui.TableNextColumn();

            var color = verdict switch
            {
                "取得できた" => new Vector4(0.4f, 1f, 0.4f, 1f),
                "推定できる" => new Vector4(1f, 0.85f, 0.3f, 1f),
                _ => new Vector4(1f, 0.45f, 0.45f, 1f),
            };

            ImGui.TextColored(color, verdict);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(note);
        }
    }
}
