# -*- coding: utf-8 -*-
"""元の MIDI と、記録した MIDI を突き合わせる。

使い方:
    python -X utf8 compare_midi.py <元のMIDI> <記録したMIDI> [オプション]

オプション:
    --tolerance <秒>   対応付けで許容するずれ（既定 0.35）
    --verbose          対応付けの一覧を出す

何を見るか:
  - 音高と音順が一致しているか
  - 取りこぼし・余分な音がないか
  - 開始時刻がどれだけずれているか
  - 音長がどれだけずれているか

この版（v2）での変更点:
  - テンポ変更を全部反映する（手持ちの MIDI の 34% がテンポ変更を含む）
  - note_off まで読み、音長を比較する
  - 先頭が欠けても整列できるよう、ずれの推定を中央値で行う
  - 不足と余分を分けて数える
  - 統計に中央値・95 パーセンタイルを加える

注意:
  記録側は「パケット受信時刻」なので、全体が一定量ずれる。
  これは通信の遅れ・記録開始の待ち時間などが混ざったもので、
  内訳は分からない。そこで「開始位置の差」と呼び、
  それを補正したうえで相対的なずれを見る。
"""
import argparse
import statistics
import sys

import mido

NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B']


def note_name(n):
    return f"{NAMES[n % 12]}{n // 12 - 1}"


def build_tempo_map(mid):
    """(tick, tempo) の一覧を返す。テンポ変更を全部拾う。"""
    events = []
    t = 0
    for msg in mido.merge_tracks(mid.tracks):
        t += msg.time
        if msg.type == 'set_tempo':
            events.append((t, msg.tempo))

    if not events or events[0][0] != 0:
        # 先頭にテンポ指定が無ければ既定値（120BPM）から始まる
        events.insert(0, (0, 500000))

    return events


def tick_to_second(tick, tempo_map, tpqn):
    """テンポ変更を反映して tick を秒に直す。

    区間ごとに経過秒を積算する。最初のテンポだけを使うと、
    途中でテンポが変わる曲で時刻が狂う。
    """
    seconds = 0.0
    prev_tick, prev_tempo = tempo_map[0]

    for change_tick, tempo in tempo_map[1:]:
        if tick < change_tick:
            break
        seconds += mido.tick2second(change_tick - prev_tick, tpqn, prev_tempo)
        prev_tick, prev_tempo = change_tick, tempo

    seconds += mido.tick2second(tick - prev_tick, tpqn, prev_tempo)
    return seconds


def load_notes(path):
    """(開始秒, 長さ秒, ノート番号, チャンネル) の一覧を返す。

    note_off まで読んで音長も取る。
    同じ音高が重なる場合に備え、チャンネルと音高ごとにキューで対応付ける。
    """
    mid = mido.MidiFile(path)
    tempo_map = build_tempo_map(mid)
    tpqn = mid.ticks_per_beat

    open_notes = {}
    notes = []
    t = 0

    for msg in mido.merge_tracks(mid.tracks):
        t += msg.time

        if msg.type == 'note_on' and msg.velocity > 0:
            key = (msg.channel, msg.note)
            open_notes.setdefault(key, []).append(t)

        elif msg.type == 'note_off' or (msg.type == 'note_on' and msg.velocity == 0):
            key = (msg.channel, msg.note)
            queue = open_notes.get(key)
            if queue:
                start_tick = queue.pop(0)
                start = tick_to_second(start_tick, tempo_map, tpqn)
                end = tick_to_second(t, tempo_map, tpqn)
                notes.append((start, max(0.0, end - start), msg.note, msg.channel))

    # 閉じなかった音は長さ不明として残す
    for (channel, note), queue in open_notes.items():
        for start_tick in queue:
            notes.append(
                (tick_to_second(start_tick, tempo_map, tpqn), None, note, channel))

    notes.sort(key=lambda n: (n[0], n[2]))
    return notes


