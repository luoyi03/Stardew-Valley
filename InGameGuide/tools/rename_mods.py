# -*- coding: utf-8 -*-
"""把 Mod 的显示名（manifest.json 的 Name）改成中文。

- 只改「显示名」，绝不碰 UniqueID / ContentPackFor（依赖关系靠 UniqueID，所以改 Name 安全）
- 中文名后面保留英文原名，方便以后去 Nexus 搜索或对照
- 改之前会把原始 manifest.json 备份到 Manifest备份/

用法：
    python tools/rename_mods.py            # 应用中文显示名
    python tools/rename_mods.py --restore  # 还原成备份的原始 manifest
    python tools/rename_mods.py --dry      # 只看会改什么，不实际改
"""
import os
import re
import shutil
import sys

MODS = r"E:\Steam\steamapps\common\Stardew Valley\Mods"
BACKUP = r"f:\游戏空间\星露谷\Manifest备份"

# UniqueID -> 中文显示名（保留英文原名便于对照）
NAMES = {
    "ly.InGameCodex": "游戏内图鉴 (InGameCodex)",
    "ly.InGameGuide": "游戏内教程与查询 (InGameGuide)",
    "ly.StardewAI": "星露谷AI助手 (StardewAI)",
    "Annosz.UiInfoSuite2": "UI信息显示2 (UI Info Suite 2)",
    "spacechase0.GenericModConfigMenu": "通用模组配置菜单 (GMCM)",
    "ccr.TeleportNPCLocation": "NPC传送 (Teleport NPC Location)",
    "Bouhm.NPCMapLocations": "NPC地图位置 (NPC Map Locations)",
    "BetterRanching": "畜牧助手 (Better Ranching)",
    "Richard2091.MapTeleport": "地图传送 (MapTeleport)",
    "BadNetCode.PonyWeightLossProgram": "小马瘦身 (Pony Weight Loss)",
    "Coldopa.MiniBars": "怪物血条 (Mini Bars)",
    "NormanPCN.CombatControlsRedux": "战斗操作优化 (Combat Controls Redux)",
    "Pathoschild.TractorMod": "拖拉机 (Tractor Mod)",
    "Esca.FarmTypeManager": "农场类型管理器 (FTM)",
    "FlashShifter.SVECode": "星露谷扩展 (SVE 主体)",
    "FlashShifter.StardewValleyExpandedCP": "星露谷扩展·内容包 (SVE CP)",
    "FlashShifter.SVE-FTM": "星露谷扩展·农场生成 (SVE FTM)",
    "thimadera.StackEverythingRedux": "万物堆叠 (Stack Everything Redux)",
    "shekurika.WaterFish": "水中可见鱼群 (Visible Fish)",
    "aedenthorn.ImmersiveSprinklersAndScarecrows": "洒水器与稻草人范围 (Immersive Sprinklers)",
    "NCarigon.PassableCrops": "可穿越作物 (Passable Crops)",
    "Pathoschild.ContentPatcher": "内容补丁 (Content Patcher)",
    "platinummyr.CustomBackpackFramework": "自定义背包框架 (Custom Backpack)",
    "platinummyr.CPMoreBackpacks": "更多背包升级 (More Backpack Upgrades)",
    "AaronTaggart.AutoAnimalDoors": "自动开关动物门 (Auto Animal Doors)",
    "Siweipancc.AutoFillPetBowl": "宠物碗自动加水 (Auto Fill Pet Bowl)",
    "mushymato.SpecialOrderNotifications": "特别订单提醒 (Special Order Notifications)",
    "DIGUS.MailFrameworkMod": "邮件框架 (Mail Framework Mod)",
    "Digus.MailServicesMod": "邮件服务 (Mail Services Mod)",
    "ChibiKyu.FishingAssistant2": "钓鱼助手2 (Fishing Assistant 2)",
    "yangZ.Bank": "银行 (Bank)",
}


def grab(text, key):
    m = re.search(r'"%s"\s*:\s*"([^"]*)"' % key, text)
    return m.group(1) if m else ""


def read(path):
    with open(path, "rb") as f:
        raw = f.read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    return raw.decode("utf-8-sig" if bom else "utf-8", errors="replace"), bom


def write(path, text, bom):
    data = text.encode("utf-8")
    if bom:
        data = b"\xef\xbb\xbf" + data
    with open(path, "wb") as f:
        f.write(data)


def main():
    restore = "--restore" in sys.argv
    dry = "--dry" in sys.argv

    changed = 0
    for root, dirs, files in os.walk(MODS):
        if "manifest.json" not in files:
            continue
        path = os.path.join(root, "manifest.json")
        rel = os.path.relpath(root, MODS)
        bak = os.path.join(BACKUP, rel, "manifest.json")

        if restore:
            if os.path.exists(bak):
                shutil.copy2(bak, path)
                print("已还原  %s" % rel)
                changed += 1
            continue

        text, bom = read(path)
        uid = grab(text, "UniqueID")
        if uid not in NAMES:
            continue

        os.makedirs(os.path.dirname(bak), exist_ok=True)
        if not os.path.exists(bak):
            shutil.copy2(path, bak)

        old = grab(text, "Name")
        new = NAMES[uid]
        if old == new:
            continue

        text2 = re.sub(r'("Name"\s*:\s*)"[^"]*"', lambda m: m.group(1) + '"' + new + '"', text, count=1)
        if dry:
            print("将改名  %-52s %s  ->  %s" % (rel, old, new))
        else:
            write(path, text2, bom)
            print("已改名  %-52s %s  ->  %s" % (rel, old, new))
        changed += 1

    print("\n共 %d 个" % changed)
    if not restore:
        print("原始文件已备份到：%s" % BACKUP)
        print("想还原：python tools/rename_mods.py --restore")
    return 0


if __name__ == "__main__":
    sys.exit(main())
