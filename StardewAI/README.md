# StardewAI — 游戏内 AI 助手 Mod

在《星露谷物语》里直接和大模型对话：

- **NPC 自由对话**（默认 `F4`）：对着附近的 NPC 按键，就能用他的口吻和你聊天；每个 NPC 有独立的对话记忆。
- **AI 问答面板**：默认**已禁用**（`AskKey = "None"`）。问答面板统一由 **InGameCodex 的 `F5`** 提供（它那个 AI 页就是从本 Mod 搬过去的），两边都占 `F5` 会一次弹出两个面板。想启用本 Mod 的面板，把 `AskKey` 改成 `F5` 或别的键即可；接口地址 / 密钥 / 模型两边仍然共用（InGameCodex 首次启动会读这份配置）。
- **语音聊天**（`aivoice on` 开启）：她的回答会被**念出来**（屏幕下方同步显示字幕）；**按住 `V` 说话**，松开自动把你说的转成文字发给她。于是两种玩法都能用：**你说话 → 她语音回答**、**你打字 → 她语音回答**。
- **她会主动看你正在干什么**：钓到鱼、下到新矿层、技能升级、进新地图、受伤、体力见底、站着发呆时，她会**主动开口**——头顶弹一句气泡，也会念出来（可以只留文字或整个关掉）。她还知道你现在手里拿着什么工具、在矿洞第几层、体力还剩多少。
- 回答**流式显示**（一个字一个字蹦出来），回答中可以点「停止」打断。

## 一、配置 API（必做）

### 推荐：在游戏里改（不用碰文件）

在 SMAPI 控制台输入 `aisettings`（或用 `F5` 打开 InGameCodex 的问答面板）→ 点右上角 **「设置」** 按钮，就能在可视化界面里改所有选项：
接口地址 / 密钥 / 模型直接打字，开关一键切换，数值用 − + 调，按键点一下再按新键即可。
底部四个按钮：**保存**（写回 config.json）、**取消**、**恢复默认**、**测试连接**（真发一条请求验证 Key 能不能用，结果直接显示在面板上）。
也可以直接在 SMAPI 控制台输入 `aisettings` 打开它。

### 模型方案一键切换

设置界面第一行就是「**模型方案**」，点它循环切换，会自动填好地址和模型：

| 方案 | 地址 | 默认模型 |
| --- | --- | --- |
| 魔芋 | `https://www.moyu.cn/v1` | `DeepSeek-V4.1-flash` |
| 美团 LongCat | `https://api.longcat.chat/openai` | `LongCat-2.0`（推理模型，回答上限建议 ≥2000） |
| 智谱 GLM | `https://open.bigmodel.cn/api/paas/v4` | `glm-4.5-air`（`glm-5.3` 系列需付费资源包） |
| 硅基流动 | `https://api.siliconflow.cn/v1` | `Qwen/Qwen2.5-7B-Instruct`（账户需充值） |
| DeepSeek 官方 | `https://api.deepseek.com/v1` | `deepseek-chat` |
| OpenAI | `https://api.openai.com/v1` | `gpt-4o-mini` |
| 本地 Ollama | `http://localhost:11434/v1` | `qwen2.5:7b`（不用密钥） |
| 本地 LM Studio | `http://localhost:1234/v1` | `local-model`（不用密钥） |
| 自定义 | 自己填 | 自己填 |

**每个方案的密钥是分开保存的**（存在 `config.json` 的 `PresetKeys` 里）：给智谱填过一次，切到硅基流动再切回智谱，密钥会自动带出来，不用重新粘贴。

> 想完全离线免费：装 Ollama → `ollama pull qwen2.5:7b` → 方案选「本地 Ollama」→ 密钥留空。
> 用免费小模型时建议把 `MaxTutorialChars` 调到 4000~6000，让本地教程给它补知识。

### 也可以手改文件

编辑 `F:\steam\steamapps\common\Stardew Valley\Mods\StardewAI\config.json`：

```json
{
  "BaseUrl": "https://api.deepseek.com/v1",
  "ApiKey": "sk-xxxxxxxxxxxxxxxx",
  "Model": "deepseek-chat"
}
```

