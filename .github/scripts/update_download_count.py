#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
repo.json の各プラグインの DownloadCount を、このリポジトリのリリースのダウンロード数で書き換える。
Dalamud のプラグインの画面は、DownloadCount が 0 より大きいと作者名の横に「(N downloads)」と出す。

数え方：リリースのタグは「<InternalName>-<版>」（例 GBRHelper-0.3.0.13）。同じ InternalName のリリースの
添付ファイルのダウンロード数を、版をまたいで合計する（更新のダウンロードも数える）。

  python3 .github/scripts/update_download_count.py repo.json

 - リリースを読めなかったときは何も書かずに終了コード 1（0 で上書きしない）。
 - リリースが 1 つも無いプラグインの値は変えない。
 - 書き方は今の repo.json と同じ（BOM 付き UTF-8・字下げ 2・改行は LF・末尾に改行なし）。変わるのは DownloadCount の行だけ。
 - 値が変わらなければファイルに触らない。
"""
import json
import os
import re
import sys
import urllib.request

API = "https://api.github.com"
TAG = re.compile(r"^(?P<name>.+)-(?P<version>\d+(?:\.\d+)+)$")


def releases(repo: str, token: str):
    page = 1
    while True:
        req = urllib.request.Request(f"{API}/repos/{repo}/releases?per_page=100&page={page}", headers={
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "Tashi-sysins-repo-download-count",
            **({"Authorization": f"Bearer {token}"} if token else {}),
        })
        with urllib.request.urlopen(req, timeout=30) as res:
            batch = json.loads(res.read().decode("utf-8"))
        if not isinstance(batch, list):
            raise RuntimeError(f"リリースの一覧の形が違う：{str(batch)[:200]}")
        yield from batch
        if len(batch) < 100:
            return
        page += 1


def main() -> int:
    path = sys.argv[1] if len(sys.argv) > 1 else "repo.json"
    repo = os.environ.get("GITHUB_REPOSITORY", "Tashi-sysins/Tashi-sysins-repo")
    token = os.environ.get("GITHUB_TOKEN", "").strip()

    try:
        totals: dict[str, int] = {}
        for r in releases(repo, token):
            m = TAG.match(r.get("tag_name") or "")
            if not m or r.get("draft"):
                continue
            n = sum(int(a.get("download_count") or 0) for a in r.get("assets") or [])
            totals[m.group("name")] = totals.get(m.group("name"), 0) + n
    except Exception as e:  # 読めなければ書かない（0 で上書きしない）
        print(f"リリースを読めませんでした（何も書きません）：{e}", file=sys.stderr)
        return 1

    raw = open(path, "rb").read()
    data = json.loads(raw.decode("utf-8-sig"))
    changed = []
    for entry in data:
        name = entry.get("InternalName")
        if name not in totals:
            print(f"{name}：リリースが見つからないので変えない（{entry.get('DownloadCount')}）")
            continue
        old = entry.get("DownloadCount")
        if old != totals[name]:
            entry["DownloadCount"] = totals[name]
            changed.append(f"{name} {old}→{totals[name]}")
        print(f"{name}：{totals[name]}")

    if not changed:
        print("変化なし")
        return 0
    text = json.dumps(data, ensure_ascii=False, indent=2)
    out = (("﻿" if raw.startswith(b"\xef\xbb\xbf") else "") + text).encode("utf-8")
    if b"\r\n" in raw:
        out = out.replace(b"\n", b"\r\n")
    with open(path, "wb") as f:
        f.write(out)
    print("書き換えた：" + "・".join(changed))
    return 0


if __name__ == "__main__":
    sys.exit(main())
