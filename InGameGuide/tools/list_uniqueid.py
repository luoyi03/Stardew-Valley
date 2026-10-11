# -*- coding: utf-8 -*-
"""导出每个 Mod 的 UniqueID / 当前 Name / 位置，用于准备中文显示名映射。"""
import os
import re
import sys

MODS = r"E:\Steam\steamapps\common\Stardew Valley\Mods"


def grab(text, key):
    m = re.search(r'"%s"\s*:\s*"([^"]*)"' % key, text)
    return m.group(1) if m else ""


def main():
    rows = []
    for root, dirs, files in os.walk(MODS):
        if "manifest.json" not in files:
            continue
        path = os.path.join(root, "manifest.json")
        rel = os.path.relpath(root, MODS)
        with open(path, "r", encoding="utf-8-sig", errors="replace") as f:
            text = f.read()
        rows.append((grab(text, "UniqueID"), grab(text, "Name"), rel))

    rows.sort(key=lambda r: r[2])
    for uid, name, rel in rows:
        print("%-52s | %-40s | %s" % (uid, name, rel))
    print("\n共 %d 个" % len(rows))
    return 0


if __name__ == "__main__":
    sys.exit(main())