def estimate_offset(src, rec):
    """記録側の「開始位置の差」を推定する。

    先頭どうしの差だけで決めると、先頭の音が欠けていたり
    余分な音が混ざっていたときに全体がずれる。
    同じ音高の組み合わせから差を集め、その中央値を使う。
    """
    if not src or not rec:
        return 0.0

    diffs = []
    for s_start, _, s_note, _ in src:
        for r_start, _, r_note, _ in rec:
            if r_note == s_note:
                diffs.append(r_start - s_start)

    if not diffs:
        return rec[0][0] - src[0][0]

    # 素直な中央値だと外れ値に寄ることがあるので、
    # 中央値のまわりに集まっている差だけで取り直す。
    rough = statistics.median(diffs)
    near = [d for d in diffs if abs(d - rough) <= 0.5]
    return statistics.median(near) if near else rough


def match(src, rec, tol):
    """元と記録を突き合わせる。

    音高が同じもののうち、補正後の時刻がいちばん近い未使用の音を選ぶ。

    音長も手がかりに使う。
    2人が同じ音高を同時に弾くと、開始時刻だけでは
    どちらの音か決められない（実データで取り違えが起きた）。
    開始が近いものが複数あるときは、音長が近いほうを選ぶ。
    """
    offset = estimate_offset(src, rec)

    used = [False] * len(rec)
    pairs = []
    missing = []

    for s_start, s_len, s_note, s_ch in src:
        want = s_start + offset
        best = None
        best_d = None

        for i, (r_start, r_len, r_note, r_ch) in enumerate(rec):
            if used[i] or r_note != s_note:
                continue

            d = abs(r_start - want)
            if d > tol:
                continue

            # 開始時刻が同じくらいなら、音長が近いほうを選ぶ。
            # 2人が同じ音を同時に弾いたときの取り違えを防ぐ。
            if s_len is not None and r_len is not None:
                score = d + abs(r_len - s_len) * 0.5
            else:
                score = d

            if best_d is None or score < best_d:
                best, best_d = i, score

        # 上のループで tol を超える候補は除いてあるので、
        # 見つかっていればそのまま採用してよい。
        if best is not None:
            used[best] = True
            pairs.append((
                (s_start, s_len, s_note),
                (rec[best][0], rec[best][1], rec[best][2]),
                rec[best][0] - want,
            ))
        else:
            missing.append((s_start, s_note))

    extra = [(rec[i][0], rec[i][2]) for i in range(len(rec)) if not used[i]]
    return pairs, missing, extra, offset


def percentile(values, q):
    """q パーセンタイル（0〜100）。"""
    if not values:
        return 0.0
    ordered = sorted(values)
    if len(ordered) == 1:
        return ordered[0]
    pos = (len(ordered) - 1) * q / 100.0
    low = int(pos)
    high = min(low + 1, len(ordered) - 1)
    frac = pos - low
    return ordered[low] * (1 - frac) + ordered[high] * frac


def show_stats(label, values, unit="ms", scale=1000.0):
    if not values:
        print(f"   {label}: 対象なし")
        return

    scaled = [abs(v) * scale for v in values]
    signed = [v * scale for v in values]

    print(f"   {label}")
    print(f"     平均       : {statistics.mean(scaled):8.1f} {unit}")
    print(f"     中央       : {statistics.median(scaled):8.1f} {unit}")
    print(f"     95%ile     : {percentile(scaled, 95):8.1f} {unit}")
    print(f"     最大       : {max(scaled):8.1f} {unit}")
    print(f"     符号つき平均: {statistics.mean(signed):+8.1f} {unit}"
          "  （＋なら記録が遅い）")