改完重启游戏，或在 SMAPI 控制台执行 `aikey sk-xxx`。

### 各家接口填法

| 服务商 | BaseUrl | Model | 备注 |
| --- | --- | --- | --- |
| DeepSeek | `https://api.deepseek.com/v1` | `deepseek-chat` | 便宜，中文好，默认配置 |
| OpenAI | `https://api.openai.com/v1` | `gpt-4o-mini` | 需要能直连 |
| 智谱 GLM | `https://open.bigmodel.cn/api/paas/v4` | `glm-4-flash` | 有免费额度 |
| 月之暗面 Kimi | `https://api.moonshot.cn/v1` | `moonshot-v1-8k` | |
| 阿里通义千问 | `https://dashscope.aliyuncs.com/compatible-mode/v1` | `qwen-plus` | |
| 硅基流动 SiliconFlow | `https://api.siliconflow.cn/v1` | `Qwen/Qwen2.5-7B-Instruct` | 有免费额度 |
| 本地 Ollama | `http://localhost:11434/v1` | `qwen2.5:7b` | **完全免费、离线**，ApiKey 留空即可 |
| LM Studio | `http://localhost:1234/v1` | 模型自选 | 同上 |

> 任意一个「OpenAI 兼容」的接口都能用。本地 Ollama 需要先执行 `ollama pull qwen2.5:7b`，
> 并用环境变量 `OLLAMA_ORIGINS=*` 或直接启动（Stardew 是本机访问，通常无需额外设置）。

## 二、按键

| 按键 | 作用 |
| --- | --- |
| `F5` | 打开/关闭 AI 问答面板——**默认禁用**（`AskKey = "None"`），问答统一用 InGameCodex 的 `F5`；改 `AskKey` 可启用本 Mod 的面板 |
| `F4` | 身边有 NPC 就直接开聊；**身边没人则打开 NPC 选择面板**（改 `NpcChatKey`） |
| `F7` | 打开/关闭**她和你的单独对话窗口**（改 `CompanionKey`）；`CompanionAutoOpen`（默认关）可让它进存档自动打开 |
| **按住 `V`** | **按住说话**：松开后自动转写成文字发给她，她的回答用语音念出来（改 `PttKey`；`VoiceEnabled` 关着时不生效） |
| 回车 | 发送；发送中按同键或点「停止」可中断 |
| `Esc` | 关闭面板 |
| 滚轮 | 滚动聊天记录（`InvertScroll: true` 可反转） |

> 「按住 `V`」在有聊天输入框打开时**不生效**——不然聊天框里就打不出 `v` 这个字母了。
> 走路时按（也就是没有面板打开时）它会自动打开/复用当前对话：正在和 NPC 聊就发给那个 NPC，没在聊就打开「语音伴侣」面板。
> 想聊天框开着也能按，把 `PttInChat` 改成 `true`（代价是那个字母打不出来）。

控制台命令（SMAPI 窗口里输入）：

```
ai                 显示帮助
aiask 春天种什么最赚  直接提问
aitalk 谢恩         和指定 NPC 对话（不填则取最近的）
aicompanion        打开语音伴侣面板
aivoice on|off     语音开关（听你说话 / 她说话）
aitts 你好呀        试听语音合成（验证 TTS 链路）
aisettings         打开游戏内设置界面
aiclear            清空所有对话历史
aikey sk-xxx       设置 API Key
```

设置界面里可以改的东西：接口地址 / 密钥 / 模型 / 流式输出 / 温度 / 回答上限 / 超时 /
上下文轮数 / 是否附带存档状态 / 是否检索本地教程 / 教程字数上限 / 是否禁用 Markdown /
额外要求 / NPC 触发方式（Hotkey、Always）/ NPC 黑名单 / NPC 主动打招呼 / 快捷键 /
滚轮反转 / 快捷提问列表（用 `|` 分隔），以及语音与伴侣的全部参数。

界面是**分页**的（顶部一排页签，点一下切换，滚轮只滚当前这页）：

