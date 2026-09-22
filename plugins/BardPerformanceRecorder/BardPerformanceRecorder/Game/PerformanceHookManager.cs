using System;
using BardPerformanceRecorder.Recording;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace BardPerformanceRecorder.Game;

/// <summary>
/// 演奏受信ハンドラのフック。
///
/// フック内でやってよいのは「必要なバイトを固定長バッファへ写して
/// キューに積む」ことだけ。例外はすべて内側で握りつぶし、
/// 元の処理は必ず呼ぶ（ゲームを壊さないため）。
///
/// フック対象の関数シグネチャは MidiBard プロジェクト
/// (akira0245, AGPL-3.0) の Offsets.cs に由来する。
/// </summary>
public sealed unsafe class PerformanceHookManager : IDisposable
{
    private delegate IntPtr PerformanceRecvDelegate(uint sourceId, IntPtr data);

    /// <summary>
    /// パケット受信ディスパッチャ。第3引数がパケット先頭で、その +2 が opcode。
    /// </summary>
    private delegate IntPtr PacketDispatcherDelegate(IntPtr a1, uint sourceId, IntPtr packet);

    private Hook<PerformanceRecvDelegate> soloHook;
    private Hook<PerformanceRecvDelegate> ensembleHook;
    private Hook<PacketDispatcherDelegate> dispatcherHook;

    /// <summary>
    /// 演奏パケットの opcode。
    /// 2026-09-20 に実機の Network Monitor で観測した値。
    /// ゲーム更新で変わるため、設定から変えられるようにする。
    /// </summary>
    public int PerformanceOpcode { get; set; } = 0x337;

    /// <summary>観測した opcode ごとの件数。</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, int> OpcodeCounts = new();

    /// <summary>ディスパッチャが呼ばれた総数。</summary>
    public long DispatcherCallCount { get; private set; }

    /// <summary>演奏 opcode に一致した件数。</summary>
    public long PerformanceOpcodeHits { get; private set; }

    /// <summary>opcode の観測を有効にするか。</summary>
    public Func<bool> ShouldWatchOpcodes { get; set; } = () => false;

    private readonly PerformanceRecorder recorder;
    private readonly IPluginLog log;
    private readonly IObjectTable objectTable;
    private readonly RawPacketLog rawLog;

    /// <summary>
    /// 生パケットを残すか。既定で有効。
    /// こちらの解釈が間違っていても、生さえ残っていれば読み直せる。
    /// </summary>
    public Func<bool> ShouldLogRaw { get; set; } = () => true;

    /// <summary>
    /// ペイロードから写すバイト数。
    ///
    /// 【未解決の問題】
    /// ディスパッチャの引数に長さは渡されていない（逆アセンブルで確認）。
    /// ヘッダから長さを読めるかも未確認。そのため、ここで指定する長さは
    /// 「安全に読めると確かめた値」ではない。
    ///
    /// ソロは 24 バイトで足りることが分かっているが、
    /// 合奏など別形式では足りない可能性がある。
    /// 調査のために広げられるようにしておく。
    /// </summary>
    public int CaptureBytes { get; set; } = PerformanceConstants.SoloPacketSize;

    /// <summary>
    /// IPC ヘッダ（ペイロードの手前 0x10 バイト）も残すか。
    ///
    /// ヘッダに長さや種別が入っていれば、
    /// 合奏パケットの形式を突き止める手がかりになる。
    /// 調査用なので既定では無効。
    /// </summary>
    public Func<bool> ShouldLogHeader { get; set; } = () => false;

    /// <summary>
    /// 記録開始からの経過時間を得る。Recorder と同じ時計を使う。
    /// </summary>
    public Func<TimeSpan> Clock { get; set; } = () => TimeSpan.Zero;

