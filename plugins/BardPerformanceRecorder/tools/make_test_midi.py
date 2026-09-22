# -*- coding: utf-8 -*-
"""制御実験用の MIDI を作る。

使い方:
    python -X utf8 make_test_midi.py [出力フォルダ]

なぜ必要か:
  実際の曲は音高・音長・和音が同時に変わるため、
  「どの要素が結果に効いたか」を切り分けられない。
  1つだけを変えた MIDI を作って、挙動を1つずつ確かめる。

作るもの（詳細は各関数の説明を参照）:
  D  音長テスト  … 0xFE の意味と音長の取得限界
  B  繰り返し    … 同じ内容の別区間が捨てられないか
  C  境界の連打  … パケット境界で連打が潰れないか
  A  半音階      … 音高変換と音域の端
"""
import json
import os
import re
import sys

import mido

# FF14 の演奏音域は C3(48) 〜 C6(84)。
LOWEST = 48
HIGHEST = 84
MIDDLE_C = 60          # C4

# 4人用の検証で使う楽器。
#
# トラック名にこの綴りを入れておくと、MidiBard2 が自動で楽器を選ぶ
# （`Midibard/Control/MidiControl/PlaybackInstance/TrackInfo.cs` の
#  instrumentIdMap。大文字小文字と空白は無視され、部分一致で判定される）。
#
# 楽器ID（同ファイルの対応表より）:
#   harp=1 piano=2 lute=3 fiddle=4 flute=5 oboe=6 clarinet=7 fife=8
#   panpipes=9 timpani=10 bongo=11 bassdrum=12 snaredrum=13 cymbal=14
#   trumpet=15 trombone=16 tuba=17 horn=18 saxophone=19 violin=20
#   viola=21 cello=22 doublebass=23 electricguitar*=24〜28
#
# ここでは「音が持続する管弦楽器」を選ぶ。
# 打楽器（timpani 等）は音が短く、音長の検証に向かない。
QUARTET_INSTRUMENTS = ["Harp", "Flute", "Violin", "Trumpet"]
QUARTET_INSTRUMENT_IDS = {"Harp": 1, "Flute": 5, "Violin": 20, "Trumpet": 15}
TPQN = 480
TEMPO = 500000         # 120 BPM → 4分音符 0.5 秒
TICKS_PER_SEC = TPQN * 2


def sec_to_tick(sec):
    return int(round(sec * TICKS_PER_SEC))


def write(path, events, title):
    """(開始秒, 長さ秒, ノート番号) の一覧から MIDI を書く。"""
    mid = mido.MidiFile(ticks_per_beat=TPQN)
    track = mido.MidiTrack()
    mid.tracks.append(track)

    track.append(mido.MetaMessage('set_tempo', tempo=TEMPO, time=0))
    track.append(mido.MetaMessage('track_name', name=title, time=0))

    timeline = []
    for start, length, note in events:
        timeline.append((start, 'on', note))
        timeline.append((start + length, 'off', note))

    # 同時刻は消音を先に（同じ音の連打で重ならないように）
    timeline.sort(key=lambda e: (e[0], 0 if e[1] == 'off' else 1))

    prev = 0
    for sec, kind, note in timeline:
        tick = sec_to_tick(sec)
        delta = max(0, tick - prev)
        prev = tick

        track.append(mido.Message(
            'note_on' if kind == 'on' else 'note_off',
            note=note,
            velocity=100 if kind == 'on' else 0,
            time=delta))

    mid.save(path)
    return len(events)


def case_d_lengths(out_dir):
    """D: 音長テスト（いちばん重要）

    同じ音高・同じ間隔で、音長だけを変える。
    パケット間隔（約0.49秒）より短いもの・長いものを混ぜる。

    見たいこと:
      - 長い音のあいだ 0xFE が出続けるか（保持という解釈が正しいか）
      - 0.1秒の音と2秒の音が、記録上どう違うか
      - 音長の取得限界はどこか

    音を十分に離してあるので、どの音がどの長さか迷わない。
    """
    lengths = [0.1, 0.25, 0.5, 1.0, 2.0, 3.0]
    events = []
    t = 1.0

    for length in lengths:
        events.append((t, length, MIDDLE_C))
        # 次の音まで、音長 + 2 秒あける（確実に切り分けるため）
        t += length + 2.0

    path = os.path.join(out_dir, "test_D_音長.mid")
    n = write(path, events, "D: note lengths")

    print(f"D 音長テスト : {n}音 / {t:.1f}秒 → {os.path.basename(path)}")
    print("   C4 を " + "、".join(f"{x}秒" for x in lengths) + " 保持")
    print("   ※ いちばん重要。0xFE の意味と音長の限界を見る")
    return path


