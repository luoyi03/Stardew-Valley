#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
StardewAI 的常驻语音合成助手。

为什么要常驻：edge-tts 和 miniaudio 都是 Python 库，如果每句话都新起一个进程再 import 一遍，
Windows 上光启动就要 1~2 秒。这里一次启动、常驻读请求，所以每句只花网络合成的时间。

协议（一行一个 JSON，UTF-8）：
  请求：{"text_b64": "<文本的 UTF-8 base64>", "out": "<wav 输出路径>",
         "voice": "zh-CN-XiaoxiaoNeural", "rate": "+0%", "rate_percent": 0}
  应答：{"ok": true, "bytes": 12345, "ms": 820}
        {"ok": false, "error": "..."}
  特殊：请求 {"cmd": "ping"} → {"ok": true, "pong": true}
       stdin 关闭（EOF）时退出。

直接运行也可以，用来手工验证：
  echo {"cmd":"ping"} | python -u tts_worker.py
"""

import asyncio
import base64
import json
import sys
import time
import wave

# 中文 Windows 上 Python 默认按 GBK 解 stdin，而调用方（C#）写的是 UTF-8，
# 不统一的话第一行 JSON 会解析失败。强制两端都按 UTF-8 走。
try:
    sys.stdin.reconfigure(encoding="utf-8", errors="replace")
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

try:
    import edge_tts
    import miniaudio
except Exception as ex:  # 缺库时明确报出来，别让 C# 那边只看到进程退出
    sys.stdout.write(json.dumps({"ok": False, "error": "缺少依赖（pip install edge-tts miniaudio）：%s" % ex}, ensure_ascii=False) + "\n")
    sys.stdout.flush()
    raise SystemExit(2)


LOOP = asyncio.new_event_loop()
asyncio.set_event_loop(LOOP)


async def synth(text, voice, rate, timing):
    """向 edge 的朗读服务要音频，返回 mp3 字节，并把耗时记进 timing。"""
    started = time.time()
    comm = edge_tts.Communicate(text, voice, rate=rate)

    buf = bytearray()
    async for chunk in comm.stream():
        if chunk.get("type") == "audio":
            if not buf:
                timing["first_ms"] = int((time.time() - started) * 1000)
            buf += chunk["data"]

    timing["total_ms"] = int((time.time() - started) * 1000)
    return bytes(buf)


def to_wav(mp3_bytes, out_path):
    """mp3 → 16bit 单声道 24kHz wav（游戏音频引擎只认 wav）。"""
    decoded = miniaudio.decode(
        mp3_bytes,
        output_format=miniaudio.SampleFormat.SIGNED16,
        nchannels=1,
        sample_rate=24000,
    )
    with wave.open(out_path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(24000)
        w.writeframes(decoded.samples.tobytes())
    return len(decoded.samples) * 2


def handle(req):
    if req.get("cmd") == "ping":
        return {"ok": True, "pong": True}

    text = base64.b64decode(req["text_b64"]).decode("utf-8", "ignore")
    voice = req.get("voice") or "zh-CN-XiaoxiaoNeural"
    rate = req.get("rate") or "+0%"
    out_path = req["out"]

    timing = {}
    mp3 = LOOP.run_until_complete(synth(text, voice, rate, timing))
    if not mp3:
        return {"ok": False, "error": "合成结果为空（网络或音色有问题？）"}

    decode_ms = int(time.time() * 1000)
    size = to_wav(mp3, out_path)
    timing["decode_ms"] = int(time.time() * 1000) - decode_ms

    if size <= 0:
        return {"ok": False, "error": "解码出来是空的"}

    timing["ok"] = True
    timing["bytes"] = size
    return timing


def main():
    for line in sys.stdin:
        # 有的调用方（PowerShell / 默认编码的 StreamWriter）会在最前面塞 BOM，去掉它
        line = line.lstrip("\ufeff").strip()
        if not line:
            continue

        started = time.time()
        try:
            req = json.loads(line)
            res = handle(req)
        except Exception as ex:
            res = {"ok": False, "error": "%s: %s" % (type(ex).__name__, ex)}

        res["ms"] = int((time.time() - started) * 1000)
        sys.stdout.write(json.dumps(res, ensure_ascii=False) + "\n")
        sys.stdout.flush()


if __name__ == "__main__":
    main()