    /// <summary>
    /// 合奏パケットを記録対象にするか。設定から与えられる。
    /// false のときもフック自体は生かしておき、受信数だけ数える
    /// （「届いてはいるが記録していない」と「そもそも届いていない」を
    /// 画面で区別できるようにするため）。
    /// </summary>
    public Func<bool> ShouldRecordEnsemble { get; set; } = () => false;

    /// <summary>
    /// フック内で使う一時バッファ。毎回確保しないよう使い回す。
    /// フックはゲームスレッドからのみ呼ばれる前提。
    /// </summary>
    private readonly byte[] noteBuffer = new byte[PerformanceConstants.EnsembleNoteCount];
    private readonly byte[] toneBuffer = new byte[PerformanceConstants.EnsembleNoteCount];

    public bool SoloHooked => soloHook is { IsDisposed: false };
    public bool EnsembleHooked => ensembleHook is { IsDisposed: false };

    /// <summary>ソロパケットを受け取った回数。取得可能性の確認に使う。</summary>
    public long SoloPacketCount { get; private set; }

    /// <summary>合奏パケットを受け取った回数。</summary>
    public long EnsemblePacketCount { get; private set; }

    /// <summary>サイズ検証などで破棄したパケット数。</summary>
    public long RejectedPacketCount { get; private set; }

    /// <summary>
    /// パケットの中身が想定レイアウトと食い違った回数。
    ///
    /// ここが増えるのは、ゲーム更新でパケットの形が変わったとき。
    /// 「音が録れない」より先にこの数字で気づけるようにする。
    /// </summary>
    public long LayoutMismatchCount { get; private set; }

    /// <summary>
    /// 長さが足りずに読むのをやめたパケットの数。
    ///
    /// ディスパッチャは長さを渡してこないので、
    /// こちらで「ここまでしか読まない」と決めている。
    /// </summary>
    public long ShortPacketCount { get; private set; }

    /// <summary>
    /// 先頭 8 バイトのヘッダが付いた形（形B）で届いた合奏パケットの数。
    ///
    /// どちらの形で届くかの条件が未解明なので、
    /// 画面に出して手がかりを集められるようにしておく。
    /// </summary>
    public long LegacyHeaderCount { get; private set; }

    public string LastHookError { get; private set; }

    public PerformanceHookManager(
        PerformanceRecorder recorder,
        IPluginLog log,
        IObjectTable objectTable,
        RawPacketLog rawLog)
    {
        this.recorder = recorder;
        this.log = log;
        this.objectTable = objectTable;
        this.rawLog = rawLog;
    }

    public void Install(
        PerformanceSignatures signatures,
        IGameInteropProvider interop)
    {
        if (signatures.SoloResolved)
        {
            try
            {
                soloHook = interop.HookFromAddress<PerformanceRecvDelegate>(
                    signatures.SoloAddress, SoloDetour);
                soloHook.Enable();
                log.Information("ソロ受信フックを有効にしました。");
            }
            catch (Exception e)
            {
                LastHookError = $"ソロ受信フックの作成に失敗: {e.Message}";
                log.Error(e, "ソロ受信フックの作成に失敗しました。");
            }
        }

        if (signatures.EnsembleResolved)
        {
            try
            {
                ensembleHook = interop.HookFromAddress<PerformanceRecvDelegate>(
                    signatures.EnsembleAddress, EnsembleDetour);
                ensembleHook.Enable();
                log.Information("合奏受信フックを有効にしました。");
            }
            catch (Exception e)
            {
                LastHookError = $"合奏受信フックの作成に失敗: {e.Message}";
                log.Error(e, "合奏受信フックの作成に失敗しました。");
            }
        }

        if (signatures.DispatcherResolved)
        {
            try
            {
                dispatcherHook = interop.HookFromAddress<PacketDispatcherDelegate>(
                    signatures.DispatcherAddress, DispatcherDetour);
                dispatcherHook.Enable();
                log.Information("パケットディスパッチャのフックを有効にしました。");
            }
            catch (Exception e)
            {
                LastHookError = $"ディスパッチャフックの作成に失敗: {e.Message}";
                log.Error(e, "ディスパッチャフックの作成に失敗しました。");
            }
        }
    }