def case_b_repeat(out_dir):
    """B: 同じ内容の繰り返し

    同じ音を一定周期で繰り返す。
    二重観測の除外が、正当な繰り返しまで捨てていないか確かめる。
    """
    events = []
    t = 1.0
    for _ in range(8):
        events.append((t, 0.3, MIDDLE_C))
        t += 1.0

    path = os.path.join(out_dir, "test_B_繰り返し.mid")
    n = write(path, events, "B: repeated same note")

    print(f"B 繰り返し   : {n}音 / {t:.1f}秒 → {os.path.basename(path)}")
    print("   C4 を 1 秒ごとに 8 回。全部残れば正しい")
    return path


def case_c_boundary(out_dir):
    """C: パケット境界での同音連打

    約0.49秒ごとの区間をまたぐように同じ音を連打する。
    境界で潰れたり、二重に数えられたりしないか確かめる。
    """
    events = []
    t = 1.0

    # 0.49秒の区間に対し、わざと半端な間隔（0.16秒）で並べる
    for i in range(12):
        events.append((t, 0.1, MIDDLE_C))
        t += 0.16

    t += 2.0

    # 続けて別の音高でも同じことをする
    for i in range(12):
        events.append((t, 0.1, MIDDLE_C + 7))
        t += 0.16

    path = os.path.join(out_dir, "test_C_境界連打.mid")
    n = write(path, events, "C: repeats across packet boundary")

    print(f"C 境界連打   : {n}音 / {t:.1f}秒 → {os.path.basename(path)}")
    print("   0.16秒間隔の連打。パケット境界(約0.49秒)をまたぐ")
    return path


def case_a_chromatic(out_dir):
    """A: 半音階

    音域の端から端まで、1音ずつ上がる。
    音高変換と、C3/C6 が切れないかを確かめる。
    """
    events = []
    t = 1.0
    for note in range(LOWEST, HIGHEST + 1):
        events.append((t, 0.4, note))
        t += 0.8

    path = os.path.join(out_dir, "test_A_半音階.mid")
    n = write(path, events, "A: chromatic scale")

    print(f"A 半音階     : {n}音 / {t:.1f}秒 → {os.path.basename(path)}")
    print("   C3〜C6 を半音ずつ。音高変換の確認用")
    return path


def write_settings_json(midi_path, tracks):
    """MidiBard2 が読む設定ファイル（.json）を、MIDI と同じ名前で作る。

    MidiBard2 は曲ごとに、どのトラックを誰がどの楽器で弾くかを
    この .json に保存する。あらかじめ用意しておくと、
    **トラックと楽器の対応が確定した状態で検証を始められる**。

    実際に生成されたファイルの形をそのまま真似ている。

      Index        … トラック番号（音のあるトラックを 0 から数える）
      Enabled      … 演奏するか
      Name         … トラック名（楽器の自動判定にも使われる）
      Transpose    … 移調（0 のまま）
      Instrument   … 楽器ID
      AssignedCids … 担当する人の ContentId

    AssignedCids は空で出す。誰が弾くかはゲーム内で割り当てるため。
    （ContentId は永続IDで、プラグインが記録する EntityId とは別物。
      こちらから決め打ちできない）
    """
    data = {
        "Tracks": [
            {
                "Index": i,
                "Enabled": True,
                "Name": name,
                "Transpose": 0,
                "Instrument": instrument_id_from_name(name),
                "AssignedCids": [],
            }
            for i, (name, _) in enumerate(tracks)
        ],
        "ToneMode": 0,
        "AdaptNotes": True,
        "Speed": 1.0,
    }

    json_path = os.path.splitext(midi_path)[0] + ".json"
    with open(json_path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)

    return json_path


def instrument_id_from_name(track_name):
    """トラック名から楽器IDを求める。

    MidiBard2 の `TrackInfo.GetInstrumentIDByName` と同じ規則。
    空白とコロンを取り除き、小文字にして、楽器名を部分一致で探す。
    """
    sanitized = re.sub(r"(\s+|:)", "", track_name).lower()

    for name, instrument_id in INSTRUMENT_IDS.items():
        if name in sanitized:
            return instrument_id

    return 0


