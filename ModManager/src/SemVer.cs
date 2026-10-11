using System;
using System.Globalization;

namespace ModManager
{
    /// <summary>极简语义化版本（支持 1.2 / 1.2.3 / 1.2.3.4 / 1.2.3-beta.5）。</summary>
    public sealed class SemVer : IComparable<SemVer>
    {
        public readonly int[] Parts = new int[4];
        public readonly string Pre;
        public readonly string Raw;

        private SemVer(int[] parts, string pre, string raw)
        {
            Parts = parts;
            Pre = pre;
            Raw = raw;
        }

        public bool IsPrerelease => Pre != null;

        public static bool TryParse(string input, out SemVer v)
        {
            v = null;
            if (string.IsNullOrWhiteSpace(input))
                return false;

            string s = input.Trim();
            if (s[0] == 'v' || s[0] == 'V')
                s = s.Substring(1);

            int plus = s.IndexOf('+');
            if (plus >= 0)
                s = s.Substring(0, plus);

            string pre = null;
            int dash = s.IndexOf('-');
            if (dash > 0)
            {
                pre = s.Substring(dash + 1).Trim();
                s = s.Substring(0, dash);
            }

            string[] bits = s.Split('.');
            int[] parts = new int[4];
            for (int i = 0; i < 4; i++)
            {
                if (i >= bits.Length)
                {
                    parts[i] = 0;
                    continue;
                }
                string b = bits[i].Trim();
                if (b.Length == 0)
                {
                    parts[i] = 0;
                    continue;
                }
                string digits = "";
                foreach (char c in b)
                {
                    if (char.IsDigit(c))
                        digits += c;
                    else
                        break;
                }
                if (digits.Length == 0)
                    return false;
                parts[i] = int.Parse(digits, CultureInfo.InvariantCulture);
                if (pre == null && b.Length > digits.Length)
                    pre = b.Substring(digits.Length);
            }

            v = new SemVer(parts, string.IsNullOrWhiteSpace(pre) ? null : pre, input.Trim());
            return true;
        }

        public int CompareTo(SemVer o)
        {
            if (o == null)
                return 1;
            for (int i = 0; i < 4; i++)
            {
                int c = Parts[i].CompareTo(o.Parts[i]);
                if (c != 0)
                    return c;
            }
            bool aPre = IsPrerelease, bPre = o.IsPrerelease;
            if (!aPre && !bPre) return 0;
            if (aPre && !bPre) return -1;
            if (!aPre && bPre) return 1;
            return ComparePre(Pre, o.Pre);
        }

        private static int ComparePre(string a, string b)
        {
            string[] x = a.Split('.'), y = b.Split('.');
            int n = Math.Min(x.Length, y.Length);
            for (int i = 0; i < n; i++)
            {
                bool xn = int.TryParse(x[i], out int xv);
                bool yn = int.TryParse(y[i], out int yv);
                int c;
                if (xn && yn) c = xv.CompareTo(yv);
                else if (xn) c = -1;
                else if (yn) c = 1;
                else c = string.CompareOrdinal(x[i], y[i]);
                if (c != 0)
                    return c;
            }
            return x.Length.CompareTo(y.Length);
        }

        /// <summary>candidate 是否比 installed 新（解析失败时保守返回 false）。</summary>
        public static bool IsNewer(string candidate, string installed)
        {
            if (!TryParse(candidate, out var c))
                return false;
            if (!TryParse(installed, out var i))
                return true;
            return c.CompareTo(i) > 0;
        }

        public override string ToString() => Raw;
    }
}
