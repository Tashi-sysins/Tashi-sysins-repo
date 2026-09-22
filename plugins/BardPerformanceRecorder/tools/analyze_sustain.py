# -*- coding: utf-8 -*-
"""音長テストの生ログを読み、0xFE（保持と解釈している値）の意味を調べる。

使い方:
    python -X utf8 analyze_sustain.py <raw_日時.csv> [--expect 0.1,0.25,0.5,1.0,2.0,3.0]

何を見るか:
  - 音が鳴っているあいだ 0xFE が出続けるか
  - 音長ごとに 0xFE の数がどう変わるか
  - いまの実装（次のパケットで消えたら消音）で得られる音長

これで 0xFE の解釈が正しいか判断できる。
  出続ける → 「保持」という解釈は正しく、音長に使える
  出ない   → 解釈が誤り。別の手がかりを探す
"""
import argparse
import csv
import sys

NOTE_EMPTY = 0xFF
NOTE_SUSTAIN = 0xFE
NOTE_OFFSET = 24
SLOT_COUNT = 10
SLOT_START = 1
TONE_START = 11

NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B']


def note_name(raw):
    midi = raw + NOTE_OFFSET
    return f"{NAMES[midi % 12]}{midi // 12 - 1}"


def load(path):
    """(秒, 音符スロット10個) の一覧。重複行も落とさずそのまま返す。"""
    rows = []
    with open(path, encoding='utf-8-sig', newline='') as f:
        for row in csv.DictReader(f):
            try:
                sec = float(row['seconds'])
                data = bytes.fromhex(row['bytes_hex'])
            except (ValueError, KeyError):
                continue

            if len(data) < SLOT_START + SLOT_COUNT:
                continue

            rows.append((sec, data[SLOT_START:SLOT_START + SLOT_COUNT]))
    return rows


def main():
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument('csv_path', nargs='?')
    parser.add_argument('--expect', default='0.1,0.25,0.5,1.0,2.0,3.0')
    args = parser.parse_args()

    if not args.csv_path:
        print(__doc__)
        return 1

    rows = load(args.csv_path)
    if not rows:
        print("生ログを読めませんでした。")
        return 1

    expected = [float(x) for x in args.expect.split(',') if x.strip()]

    print("0xFE（保持）の調査")
    print("=" * 66)
    print(f"生ログ : {args.csv_path}")
    print(f"行数   : {len(rows)}")
    print()

    # ---- 1. パケットの受信間隔 ----
    uniq = sorted({round(s, 4) for s, _ in rows})
    gaps = [uniq[i + 1] - uniq[i] for i in range(len(uniq) - 1)]
    wide = [g for g in gaps if g > 0.05]
    if wide:
        print(f"■ パケット間隔 : 最小 {min(wide) * 1000:.0f}ms / "
              f"最大 {max(wide) * 1000:.0f}ms / "
              f"中央 {sorted(wide)[len(wide) // 2] * 1000:.0f}ms")
        print()

    # ---- 2. 0xFE の出現 ----
    fe_rows = [(s, slots) for s, slots in rows if NOTE_SUSTAIN in slots]
    print(f"■ 0xFE を含むパケット : {len(fe_rows)} / {len(rows)} 件")

    if not fe_rows:
        print()
        print("  0xFE が一度も現れませんでした。")
        print("  → 「保持」という解釈は成り立ちません。")
        print("     長い音を鳴らしても保持の合図は出ない、ということになります。")
    else:
        total_fe = sum(sum(1 for v in slots if v == NOTE_SUSTAIN)
                       for _, slots in fe_rows)
        print(f"  0xFE の総数 : {total_fe} 個")
        print()
        print("  時刻      0xFE のスロット        同じパケットの実音")
        for s, slots in fe_rows[:40]:
            fe = [i for i, v in enumerate(slots) if v == NOTE_SUSTAIN]
            real = [f"{note_name(v)}@{i}" for i, v in enumerate(slots)
                    if v not in (NOTE_EMPTY, NOTE_SUSTAIN)]
            print(f"  {s:8.3f}  {str(fe):22} {real if real else 'なし'}")
        if len(fe_rows) > 40:
            print(f"  ... 他 {len(fe_rows) - 40} 件")

    print()

    # ---- 3. 実音の区間（いまの実装での音長） ----
    print("■ 実音が観測された区間（重複行を除いたうえで）")
    print("   ここから、いまの実装で得られる音長が分かる。")
    print()

    seen = set()
    events = []
    for s, slots in rows:
        key = (round(s, 4), bytes(slots))
        if key in seen:
            continue
        seen.add(key)
        real = {v for v in slots if v not in (NOTE_EMPTY, NOTE_SUSTAIN)}
        events.append((s, real, slots))

    segments = []
    active = {}
    for s, real, _ in events:
        for v in real:
            if v not in active:
                active[v] = s
        for v in list(active):
            if v not in real:
                segments.append((active.pop(v), s, v))

    last = events[-1][0] if events else 0.0
    for v, start in active.items():
        segments.append((start, last, v))

    segments.sort()

    if not segments:
        print("   実音が観測されていません。")
    else:
        print("   開始      終了      長さ     音")
        for start, end, v in segments:
            print(f"   {start:8.3f}  {end:8.3f}  {end - start:6.3f}s  {note_name(v)}")

        print()
        print(f"   区間の数 : {len(segments)}")

        if expected:
            print()
            print("■ 元の音長との突き合わせ")
            print(f"   元の音長 : {expected}")
            print(f"   観測値  : {[round(e - s, 3) for s, e, _ in segments]}")
            print()

            if len(segments) == len(expected):
                print("   元     観測     差")
                for exp, (s, e, _) in zip(expected, segments):
                    got = e - s
                    print(f"   {exp:5.2f}s  {got:5.2f}s  {got - exp:+6.2f}s")
            else:
                print(f"   ※ 音の数が合いません（元 {len(expected)} / 観測 {len(segments)}）")

    print()
    print("■ 判断の目安")
    print("   0xFE が長い音のあいだ出続けている → 音長の改善に使える")
    print("   0xFE がほとんど出ない             → 現状の推定が限界")

    return 0


if __name__ == '__main__':
    sys.exit(main())
