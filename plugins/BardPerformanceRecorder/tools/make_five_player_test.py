"""5人で未確認の楽器12種類を確かめる検証曲。

未確認は12種類。5人なので1曲あたり最大5楽器。
12 = 4 + 4 + 4 の3曲に分ける。

なぜ 5+5+2 ではなく 4+4+4 か：
最後の曲が2人だけだと、3人が待つだけになる。
4人ずつなら毎回ほぼ全員が弾けて、
「同時に何人ぶん届くか」の条件も3曲でそろう。

5人目は毎回別の役をやってもらう想定：
  - 5人しかいない場合 … 1人が観測役（記録する側）を兼ねる
  - 観測役が別にいる場合 … 5人目も弾けるよう5トラック目を用意した曲も出す

各トラックの中身：
  短音 0.3秒 ×2 → 長音 1.5秒 → 連打 0.2秒 ×4（計7音）
音長の復元が楽器によって変わらないかを見るため、
長さの違う音を必ず入れる。

音域は全トラックで重ねない（記録を見るだけで誰の音か分かる）。
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from make_test_midi import write_multi  # noqa: E402

OUT_DIR = r"D:\MIDI\_検証用"

# 未確認の12種類を3曲に分ける。5人なので1曲5トラック。
#
# 5トラック×3曲 = 15枠に対し、未確認は12種類。
# 余る3枠には**確認済みの楽器**を入れて、5人全員が毎回弾けるようにした。
# （4人ずつにすると毎回1人が余って待つことになる）
#
# 確認済みを混ぜるのは無駄ではない：
# 未確認の楽器と同時に鳴らすことで、
# 「既知の楽器は正しく、未確認だけが崩れる」という切り分けができる。
# 全部が未確認だと、崩れたとき原因が楽器なのか他の要因なのか分からない。
#
# 性質の近いものを固めず、毎回ばらけさせる。
# 1曲が丸ごと失敗しても、残り2曲で傾向が分かるようにするため。
#
# 音域は C3 / F3 / A#3 / D4 / G4 と 5 段に分け、重ならないようにした。
BASES = [48, 53, 58, 62, 67]   # C3 F3 A#3 D4 G4

SONGS = [
    ("five_1.mid", "five 1", [
        ("Fiddle", 4, False),                      # 未確認・弦
        ("BassDrum", 12, False),                   # 未確認・打楽器
        ("ElectricGuitarOverdriven", 24, False),   # 未確認・エレキ
        ("Panpipes", 9, False),                    # 未確認・木管
        ("Harp", 1, True),                         # 確認済み（比較用の基準）
    ]),
    ("five_2.mid", "five 2", [
        ("Trombone", 16, False),                   # 未確認・金管
        ("SnareDrum", 13, False),                  # 未確認・打楽器
        ("ElectricGuitarClean", 25, False),        # 未確認・エレキ
        ("ElectricGuitarMuted", 26, False),        # 未確認・エレキ
        ("Flute", 5, True),                        # 確認済み（比較用の基準）
    ]),
    ("five_3.mid", "five 3", [
        ("Bongo", 11, False),                      # 未確認・打楽器
        ("Cymbal", 14, False),                     # 未確認・打楽器
        ("ElectricGuitarPowerChords", 27, False),  # 未確認・エレキ
        ("ElectricGuitarSpecial", 28, False),      # 未確認・エレキ
        ("Violin", 20, True),                      # 確認済み（比較用の基準）
    ]),
]


def phrase(base):
    """短音 → 長音 → 連打。計7音。"""
    out = []
    t = 0.5

    for _ in range(2):          # 短音 0.3秒 ×2
        out.append((t, 0.3, base))
        t += 0.3 + 0.5

    out.append((t, 1.5, base))  # 長音 1.5秒
    t += 1.5 + 0.5

    for _ in range(4):          # 連打 0.2秒 ×4
        out.append((t, 0.2, base))
        t += 0.4

    return out


def nm(x):
    names = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B']
    return names[x % 12] + str(x // 12 - 1)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)

    for filename, title, group in SONGS:
        path = os.path.join(OUT_DIR, filename)

        tracks = []
        for i, (instrument, _, _) in enumerate(group):
            tracks.append((f"{instrument} t", phrase(BASES[i])))

        write_multi(path, tracks, title)

        total = sum(len(ev) for _, ev in tracks)
        end = max(s + l for _, ev in tracks for s, l, _ in ev)

        print(f"{path}")
        print(f"   {len(tracks)}トラック / {total}音 / 約{end:.1f}秒")
        for i, (instrument, iid, known) in enumerate(group):
            mark = "確認済み" if known else "★未確認"
            print(f"     Trk{i + 1}  {instrument:<28} ID{iid:<3} {nm(BASES[i]):<4} {mark}")
        print()

    unknown = sum(1 for _, _, g in SONGS for _, _, k in g if not k)
    print(f"未確認の楽器: 計 {unknown} 種類（3曲で全部そろう）")
    print("各トラック: 短音0.3秒×2 → 長音1.5秒 → 連打0.2秒×4（計7音）")
    print("音域は全トラックで重ならないので、誰の音かは記録を見れば分かる。")


if __name__ == "__main__":
    main()
