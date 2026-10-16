# InGameGuide —— 星露谷内置教程 & 物品代码查询 Mod

把《星露谷新手图文教程》和《星露谷物品代码查询工具》内置进游戏，随时在游戏里查阅，不用切窗口。

## 按键

| 按键 | 作用 |
|---|---|
| `F2` | 打开「新手教程」面板（再按一次则关闭） |
| `F3` | 直接打开「物品代码查询」面板 |
| `Esc` | 关闭面板 |
| `Tab` | 在「教程」/「查询」两个标签之间切换 |
| 滚轮 / `↑` `↓` / `PageUp` `PageDown` / `Home` `End` | 滚动内容 |
| `←` `→` | 教程页切换上一章 / 下一章 |

按键可在 `config.json`（首次启动游戏后生成）里改 `ToggleKey` / `QueryKey`。

## 直接获取物品

三種方式：

1. **游戏内点按钮**：`F3` 打开查询页，每行左侧有个「获取」按钮，点一下物品直接进背包
   （背包满了会掉在脚下）。点条目本身则是复制代码。
2. **让 AI 帮你发**：直接跟 AI 说「给我 5 个铱制洒水器」即可，AI 会调用 `tools/cmd.py` 发命令进游戏。
3. **自己敲命令**：
   ```powershell
   python tools/cmd.py add 铱制洒水器 5
   python tools/cmd.py add 钻石 1 --quality 4     # 品质 0普通 1银 2金 3铱
   python tools/cmd.py search 洒水器
   python tools/cmd.py check 自动抚摸机    # 只探测能不能生成，不放进背包
   python tools/cmd.py ping
   ```

12 类物品全部支持 `add`：物体 (O)、大型可制作物 (BC)、工具 (T)、武器 (W)、家具 (F)、
上衣 (S)、帽子 (H)、裤子 (P)、鞋子 (B)、饰品 (TR)、人偶 (M)、壁纸 (WP)、地板 (FL)。
其中只有物体类 (O) 能走 `name` 命名彩蛋。

### 小鸡代码流

```powershell
python tools/cmd.py name 铱制洒水器
```

会生成 `[645]` 并复制到剪贴板，同时设为「待用命名」。之后去玛妮牧场买鸡/牛，
弹出命名框时 Mod 会自动把 `[645]` 填进去，确认即可白嫖物品。
（物体类才能走命名彩蛋；其余 11 类只能用 add。）

### AI 桥接原理

`Mods\InGameGuide\bridge\request.json` ← 外部写命令
`Mods\InGameGuide\bridge\result.json`  → Mod 回写结果

Mod 每 12 帧检查一次 request.json 的时间戳，变了就执行并回写。
不走网络端口，所以不会有防火墙问题。

## 两个标签页

**新手教程**：左侧 14 章目录，点击切换；右侧正文（标题 / 段落 / 提示框 / 表格 / 卡片 / 步骤列表）全都在游戏里重绘，支持中文字体。

**物品代码查询**：收录 1.6 版全部 12 类共 2446 条物品。支持中文名、英文名、代码搜索，可按类型筛选，点击条目即复制到系统剪贴板。

- 绿色代码 = 物体类（Object），复制出来是 `[74]` 形式，可直接粘到动物命名框里用命名彩蛋白嫖物品。
- 金色代码 = 工具 / 武器 / 家具 / 服饰等其余 11 类，需 PC 端 SMAPI 控制台 `player_add` 使用。

## 目录结构

```
InGameGuide/
  InGameGuide.csproj     # 项目文件（GamePath 指向游戏目录，构建后自动部署到 Mods）
  manifest.json          # SMAPI 清单
  src/                   # C# 源码
  assets/
    guide.json           # 由教程 HTML 抽取出的结构化章节内容
    items.json           # 2446 条物品代码库
  tools/
    extract_tutorial.py  # HTML -> guide.json
    extract_items.py     # HTML -> items.json
  _input/                # 原始 HTML 源文件的副本
```

## 重新生成数据

教程 HTML 改过之后，重新抽取即可，不用改 C#：

```powershell
cd 游戏空间\星露谷\InGameGuide
python tools\extract_tutorial.py
python tools\extract_items.py
```

## 编译

需要 .NET SDK（可编译 net6.0）：

```powershell
cd e:\星露谷\InGameGuide
dotnet build -c Release
```

构建成功后会自动复制到 `F:\steam\steamapps\common\Stardew Valley\Mods\InGameGuide`，并生成一份可分享的 zip
（路径取自 csproj 里的 `GamePath`，游戏换位置时改那一项即可）。

## 已知取舍

- 原 HTML 里的 SVG 色轮、CSS 绘制的矿层色块等纯装饰图形没有搬进游戏，对应位置以文字说明保留。
- 其余正文、表格（献祭清单、误区对照、四季规划等）全部完整保留。