| 页签 | 装什么 |
| --- | --- |
| 接口与模型 | 模型方案、接口地址、密钥、模型、流式、温度、回答上限、超时、上下文、省积分模式 |
| 回答与教程 | 存档状态、本地教程、教程字数、是否允许给物品、Markdown、额外要求 |
| NPC 与按键 | NPC 触发方式、黑名单、打招呼，以及各个快捷键、滚轮、快捷提问 |
| 语音 · 她说话 | 语音总开关、念回答、合成后端（openai / fish / sapi / edge）、各家的地址密钥模型音色、语速音量、返回格式 |
| 语音 · 听你说 | 按住说话开关与按键、识别后端与模型/语言、录音时长阈值 |
| 伴侣与搭话 | 伴侣名字与人设、是否让她知道你此刻在干什么、主动搭话的开关/冷却/概率/发呆与定时闲聊秒数 |

底部的「保存 / 取消 / 恢复默认 / 测试连接 / 试听音色」在**任何页**都能点。

## 三、config.json 全部字段

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `AskKey` / `NpcChatKey` | `F5` / `F4` | 快捷键，支持所有 SMAPI 键名，如 `LeftShift + F5` 不支持，用单个键 |
| `NpcMode` | `Hotkey` | `Hotkey`=只用按键触发；`Always`=和 NPC 说话时自动接管原版对话 |
| `NpcBlacklist` | 空 | `Always` 模式下不接管的 NPC 显示名，逗号分隔，如 `法师,矮人` |
| `BaseUrl` / `ApiKey` / `Model` | DeepSeek | 见上表 |
| `Temperature` | 0.7 | 越小越严谨；攻略类问题可以调到 0.2 |
| `MaxTokens` | 1500 | 单次回答上限 |
| `TimeoutSeconds` | 120 | 超时 |
| `Stream` | true | 流式输出。若你的网关不支持 SSE 就关掉 |
| `MaxHistory` | 10 | 保留多少「一问一答」作为上下文，越多越费钱 |
| `IncludeGameState` | true | 把季节/日期/天气/地点/技能/背包等一起发给模型 |
| `UseLocalTutorial` | true | 自动检索 `InGameGuide` 里的本地教程作为回答依据 |
| `MaxTutorialChars` | 2500 | 注入的教程字数上限（越大越费 token） |
| `ExtraSystemPrompt` | 空 | 追加的额外要求，比如「用粤语回答」 |
| `NpcAutoGreet` | true | 打开 NPC 对话时让他主动打招呼（消耗一次请求） |
| `NoMarkdown` | true | 让模型别输出 `#`、`*` 等 Markdown 符号 |
| `QuickPrompts` | 6 条 | 问答面板上的一键提问按钮，随意增删 |
| `InvertScroll` | false | 滚轮方向反转 |

## 四、常见问题（测试连接失败怎么办）

设置界面里改完**立刻生效**，点「保存」只是写回文件。所以「测试连接」用的永远是你当前填的内容，不用担心测得是旧值。

失败时面板会直接显示原始错误 + 一句人话提示，同时完整日志会输出到 SMAPI 控制台（在那里能看到实际请求的 URL、模型名和密钥长度）。对照排查：

| 面板提示 | 原因 | 处理 |
| --- | --- | --- |
| `HTTP 401 未提供令牌` / `无效的令牌` | 密钥没填、填错，或末尾/开头多了空格、引号 | 重新完整粘贴 `sk-...`，注意不要带空格 |
| `模型不存在` / `无可用渠道` / 提到 model | 模型名不对。**中转站的模型名经常不是官方名字**（比如把 `deepseek-chat` 写成 `DeepSeek-V3.1-Terminus`） | 去中转站后台/文档复制它列表里的准确名字 |
| `接口返回了网页而不是数据` | BaseUrl 填成了网站首页 | 填到 `/v1` 结尾，如 `https://www.moyu.cn/v1` |
| `网络错误：...` | 地址拼错 / DNS / 需要代理 | 检查网址，必要时挂代理 |
| `请求超时` | 网络慢或模型排队 | 把「超时秒数」调到 300 |
| 余额/额度相关 | 账户没钱了 | 充值 |

