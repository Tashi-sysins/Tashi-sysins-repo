// このファイルに含まれるシグネチャは、MidiBard プロジェクト
// (Copyright (C) 2022 akira0245) の Midibard/Managers/Offsets.cs に由来します。
//
// MidiBard は GNU Affero General Public License v3.0 で配布されています。
// 原作者 akira0245 の表示と、MidiBard プロジェクトで使われていたものである旨を
// 明示することが、元のライセンスで求められています。
//
// 元ライセンス: https://github.com/akira0245/MidiBard/blob/master/LICENSE
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using Dalamud.Plugin.Services;

namespace BardPerformanceRecorder.Game;

/// <summary>
/// 演奏受信ハンドラのアドレス解決。
///
/// シグネチャは MidiBard プロジェクト (akira0245, AGPL-3.0) の Offsets.cs に由来する。
/// 2026-09-20 に ffxiv_dx11.exe ver 2026.09.15.0000.0000 に対して静的検索し、
/// 両方とも一意にヒットすることを確認済み。
/// ただしゲーム更新で無効になりうるため、失敗を前提に扱う。
/// </summary>
public sealed class PerformanceSignatures
{
    /// <summary>ソロ演奏の受信ハンドラ。<c>IntPtr f(uint sourceId, IntPtr data)</c></summary>
    public const string SoloReceivedHandler =
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 48 8B DA 8B F9";

    /// <summary>合奏演奏の受信ハンドラ。<c>IntPtr f(uint sourceId, IntPtr data)</c></summary>
    public const string EnsembleReceivedHandler =
        "4C 8B C2 8B D1 48 8D 0D ?? ?? ?? ?? E9 ?? ?? ?? ?? CC CC CC CC CC CC CC CC "
        + "CC CC CC CC CC CC CC 40 53 48 83 EC 20 48 8B D9";

    /// <summary>
    /// パケット受信のディスパッチャ本体。
    /// <c>f(IntPtr a1, uint sourceId, IntPtr packet)</c>
    /// パケットの +2 が opcode。
    ///
    /// これは MidiBard 由来ではなく、2026-09-20 に
    /// ffxiv_dx11.exe ver 2026.09.15 を解析して独自に特定したもの。
    /// 例外テーブル(.pdata)から関数の入口 0x1418D9500 を確定させ、
    /// そこから一意になるパターンを作った。
    ///
    /// 演奏ハンドラ個別のフックが呼ばれない場合でも、
    /// ここなら全 opcode を観測できる。
    /// </summary>
    public const string PacketDispatcher =
        "48 89 5C 24 20 55 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 ?? ?? ?? ?? B8 70 3C 00 00";

    public IntPtr SoloAddress { get; private set; } = IntPtr.Zero;
    public IntPtr EnsembleAddress { get; private set; } = IntPtr.Zero;
    public IntPtr DispatcherAddress { get; private set; } = IntPtr.Zero;

    public string DispatcherError { get; private set; }
    public bool DispatcherResolved => DispatcherAddress != IntPtr.Zero;

    public string SoloError { get; private set; }
    public string EnsembleError { get; private set; }

    public bool SoloResolved => SoloAddress != IntPtr.Zero;
    public bool EnsembleResolved => EnsembleAddress != IntPtr.Zero;

    /// <summary>
    /// シグネチャを解決する。片方が失敗しても例外は投げず、
    /// 失敗理由を文字列で保持して画面に出せるようにする。
    /// </summary>
    public void Resolve(ISigScanner sigScanner, IPluginLog log)
    {
        try
        {
            SoloAddress = sigScanner.ScanText(SoloReceivedHandler);
            log.Information($"SoloReceivedHandler resolved: {SoloAddress:X}");
        }
        catch (Exception e)
        {
            SoloError = e.Message;
            log.Error(e, "SoloReceivedHandler のシグネチャ解決に失敗しました。");
        }

        try
        {
            EnsembleAddress = sigScanner.ScanText(EnsembleReceivedHandler);
            log.Information($"EnsembleReceivedHandler resolved: {EnsembleAddress:X}");
        }
        catch (Exception e)
        {
            EnsembleError = e.Message;
            log.Error(e, "EnsembleReceivedHandler のシグネチャ解決に失敗しました。");
        }

        try
        {
            DispatcherAddress = sigScanner.ScanText(PacketDispatcher);
            log.Information($"PacketDispatcher resolved: {DispatcherAddress:X}");
        }
        catch (Exception e)
        {
            DispatcherError = e.Message;
            log.Error(e, "PacketDispatcher のシグネチャ解決に失敗しました。");
        }
    }
}
