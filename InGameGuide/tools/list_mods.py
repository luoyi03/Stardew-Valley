# -*- coding: utf-8 -*-
"""列出星露谷 Mods 目录里装的全部 Mod（递归扫描 manifest.json）。"""
import json
import os
import sys

MODS = r"E:\Steam\steamapps\common\Stardew Valley\Mods"


def main():
    found = []
    for root, dirs, files in os.walk(MODS):
        if "manifest.json" not in files:
            continue
        path = os.path.join(root, "manifest.json")
        rel = os.path.relpath(root, MODS)
        try:
            with open(path, "r", encoding="utf-8-sig") as f:
                j = json.load(f)
        except Exception as e:
            found.append(("(解析失败) " + rel, "", "", "?", rel, str(e)[:40]))
            continue
        kind = "内容包" if j.get("ContentPackFor") else "C# Mod"
        found.append((j.get("Name", "?"), j.get("Version", ""), j.get("Author", ""), kind, rel, ""))

    found.sort(key=lambda r: (r[3], r[0]))

    w = max((len(r[0]) for r in found), default=10)
    print(f"{'名称':<{w}}  {'版本':<10} {'类型':<7} 位置")
    print("-" * 100)
    for name, ver, author, kind, rel, err in found:
        line = f"{name:<{w}}  {ver:<10} {kind:<7} {rel}"
        if err:
            line += f"  [{err}]"
        print(line)

    code = sum(1 for r in found if r[3] == "C# Mod")
    pack = sum(1 for r in found if r[3] == "内容包")
    print("-" * 100)
    print(f"共 {len(found)} 个：C# Mod {code} 个，内容包 {pack} 个")
    return 0


if __name__ == "__main__":
    sys.exit(main())
