using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewAI.Llm;
using StardewModdingAPI;

namespace StardewAI.Game
{
    /// <summary>把每个 NPC 的聊天记录存成文件，跨天、跨存档都记得住。</summary>
    public static class NpcMemoryStore
    {
        private static readonly Dictionary<string, List<ChatMsg>> Data = new();

        private static string Path =>
            System.IO.Path.Combine(ModEntry.Hlp?.DirectoryPath ?? ".", "npc-memory.json");

        public static List<ChatMsg> Get(string npcName)
        {
            if (!string.IsNullOrWhiteSpace(npcName) && Data.TryGetValue(npcName, out var list))
                return new List<ChatMsg>(list);

            return new List<ChatMsg>();
        }

        public static void Set(string npcName, List<ChatMsg> messages)
        {
            if (string.IsNullOrWhiteSpace(npcName) || messages == null)
                return;

            int keep = Math.Max(1, ModEntry.Config?.NpcMemoryMessages ?? 20);
            if (messages.Count > keep)
                messages = messages.GetRange(messages.Count - keep, keep);

            Data[npcName] = new List<ChatMsg>(messages);
        }

        public static void Clear(string npcName)
        {
            if (!string.IsNullOrWhiteSpace(npcName))
                Data.Remove(npcName);
        }

        public static void Load(IMonitor log)
        {
            try
            {
                string path = Path;
                if (!File.Exists(path))
                    return;

                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var parsed = JsonSerializer.Deserialize<Dictionary<string, List<ChatMsg>>>(File.ReadAllText(path), opts);
                if (parsed == null)
                    return;

                Data.Clear();
                foreach (var kv in parsed)
                {
                    if (kv.Value == null)
                        continue;
                    Data[kv.Key] = kv.Value;
                }

                log?.Log($"NPC 对话记忆已载入：{Data.Count} 个角色", LogLevel.Trace);
            }
            catch (Exception ex)
            {
                log?.Log("NPC 对话记忆载入失败：" + ex.Message, LogLevel.Warn);
            }
        }

        public static void Save(IMonitor log)
        {
            try
            {
                if (ModEntry.Config != null && !ModEntry.Config.NpcMemoryEnabled)
                    return;

                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(Path, JsonSerializer.Serialize(Data, opts));
            }
            catch (Exception ex)
            {
                log?.Log("NPC 对话记忆保存失败：" + ex.Message, LogLevel.Warn);
            }
        }
    }
}
