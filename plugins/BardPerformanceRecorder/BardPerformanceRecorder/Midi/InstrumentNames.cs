using System.Collections.Generic;

namespace BardPerformanceRecorder.Midi;

/// <summary>
/// FF14 の楽器 ID（Perform シート行 ID）と、MidiBard2 が
/// トラック名から楽器を判別するときに使う名前の対応。
///
/// なぜこの名前でなければならないか：
/// MidiBard2 は MIDI のトラック名に楽器名が含まれていると、
/// そのトラックを演奏するときに楽器を自動で持ち替える
/// （TrackInfo.GetInstrumentIDByName）。
/// つまりここで正しい綴りを出しておけば、
/// 記録した MIDI をそのまま読み込ませるだけで
/// 「どのトラックを誰が何の楽器で弾くか」が再現される。
///
/// 綴りは MidiBard2 側の対応表に合わせてある。
/// 変えると自動持ち替えが効かなくなるので、勝手に整形しないこと。
/// </summary>
public static class InstrumentNames
{
    private static readonly Dictionary<int, string> ById = new()
    {
        [1] = "Harp",
        [2] = "Piano",
        [3] = "Lute",
        [4] = "Fiddle",
        [5] = "Flute",
        [6] = "Oboe",
        [7] = "Clarinet",
        [8] = "Fife",
        [9] = "Panpipes",
        [10] = "Timpani",
        [11] = "Bongo",
        [12] = "BassDrum",
        [13] = "SnareDrum",
        [14] = "Cymbal",
        [15] = "Trumpet",
        [16] = "Trombone",
        [17] = "Tuba",
        [18] = "Horn",
        [19] = "Saxophone",
        [20] = "Violin",
        [21] = "Viola",
        [22] = "Cello",
        [23] = "DoubleBass",
        [24] = "ElectricGuitarOverdriven",
        [25] = "ElectricGuitarClean",
        [26] = "ElectricGuitarMuted",
        [27] = "ElectricGuitarPowerChords",
        [28] = "ElectricGuitarSpecial",
    };

    /// <summary>
    /// 楽器 ID から MidiBard2 が認識する名前を返す。
    /// 未知の ID なら null（トラック名に楽器名を入れない）。
    /// </summary>
    public static string TryGet(int instrumentId)
        => ById.TryGetValue(instrumentId, out var name) ? name : null;

    /// <summary>
    /// 一般的な MIDI 音源で近い音が鳴るようにするための
    /// General MIDI プログラム番号（0 起点）。
    ///
    /// これは「それらしく聞こえるようにする」ための対応であって、
    /// ゲーム内の音を再現するものではない。
    /// MidiBard2 で弾き直す場合はトラック名のほうが使われる。
    /// </summary>
    private static readonly Dictionary<int, byte> GmById = new()
    {
        [1] = 46,   // Orchestral Harp
        [2] = 0,    // Acoustic Grand Piano
        [3] = 24,   // Acoustic Guitar (nylon)
        [4] = 110,  // Fiddle
        [5] = 73,   // Flute
        [6] = 68,   // Oboe
        [7] = 71,   // Clarinet
        [8] = 72,   // Piccolo
        [9] = 75,   // Pan Flute
        [10] = 47,  // Timpani
        [11] = 115, // Woodblock（打楽器の代用）
        [12] = 116, // Taiko Drum
        [13] = 117, // Melodic Tom
        [14] = 119, // Reverse Cymbal
        [15] = 56,  // Trumpet
        [16] = 57,  // Trombone
        [17] = 58,  // Tuba
        [18] = 60,  // French Horn
        [19] = 65,  // Alto Sax
        [20] = 40,  // Violin
        [21] = 41,  // Viola
        [22] = 42,  // Cello
        [23] = 43,  // Contrabass
        [24] = 29,  // Overdriven Guitar
        [25] = 27,  // Electric Guitar (clean)
        [26] = 28,  // Electric Guitar (muted)
        [27] = 30,  // Distortion Guitar
        [28] = 31,  // Guitar Harmonics
    };

    /// <summary>
    /// 楽器 ID から General MIDI プログラム番号を返す。
    /// 未知の ID なら null（プログラムチェンジを書かない）。
    /// </summary>
    public static byte? TryGetGeneralMidiProgram(int instrumentId)
        => GmById.TryGetValue(instrumentId, out var p) ? p : null;
}