另外提醒：把 BaseUrl 填成 `.../v1/` 或直接填 `.../chat/completions` 都能识别，不用纠结结尾斜杠。

## 五、让 AI 直接给你物品

在问答面板里直接说「给我一个自动抚摸机」，AI 会在回答**下方生成一个绿色「获取：自动抚摸机」按钮**，
点一下东西就真进背包了（同时弹一条 HUD 提示）。

- 开关：设置里「允许 AI 给物品」（`AiCanGiveItems`，默认开）
- 名字必须对得上：物品表里叫「**自动抚摸机**」，你说「抚摸器」它会先纠正再给
- 名字对不上时按钮点了会提示「没在物品表里找到…」，你可以去 InGameGuide 的物品代码查询核对名字
- 一次给一个；背包满时会弹在旁边等你拿

> 实现：AI 在回答末尾输出 `[[GIVE:物品名]]` 标记，Mod 把它抽出来（正文里看不见）并换成按钮，
> 物品 id 从 InGameGuide 的 `items.json` 里查（2446 个物品），再交给游戏的 `ItemRegistry` 生成。
> 不点按钮就不会给，所以不会误操作。

## 六、NPC 自由对话

**不用跑到 NPC 身边。** 按 `F4`：身边 4.5 格内有村民就直接开聊；没有就弹出「找谁聊天」面板：

- 列出**全地图**村民（不只当前地图），每行显示立绘、好感度、生日、所在地点
- 搜索框支持中文名/内部名/地点
- 筛选：全部 / 附近 / 可结婚 / 已认识（排序：附近的、今天生日的、好感高的靠前）
- 点一下就开始聊

对话面板里还有一排**快捷话题**：`换个人聊`、`重新开始`、`你最喜欢什么礼物`、`讲讲你的故事`、`今天镇上有什么新鲜事`、`给我一句今天的建议`。

AI 会拿到这些信息来扮演角色：性格设定、好感度、你们的关系（配偶/订婚/交往中/普通朋友）、
他当前在哪、当前季节日期天气时间、你的技能与背包、以及**你们之前聊过的内容**。

### 记忆持久化

每个 NPC 的聊天记录会存到 `Mods/StardewAI/npc-memory.json`（默认开启），
**下次进游戏他还记得上次聊到哪**。设置项：

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `NpcMemoryEnabled` | true | 关掉则每次都是全新对话 |
| `NpcMemoryMessages` | 20 | 每个 NPC 最多记住多少条 |
| `ChatFriendshipPoints` | 0 | 每聊完一轮加多少好感度，**0 = 不改原版数值**（想让聊天真的推进关系就填 5~10） |
| `ChatFriendshipDailyCap` | 40 | 同一 NPC 每天靠聊天最多涨多少好感（防止刷满） |

点「重新开始」或「清空」会同时忘掉文件里存的记忆。

## 七、Always 模式说明

`NpcMode` 改成 `Always` 后，和任意村民说话会直接进入自由对话，面板上有「原版对话」按钮可以切回原版台词。
以下情况会自动放过、不接管：剧情事件进行中、`NpcBlacklist` 里的角色、非村民（马/宠物/怪物）。
如果发现某个 NPC 的原版功能被挡住了，把它加进黑名单即可。

## 八、本地教程检索

如果同时装了 `InGameGuide`，Mod 会自动加载 `Mods/InGameGuide/assets/guide.json`（含物品代码表 items.json），
按你的问题做关键词检索，把最相关的片段塞进提示词，所以回答会比纯模型瞎猜更贴合你那份教程。
不需要时把 `UseLocalTutorial` 设为 `false` 即可（面板右上角也会显示加载状态）。

## 九、语音（你说话 / 她说话）

开启方式：SMAPI 控制台输入 `aivoice on`（会写进 `config.json`），或直接把 `VoiceEnabled` 改成 `true`。先用 `aitts 你好呀` 试听一句，能听见就说明 TTS 通了。

