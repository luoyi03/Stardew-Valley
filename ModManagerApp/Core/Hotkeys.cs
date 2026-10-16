using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SMM
{
    /// <summary>一条按键绑定（读自某个 Mod 的 config.json）。</summary>
    public sealed class HotkeyBinding
    {
        public string ModId { get; set; }
        public string ModName { get; set; }
        public string FolderName { get; set; }

        /// <summary>config.json 的完整路径。</summary>
        public string ConfigPath { get; set; }

        /// <summary>在 config.json 里的位置，如 .OpenMenuKey 或 .ai.askKey 或 .Keys[0]。</summary>
        public string JsonPath { get; set; }

        /// <summary>配置里原来的值。</summary>
        public string Value { get; set; }

        /// <summary>归一化后的组合（令牌排序、统一大小写），用于比对重复。</summary>
        public string Normalized { get; set; }

        /// <summary>用来唯一标识这条绑定（记住原值 / 定位）。</summary>
        public string Key => (ModId ?? "") + "|" + (JsonPath ?? "");

        /// <summary>值是不是 None（= 被禁用）。</summary>
        public bool IsDisabled => Hotkeys.IsNone(Value);

        /// <summary>界面上显示的按键（禁用的显示成「已禁用」）。</summary>
        public string ValueText => IsDisabled ? "已禁用" : Value;

        /// <summary>状态列。</summary>
        public string StateText => IsDisabled ? "已禁用" : "启用中";

        /// <summary>重复标记（由页面在扫描后填上）。</summary>
        public string ConflictText { get; set; }
    }

    /// <summary>同一个按键组合被多条绑定占用。</summary>
    public sealed class HotkeyConflict
    {
        public string Combo { get; set; }
        public List<HotkeyBinding> Items { get; set; } = new();

        public bool SameModOnly => Items.Select(i => i.ModId).Distinct(StringComparer.OrdinalIgnoreCase).Count() <= 1;

        public string Text => $"{Combo}　被 {Items.Count} 处使用：" + string.Join("；",
            Items.Select(i => $"{i.ModName}（{i.JsonPath.TrimStart('.')}）"));
    }

    /// <summary>
    /// 快捷键：读各 Mod 的 config.json，列出按键绑定、找出重复、并支持改键 / 禁用 / 启用。
    ///
    /// 说明：只能读「已经写进配置文件」的按键。极大多数 Mod 的 config.json 是第一次运行游戏时
    /// 才生成的（内容是它当时的默认键），所以在游戏至少跑过一次之前，这里能看到的条目会很少。
    /// 改键 / 禁用都是写回该 Mod 自己的 config.json，游戏重启后生效（GMCM 类 Mod 也可能立刻生效）。
    /// </summary>
    public static class Hotkeys
    {
        /// <summary>加上 None（= 不绑定）在内、认得出来的按键名 → 统一写法（大小写）。</summary>
        private static readonly Dictionary<string, string> Known = BuildKnownKeys();

        private static Dictionary<string, string> BuildKnownKeys()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Add(string name) => map[name] = name;

            Add("None");
            for (int i = 1; i <= 24; i++)
                Add("F" + i);
            for (char c = 'A'; c <= 'Z'; c++)
                Add(c.ToString());
            for (int i = 0; i <= 9; i++)
            {
                Add("D" + i);
                Add("NumPad" + i);
            }

            foreach (string k in new[]
            {
                "LeftShift", "RightShift", "LeftControl", "RightControl", "LeftAlt", "RightAlt",
                "LeftWindows", "RightWindows",
                "Space", "Enter", "Escape", "Tab", "Back", "Insert", "Delete", "Home", "End",
                "PageUp", "PageDown", "Up", "Down", "Left", "Right", "CapsLock", "NumLock", "Scroll",
                "PrintScreen", "Pause",
                "MouseLeft", "MouseMiddle", "MouseRight", "MouseX1", "MouseX2",
                "OemComma", "OemPeriod", "OemMinus", "OemPlus", "OemTilde", "OemOpenBrackets",
                "OemCloseBrackets", "OemPipe", "OemQuotes", "OemSemicolon", "OemQuestion", "OemBackslash",
                "VolumeMute", "VolumeUp", "VolumeDown", "MediaNextTrack", "MediaPreviousTrack",
                "MediaStop", "MediaPlayPause",
                "ControllerA", "ControllerB", "ControllerX", "ControllerY", "ControllerBack",
                "ControllerStart", "ControllerLeftShoulder", "ControllerRightShoulder",
                "ControllerLeftTrigger", "ControllerRightTrigger",
                "ControllerDPadUp", "ControllerDPadDown", "ControllerDPadLeft", "ControllerDPadRight",
                "ControllerLeftStickUp", "ControllerLeftStickDown", "ControllerLeftStickLeft", "ControllerLeftStickRight",
                "ControllerRightStickUp", "ControllerRightStickDown", "ControllerRightStickLeft", "ControllerRightStickRight",
                "ControllerLeftStick", "ControllerRightStick", "ControllerBigBig"
            })
                Add(k);

            return map;
        }

        public static bool IsNone(string value)
            => string.IsNullOrWhiteSpace(value)
               || value.Trim().Equals("None", StringComparison.OrdinalIgnoreCase);

        /// <summary>把一行文本拆成令牌（+ 连接组合键，逗号分隔多个备选键）。</summary>
        private static List<string> Tokens(string value)
            => (value ?? "").Split(new[] { '+', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        /// <summary>能不能识别成按键；返回 null 表示认识，否则返回原因（给界面显示）。</summary>
        public static string WhyNotKey(string value)
        {
            var tokens = Tokens(value);
            if (tokens.Count == 0)
                return "空值";
            if (tokens.Count > 4)
                return "令牌太多（最多 4 个）";
            foreach (string t in tokens)
            {
                if (!Known.ContainsKey(t))
                    return $"「{t}」不是认得的按键名";
            }
            return null;
        }

        /// <summary>校验用户输入的按键，返回错误提示（null = 通过）。</summary>
        public static string ValidateInput(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "不能为空。想停用这个快捷键请用「禁用」按钮。";
            if (value.Trim().Length > 40)
                return "太长了。组合键用 + 连接（例如 LeftShift + F5），多个备选键用逗号隔开（例如 F5, F6）。";
            foreach (string t in Tokens(value))
            {
                if (!Known.ContainsKey(t))
                    return $"「{t}」不是有效的按键名。可用：F1-F24、A-Z、D0-D9、LeftShift/LeftControl/LeftAlt、" +
                           "Up/Down/Left/Right、Space/Enter/Escape/Tab、MouseLeft/Right，组合用 + 连接，" +
                           "多个备选键用逗号隔开。";
            }
            return null;
        }

        /// <summary>归一化：令牌统一成标准写法并按字母排序，方便比对（"f5 + leftshift" == "LeftShift + F5"）。</summary>
        public static string Normalize(string value)
        {
            var tokens = Tokens(value)
                .Select(t => Known.TryGetValue(t, out string canon) ? canon : t)
                .Where(t => !t.Equals("None", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.ToLowerInvariant())
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList();
            return string.Join("+", tokens);
        }

        /// <summary>扫描所有已装 Mod 的 config.json，列出按键绑定。</summary>
        public static List<HotkeyBinding> Scan(AppEnv env)
        {
            var list = new List<HotkeyBinding>();
            if (env == null || string.IsNullOrWhiteSpace(env.ModsPath))
                return list;

            List<LocalMod> mods;
            try { mods = ModScanner.Scan(env.ModsPath, env.DisabledDir); }
            catch { return list; }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in mods)
            {
                if (string.IsNullOrWhiteSpace(m.Dir))
                    continue;

                string cfg = Path.Combine(m.Dir, "config.json");
                if (!File.Exists(cfg) || !seen.Add(cfg))
                    continue;

                try
                {
                    using var doc = JsonUtil.Parse(File.ReadAllText(cfg));
                    Walk(doc.RootElement, "", (path, value) =>
                    {
                        list.Add(new HotkeyBinding
                        {
                            ModId = m.Id,
                            ModName = ModNames.Display(m),
                            FolderName = Path.GetFileName(m.Dir),
                            ConfigPath = cfg,
                            JsonPath = path,
                            Value = value,
                            Normalized = Normalize(value)
                        });
                    });
                }
                catch (Exception ex)
                {
                    env.Log($"读按键配置失败（{Path.GetFileName(m.Dir)}）：{ex.Message}");
                }
            }

            return list
                .OrderBy(b => b.ModName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(b => b.JsonPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>单看名字容易跟别的设置混淆的键名（Left/Right 也可能是"位置"这种枚举值）。</summary>
        private static readonly HashSet<string> AmbiguousTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "Left", "Right", "Up", "Down", "Space", "Home", "End", "Back", "Tab", "Enter", "None"
        };

        /// <summary>字段名看起来像按键设置吗（Key / Button / Bind / Hotkey / Toggle / Controls…）。</summary>
        private static bool LooksLikeKeyField(string path)
        {
            string name = path ?? "";
            int dot = name.LastIndexOf('.');
            if (dot >= 0)
                name = name.Substring(dot + 1);
            int br = name.IndexOf('[');
            if (br > 0)
                name = name.Substring(0, br);
            if (name.Length == 0)
                return true;      // 形如 .Keys[0]：上一层名字没了，交给值本身判断

            foreach (string hint in new[] { "key", "button", "bind", "hotkey", "toggle", "controls", "shortcut" })
            {
                if (name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>值只有一个令牌、且这个令牌本身有歧义（Left / None 之类）。</summary>
        private static bool IsAmbiguousSingle(string value)
        {
            var tokens = Tokens(value);
            return tokens.Count == 1 && AmbiguousTokens.Contains(tokens[0]);
        }

        /// <summary>递归找「值看起来是按键」的字段（用已知按键名白名单判断，避免把 "ask"/"zh" 误认成按键）。</summary>
        private static void Walk(System.Text.Json.JsonElement el, string path, Action<string, string> emit)
        {
            switch (el.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                    foreach (var p in el.EnumerateObject())
                        Walk(p.Value, path + "." + p.Name, emit);
                    break;

                case System.Text.Json.JsonValueKind.Array:
                    int i = 0;
                    foreach (var x in el.EnumerateArray())
                        Walk(x, path + "[" + i++ + "]", emit);
                    break;

                case System.Text.Json.JsonValueKind.String:
                    string v = el.GetString();
                    if (string.IsNullOrWhiteSpace(v) || WhyNotKey(v) != null)
                        break;

                    // "Left"/"None" 这种单令牌值，字段名不像按键设置时多半是别的设置（例如 ModStatusPosition=Left）
                    if (IsAmbiguousSingle(v) && !LooksLikeKeyField(path))
                        break;

                    emit(path, v);
                    break;
            }
        }

        /// <summary>
        /// 把一行按键值拆成「备选键」：F5, F6 是两个备选键；LeftShift + F5 是一个组合键。
        /// 返回归一化后的形式（排序、小写），用于查重。
        /// </summary>
        public static List<string> Alternatives(string value)
        {
            var list = new List<string>();
            foreach (string part in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string norm = Normalize(part);
                if (norm.Length == 0)
                    continue;
                if (!list.Contains(norm, StringComparer.Ordinal))
                    list.Add(norm);
            }
            return list;
        }

        /// <summary>归一化形式转回好看的样子：leftshift+f5 → LeftShift + F5。</summary>
        public static string Pretty(string normalized)
        {
            if (string.IsNullOrWhiteSpace(normalized))
                return "";
            return string.Join(" + ", normalized.Split('+')
                .Select(t => Known.Values.FirstOrDefault(k => k.ToLowerInvariant() == t) ?? t));
        }

        /// <summary>
        /// 找出重复的按键：按「备选键」逐个比（所以 F5, F6 与 F6 也能查出重复）；
        /// 禁用的绑定不参与。
        /// </summary>
        public static List<HotkeyConflict> Conflicts(List<HotkeyBinding> bindings)
        {
            var groups = new Dictionary<string, List<HotkeyBinding>>(StringComparer.Ordinal);

            foreach (var b in (bindings ?? new List<HotkeyBinding>()).Where(x => !x.IsDisabled))
            {
                foreach (string alt in Alternatives(b.Value))
                {
                    if (!groups.TryGetValue(alt, out var list))
                        groups[alt] = list = new List<HotkeyBinding>();
                    if (!list.Contains(b))
                        list.Add(b);
                }
            }

            return groups
                .Where(g => g.Value.Count > 1)
                .Select(g => new HotkeyConflict { Combo = Pretty(g.Key), Items = g.Value })
                .OrderByDescending(c => c.Items.Count)
                .ThenBy(c => c.Combo)
                .ToList();
        }

        /// <summary>
        /// 把 self 的按键改成 newValue 时，会不会跟别人撞上？返回撞上的那些绑定（不含自己、不含已禁用的）。
        /// 改键前用它拦住"保存成功但一按两个功能一起触发"。
        /// </summary>
        public static List<HotkeyBinding> ConflictsFor(List<HotkeyBinding> all, HotkeyBinding self, string newValue)
        {
            var mine = Alternatives(newValue);
            if (mine.Count == 0)
                return new List<HotkeyBinding>();

            return (all ?? new List<HotkeyBinding>())
                .Where(b => !b.IsDisabled
                            && !string.Equals(b.Key, self?.Key, StringComparison.OrdinalIgnoreCase)
                            && Alternatives(b.Value).Any(a => mine.Contains(a, StringComparer.Ordinal)))
                .ToList();
        }

        /// <summary>把新按键写回该 Mod 的 config.json（其它字段原样保留）。</summary>
        public static void Write(AppEnv env, HotkeyBinding binding, string newValue)
        {
            if (binding == null || string.IsNullOrWhiteSpace(binding.ConfigPath))
                throw new Exception("这条绑定没有对应的配置文件");

            newValue = (newValue ?? "").Trim();
            string bad = ValidateInput(newValue);
            if (bad != null)
                throw new Exception(bad);

            WriteRaw(env, binding.ConfigPath, binding.JsonPath, newValue);

            // 用户主动改成了别的键，之前记的「原值」就作废了
            if (!IsNone(newValue) && env.Config.HotkeyOriginal?.Remove(binding.Key) == true)
                env.Config.Save();

            env.Log($"改快捷键：{binding.ModName} {binding.JsonPath} → {newValue}"
                    + (newValue.Equals(binding.Value, StringComparison.OrdinalIgnoreCase) ? "（没变）" : ""));
            binding.Value = newValue;
            binding.Normalized = Normalize(newValue);
        }

        /// <summary>禁用：记住原来的键，然后把值写成 None（SMAPI 把 None 当作没绑定）。</summary>
        public static void Disable(AppEnv env, HotkeyBinding binding)
        {
            if (IsNone(binding.Value))
                return;

            env.Config.HotkeyOriginal ??= new Dictionary<string, string>();
            env.Config.HotkeyOriginal[binding.Key] = binding.Value;
            env.Config.Save();

            WriteRaw(env, binding.ConfigPath, binding.JsonPath, "None");
            env.Log($"禁用快捷键：{binding.ModName} {binding.JsonPath}（原为 {binding.Value}）");
            binding.Value = "None";
            binding.Normalized = "";
        }

        /// <summary>启用：把之前禁用时记住的键写回去。</summary>
        public static void Enable(AppEnv env, HotkeyBinding binding)
        {
            if (!IsNone(binding.Value))
                return;

            string original = null;
            env.Config.HotkeyOriginal?.TryGetValue(binding.Key, out original);
            if (string.IsNullOrWhiteSpace(original))
                throw new Exception("没有记到它原来的按键，请用「改键」直接填一个（例如 F5）");

            WriteRaw(env, binding.ConfigPath, binding.JsonPath, original);
            env.Log($"启用快捷键：{binding.ModName} {binding.JsonPath} → {original}");
            binding.Value = original;
            binding.Normalized = Normalize(original);
        }

        // ------------------------------------------------------------------
        /// <summary>写回配置（实际读写都在 JsonEdit 里，快捷键页与 AI 设置页共用同一套）。</summary>
        private static void WriteRaw(AppEnv env, string configPath, string jsonPath, string value)
            => JsonEdit.Write(env, configPath, jsonPath, value);
    }
}
