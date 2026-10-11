# -*- coding: utf-8 -*-
"""从「星露谷物品代码查询工具.html」里抽出 var DATA=[[type,zh,en,code],...]，
输出 assets/items.json：
    { "types": {"O":"物体", ...}, "order": [...], "items": [[type,zh,en,code], ...] }
"""
import json, os, re

SRC = os.path.join(os.path.dirname(__file__), "..", "_input", "星露谷物品代码查询工具.html")
OUT = os.path.join(os.path.dirname(__file__), "..", "assets", "items.json")

LABEL = {
    "O": "物体", "BC": "大型可制作物", "T": "工具", "W": "武器", "F": "家具",
    "S": "上衣", "H": "帽子", "P": "裤子", "B": "鞋子", "TR": "饰品",
    "M": "人偶", "WP": "壁纸", "FL": "地板",
}
ORDER = ["O", "T", "W", "BC", "F", "S", "H", "P", "B", "TR", "M", "WP", "FL"]


def main():
    src = os.path.abspath(SRC)
    with open(src, "r", encoding="utf-8") as f:
        html = f.read()

    m = re.search(r"var\s+DATA\s*=\s*(\[.*?\])\s*;", html, re.S)
    if not m:
        raise SystemExit("未找到 var DATA")
    raw = m.group(1)
    data = json.loads(raw)

    items = []
    for row in data:
        if not isinstance(row, list) or len(row) < 4:
            continue
        t, zh, en, code = row[0], row[1], row[2], row[3]
        items.append([t, zh or "", en or "", str(code)])

    out = os.path.abspath(OUT)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w", encoding="utf-8") as f:
        json.dump({"types": LABEL, "order": ORDER, "items": items}, f, ensure_ascii=False, separators=(",", ":"))

    print("items:", len(items))
    print("bytes:", os.path.getsize(out))


if __name__ == "__main__":
    main()