语音相关的开关、按键、音色、语速、音量、伴侣人设、主动搭话参数**都能在游戏内设置面板里改**
（`aisettings` 打开，往下滚就是最后那一段「语音 / 你的伴侣」），也不用碰文件。
面板里改完点「保存」写回 `config.json`；想立刻验证效果可以在控制台敲 `aitts 你好呀`。

**音色是下拉选的**（「音色 TtsVoice」那一行点一下弹出清单），常用的都在里面：

| 音色 | 名字 | 感觉 |
| --- | --- | --- |
| 甜美（女） | `female-tianmei` | 甜、活泼，适合陪着玩 |
| 少女（女） | `female-shaonv` | 更年轻 |
| 御姐（女） | `female-yujie` | 沉稳、有气场 |
| 成熟（女） | `female-chengshu` | 温和、解说感 |
| 温暖（女） | `Chinese (Mandarin)_Warm_Girl` | 温柔 |
| 晓晓 | `zh-CN-XiaoxiaoNeural` | 原来 edge 用的那个 |
| 通用 | `nova` / `alloy` | 中性 |
| 男声 | `male-qn-qingse`（青涩）/ `male-qn-jingying`（精英） | |

清单里永远把**当前音色排在第一位**，所以以前手打进 `config.json` 的自定义音色名不会被这个下拉弄丢。
面板底部还有一个**「试听音色」**按钮：它按**当前（哪怕还没保存）**的设置念一句
「你好呀，我是小星，很高兴陪你一起玩。」，挑音色、试语速都不用进游戏来回试。

### 两种用法

| 你说 | 她答 |
| --- | --- |
| **按住 `V` 说话**，松开 | 自动识别成文字 → 当作你的问题发出去 → 她**语音回答** + 字幕 |
| **打字**（`F4` NPC 面板 / 伴侣面板 / 问答面板） | 她**语音回答** + 字幕 |

语音朗读是"边流式边念"：模型还在往外蹦字的时候，已经说完的整句就已经送去合成了，下一句会提前合成好排队等着，所以听起来接近连续说话。出字幕、说话中再问下一句会**自动打断**上一段。

### 她和你的单独窗口（默认 `F7`，进存档自动打开）

一个窗口装下你们俩的全部对话：**她说的**（包括她主动搭话的那些）、**你打的字**、**你按住说话转出来的字**，
全都在同一个窗口里按时间排下去；下次打开还看得到（会话记忆一起存在 `npc-memory.json` 里）。

窗口底部四个按钮：

| 按钮 | 作用 |
| --- | --- |
| 发送 / 停止 | 回车同效；她正在回答时可以点「停止」打断 |
| 清空 | 忘掉这段对话 |
| **说话** | 点一下开始录（按钮变成「停止并发送」），再点一下就转成文字发出去 |
| 找村民聊 | 跳到 NPC 选择面板，不影响你和她的对话 |

几个细节：

- **面板开着时按 `V` 打不出字**（键盘归输入框），所以面板里用「说话」按钮；面板关着时（走路时）按住 `V` 照样能说：
  **不会弹出任何面板**，你刚说的话会以头顶气泡飘一下（"我说：…"），她照样语音回答 + 屏幕下方字幕，
  对话内容都记进那个窗口，随时按 `F7` 回看。想让 `V` 在面板里也能用，就把 `PttInChat` 打开（代价是打不出 v）。
- **面板开着也照样会主动搭话**（她的窗口就是她说话的地方）。别的时候（背包、商店、剧情）她不会插嘴。
- 默认**不会**自动弹出（`CompanionAutoOpen: false`）；想让它进存档就自己开，把「进存档就自动打开伴侣对话」打开即可。

### 她怎么称呼你 / 开场会不会老一套

- **称呼**：设置面板「伴侣与搭话」里的**「她怎么称呼你」**。填"老板""老公""小主人"都行；
  **留空就自动用你的游戏名字**。这个称呼会进她的提示词，她说话时会这么叫你。
