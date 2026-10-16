using System;
using System.Collections.Generic;
using System.Linq;

namespace SMM
{
    /// <summary>AI 接口预设（与游戏内 InGameCodex / StardewAI 的 Presets.cs 保持一致）。</summary>
    public sealed class AiPreset
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string BaseUrl { get; set; }
        public string Model { get; set; }
        public bool NeedsKey { get; set; } = true;

        public static readonly List<AiPreset> All = new()
        {
            new AiPreset { Id = "moyu", Label = "魔芋", BaseUrl = "https://www.moyu.cn/v1", Model = "DeepSeek-V4.1-flash" },
            new AiPreset { Id = "longcat", Label = "美团 LongCat", BaseUrl = "https://api.longcat.chat/openai", Model = "LongCat-2.0" },
            new AiPreset { Id = "zhipu", Label = "智谱 GLM", BaseUrl = "https://open.bigmodel.cn/api/paas/v4", Model = "glm-4.5-air" },
            new AiPreset { Id = "siliconflow", Label = "硅基流动", BaseUrl = "https://api.siliconflow.cn/v1", Model = "Qwen/Qwen2.5-7B-Instruct" },
            new AiPreset { Id = "deepseek", Label = "DeepSeek 官方", BaseUrl = "https://api.deepseek.com/v1", Model = "deepseek-chat" },
            new AiPreset { Id = "openai", Label = "OpenAI", BaseUrl = "https://api.openai.com/v1", Model = "gpt-4o-mini" },
            new AiPreset { Id = "doubao", Label = "火山方舟豆包（Trae 同款）", BaseUrl = "https://ark.cn-beijing.volces.com/api/v3", Model = "doubao-1.5-pro-32k" },
            new AiPreset { Id = "trio", Label = "本机 DSH 网关（Trae/WorkBuddy/Qoder）", BaseUrl = "http://127.0.0.1:52120/v1", Model = "hy3" },
            new AiPreset { Id = "ollama", Label = "本地 Ollama（免费离线）", BaseUrl = "http://localhost:11434/v1", Model = "qwen2.5:7b", NeedsKey = false },
            new AiPreset { Id = "lmstudio", Label = "本地 LM Studio（免费离线）", BaseUrl = "http://localhost:1234/v1", Model = "local-model", NeedsKey = false },
            new AiPreset { Id = "custom", Label = "自定义（自己填）", BaseUrl = "", Model = "" }
        };

        public static AiPreset Get(string id)
            => string.IsNullOrWhiteSpace(id)
                ? null
                : All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

        public static AiPreset ByLabel(string label)
            => string.IsNullOrWhiteSpace(label)
                ? null
                : All.FirstOrDefault(p => string.Equals(p.Label, label, StringComparison.OrdinalIgnoreCase));

        /// <summary>按接口地址猜是哪个预设（和游戏内的 Presets.GuessId 规则一致）。</summary>
        public static string GuessId(string baseUrl)
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
            if (u.Contains("volces.com")) return "doubao";
            if (u.Contains("localhost:11434")) return "ollama";
            if (u.Contains("localhost:1234")) return "lmstudio";
            if (u.Contains("127.0.0.1") || u.Contains("localhost")) return "trio";
            return "custom";
        }

        /// <summary>地址是否就是这个预设的地址（用于判断用户有没有改过）。</summary>
        public static bool Matches(AiPreset p, string baseUrl)
            => p != null
               && !string.IsNullOrWhiteSpace(p.BaseUrl)
               && string.Equals(p.BaseUrl.TrimEnd('/'), (baseUrl ?? "").TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 模型候选（与两个 Mod 里的 HiddenModels / StandbyModels 保持一致）：
    /// Standby 里的永远可选，Hidden 里的不出现在候选里。
    /// </summary>
    public static class AiModels
    {
        public static readonly string[] Standby = { "hy3", "deepseek-v4.1-flash", "glm-5.3-flash" };
        public static readonly string[] Hidden = { "hy4-preview-f" };

        /// <summary>把接口拉到的清单 + 常备款合成候选（去重、剔除屏蔽项；当前值始终保留）。</summary>
        public static List<string> Merge(IEnumerable<string> fetched, string current)
        {
            var list = new List<string>();

            void Add(string id)
            {
                id = (id ?? "").Trim();
                if (id.Length == 0)
                    return;
                if (list.Any(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase)))
                    return;

                bool isCurrent = list.Count == 0 && !string.IsNullOrWhiteSpace(current)
                                 && string.Equals(current.Trim(), id, StringComparison.OrdinalIgnoreCase);
                if (!isCurrent && Hidden.Any(h => string.Equals(h, id, StringComparison.OrdinalIgnoreCase)))
                    return;

                list.Add(id);
            }

            Add(current);
            foreach (string m in fetched ?? Enumerable.Empty<string>())
                Add(m);
            foreach (string m in Standby)
                Add(m);

            return list;
        }
    }
}