    public bool DispatcherHooked => dispatcherHook is { IsDisposed: false };

    /// <summary>
    /// ディスパッチャのフック。全 opcode がここを通る。
    ///
    /// 演奏ハンドラ個別のフックが呼ばれない場合でも、
    /// ここで演奏パケットを拾える。
    /// </summary>
    private IntPtr DispatcherDetour(IntPtr a1, uint sourceId, IntPtr packet)
    {
        try
        {
            DispatcherCallCount++;

            if (packet != IntPtr.Zero && ShouldWatchOpcodes())
            {
                var opcode = *(ushort*)((byte*)packet + 2);

                OpcodeCounts.AddOrUpdate(opcode, 1, (_, c) => c + 1);

                if (opcode == PerformanceOpcode)
                {
                    PerformanceOpcodeHits++;
                    HandlePerformancePacket(sourceId, packet);
                }
            }
        }
        catch (Exception e)
        {
            LastHookError = e.Message;
        }

        return dispatcherHook.Original(a1, sourceId, packet);
    }

    /// <summary>
    /// 演奏 opcode のパケットを処理する。
    ///
    /// ペイロードは IPC ヘッダ (0x10) の直後から始まる。
    /// ゲーム側も lea rdx,[rdi+0x10] で同じ位置を渡している。
    /// </summary>
    private void HandlePerformancePacket(uint sourceId, IntPtr packet)
    {
        const int headerSize = 0x10;
        var payload = (byte*)packet + headerSize;

        // ---- 読める長さを確かめる ----
        //
        // ディスパッチャの引数に長さは渡ってこない（逆アセンブルで確認済み）。
        // ただし FFXIV のパケットは IPC ヘッダの手前に
        // セグメントヘッダ (16バイト) があり、その +0 が
        // 「このセグメント全体の長さ」になっている。
        //   出典: https://xiv.dev/network/packet-structure
        //
        // packet は IPC ヘッダの先頭を指しているので、
        // その 16 バイト手前を読めば長さが分かるはず。
        //
        // ただし「そう読めるか」は実機で未確認なので、
        // 値が妥当な範囲のときだけ信用し、
        // 妥当でなければ従来どおり固定長で扱う。
        // 推測に賭けて読み進めるより、読む量を絞るほうが安全。
        var payloadLimit = TryGetPayloadLimit((byte*)packet, headerSize);

        // 【2026-09-21 修正・重要】
        // ここで「短いから」とパケットを捨ててはいけない。
        //
        // 最初の実装は payloadLimit が SoloRequiredBytes 未満なら
        // return していたが、**実機でパケットが1件も記録されなくなった**
        // （3回の記録すべてで生ログ 0 行）。
        //
        // セグメントヘッダが本当にその位置にあるかは未確認のまま
        // 「読めた値」を信じて捨てていたのが誤り。
        // 手前にあったのは長さではなく、別の意味の値だった可能性が高い。
        //
        // 未確認の推測でデータを捨てるのは、安全策ではなく事故。
        // 長さは**読む量を減らす**のにだけ使い、
        // 捨てる判断には使わない。
        if (payloadLimit >= 0 && payloadLimit < PerformanceConstants.SoloRequiredBytes)
            ShortPacketCount++;

        var info = TryGetPerformerInfo(sourceId);

        // 生バイトは必ず残す。読み方が違っても後から読み直せるように。
        //
        // 【保存範囲について・重要】
        // ここで写す長さは「安全に読めると確かめた長さ」ではなく、
        // ソロの形式を前提にした固定長でしかない。
        // ディスパッチャの引数には長さが渡されておらず（実測）、
        // ヘッダから長さを読めるかも未確認。
        //
        // そのため合奏など別形式のパケットでは、
        // **全体を保存できていない可能性がある**。
        // 「生ログがあるから録り直し不要」と言えるのは
        // ソロ形式に限る。
        if (ShouldLogRaw())
        {
            // 調査用にヘッダも残す場合は、ヘッダ＋ペイロードを続けて写す。
            var withHeader = ShouldLogHeader();
            var start = withHeader ? -headerSize : 0;

            // 保存長も、未確認の値では削らない。
            //
            // 削ってしまうと「音が取れない」ときに
            // 生ログからも原因が追えなくなる。
            // 生ログは調べるためのものなので、
            // 疑わしいときほど多めに残す。
            //
            // payloadLimit が確かめられた日には、
            // ここを上限として使えるようになる。
            var length = CaptureBytes + (withHeader ? headerSize : 0);

            var raw = new byte[length];
            for (var i = 0; i < length; i++)
                raw[i] = payload[start + i];

            rawLog.Enqueue(new RawPacketLog.Entry(
                false, sourceId, Clock(), raw,
                info.Instrument, info.Mode, info.ModeParam, info.Name));
        }

        var count = payload[PerformanceConstants.SoloOffsetCount];
        if (count > PerformanceConstants.SoloNoteCount)
        {
            RejectedPacketCount++;
            return;
        }

        var n = PerformanceConstants.SoloNoteCount;
        for (var i = 0; i < n; i++)
        {
            noteBuffer[i] = payload[PerformanceConstants.SoloOffsetNotes + i];
            toneBuffer[i] = payload[PerformanceConstants.SoloOffsetTones + i];
        }

        recorder.Feed(
            sourceId,
            new ReadOnlySpan<byte>(noteBuffer, 0, n),
            new ReadOnlySpan<byte>(toneBuffer, 0, n),
            info.Instrument,
            NoteSource.SoloPacket);
    }

