"""未確認の楽器12種類を確かめる検証曲。

これまで実機で確認できた楽器は16種類。残り12種類が未確認。
特に打楽器とエレキギター系は、他の楽器と挙動が違う可能性がある。

  - 打楽器（Timpani以外）… 音程が無い、または扱いが特殊かもしれない
  - エレキギター系5種 … 「音色」(tone)バイトを使う唯一の楽器群

作る曲：
  第1曲 打楽器・木管など6種（1人6トラック or 6人）
  第2曲 エレキギター5種＋Fiddle（音色バイトの検証を兼ねる）

各トラックは
  短音 → 長音 → 連打
の3種類を入れる。音長の復元が楽器によって変わらないかを見るため。
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from make_test_midi import write_multi  # noqa: E402

OUT_DIR = r"D:\MIDI\_検証用"

# 未確認の12種類。音域が重ならないよう割り当てる。
#
# 打楽器は音程を持たない可能性があるが、
# 「持たない」ことも確かめたいので普通に音程を付けて出す。
GROUP_A = [
    ("Fiddle",    4, 48),   # C3
    ("Panpipes",  9, 53),   # F3
    ("Trombone", 16, 58),   # A#3
    ("BassDrum", 12, 62),   # D4
    ("SnareDrum",13, 67),   # G4
    ("Cymbal",   14, 72),   # C5
]

GROUP_B = [
    ("Bongo",                     11, 48),  # C3
    ("ElectricGuitarOverdriven",  24, 53),  # F3
    ("ElectricGuitarClean",       25, 58),  # A#3
    ("ElectricGuitarMuted",       26, 62),  # D4
    ("ElectricGuitarPowerChords", 27, 67),  # G4
    ("ElectricGuitarSpecial",     28, 72),  # C5
]


def phrase(base):
    """短音 → 長音 → 連打 の順に並べる。

    楽器によって音長の復元が変わらないかを見たいので、
    長さの違う音を必ず入れる。
    """
    out = []
    t = 0.5

    # 短音 0.3秒 ×2
    for _ in range(2):
        out.append((t, 0.3, base))
        t += 0.3 + 0.5

    # 長音 1.5秒
    out.append((t, 1.5, base))
    t += 1.5 + 0.5

    # 連打 0.2秒 ×4（間隔も0.2秒）
    for _ in range(4):
        out.append((t, 0.2, base))
        t += 0.4

    return out


def nm(x):
    names = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B']
    return names[x % 12] + str(x // 12 - 1)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)

    songs = [
        ("rest_A_打楽器ほか.mid",    GROUP_A, "rest A"),
        ("rest_B_エレキギター.mid",  GROUP_B, "rest B"),
    ]

    for filename, group, title in songs:
        path = os.path.join(OUT_DIR, filename)
        tracks = [(f"{name} t", phrase(base)) for name, _, base in group]
        write_multi(path, tracks, title)

        total = sum(len(ev) for _, ev in tracks)
        end = max(s + l for _, ev in tracks for s, l, _ in ev)

        print(f"{path}")
        print(f"   {len(tracks)}トラック / {total}音 / 約{end:.1f}秒")
        for name, iid, base in group:
            print(f"     {name:<28} ID{iid:<3} {nm(base)} ×7音")
        print()

    print("各トラックの中身: 短音0.3秒×2 → 長音1.5秒 → 連打0.2秒×4（計7音）")
    print("→ 楽器によって音長の復元が変わらないかを見る。")
    print()
    print("人数が足りなければ、1人が複数トラックを弾いてもよい")
    print("（ただし楽器は曲中1種類しか使えないので、その場合は分けて録る）。")


if __name__ == "__main__":
    main()
