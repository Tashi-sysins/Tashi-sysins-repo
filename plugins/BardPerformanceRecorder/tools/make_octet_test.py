"""8人合奏の検証用MIDI。

なぜ専用の曲が要るか：
8人が揃う機会は貴重なので、失敗したときに
「誰の音が落ちたか」「どこで落ちたか」を
即座に特定できる曲にしておきたい。

実際の曲だと、音が落ちても和音に紛れて気づけない。

設計：
  - 8トラック、1人1トラック
  - 8人の音域を完全に分ける → 音を見れば誰か分かる
  - 8人の楽器を全部変える → 楽器IDでも判別できる
  - 全部単音・和音なし → 消音マーカーが出るので音長も測れる

2曲作る：
  第1曲 順番に鳴らす（同時に鳴らない）
        → 誰か1人でも落ちればすぐ分かる。人数の上限を調べる。
  第2曲 全員同時に鳴らす
        → 8人ぶんのパケットが同時に届いたとき取りこぼさないか。
          第1曲が通って第2曲が落ちるなら、原因は「同時性」と分かる。
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from make_test_midi import write_multi  # noqa: E402

OUT_DIR = r"D:\MIDI\_検証用"

NOTE_LEN = 0.8
GAP = 0.4

# 8人ぶんの割り当て。
#
# 音域は C3(48) から C6(84) までの37音しかないので、
# 8人で分けると 1人あたり4〜5音ぶん。
# 各自に「連続しない3音」を与えて、重ならないようにした。
#
# 楽器は、これまでの記録で判別実績のあるものと
# 未検証のものを混ぜる。判別できる楽器を広げるため。
PARTS = [
    # (番号, 楽器,        音, 音, 音)
    (1, "Tuba",      [48, 50, 52]),   # C3  D3  E3   最低音域
    (2, "Cello",     [53, 55, 57]),   # F3  G3  A3
    (3, "Horn",      [59, 60, 62]),   # B3  C4  D4
    (4, "Lute",      [64, 65, 67]),   # E4  F4  G4
    (5, "Harp",      [69, 71, 72]),   # A4  B4  C5
    (6, "Violin",    [74, 76, 77]),   # D5  E5  F5
    (7, "Clarinet",  [79, 81, 83]),   # G5  A5  B5
    (8, "Fife",      [84, 84, 84]),   # C6 ×3       最高音
]


def nm(x):
    names = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B']
    return names[x % 12] + str(x // 12 - 1)


def build_sequential():
    """第1曲：1人ずつ順番に鳴らす。

    誰か1人でも落ちたら、空白ができるので一目で分かる。
    8人ぶんの EntityId が全部拾えるかを確かめる。
    """
    tracks = []
    t = 0.5

    for number, instrument, notes in PARTS:
        events = []
        for n in notes:
            events.append((t, NOTE_LEN, n))
            t += NOTE_LEN + GAP

        # 次の人との間に少し空ける（区切りを分かりやすく）
        t += 0.6
        tracks.append((f"{instrument} p{number}", events))

    return tracks


def build_simultaneous():
    """第2曲：全員が同時に鳴らす。

    8人ぶんのパケットが同じ瞬間に届いたとき、
    取りこぼさずに処理できるかを確かめる。

    第1曲が通って第2曲だけ落ちるなら、
    原因は人数ではなく「同時に届くこと」だと切り分けられる。
    """
    tracks = []

    for number, instrument, notes in PARTS:
        events = []
        t = 0.5
        for n in notes:
            events.append((t, NOTE_LEN, n))
            t += NOTE_LEN + GAP
        tracks.append((f"{instrument} p{number}", events))

    return tracks


def main():
    os.makedirs(OUT_DIR, exist_ok=True)

    songs = [
        ("octet_1_順番に.mid", build_sequential(), "octet sequential"),
        ("octet_2_同時に.mid", build_simultaneous(), "octet simultaneous"),
    ]

    for filename, tracks, title in songs:
        path = os.path.join(OUT_DIR, filename)
        write_multi(path, tracks, title)

        total = sum(len(ev) for _, ev in tracks)
        end = max(s + l for _, ev in tracks for s, l, _ in ev)
        print(f"{path}")
        print(f"   {len(tracks)}トラック / {total}音 / 約{end:.1f}秒")
        print()

    print("トラックの割り当て（8人にこの番号で配る）")
    print("-" * 56)
    print(" Trk  楽器        音            担当")
    for i, (number, instrument, notes) in enumerate(PARTS, start=1):
        pitches = " ".join(nm(n) for n in notes)
        print(f" {i:>3}  {instrument:<10}  {pitches:<14} 8人目の{number}番")
    print()
    print("音域が重ならないので、記録された音を見るだけで")
    print("「誰の音が落ちたか」が特定できる。")


if __name__ == "__main__":
    main()
