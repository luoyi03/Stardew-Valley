using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace StardewAI.Game
{
    /// <summary>
    /// 记下她每次进游戏时说的开场白，下次把最近几句塞进提示词里让她"别再说一样的"。
    /// 存在 Mods/StardewAI/voice/greetings.txt，一行一句，只留最近 12 句。
    /// </summary>
    internal static class GreetingStore
    {
        private const int Keep = 12;

        private static readonly List<string> Lines = new();
        private static bool _loaded;
        private static string _path;

        private static string Path
        {
            get
            {
                if (_path == null)
                {
                    string dir = string.IsNullOrEmpty(Voice.VoiceAssistant.Dir)
                        ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "StardewAI_voice")
                        : Voice.VoiceAssistant.Dir;

                    _path = System.IO.Path.Combine(dir, "greetings.txt");
                }

                return _path;
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
                return;

            _loaded = true;

            try
            {
                if (!File.Exists(Path))
                    return;

                foreach (string line in File.ReadAllLines(Path, Encoding.UTF8))
                {
                    string s = (line ?? "").Trim();

                    if (s.Length > 0)
                        Lines.Add(s);
                }
            }
            catch
            {
                // 读不到就当空的
            }
        }

        /// <summary>最近几句开场白（给模型看，让它换着说）。</summary>
        public static List<string> Recent()
        {
            EnsureLoaded();
            return new List<string>(Lines);
        }

        /// <summary>记下一次开场白（去重、只留最近 Keep 句）。</summary>
        public static void Add(string text)
        {
            EnsureLoaded();

            string s = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

            if (s.Length == 0)
                return;

            if (s.Length > 60)
                s = s.Substring(0, 60);

            if (Lines.Contains(s))
                return;

            Lines.Add(s);

            while (Lines.Count > Keep)
                Lines.RemoveAt(0);

            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                File.WriteAllLines(Path, Lines, new UTF8Encoding(false));
            }
            catch
            {
                // 写不进就算了，不影响聊天
            }
        }
    }
}
