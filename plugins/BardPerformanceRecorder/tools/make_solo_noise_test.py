"""近くで別々の人が演奏しているとき、記録が混ざるかを調べる検証曲。

なぜ3つに分けるか：
記録されたパケットには「誰が弾いたか」しか入っておらず、
「何を弾いたか」から人を特定することはできない。
そこで3人に**別々の曲・別々の楽器**を弾いてもらい、
記録に現れた音から「どの曲が混ざったか」を判別できるようにする。

判別しやすくするための作り：
  - 3曲とも音域を重ねない（低・中・高で分ける）
  - 3曲とも楽器を変える（記録側の楽器IDで判別できる）
  - 音の数を変える（数えるだけでどの曲か分かる）
  - 単音のみ・和音なし（消音マーカーが必ず出るので音長も検証できる）

楽器は、これまでの記録で一度も出ていないものを選んだ。
楽器判別が正しいかを、同時に広げて確かめるため。
（既出: Harp / Piano / Flute / Oboe / Lute / Horn / Violin / Cello）
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from make_test_midi import write_multi  # noqa: E402

OUT_DIR = r"D:\MIDI\_検証用"

# 1音ずつゆっくり。人の耳でも数えられる速さにする。
# 速いと、混ざったとき「どちらの音か」を耳で確かめられない。
NOTE_LEN = 0.8
GAP = 0.4


def line(notes, start=0.5):
    """単音を一定間隔で並べる。"""
    out = []
    t = start
    for n in notes:
        out.append((t, NOTE_LEN, n))
        t += NOTE_LEN + GAP
    return out


# --- 曲A：低音域・Tuba・8音 ---------------------------------
# C3 から順に上がる。FF14 の最低音 C3 を含めて下端も確かめる。
A_NOTES = [48, 50, 52, 53, 55, 57, 59, 60]   # C3 D3 E3 F3 G3 A3 B3 C4

# --- 曲B：中音域・Clarinet・12音 -----------------------------
# 曲Aとも曲Cとも重ならない帯に置く。
B_NOTES = [62, 64, 65, 67, 69, 71, 72, 71, 69, 67, 65, 64]  # D4..C5..D4

# --- 曲C：高音域・Fife・6音 ----------------------------------
# 上端 C6 を含める。
C_NOTES = [74, 76, 78, 79, 81, 84]   # D5 E5 F#5 G5 A5 C6


def main():
    os.makedirs(OUT_DIR, exist_ok=True)

    songs = [
        ("noise_A_低音_Tuba.mid",      "Tuba",     A_NOTES, "A low tuba"),
        ("noise_B_中音_Clarinet.mid",  "Clarinet", B_NOTES, "B mid clarinet"),
        ("noise_C_高音_Fife.mid",      "Fife",     C_NOTES, "C high fife"),
    ]

    def nm(x):
        names = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B']
        return names[x % 12] + str(x // 12 - 1)

    for filename, instrument, notes, title in songs:
        path = os.path.join(OUT_DIR, filename)

        # トラック名に楽器名を入れると MidiBard2 が自動で持ち替える。
        track_name = f"{instrument} solo"
        write_multi(path, [(track_name, line(notes))], title)

        span = 0.5 + len(notes) * (NOTE_LEN + GAP)
        print(f"{path}")
        print(f"   楽器 {instrument} / {len(notes)}音 / 約{span:.1f}秒")
        print(f"   音域 {nm(min(notes))}～{nm(max(notes))}")
        print(f"   {' '.join(nm(n) for n in notes)}")
        print()

    print("判別の手がかり")
    print("  曲A: 8音 / C3～C4  / Tuba(ID 17)")
    print("  曲B: 12音 / D4～C5 / Clarinet(ID 7)")
    print("  曲C: 6音 / D5～C6  / Fife(ID 8)")
    print("  → 音域も楽器も音数も重ならないので、混ざればすぐ分かる。")


if __name__ == "__main__":
    main()
