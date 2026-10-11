# -*- coding: utf-8 -*-
"""向正在运行的星露谷发送命令（AI 侧使用的入口）。

用法：
    python tools/cmd.py ping
    python tools/cmd.py search 洒水器
    python tools/cmd.py add 铱制洒水器 5
    python tools/cmd.py add 钻石 1 --quality 4      # 品质 0普通 1银 2金 3铱
    python tools/cmd.py name 铱制洒水器              # 小鸡代码流：生成命名码
    python tools/cmd.py check 自动抚摸机             # 只探测能不能生成，不放进背包
    python tools/cmd.py pick 3                       # 从上次歧义候选里选第 3 个

匹配到多个物品时不会自动选，而是列出候选让你挑，然后再 pick：

    python tools/cmd.py add 洒水器 1   -> 列出 洒水器 / 优质洒水器 / 铱制洒水器
    python tools/cmd.py pick 3         -> 选铱制洒水器

想跳过确认就直接给完整名字或代码：add 铱制洒水器 1 / add 645 1 / add "(O)645" 1

原理：往 Mods/InGameGuide/bridge/request.json 写命令，
      Mod 每 12 帧检查一次，执行后把结果写回 bridge/result.json，本脚本轮询取回。
"""
import json
import os
import sys
import time

BRIDGE = r"E:\Steam\steamapps\common\Stardew Valley\Mods\InGameGuide\bridge"
REQ = os.path.join(BRIDGE, "request.json")
RES = os.path.join(BRIDGE, "result.json")
TIMEOUT = 10.0


def read_json(path, default=None):
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return default


def send(action, item=None, count=1, quality=0):
    last = read_json(RES) or {}
    req_id = int(last.get("Id", 0)) + 1

    payload = {"Id": req_id, "Action": action, "Item": item or "", "Count": count, "Quality": quality}
    with open(REQ, "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False)

    deadline = time.time() + TIMEOUT
    while time.time() < deadline:
        time.sleep(0.25)
        res = read_json(RES)
        if res and int(res.get("Id", -1)) == req_id:
            return res
    return {"Id": req_id, "Ok": False, "Message": "超时：游戏没有响应（游戏是否开着、是否已进入存档？）"}


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1

    action = sys.argv[1].lower()
    args = sys.argv[2:]
    query = " ".join(args)

    quality = 0
    if "--quality" in args:
        i = args.index("--quality")
        quality = int(args[i + 1])
        args = args[:i] + args[i + 2:]

    if action == "ping":
        res = send("ping")
    elif action == "search":
        res = send("search", " ".join(args))
    elif action == "check":
        res = send("check", " ".join(args))
    elif action in ("pick", "choose"):
        res = send("pick", " ".join(args))
    elif action in ("add", "give"):
        if not args:
            print("需要物品名，例如：python tools/cmd.py add 铱制洒水器 5")
            return 1
        count = int(args[-1]) if args and args[-1].isdigit() else 1
        name = " ".join(args[:-1]) if args and args[-1].isdigit() else " ".join(args)
        query = name
        res = send("add", name, count, quality)
    elif action == "name":
        res = send("name", " ".join(args))
    else:
        print("未知动作：" + action)
        print(__doc__)
        return 1

    flag = "OK  " if res.get("Ok") else "FAIL"
    print("[%s] %s" % (flag, res.get("Message", "")))

    if res.get("Candidates"):
        if res.get("NeedConfirm"):
            print("\n「%s」匹配到多个，请选择要哪一个：" % query)
        else:
            print("\n可选：")
        for c in res["Candidates"]:
            mark = "  ★" if c.get("Exact") else "   "
            print("   %d)%s %-16s %-22s %s" % (
                c["Index"], mark, c["Name"], c["QualifiedId"], c.get("Type", "")))
        print("\n然后执行：  python tools/cmd.py pick <序号或完整名字>")
        print("例如：      python tools/cmd.py pick 3")
        return 3 if res.get("NeedConfirm") else 2

    if res.get("QualifiedId"):
        print("      物品=%s  代码=%s  限定ID=%s  数量=%s  存档就绪=%s"
              % (res.get("Item"), res.get("Code"), res.get("QualifiedId"),
                 res.get("Count"), res.get("WorldReady")))
    return 0 if res.get("Ok") else 2


if __name__ == "__main__":
    sys.exit(main())
