using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SMM
{
    /// <summary>
    /// 中文名：用户在本管理器里维护的中文名会写进每个 Mod 自己的 manifest.json 的 Name 字段。
    /// 这样不只是本管理器里显示中文，游戏里（SMAPI 控制台启动清单、游戏内 Mod 管理器列表）也会显示中文。
    /// manifest 里除了 Name 以外的字段（UniqueID / EntryDll / UpdateKeys…）全部原样保留。
    /// </summary>
    public static class ModNamer
    {
        private static readonly UTF8Encoding Utf8NoBom = new(false);

        private static readonly JsonSerializerOptions WriteOpts = new()
        {
            WriteIndented = true,
            // 不转义中文，否则会写成 \u4e2d\u6587
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static string ManifestPath(string modDir) => Path.Combine(modDir ?? "", "manifest.json");

        /// <summary>读 manifest 里现在的 Name（读不到返回 null）。</summary>
        public static string ReadCurrentName(string modDir)
        {
            try
            {
                string path = ManifestPath(modDir);
                if (!File.Exists(path))
                    return null;
                var root = JsonUtil.ParseNode(File.ReadAllText(path)) as JsonObject;
                return root?["Name"]?.GetValue<string>();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>把 Name 字段改成 newName（其它字段原样保留），返回改之前的名字。</summary>
        public static string WriteName(string modDir, string newName)
        {
            string path = ManifestPath(modDir);
            if (!File.Exists(path))
                throw new Exception("这个 Mod 目录里没有 manifest.json：" + (modDir ?? "(空)"));

            string old = null;
            JsonObject root;
            try
            {
                root = JsonUtil.ParseNode(File.ReadAllText(path)) as JsonObject;
            }
            catch (Exception ex)
            {
                throw new Exception("manifest.json 读不出来：" + ex.Message);
            }

            if (root == null)
                throw new Exception("manifest.json 不是标准的 JSON 对象");

            try { old = root["Name"]?.GetValue<string>(); }
            catch { old = null; }

            root["Name"] = newName;
            File.WriteAllText(path, root.ToJsonString(WriteOpts), Utf8NoBom);
            return old;
        }

        /// <summary>给一个 Mod 应用中文名：写 manifest + 记进配置（配置文件写失败会抛异常）。</summary>
        public static string Apply(AppEnv env, LocalMod mod, string chineseName)
        {
            string name = (chineseName ?? "").Trim();
            if (name.Length == 0)
                throw new Exception("中文名不能是空的");
            if (name.Length > 80)
                throw new Exception("名字太长了，请控制在 80 个字以内");

            // 先写文件：写不进去就不动配置，避免配置和实际文件对不上
            string old = WriteName(mod.Dir, name);

            var cfg = env.Config;
            cfg.ChineseNames ??= new Dictionary<string, string>();
            cfg.OriginalNames ??= new Dictionary<string, string>();

            if (!cfg.OriginalNames.ContainsKey(mod.Id))
            {
                string orig = !string.IsNullOrWhiteSpace(mod.OriginalName) ? mod.OriginalName : old;
                if (!string.IsNullOrWhiteSpace(orig))
                    cfg.OriginalNames[mod.Id] = orig;
            }

            cfg.ChineseNames[mod.Id] = name;
            cfg.Save();

            mod.Name = name;
            env.Log($"已改中文名：{mod.Id} → {name}（原名 {old ?? mod.OriginalName ?? "?"}）");
            return $"已改名为「{name}」，游戏里也会显示这个名字（重启游戏后生效）";
        }

        /// <summary>还原成 manifest 里的原名。</summary>
        public static string Restore(AppEnv env, LocalMod mod)
        {
            var cfg = env.Config;
            string orig = null;
            cfg.OriginalNames?.TryGetValue(mod.Id, out orig);
            if (string.IsNullOrWhiteSpace(orig))
                throw new Exception("没有记到这个 Mod 的原名，没法自动还原（可以手动填英文名再保存）");

            WriteName(mod.Dir, orig);

            cfg.ChineseNames?.Remove(mod.Id);
            cfg.OriginalNames?.Remove(mod.Id);
            cfg.Save();

            mod.Name = orig;
            mod.OriginalName = orig;
            env.Log($"已还原原名：{mod.Id} → {orig}");
            return $"已还原成原名「{orig}」";
        }

        /// <summary>有没有给这个 Mod 配过中文名。</summary>
        public static bool HasChineseName(AppEnv env, LocalMod mod)
            => env?.Config?.ChineseNames != null
               && env.Config.ChineseNames.TryGetValue(mod.Id ?? "", out string v)
               && !string.IsNullOrWhiteSpace(v);

        /// <summary>
        /// 装好 / 更新完 Mod 之后调用：Mod 更新会把 manifest.json 覆盖成英文原名，
        /// 这里把配过中文名的重新写回中文。返回这次重写了几个。
        /// </summary>
        public static int ReapplyAll(AppEnv env)
        {
            var cfg = env?.Config;
            if (cfg?.ChineseNames == null || cfg.ChineseNames.Count == 0)
                return 0;

            List<LocalMod> mods;
            try { mods = ModScanner.Scan(env.ModsPath, env.DisabledDir); }
            catch { return 0; }

            int count = 0;
            foreach (var mod in mods)
            {
                if (!cfg.ChineseNames.TryGetValue(mod.Id ?? "", out string cn) || string.IsNullOrWhiteSpace(cn))
                    continue;

                // 已经被写进 manifest 的跳过，不用碰文件
                if (string.Equals(ReadCurrentName(mod.Dir), cn, StringComparison.Ordinal))
                    continue;

                try
                {
                    WriteName(mod.Dir, cn);
                    count++;
                    env.Log($"更新后重新写回中文名：{mod.Id} → {cn}");
                }
                catch (Exception ex)
                {
                    env.Log($"重新写回中文名失败：{mod.Id} → {ex.Message}");
                }
            }
            return count;
        }
    }
}