def main():
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument('src', nargs='?')
    parser.add_argument('rec', nargs='?')
    parser.add_argument('--tolerance', type=float, default=0.35)
    parser.add_argument('--verbose', action='store_true')
    args = parser.parse_args()

    if not args.src or not args.rec:
        print(__doc__)
        return 1

    src = load_notes(args.src)
    rec = load_notes(args.rec)

    print("MIDI 比較 (v2)")
    print("=" * 62)
    print(f"元   : {args.src}")
    print(f"       {len(src)} 音")
    print(f"記録 : {args.rec}")
    print(f"       {len(rec)} 音")
    print(f"許容ずれ : {args.tolerance * 1000:.0f} ms")
    print()

    pairs, missing, extra, offset = match(src, rec, args.tolerance)

    print(f"■ 開始位置の差 : {offset:+.3f} 秒")
    print("   記録開始から演奏開始までの待ち時間や通信の遅れなどが")
    print("   混ざった値です。内訳は分かりません。")
    print()

    rate = len(pairs) / len(src) * 100 if src else 0
    print(f"■ 対応がついた音 : {len(pairs)} / {len(src)}  ({rate:.1f}%)")
    print(f"■ 不足（元にあって記録に無い） : {len(missing)} 音")
    print(f"■ 余分（記録にあって元に無い） : {len(extra)} 音")
    print()

    # ---- 開始時刻のずれ ----
    print("■ 開始時刻のずれ（開始位置の差を補正した後）")
    show_stats("", [d for _, _, d in pairs])
    print()

    # ---- 音長のずれ ----
    print("■ 音長のずれ")
    length_diffs = []
    unknown = 0
    for (s_start, s_len, _), (r_start, r_len, _), _ in pairs:
        if s_len is None or r_len is None:
            unknown += 1
            continue
        length_diffs.append(r_len - s_len)

    if length_diffs:
        show_stats("", length_diffs)
        ratio = [r / s for (_, s, _), (_, r, _), _ in pairs
                 if s and r and s > 0.01]
        if ratio:
            print(f"     長さの比  : 中央 {statistics.median(ratio):.2f} 倍")
    else:
        print("   比較できる音長がありません。")

    if unknown:
        print(f"   （長さが分からない音 {unknown} 個を除いています）")

    print()
    print("   ※ 記録側の音長は推定値です。パケットに音長は入っていません。")
    print()

    if missing:
        print("■ 不足した音（先頭20件）")
        for t, n in missing[:20]:
            print(f"   {t:7.3f}s  {note_name(n)}")
        if len(missing) > 20:
            print(f"   ... 他 {len(missing) - 20} 件")
        print()

    if extra:
        print("■ 余分な音（先頭20件）")
        for t, n in extra[:20]:
            print(f"   {t:7.3f}s  {note_name(n)}")
        if len(extra) > 20:
            print(f"   ... 他 {len(extra) - 20} 件")
        print()

    # ---- 音高の並び ----
    s_seq = [n for _, _, n, _ in src]
    r_seq = [n for _, _, n, _ in rec]
    print("■ 音高の並び")
    if s_seq == r_seq:
        print("   完全に一致")
    else:
        print(f"   元   : {[note_name(n) for n in s_seq[:16]]}")
        print(f"   記録 : {[note_name(n) for n in r_seq[:16]]}")
    print()

    if args.verbose and pairs:
        print("■ 対応の一覧")
        print("     元の開始   記録の開始   ずれ      元の長さ  記録の長さ  音")
        for (s_start, s_len, s_note), (r_start, r_len, _), d in pairs:
            sl = f"{s_len:7.3f}" if s_len is not None else "      ?"
            rl = f"{r_len:7.3f}" if r_len is not None else "      ?"
            print(f"   {s_start:8.3f}  {r_start - offset:9.3f}  "
                  f"{d * 1000:+7.1f}ms  {sl}  {rl}   {note_name(s_note)}")
        print()

    # ---- まとめ ----
    print("■ まとめ")
    if not missing and not extra:
        print("   音の数と並びは一致しています。")
    else:
        if missing:
            print(f"   取りこぼしが {len(missing)} 音あります。")
        if extra:
            print(f"   元に無い音が {len(extra)} 音あります。")

    if length_diffs:
        med = statistics.median([abs(d) for d in length_diffs]) * 1000
        print(f"   音長のずれは中央 {med:.0f} ms です（推定値どうしの比較ではありません）。")

    return 0


if __name__ == '__main__':
    sys.exit(main())