- **开场**：`CompanionGreet`（默认开）= 进游戏约 6 秒后她**主动开口**。为了避免"每次都一样"：
  - 开场词是**模型现编**的，不是内置台词；
  - 提示词里带着**当下的时间/天气/季节/地点/你手里拿着什么**，所以每天说的都不一样；
  - 她每次的开场白会记在 `Mods/StardewAI/voice/greetings.txt`（最近 12 句），
    下次进游戏时把这些塞进提示词，明确要求"别再这么说"。
  - 想关掉就在面板里关「进游戏她先开口打招呼」。
- 对话里也不再塞固定提示行（以前每次打开面板都会看到同一句），提示改到窗口标题栏下面。

### 为什么会等一两秒（以及怎么压下来的）

延迟几乎都在 edge 服务的网络往返上，本地环节被压到了可忽略：

| 环节 | 耗时 |
| --- | --- |
| edge 服务首包 | 1.0 ~ 1.8 s |
| 整句合成完成 | 再 +0.2 ~ 0.6 s |
| 解码成 wav（在常驻助手进程内） | ~2 ms |
| 播放（游戏音频引擎） | 立即 |

已经做的优化：

- **常驻助手**（`EdgeUseWorker`，默认开）：不再每句新起两个 Python 进程，每句从 3~4 秒压到 **1.4~2.4 秒**；
- **进游戏就预热**助手进程，第一句不用等冷启动；
- 回答有多句时，**下一句在你听这一句的时候就合成好了**，所以真正要等的只有第一句；
- 长句会**先在逗号处切一段**念出来，第一声更早。

### 语音接口怎么配

| 后端 | `TtsBackend` | 需要什么 |
| --- | --- | --- |
| **Windows 自带语音（推荐先用它）** | `sapi` | 什么都不用装、不用密钥、离线可用，中文女声（`Microsoft Huihui Desktop`）。缺点：机器音、每句要多等约 1 秒的 powershell 启动 |
| OpenAI 兼容 `/audio/speech` | `openai`（默认） | 用主接口的 `BaseUrl` / `ApiKey`（可用 `TtsBaseUrl` / `TtsApiKey` 单独指定），模型 `TtsModel`（如 `speech-2.8-turbo`、`tts-1`），音色 `TtsVoice`（如 `female-tianmei`、`nova`） |
| **Fish Audio**（有免费模型） | `fish` | 用 `FishBaseUrl`（默认 `https://api.fish.audio/v1`）+ `FishApiKey`；`FishModel` 默认 `s2.1-pro-free`（官方定价 $0/百万字节），付费档 `s2.1-pro` 约 ¥3.2/万汉字；`FishLatency` 选 `balanced`；`FishReferenceId` 填克隆音色的 id（留空用官方默认音色）。注意它的接口是 `/v1/tts`、模型名放请求头，和其它家不同 |
| 本机 edge-tts（音质好、免费免密钥） | `edge` | 先装真 Python（应用商店那个占位符不算），再 `pip install edge-tts miniaudio`；音色 `EdgeTtsVoice`（默认 `zh-CN-XiaoxiaoNeural`） |

> 走 `edge` 时用的是常驻助手 `tts_worker.py`（会随 Mod 一起部署到 `Mods/StardewAI/`）：
> 一次启动，之后每句只等网络。要是助手起不来（比如 Python 路径不对），会自动退回"命令行 edge-tts + 单独转 wav"的老路子，慢一些但照样能响。

> `TtsFallbackSapi`（默认开）= 选了 `openai`/`edge` 但那家接口没开 `/audio/speech` 时，**自动退回 Windows 自带语音**，
> 所以你什么都不用配也能立刻听见声音（控制台会打印一行说明）。
> 已知：本机 DSH 网关（`http://127.0.0.1:xxxxx/v1`）**没有** `/audio/speech` 路由，走它就会自动退回 sapi。

识别（你说话 → 文字）由 `SttBackend` 决定：