# MidiBard2 の楽器名 → ID（TrackInfo.cs の instrumentIdMap より）。
#
# 長い名前を先に並べる。"electricguitarclean" を調べる前に
# "electricguitar" で当たってしまうのを防ぐため。
INSTRUMENT_IDS = {
    "electricguitaroverdriven": 24,
    "electricguitarclean": 25,
    "electricguitarmuted": 26,
    "electricguitarpowerchords": 27,
    "electricguitarspecial": 28,
    "doublebass": 23,
    "contrabass": 23,
    "snaredrum": 13,
    "bassdrum": 12,
    "panpipes": 9,
    "clarinet": 7,
    "saxophone": 19,
    "trombone": 16,
    "trumpet": 15,
    "timpani": 10,
    "violin": 20,
    "fiddle": 4,
    "cymbal": 14,
    "bongo": 11,
    "cello": 22,
    "piano": 2,
    "flute": 5,
    "viola": 21,
    "harp": 1,
    "lute": 3,
    "oboe": 6,
    "fife": 8,
    "horn": 18,
    "tuba": 17,
    "sax": 19,
}


def write_multi(path, tracks, title):
    """複数トラックの MIDI を書く。

    MidiBard2 では 1 つのファイルの中でトラックを選んで演奏するため、
    2人用の検証曲も**1ファイル・複数トラック**にする必要がある。

    実際の曲に合わせて次の形にする。
      - Type 1（複数トラック）
      - トラックごとに別チャンネル
      - トラックに名前を付ける（演奏者が選びやすいように）

    tracks は [(トラック名, [(開始秒, 長さ秒, ノート番号), ...]), ...]

    注意：MIDI のトラック名は latin-1 でしか書けないため、
    日本語は使えない。英数字にすること（ファイル名は日本語で問題ない）。
    """
    mid = mido.MidiFile(ticks_per_beat=TPQN, type=1)

    # 先頭にテンポ用のトラックを置く（一般的な構成）
    conductor = mido.MidiTrack()
    conductor.append(mido.MetaMessage('set_tempo', tempo=TEMPO, time=0))
    conductor.append(mido.MetaMessage('track_name', name=title, time=0))
    mid.tracks.append(conductor)

    for index, (name, events) in enumerate(tracks):
        track = mido.MidiTrack()
        mid.tracks.append(track)
        track.append(mido.MetaMessage('track_name', name=name, time=0))

        channel = index % 16

        timeline = []
        for start, length, note in events:
            timeline.append((start, 'on', note))
            timeline.append((start + length, 'off', note))

        timeline.sort(key=lambda e: (e[0], 0 if e[1] == 'off' else 1))

        prev = 0
        for sec, kind, note in timeline:
            tick = sec_to_tick(sec)
            delta = max(0, tick - prev)
            prev = tick

            track.append(mido.Message(
                'note_on' if kind == 'on' else 'note_off',
                note=note,
                velocity=100 if kind == 'on' else 0,
                channel=channel,
                time=delta))

    mid.save(path)

    # MidiBard2 用の設定ファイルも一緒に作る。
    # トラックと楽器の対応が確定した状態で検証を始められる。
    write_settings_json(path, tracks)

    return sum(len(events) for _, events in tracks)


def case_duet(out_dir):
    """2人用：演奏者の分離を確かめる

    **1ファイルに2トラック**を入れる。
    MidiBard2 は1つのファイルからトラックを選んで演奏するため、
    別々のファイルを同時に弾くことはできない。

    2人に同じ内容を弾かせてはいけない。
    同じ音高・同じ時刻になり、記録結果を見ても
    どちらが弾いた音か区別できなくなる。

    そこで次の2曲を用意する。

      1曲目：低音／高音 … 音高で区別できる。演奏者の分離を確かめる
      2曲目：同音・短／長 … 同じ音高で長さだけ違う。
             終了待ちが演奏者ごとに独立しているかを確かめる
             （ここが混ざると、片方の音長がもう片方に引きずられる）
    """
    paths = []

    # --- 1曲目：音域で分ける ---
    low = [(1.0 + i * 1.0, 0.3, LOWEST) for i in range(8)]          # C3
    high = [(1.0 + i * 1.0, 0.3, LOWEST + 24) for i in range(8)]    # C5

    p = os.path.join(out_dir, "duet_1_音域で分ける.mid")
    write_multi(p, [
        ("Track1 Harp Low C3", low),
        ("Track2 Flute High C5", high),
    ], "Duet 1: split by pitch")
    paths.append(p)

    print("2人用①「音域で分ける」 : duet_1_音域で分ける.mid")
    print("   トラック1 = C3 を1秒ごとに8回")
    print("   トラック2 = C5 を1秒ごとに8回（同じタイミング）")
    print("   ※ 24半音離れているので、記録結果で確実に見分けられる")

    # --- 2曲目：同じ音で長さだけ違う ---
    short = [(1.0 + i * 1.5, 0.3, MIDDLE_C) for i in range(6)]
    long_ = [(1.0 + i * 1.5, 1.0, MIDDLE_C) for i in range(6)]

    p = os.path.join(out_dir, "duet_2_同じ音で長さ違い.mid")
    write_multi(p, [
        ("Track1 Harp Short 0.3s", short),
        ("Track2 Flute Long 1.0s", long_),
    ], "Duet 2: same note, different length")
    paths.append(p)

    print()
    print("2人用②「同じ音で長さ違い」 : duet_2_同じ音で長さ違い.mid")
    print("   トラック1 = C4 を 0.3 秒 × 6回")
    print("   トラック2 = C4 を 1.0 秒 × 6回（同じタイミング）")
    print("   ※ 終了待ちが演奏者ごとに独立しているかを見る")

    return paths


