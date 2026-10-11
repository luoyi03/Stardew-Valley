using System;
using System.Collections.Generic;

namespace StardewAI.Game
{
    /// <summary>常见 OpenAI 兼容接口的预设，方便一键切换（地址 + 模型 + 各自的密钥）。</summary>
    public sealed class Preset
    {
        public string Id;
        public string Label;
        public string Short;
        public string BaseUrl;
        public string Model;
        public bool NeedsKey = true;
        public string Note;

        public static readonly List<Preset> All = new()
        {
            new Preset { Id = "moyu", Label = "魔芋（你当前在用）", Short = "魔芋", BaseUrl = "https://www.moyu.cn/v1", Model = "DeepSeek-V4.1-flash" },
            new Preset { Id = "longcat", Label = "美团 LongCat", Short = "美团", BaseUrl = "https://api.longcat.chat/openai", Model = "LongCat-2.0", Note = "推理模型，回答上限建议 ≥2000" },
            new Preset { Id = "zhipu", Label = "智谱 GLM", Short = "智谱", BaseUrl = "https://open.bigmodel.cn/api/paas/v4", Model = "glm-4.5-air", Note = "glm-5.3 系列需付费资源包" },
            new Preset { Id = "siliconflow", Label = "硅基流动", Short = "硅基流动", BaseUrl = "https://api.siliconflow.cn/v1", Model = "Qwen/Qwen2.5-7B-Instruct", Note = "账户需充值" },
            new Preset { Id = "deepseek", Label = "DeepSeek 官方", Short = "DeepSeek", BaseUrl = "https://api.deepseek.com/v1", Model = "deepseek-chat" },
            new Preset { Id = "openai", Label = "OpenAI", Short = "OpenAI", BaseUrl = "https://api.openai.com/v1", Model = "gpt-4o-mini" },
            new Preset { Id = "doubao", Label = "火山方舟豆包（Trae 同款）", Short = "豆包", BaseUrl = "https://ark.cn-beijing.volces.com/api/v3", Model = "doubao-1.5-pro-32k", Note = "模型名也可填方舟的接入点 ID（ep- 开头）；有免费额度" },
            new Preset { Id = "trio", Label = "本机 DSH 网关（Trae/WorkBuddy/Qoder）", Short = "本机网关", BaseUrl = "", Model = "", Note = "启动时自动读取 ~/.dsh/.trio-shim.json" },
            new Preset { Id = "ollama", Label = "本地 Ollama（免费离线）", Short = "Ollama", BaseUrl = "http://localhost:11434/v1", Model = "qwen2.5:7b", NeedsKey = false },
            new Preset { Id = "lmstudio", Label = "本地 LM Studio（免费离线）", Short = "LM Studio", BaseUrl = "http://localhost:1234/v1", Model = "local-model", NeedsKey = false },
            new Preset { Id = "custom", Label = "自定义（自己填）", Short = "自定义", BaseUrl = "", Model = "", NeedsKey = true }
        };

        public static Preset Get(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;
            return All.Find(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public static string[] Ids()
        {
            var res = new string[All.Count];
            for (int i = 0; i < All.Count; i++)
                res[i] = All[i].Id;
            return res;
        }

        public static string[] Labels()
        {
            var res = new string[All.Count];
            for (int i = 0; i < All.Count; i++)
                res[i] = All[i].Label;
            return res;
        }

        /// <summary>按地址猜出用的是哪家的接口，用于老配置升级。</summary>
        public static string Detect(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                return "custom";

            string u = baseUrl.ToLowerInvariant();
            if (u.Contains("moyu.cn")) return "moyu";
            if (u.Contains("longcat.chat")) return "longcat";
            if (u.Contains("bigmodel.cn")) return "zhipu";
            if (u.Contains("siliconflow")) return "siliconflow";
            if (u.Contains("api.deepseek.com")) return "deepseek";
            if (u.Contains("api.openai.com")) return "openai";
            if (u.Contains("localhost:11434")) return "ollama";
            if (u.Contains("localhost:1234")) return "lmstudio";
            if (u.Contains("127.0.0.1") || u.Contains("localhost")) return "trio";
            return "custom";
        }
    }
}
