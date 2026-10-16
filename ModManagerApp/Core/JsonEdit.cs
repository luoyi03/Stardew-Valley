using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SMM
{
    /// <summary>
    /// 往 Mod 的 config.json 里读 / 写单个字段（只动那一项，其它设置原样保留；写前自动备份）。
    /// 路径写法：.OpenMenuKey / .Ai.Model / .Controls.Toggle / .Keys[0]。
    /// 快捷键页和 AI 设置页共用这一套，避免两边各写一份。
    /// </summary>
    public static class JsonEdit
    {
        /// <summary>Mods\&lt;Mod 目录&gt;\config.json。</summary>
        public static string ConfigPathOf(AppEnv env, string modFolder)
            => env == null || string.IsNullOrWhiteSpace(env.ModsPath) || string.IsNullOrWhiteSpace(modFolder)
                ? null
                : Path.Combine(env.ModsPath, modFolder, "config.json");

        public static bool Exists(AppEnv env, string modFolder)
        {
            string p = ConfigPathOf(env, modFolder);
            return p != null && File.Exists(p);
        }

        /// <summary>读一个字符串字段（读不到返回 null）。</summary>
        public static string Read(string configPath, string jsonPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
                    return null;

                using var doc = JsonUtil.Parse(File.ReadAllText(configPath));
                System.Text.Json.JsonElement cur = doc.RootElement;

                foreach (object tok in PathTokens(jsonPath))
                {
                    if (tok is string name)
                    {
                        if (cur.ValueKind != System.Text.Json.JsonValueKind.Object
                            || !cur.TryGetProperty(name, out var next))
                            return null;
                        cur = next;
                    }
                    else
                    {
                        if (cur.ValueKind != System.Text.Json.JsonValueKind.Array)
                            return null;
                        int idx = (int)tok;
                        if (idx < 0 || idx >= cur.GetArrayLength())
                            return null;
                        cur = cur[idx];
                    }
                }

                return cur.ValueKind == System.Text.Json.JsonValueKind.String ? cur.GetString() : cur.ToString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>写一个字符串字段（写前备份；字段不存在就新建）。</summary>
        public static void Write(AppEnv env, string configPath, string jsonPath, string value)
        {
            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
                throw new Exception("找不到配置文件：" + configPath);

            Backup(env, configPath);

            if (JsonUtil.ParseNode(File.ReadAllText(configPath)) is not JsonObject root)
                throw new Exception("配置文件读不出来（不是 JSON 对象）");

            var tokens = PathTokens(jsonPath);
            JsonNode cur = root;
            for (int i = 0; i < tokens.Count; i++)
            {
                bool last = i == tokens.Count - 1;

                if (tokens[i] is string name)
                {
                    if (cur is not JsonObject obj)
                        throw new Exception($"配置里的 {jsonPath} 结构不对（该处不是对象）");

                    if (last)
                    {
                        obj[name] = value;      // 写到当前这一层（曾经写错过层，见快捷键页的写入自检）
                        break;
                    }
                    cur = obj[name];
                }
                else
                {
                    if (cur is not JsonArray arr)
                        throw new Exception($"配置里的 {jsonPath} 结构不对（该处不是数组）");

                    int idx = (int)tokens[i];
                    if (last)
                    {
                        arr[idx] = value;
                        break;
                    }
                    cur = arr[idx];
                }

                if (cur == null)
                    throw new Exception("配置里找不到这一项：" + jsonPath);
            }

            File.WriteAllText(configPath, root.ToJsonString(AppConfig.JsonOpts), new UTF8Encoding(false));
        }

        /// <summary>
        /// 改之前先留一份原件：&lt;数据目录&gt;\config-backup\&lt;Mod 目录&gt;\config.时间戳.json。
        /// 只备份 Mods 目录下的配置（自检用的临时副本不备份）。
        /// </summary>
        public static void Backup(AppEnv env, string configPath)
        {
            try
            {
                if (env == null || string.IsNullOrWhiteSpace(env.ModsPath) || string.IsNullOrWhiteSpace(configPath))
                    return;
                if (!Path.GetFullPath(configPath).StartsWith(Path.GetFullPath(env.ModsPath), StringComparison.OrdinalIgnoreCase))
                    return;

                string modFolder = Path.GetFileName(Path.GetDirectoryName(configPath)) ?? "mod";
                string dir = Path.Combine(env.DataDir, "config-backup", Installer.Safe(modFolder));
                Directory.CreateDirectory(dir);

                string target = Path.Combine(dir, "config." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                if (!File.Exists(target))
                    File.Copy(configPath, target, true);
            }
            catch { }
        }

        public static List<object> PathTokens(string jsonPath)
        {
            var list = new List<object>();
            foreach (Match m in Regex.Matches(jsonPath ?? "", @"\.([^.\[]+)|\[(\d+)\]"))
            {
                if (m.Groups[1].Success)
                    list.Add(m.Groups[1].Value);
                else
                    list.Add(int.Parse(m.Groups[2].Value));
            }
            return list;
        }
    }
}