| 后端 | `SttBackend` | 说明 |
| --- | --- | --- |
| **Windows 自带离线识别** | `sapi` | 免安装、免密钥、离线，中文系统里是 `MS-2052-80-DESK`。缺点：没有标点、长句容易听歪，适合说短句 |
| 云端接口 | `api` | 走 `/audio/transcriptions`，`SttModel` 默认 `whisper-1`，也可用 `FunAudioLLM/SenseVoiceSmall`、`gpt-4o-transcribe`（**比离线准得多**） |
| 自动 | `auto`（默认） | 先用云端接口，那家没开这条路由就自动退回 Windows 离线识别 |

`SttLanguage` 默认 `zh`：云端当语言参数用，离线用来挑识别引擎（`zh` 会找 `zh-CN` 的引擎）。

> 注意：**不是每家中转站都开了语音接口**。实测本机 DSH 网关（`http://127.0.0.1:xxxxx/v1`）**既没有 `/audio/speech` 也没有 `/audio/transcriptions`**，
> 所以这两条路都会自动退回 Windows 自带能力：说话用 SAPI 中文女声，听话用 SAPI 离线听写。想更自然/更准就在设置面板里换成支持这些接口的服务商。
> 如果离线识别提示「没装中文语音识别引擎」，去 Windows 设置 → 时间和语言 → 语言和区域 → 中文 → 语言选项 → 补装「语音识别」。

### 和游戏的音频关系

语音走的是 Windows 自带的 MCI（`winmm`），**不依赖任何第三方库**，也不占用游戏的音频通道：

- 游戏音量滑条**不影响**语音音量，要调就改 `TtsVolume`（0~100）。
- 用的是共享模式，能和游戏声音同时响；出字幕时不需要额外的窗口。
- 语音片段和录音都写在 `Mods/StardewAI/voice/`，念完就删，进游戏时会清掉一天前的缓存。

### 和麦克风有关

- 按住录音用的是 Windows 的 `waveaudio` 录音，**先确认系统麦克风权限给了**，否则面板会提示"打不开麦克风"。
- 录音太短（< `SttMinSeconds`，默认 0.6 秒）会当成误按丢掉；超过 `SttMaxSeconds`（默认 60 秒）自动结束发送。
- 说话前她会**先停下正在念的话**，免得录到自己。
- 建议戴耳机：外放时她的声音会被麦克风录进去。
- 「按住说话」默认不在聊天输入框打开时生效（否则打不出那个字母），想改就设 `PttInChat: true`。

### 语音相关字段（`config.json`）

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `VoiceEnabled` | false | 语音总开关 |
| `PttKey` | `V` | 按住说话的键 |
| `PttInChat` | false | 聊天框打开时也能按（会吃掉这个字母的打字） |
| `SpeakReplies` | true | 把回答念出来 |
| `VoiceShortReplies` | true | 开语音时顺便要求模型用口语短句回答（更适合朗读） |
| `SpeakMaxChars` | 400 | 一次最多念多少字（`0` = 不限） |
| `TtsBackend` / `TtsBaseUrl` / `TtsApiKey` / `TtsModel` / `TtsVoice` | openai / 空 / 空 / tts-1 / nova | 语音合成 |
| `TtsFormat` / `TtsSpeed` / `TtsVolume` | mp3 / 1 / 100 | 返回格式、语速、音量 |
| `SapiVoice` / `TtsFallbackSapi` | Microsoft Huihui Desktop / true | Windows 自带语音的音色；接口不可用时是否自动退回它 |
| `EdgeTtsCommand` / `EdgeTtsVoice` / `EdgeTtsRate` | edge-tts / zh-CN-XiaoxiaoNeural / +0% | edge 后端 |
| `FishBaseUrl` / `FishApiKey` / `FishModel` / `FishLatency` / `FishReferenceId` | api.fish.audio/v1 / 空 / s2.1-pro-free / balanced / 空 | fish 后端（地址密钥与 openai 后端分开，互不影响） |
| `SttEnabled` / `SttBackend` / `SttBaseUrl` / `SttApiKey` / `SttModel` / `SttLanguage` | true / auto / 空 / 空 / whisper-1 / zh | 语音识别 |
| `SttSampleRate` / `SttMinSeconds` / `SttMaxSeconds` | 16000 / 0.6 / 60 | 录音 |
| `CompanionKey` / `CompanionAutoOpen` | F7 / false | 打开「她和我的对话」的按键、进存档是否自动打开（按住说话**不会**弹面板，边玩边聊不受影响） |
| `CompanionName` / `CompanionPersona` | 小星 / 空 | 语音伴侣的名字与自定义人设 |
| `PlayerTitle` | 空 | **她怎么称呼你**；留空 = 用你的游戏名字 |
| `CompanionGreet` | true | 进游戏时**她先开口打招呼**：每次都由她想一句新的，不是固定台词 |
| `ActivityInPrompt` | true | 让她知道「你此刻在干什么」（手里拿什么工具、在矿洞第几层、体力如何） |