    /// <summary>
    /// セグメントヘッダから「ペイロードとして読める長さ」を求める。
    /// 分からなければ -1（呼び手は従来どおり固定長で扱う）。
    ///
    /// FFXIV のパケットは
    ///   [セグメントヘッダ 16B][IPC ヘッダ 16B][ペイロード]
    /// の並びで、セグメントヘッダの +0 が
    /// **セグメント全体（ヘッダ込み）の長さ**。
    ///   出典: https://xiv.dev/network/packet-structure
    ///
    /// ディスパッチャが渡してくる packet は IPC ヘッダの先頭なので、
    /// その 16 バイト手前がセグメントヘッダになる **はず**。
    /// ここは実機で裏を取っていないので、
    /// **値が妥当なときだけ信用する**（信用できなければ -1）。
    ///
    /// 妥当性の条件：
    ///   - 全体長が 2 つのヘッダぶんより大きい
    ///   - 極端に大きくない（壊れた値・別の意味の値を弾く）
    /// </summary>
    private int TryGetPayloadLimit(byte* packet, int ipcHeaderSize)
    {
        const int SegmentHeaderSize = 0x10;

        // 常識的な上限。演奏パケットは実測 128 バイトなので、
        // これを大きく超える値は「長さではない何か」を読んでいる。
        const uint SaneMax = 0x10000;

        try
        {
            var segment = packet - SegmentHeaderSize;
            var total = *(uint*)segment;

            var overhead = (uint)(SegmentHeaderSize + ipcHeaderSize);
            if (total <= overhead || total > SaneMax)
                return -1;

            return (int)(total - overhead);
        }
        catch
        {
            // 手前が読めない配置だった場合。
            // 例外にせず「分からない」を返す。
            return -1;
        }
    }

    /// <summary>最後にフックが呼ばれた時刻と送り元。発火の確認用。</summary>
    public DateTime LastSoloHitAt { get; private set; }
    public DateTime LastEnsembleHitAt { get; private set; }
    public uint LastSoloSourceId { get; private set; }
    public uint LastEnsembleSourceId { get; private set; }

