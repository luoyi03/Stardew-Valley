# -*- coding: utf-8 -*-
"""从物品中文名生成「汉字 -> 拼音首字母」映射，供运行时的拼音首字母检索使用。

输入：InGameGuide 的 assets/items.json（[type, zh, en, code] 四条一组）
输出：本目录上一级 assets/pinyin.json（{"洒": "s", "水": "s", ...}）
用法：python tools/extract_pinyin.py
"""
import json
import os
import sys

from pypinyin import lazy_pinyin

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SRC = os.path.join(os.path.dirname(ROOT), "InGameGuide", "assets", "items.json")
DST = os.path.join(ROOT, "assets", "pinyin.json")


def is_cjk(ch):
    return "\u4e00" <= ch <= "\u9fff"


def main():
    if not os.path.exists(SRC):
        print("找不到输入文件：" + SRC)
        return 1

    with open(SRC, "r", encoding="utf-8") as f:
        data = json.load(f)

    chars = set()
    names = 0
    for row in data.get("items", []):
        if len(row) < 2:
            continue
        name = row[1] or ""
        names += 1
        for ch in name:
            if is_cjk(ch):
                chars.add(ch)

    table = {}
    for ch in sorted(chars):
        py = lazy_pinyin(ch)
        if py and py[0]:
            table[ch] = py[0][0].lower()

    os.makedirs(os.path.dirname(DST), exist_ok=True)
    with open(DST, "w", encoding="utf-8") as f:
        json.dump(table, f, ensure_ascii=False, sort_keys=False, separators=(",", ":"))

    print("物品名 %d 个，唯一汉字 %d 个，已写入 %s" % (names, len(table), DST))
    # 抽样自检
    for sample in ["洒水器", "铱制洒水器", "钻石"]:
        print("  " + sample + " -> " + "".join(table.get(c, "?") for c in sample))
    return 0


if __name__ == "__main__":
    sys.exit(main())
