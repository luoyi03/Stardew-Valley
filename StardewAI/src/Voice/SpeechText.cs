using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace StardewAI.Voice
{
    /// <summary>把模型回答整理成适合朗读的短句。</summary>
    internal static class SpeechText
    {
        private static readonly Regex GiveTag = new(@"\[\[\s*GIVE\s*:[^\]\r\n]*?\]\]", RegexOptions.IgnoreCase);

        private static readonly Regex Symbols = new(@"[*_`#>~\[\]|]");

        // emoji：非 BMP 字符（代理对）+ 常见符号区 + 变体选择符。念出来会变成怪音，直接去掉。
        private static readonly Regex Emoji = new(@"[\uD800-\uDBFF][\uDC00-\uDFFF]|[\u2190-\u21FF\u2300-\u27BF\u2B00-\u2BFF\uFE0F\uFE00-\uFE0F]");

        /// <summary>去掉不该念出来的东西（给物品标记、Markdown 符号等）。</summary>
        public static string Clean(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";

            string s = GiveTag.Replace(raw, " ");
            s = Emoji.Replace(s, " ");      // 表情符号念出来是怪音，先去掉
            s = Symbols.Replace(s, " ");
            s = s.Replace("\r", " ").Replace("\n", " ");
            s = Regex.Replace(s, @"\s{2,}", " ");
            return s.Trim();
        }

        private static bool IsEnder(char c)
            => c == '。' || c == '！' || c == '？' || c == '!' || c == '?' || c == '.' ||
               c == '…' || c == '；' || c == ';' || c == '\n';

        /// <summary>软断点：逗号。长句里先在这里切一段，能让第一声早点出来。</summary>
        private static bool IsSoftEnder(char c)
            => c == '，' || c == '、' || c == ',' || c == '：' || c == ':';

        /// <summary>软断点前至少要攒够这么多字才切，免得碎成一堆短句、反而更慢。</summary>
        private const int SoftCutMin = 16;

        /// <summary>tail 里「最后一句完整的话」的结束位置；没有完整句子时返回 0。</summary>
        public static int LastSentenceEnd(string tail)
        {
            if (string.IsNullOrEmpty(tail))
                return 0;

            int cut = 0;

            for (int i = 0; i < tail.Length; i++)
            {
                if (!IsEnder(tail[i]))
                    continue;

                if (i + 1 >= 4)
                    cut = i + 1;
            }

            if (cut > 0)
                return cut;

            // 一直没等到句号：够长了就允许在最后一个逗号处先切一段
            for (int i = tail.Length - 1; i >= SoftCutMin; i--)
            {
                if (IsSoftEnder(tail[i]))
                    return i + 1;
            }

            return 0;
        }

        /// <summary>切成适合逐句合成的短句（太长会被强行切开）。</summary>
        public static List<string> Split(string text, int maxLen = 60)
        {
            var res = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
                return res;

            maxLen = Math.Max(20, maxLen);
            var cur = new StringBuilder();

            foreach (char c in text)
            {
                if (c == '\r')
                    continue;

                cur.Append(c);

                if (cur.Length >= maxLen || (cur.Length >= 4 && IsEnder(c)) || (cur.Length >= SoftCutMin && IsSoftEnder(c)))
                    Flush(res, cur);
            }

            Flush(res, cur);

            // 太碎的片段（「嗯。」这种）并回上一句，少发几次请求
            for (int i = res.Count - 1; i > 0; i--)
            {
                if (res[i].Length >= 5)
                    continue;

                res[i - 1] += res[i];
                res.RemoveAt(i);
            }

            return res;
        }

        private static void Flush(List<string> res, StringBuilder cur)
        {
            string s = cur.ToString().Trim();
            cur.Clear();

            if (s.Length > 0)
                res.Add(s);
        }
    }
}