    private IntPtr SoloDetour(uint sourceId, IntPtr data)
    {
        try
        {
            SoloPacketCount++;
            LastSoloHitAt = DateTime.Now;
            LastSoloSourceId = sourceId;
            HandleSolo(sourceId, data);
        }
        catch (Exception e)
        {
            // フック内の例外は絶対に外へ出さない。
            LastHookError = e.Message;
            log.Error(e, "ソロ受信フックで例外が発生しました。");
        }

        return soloHook.Original(sourceId, data);
    }

    private IntPtr EnsembleDetour(uint sourceId, IntPtr data)
    {
        try
        {
            EnsemblePacketCount++;
            LastEnsembleHitAt = DateTime.Now;
            LastEnsembleSourceId = sourceId;

            // 受信数は数えたうえで、記録するかどうかは設定に従う。
            if (ShouldRecordEnsemble())
                HandleEnsemble(sourceId, data);
        }
        catch (Exception e)
        {
            LastHookError = e.Message;
            log.Error(e, "合奏受信フックで例外が発生しました。");
        }

        return ensembleHook.Original(sourceId, data);
    }

    private void HandleSolo(uint sourceId, IntPtr data)
    {
        if (data == IntPtr.Zero)
        {
            RejectedPacketCount++;
            return;
        }

        var p = (byte*)data;
        var info = TryGetPerformerInfo(sourceId);

        // 生バイトを先に残す。
        // 解釈より前に残すのは、サイズ検証で弾いたパケットこそ
        // 「読み方が違う」手がかりになるため。捨てると原因が追えない。
        if (ShouldLogRaw())
        {
            var raw = new byte[PerformanceConstants.SoloPacketSize];
            for (var i = 0; i < raw.Length; i++)
                raw[i] = p[i];

            rawLog.Enqueue(new RawPacketLog.Entry(
                false, sourceId, Clock(), raw,
                info.Instrument, info.Mode, info.ModeParam, info.Name));
        }

        // サイズ検証：実機コードは cmp al, 0Ah で上限を弾いている。
        // ここでも同じ上限を超える値は異常として破棄する。
        var count = p[PerformanceConstants.SoloOffsetCount];
        if (count > PerformanceConstants.SoloNoteCount)
        {
            RejectedPacketCount++;
            return;
        }

        // count が 0 でも「全部消音した」という情報なので通す。
        var n = PerformanceConstants.SoloNoteCount;
        for (var i = 0; i < n; i++)
        {
            noteBuffer[i] = p[PerformanceConstants.SoloOffsetNotes + i];
            toneBuffer[i] = p[PerformanceConstants.SoloOffsetTones + i];
        }

        recorder.Feed(
            sourceId,
            new ReadOnlySpan<byte>(noteBuffer, 0, n),
            new ReadOnlySpan<byte>(toneBuffer, 0, n),
            info.Instrument,
            NoteSource.SoloPacket);
    }

