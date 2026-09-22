using System;

namespace BardPerformanceRecorder.Recording;

/// <summary>
/// 連続記録の「いつ何をするか」だけを決める部分。
///
/// ゲームにも画面にも依存させない。
/// そうしないと、時間まわりの判断を実機でしか試せなくなる。
/// 「15秒途切れたら曲の終わり」のような決まりは、
/// 手元で何度でも確かめられるようにしておきたい。
/// </summary>
public static class ContinuousPolicy
{
    /// <summary>連続記録が次に取るべき動き。</summary>
    public enum Action
    {
        /// <summary>何もしない。</summary>
        None,

        /// <summary>記録を始める（演奏が始まった）。</summary>
        StartRecording,

        /// <summary>いまの曲を書き出して、また待機に戻る。</summary>
        SaveAndWait,

        /// <summary>待つのをやめる（時間切れ）。</summary>
        GiveUp,
    }

    /// <summary>
    /// いま何をすべきかを決める。
    ///
    /// <param name="isRecording">記録中か（false なら待機中）。</param>
    /// <param name="gotPacket">この瞬間に演奏パケットが届いたか。</param>
    /// <param name="secondsSincePacket">最後にパケットを見てからの秒数。</param>
    /// <param name="songGapSeconds">曲の区切りとみなす無音の長さ（秒）。</param>
    /// <param name="idleTimeoutMinutes">待機をやめるまでの時間（分）。</param>
    /// </summary>
    public static Action Decide(
        bool isRecording,
        bool gotPacket,
        double secondsSincePacket,
        double songGapSeconds,
        double idleTimeoutMinutes)
    {
        if (!isRecording)
        {
            // 待機中。演奏が始まったら記録に入る。
            if (gotPacket)
                return Action.StartRecording;

            // ずっと来ないなら、いつまでも待たない。
            if (secondsSincePacket >= idleTimeoutMinutes * 60.0)
                return Action.GiveUp;

            return Action.None;
        }

        // 記録中。
        //
        // パケットが届いた直後は当然 0 秒なので、
        // gotPacket を先に見る必要はない
        // （secondsSincePacket が 0 に戻っているため）。
        if (secondsSincePacket >= songGapSeconds)
            return Action.SaveAndWait;

        return Action.None;
    }
}