## 十、她会主动看你正在干嘛

开着 `ProactiveChat`（默认开）时，她会盯着你的一举一动，攒到有意思的事就主动开口：
**一句话气泡顶在你头上**，按 `ProactiveSpeak` 决定要不要同时念出来。文字的正文会进伴侣会话，
下次打开伴侣面板（`aicompanion`）能看到她之前都说过什么。

### 什么情况会开口

| 触发 | 说明 |
| --- | --- |
| **血量掉到 80%** | 随口提醒一句"小心点"（语气轻） |
| **血量掉到 50%** | 明显担心，劝你吃东西 / 别硬撑 |
| **血量掉到 30%** | 语气着急，让你吃药 / 撤退 / 用回程法杖。回血后再掉下来还能再说一遍 |
| **挖到矿石** | 铜/铁/金/铱/放射性矿石、煤炭、石英、精炼石英、晶球类 |
| **挖到宝石** | 紫水晶、海蓝宝石、红宝石、钻石…（按游戏品类识别，名字用游戏里的中文） |
| **钓到有星级的鱼** | 银星 / 金星会说一句；**铱星**会更激动 |
| **钓到破纪录的鱼** | 比你自己之前那条最大尺寸还大时报出"多少英寸" |
| 钓上来一条鱼（新鱼种） | 替你高兴 |
| 下到矿洞新的一层 | |
| 技能升级 | 比如"耕种技能刚升到 5 级" |
| 走进新地点 | 换地图 / 进屋 / 出城 |
| 体力快见底（低于 15%） | 心疼地劝你歇会儿 |
| 站着不动（默认 150 秒） | 问你在想什么 |
| **没事也隔一段时间（`ProactiveEverySeconds`，默认关闭）** | 不依赖任何事件，纯计时：从"你俩上次说话"起算，到点就主动聊一句。想让她"你随便逛着也时不时搭话"就把这个设成 60~300 |
| 新的一天 / 刚赚到一笔钱 / 在田里放了东西 | 优先级较低 |

> 语气是写在事件描述里交给模型的（例如"血量只剩 30%——语气着急一点"），所以同一件事在不同档位听起来不一样。
> 同一类事 **90 秒内不重复说**（防刷屏）；嫌矿石那类太吵，把「攒到事情时开口的概率 %」调低即可。

### 不会打扰你的时候

- 有面板开着（背包、商店、NPC 对话框）不说话——**但她和你的那个窗口除外**，开着照样会说；
- 剧情事件、节日当天不说话；
- 两次开口至少隔 `ProactiveCooldown` 秒（默认 180）；
- 攒到事也只有 `ProactiveChance`（默认 60%）的概率真的说；
- 你正在跟别人对话/她正在回答你时，不会插话。

表里这些都能在设置面板最下面那段调。嫌吵就把「她会主动看你在干什么并搭话」关掉，
或者把冷却调大（1800 = 半小时最多说一次）、概率调小。

## 十一、编译

需要 .NET SDK 8：

```powershell
cd e:\星露谷\StardewAI
dotnet build -c Release
```

ModBuildConfig 会自动把产物部署到 `F:\steam\steamapps\common\Stardew Valley\Mods\StardewAI`
（目标路径取自 csproj 里的 `GamePath`，游戏换位置时改那一项即可）。