    private void HandleEnsemble(uint sourceId, IntPtr data)
    {
        if (data == IntPtr.Zero)
        {
            RejectedPacketCount++;
            return;
        }

        // 【重要】合奏パケットは 1 つに 8 人分が入っている。
        //
        //   [ヘッダ 8B][0x80B × 8人分]
        //
        // 2026-09-21 に、生ログが「1人1エントリ・128バイト」に
        // 見えたことから「1パケット＝1人分」と誤認し、
        // このループを削除してしまった。
        // 結果、**合奏で1人分しか記録されなくなった**（実機で確認）。
        //
        // 生ログが1人1エントリなのは、ここで**1人分ずつ別エントリとして
        // 保存している**から。8人いれば8エントリ出る。
        // 同一時刻の8エントリは 1 パケットを展開した結果であって、
        // 8個のパケットが届いていたわけではない。
        //
        // 教訓：生ログの見た目（1件の長さ・件数）は保存側の都合であり、
        // パケットの構造ではない。
        var p = (byte*)data;

        for (var m = 0; m < PerformanceConstants.EnsembleMemberCount; m++)
        {
            var member = p
                + PerformanceConstants.EnsembleOffsetMembers
                + m * PerformanceConstants.EnsembleMemberStride;

            var characterId = *(uint*)(member + PerformanceConstants.EnsembleMemberOffsetId);

            // 空き枠は飛ばす。8人そろっていないときに埋まっている。
            if (characterId is 0 or PerformanceConstants.NullActorId)
                continue;

            var info = TryGetPerformerInfo(characterId);

            // 枠ごとに生バイトを残す（0x80 バイト＝1人分そのまま）。
            if (ShouldLogRaw())
            {
                var raw = new byte[PerformanceConstants.EnsembleMemberStride];
                for (var i = 0; i < raw.Length; i++)
                    raw[i] = member[i];

                rawLog.Enqueue(new RawPacketLog.Entry(
                    true, characterId, Clock(), raw,
                    info.Instrument, info.Mode, info.ModeParam, info.Name));
            }

            var count = member[PerformanceConstants.EnsembleMemberOffsetCount];
            if (count > PerformanceConstants.EnsembleNoteCount)
            {
                RejectedPacketCount++;
                continue;
            }

            var n = PerformanceConstants.EnsembleNoteCount;
            for (var i = 0; i < n; i++)
            {
                noteBuffer[i] = member[PerformanceConstants.EnsembleMemberOffsetNotes + i];
                toneBuffer[i] = member[PerformanceConstants.EnsembleMemberOffsetTones + i];
            }

            recorder.Feed(
                characterId,
                new ReadOnlySpan<byte>(noteBuffer, 0, n),
                new ReadOnlySpan<byte>(toneBuffer, 0, n),
                info.Instrument,
                NoteSource.EnsemblePacket);
        }
    }

    /// <summary>受信時点で観測できた演奏者の情報。</summary>
    public readonly struct PerformerInfo
    {
        /// <summary>楽器（Perform シート行ID）。取れなければ -1。</summary>
        public readonly int Instrument;

        /// <summary>Character.Mode の生値。演奏中なら 16。</summary>
        public readonly byte Mode;

        /// <summary>Character.ModeParam の生値。</summary>
        public readonly byte ModeParam;

        /// <summary>演奏者名。取れなければ空。</summary>
        public readonly string Name;

        public PerformerInfo(int instrument, byte mode, byte modeParam, string name)
        {
            Instrument = instrument;
            Mode = mode;
            ModeParam = modeParam;
            Name = name;
        }

        public static PerformerInfo Unknown => new(-1, 0, 0, string.Empty);
    }

    /// <summary>
    /// 演奏者の情報をまとめて読む。
    ///
    /// Character.Mode == Performance (16) のとき、ModeParam が
    /// Perform シートの行 ID（＝楽器）になる。
    ///
    /// Mode / ModeParam は生値もそのまま残す。演奏モードでないと
    /// 判定された場合でも、生値があれば後から読み直せるため。
    /// </summary>
    private PerformerInfo TryGetPerformerInfo(uint entityId)
    {
        try
        {
            foreach (var obj in objectTable)
            {
                if (obj.EntityId != entityId)
                    continue;

                var native = (Character*)obj.Address;
                if (native == null)
                    return PerformerInfo.Unknown;

                var mode = (byte)native->Mode;
                var param = native->ModeParam;
                var name = obj.Name.TextValue ?? string.Empty;

                var instrument = native->Mode == CharacterModes.Performance
                    ? param
                    : -1;

                return new PerformerInfo(instrument, mode, param, name);
            }
        }
        catch
        {
            // 取れなくても記録は続ける。
        }

        return PerformerInfo.Unknown;
    }

    public void Dispose()
    {
        soloHook?.Disable();
        soloHook?.Dispose();
        soloHook = null;

        ensembleHook?.Disable();
        ensembleHook?.Dispose();
        ensembleHook = null;

        dispatcherHook?.Disable();
        dispatcherHook?.Dispose();
        dispatcherHook = null;
    }
}
