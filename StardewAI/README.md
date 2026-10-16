# StardewAI — 游戏内 AI 助手 Mod

在《星露谷物语》里直接和大模型对话：

- **NPC 自由对话**（默认 `F4`）：对着附近的 NPC 按键，就能用他的口吻和你聊天；每个 NPC 有独立的对话记忆。
- **AI 问答面板**：默认**已禁用**（`AskKey = "None"`）。问答面板统一由 **InGameCodex 的 `F5`** 提供（它那个 AI 页就是从本 Mod 搬过去的），两边都占 `F5` 会一次弹出两个面板。想启用本 Mod 的面板，把 `AskKey` 改成 `F5` 或别的键即可；接口地址 / 密钥 / 模型两边仍然共用（InGameCodex 首次启动会读这份配置）。
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
| 回车 | 发送；发送中按同键或点「停止」可中断 |
| `Esc` | 关闭面板 |
| 滚轮 | 滚动聊天记录（`InvertScroll: true` 可反转） |

控制台命令（SMAPI 窗口里输入）：

```
ai                 显示帮助
aiask 春天种什么最赚  直接提问
aitalk 谢恩         和指定 NPC 对话（不填则取最近的）
aisettings         打开游戏内设置界面
aiclear            清空所有对话历史
aikey sk-xxx       设置 API Key
```

设置界面里可以改的东西：接口地址 / 密钥 / 模型 / 流式输出 / 温度 / 回答上限 / 超时 /
上下文轮数 / 是否附带存档状态 / 是否检索本地教程 / 教程字数上限 / 是否禁用 Markdown /
额外要求 / NPC 触发方式（Hotkey、Always）/ NPC 黑名单 / NPC 主动打招呼 / 两个快捷键 /
滚轮反转 / 快捷提问列表（用 `|` 分隔）。

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

## 九、编译

需要 .NET SDK 8：

```powershell
cd e:\星露谷\StardewAI
dotnet build -c Release
```

ModBuildConfig 会自动把产物部署到 `F:\steam\steamapps\common\Stardew Valley\Mods\StardewAI`
（目标路径取自 csproj 里的 `GamePath`，游戏换位置时改那一项即可）。