def case_quartet(out_dir):
    """4人用：人数が増えても分離できるかを確かめる

    2人では問題なかったが、人数が増えたときに
    崩れないかは別の話。次の2曲で確かめる。

      1曲目：音域で4分割 … 誰の音か確実に見分けられる
      2曲目：同じ音で長さ4種 … 終了待ちが4人ぶん独立しているか

    どちらも同じタイミングで鳴らすので、
    「同時に鳴るはずの音」のずれから同期精度も測れる。
    """
    paths = []

    # --- 1曲目：音域で4分割 ---
    # C3 / C4 / C5 / C6 と 1 オクターブずつ離す。
    # 12半音離れていれば、記録結果で取り違えようがない。
    #
    # トラック名に楽器名を入れると、MidiBard2 が自動で楽器を切り替える
    # （`TrackInfo.GetInstrumentIDByName`）。演奏者が手で選ぶ必要がなくなり、
    # 楽器IDが正しく記録されるかも同時に確かめられる。
    tracks = []
    for i, instrument in enumerate(QUARTET_INSTRUMENTS):
        note = LOWEST + i * 12
        events = [(1.0 + j * 1.0, 0.3, note) for j in range(8)]
        tracks.append((f"Track{i + 1} {instrument} {nm_ascii(note)}", events))

    p = os.path.join(out_dir, "quartet_1_音域で4分割.mid")
    write_multi(p, tracks, "Quartet 1: split by pitch")
    paths.append(p)

    print("4人用①「音域で4分割」 : quartet_1_音域で4分割.mid")
    for i, ins in enumerate(QUARTET_INSTRUMENTS):
        note = LOWEST + i * 12
        print(f"   Track{i + 1} {ins:8}(ID {QUARTET_INSTRUMENT_IDS[ins]:2}) = {nm_ascii(note)} × 8音")
    print("   1オクターブずつ離してあるので確実に見分けられる")

    # --- 2曲目：同じ音で長さだけ4種類 ---
    lengths = [0.2, 0.5, 1.0, 2.0]
    tracks = []
    for i, length in enumerate(lengths):
        events = [(1.0 + j * 2.5, length, MIDDLE_C) for j in range(5)]
        instrument = QUARTET_INSTRUMENTS[i]
        tracks.append((f"Track{i + 1} {instrument} {length}s", events))

    p = os.path.join(out_dir, "quartet_2_同じ音で長さ4種.mid")
    write_multi(p, tracks, "Quartet 2: same note, 4 lengths")
    paths.append(p)

    print()
    print("4人用②「同じ音で長さ4種」 : quartet_2_同じ音で長さ4種.mid")
    for i, ins in enumerate(QUARTET_INSTRUMENTS):
        print(f"   Track{i + 1} {ins:8}(ID {QUARTET_INSTRUMENT_IDS[ins]:2}) = C4 × 5音 / {lengths[i]}秒")
    print("   ※ 終了待ちが4人ぶん独立しているかを見る")

    return paths


def nm_ascii(note):
    """トラック名用の音名。MIDI のトラック名は latin-1 しか使えない。"""
    names = ['C', 'Cs', 'D', 'Ds', 'E', 'F', 'Fs', 'G', 'Gs', 'A', 'As', 'B']
    return f"{names[note % 12]}{note // 12 - 1}"


def main():
    out_dir = sys.argv[1] if len(sys.argv) > 1 else r"D:\MIDI\_検証用"
    os.makedirs(out_dir, exist_ok=True)

    print("制御実験用の MIDI を作ります")
    print("=" * 56)
    print(f"出力先: {out_dir}")
    print()

    case_d_lengths(out_dir)
    print()
    case_b_repeat(out_dir)
    print()
    case_c_boundary(out_dir)
    print()
    case_a_chromatic(out_dir)
    print()
    case_duet(out_dir)
    print()
    case_quartet(out_dir)

    print()
    print("すべて C3〜C6 の音域に収めてあります。")
    print("まず D（音長）から試してください。")
    return 0


if __name__ == '__main__':
    sys.exit(main())
