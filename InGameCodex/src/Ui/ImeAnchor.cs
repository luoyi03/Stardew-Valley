using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace InGameCodex.Ui
{
    /// <summary>
    /// 让中文输入法（IME）在游戏里「看得见」。
    ///
    /// 背景：SDL / MonoGame 只把**上屏后**的文本交给游戏（TextInput 事件），拼音串与候选词列表
    /// 全程由 Windows 自己画在一个独立小窗口里；而 SDV 从不告诉系统「输入框在哪」，
    /// 候选窗默认弹在光标**下方**。AI 页的输入框贴在屏幕最底部 → 候选窗整个落到画面外，
    /// 玩家就「看不到自己在打什么字」（0~4 页搜索框在面板顶部、候选窗朝下，所以一直没暴露）。
    ///
    /// 这里做四件事，全部只在输入框聚焦时执行，任何一步失败都静默跳过（不影响打字）：
    /// 1. <c>SDL_SetTextInputRect</c>：告诉 SDL 输入区域在哪；
    /// 2. imm32 的 <c>ImmSetCompositionWindow</c> / <c>ImmSetCandidateWindow</c>：把组合串锚进输入框里的
    ///    文字行，候选窗钉到输入框正下方（输入法默认把它画在组合串旁边，正好压住框里打的拼音）；
    /// 3. imm32 的 <c>ImmGetCompositionStringW</c> / <c>ImmGetCandidateListW</c>：把「正在输入、还没上屏」
    ///    的拼音串与候选词读回来，交给界面自己画进游戏 —— 即使系统候选窗仍在画面外也能看见、能选；
    /// 4. 诊断日志：定位成功/失败各打一次（SMAPI 控制台可见），便于排查为什么看不到。
    ///
    /// 踩坑记录：MonoGame(DesktopGL) 的 <c>Game1.game1.Window.Handle</c> 返回的是 <c>SDL_Window*</c>，
    /// **不是** Win32 HWND，直接喂给 <c>ImmGetContext</c> 会一直返回 NULL（等于 imm32 这条路全废）。
    /// 所以这里用 IsWindow 校验 + 多路兜底（Window.Handle → 本线程活动窗口 → 前台窗口）拿真 HWND。
    /// </summary>
    internal static class ImeAnchor
    {
        // ---------------- 原生结构 / 接口 ----------------

        [StructLayout(LayoutKind.Sequential)]
        private struct SdlRect
        {
            public int x, y, w, h;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int x, y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int left, top, right, bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CompositionForm
        {
            public int dwStyle;
            public NativePoint ptCurrentPos;
            public NativeRect rcArea;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CandidateForm
        {
            public int dwIndex;
            public int dwStyle;
            public NativePoint ptCurrentPos;
            public NativeRect rcArea;
        }

        /// <summary>MonoGame(DesktopGL) 用的就是游戏目录里这份 SDL2。</summary>
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_SetTextInputRect")]
        private static extern void SDL_SetTextInputRect(ref SdlRect rect);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("imm32.dll", ExactSpelling = true)]
        private static extern IntPtr ImmGetContext(IntPtr hWnd);

        [DllImport("imm32.dll", ExactSpelling = true)]
        private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

        [DllImport("imm32.dll", ExactSpelling = true)]
        private static extern bool ImmSetCompositionWindow(IntPtr hIMC, ref CompositionForm lpCompForm);

        [DllImport("imm32.dll", ExactSpelling = true)]
        private static extern bool ImmSetCandidateWindow(IntPtr hIMC, ref CandidateForm lpCandidate);

        [DllImport("imm32.dll", ExactSpelling = true, EntryPoint = "ImmGetCompositionStringW")]
        private static extern int ImmGetCompositionStringW(IntPtr hIMC, int dwIndex, byte[] lpBuf, int dwBufLen);

        [DllImport("imm32.dll", ExactSpelling = true, EntryPoint = "ImmGetCandidateListW")]
        private static extern int ImmGetCandidateListW(IntPtr hIMC, int dwIndex, byte[] lpCandList, int dwBufLen);

        private const int CFS_POINT = 0x0002;           // 组合窗按「点」定位
        private const int CFS_CANDIDATEPOS = 0x0040;    // 候选窗按「点」定位（强制放在指定处）
        private const int GCS_COMPSTR = 0x0008;         // 正在组合的串（拼音）

        // ---------------- 状态 ----------------

        private static bool _sdlOk = true;      // 某条原生路径不可用后就不再重试
        private static bool _immOk = true;

        private static IntPtr _hwnd = IntPtr.Zero;
        private static string _hwndSource = "";

        private static string _composition = "";
        private static readonly List<string> _candidates = new();
        private static int _selected;
        private static int _pageStart;
        private static int _pageSize;

        private static bool _warnedNoHwnd;
        private static bool _loggedComp;
        private static bool _loggedCand;
        private static bool _loggedPos;

        /// <summary>当前「正在输入、还没上屏」的组合串（没有则为空串）。</summary>
        internal static string Composition => _composition ?? "";

        /// <summary>当前候选词（拿不到就是空表，此时只能靠系统候选窗）。</summary>
        internal static IReadOnlyList<string> Candidates => _candidates;

        internal static int SelectedIndex => _selected;
        internal static int PageStart => _pageStart;
        internal static int PageSize => _pageSize;

        /// <summary>输入框失去键盘焦点时调用。</summary>
        internal static void Blur() => Clear();

        private static void Clear()
        {
            _composition = "";
            _candidates.Clear();
            _selected = 0;
            _pageStart = 0;
            _pageSize = 0;
        }

        /// <summary>
        /// 输入框聚焦时每帧调用：把 IME 锚到 <paramref name="screenRect"/>（窗口客户区坐标），
        /// 并刷新组合串 / 候选词。
        /// </summary>
        internal static void Focus(Rectangle screenRect)
        {
            // 1) SDL：声明输入区域（SDL 据此摆放组合窗，并让候选窗避开该矩形）
            if (_sdlOk)
            {
                try
                {
                    var sdl = new SdlRect { x = screenRect.X, y = screenRect.Y, w = screenRect.Width, h = screenRect.Height };
                    SDL_SetTextInputRect(ref sdl);
                }
                catch
                {
                    _sdlOk = false;
                }
            }

            if (!_immOk)
            {
                Clear();
                return;
            }

            // 2) 3) 4) imm32：钉位置 + 读数据 + 诊断
            IntPtr hwnd = IntPtr.Zero;
            IntPtr himc = IntPtr.Zero;
            try
            {
                hwnd = ResolveHwnd();
                if (hwnd == IntPtr.Zero)
                {
                    if (!_warnedNoHwnd)
                    {
                        _warnedNoHwnd = true;
                        ModEntry.Log?.Log("AI 输入法：拿不到游戏窗口句柄（Window.Handle 可能不是 HWND，活动/前台窗口也没 IME 上下文），"
                            + "系统候选窗可能显示在画面外。可先把显示模式改成「无边框全屏」再试。", LogLevel.Warn);
                    }
                    Clear();
                    return;
                }

                himc = ImmGetContext(hwnd);
                if (himc == IntPtr.Zero)
                {
                    Clear();
                    return;
                }

                _composition = ReadComposition(himc);
                ReadCandidates(himc);

                // 拼音串：贴在输入框内的文字行处（与 SDV TextBox 的文本原点 X+16 / Y+12 对齐）
                var comp = new CompositionForm
                {
                    dwStyle = CFS_POINT,
                    ptCurrentPos = new NativePoint { x = screenRect.X + 18, y = screenRect.Y + 12 }
                };
                ImmSetCompositionWindow(himc, ref comp);

                // 候选列表：优先钉到输入框**下方**；下方被窗口底挡住时退到输入框上方。
                // ptCurrentPos 是候选窗的**左上角**，所以「放在输入框上方」必须自己减掉候选条高度，
                // 而候选条高度由输入法自己的窗口决定（微信输入法实测约 70px）—— 这里宁大勿小，
                // 估小了候选条就会从输入框顶部往下压，盖住框里正在打的拼音串（踩过两次）。
                const int candidateHeight = 80;
                int candX = screenRect.X + 6;
                int candY = screenRect.Bottom + 8;
                int clientH = WindowClientHeight(hwnd);
                bool putAbove = false;
                if (clientH > screenRect.Bottom && candY + candidateHeight > clientH - 4)
                {
                    candY = screenRect.Y - candidateHeight - 12;   // 下方塞不下（面板贴窗口底）→ 退到输入框上方
                    putAbove = true;
                }

                var cand = new CandidateForm
                {
                    dwIndex = 0,
                    dwStyle = CFS_CANDIDATEPOS,
                    ptCurrentPos = new NativePoint { x = candX, y = candY }
                };
                ImmSetCandidateWindow(himc, ref cand);

                if (!_loggedPos)
                {
                    _loggedPos = true;
                    ModEntry.Log?.Log(
                        $"AI 输入法：候选窗 (x={candX}, y={candY}) {(putAbove ? "放在输入框上方（下方塞不下）" : "放在输入框下方")}；"
                        + $"输入框 {screenRect.X},{screenRect.Y} {screenRect.Width}x{screenRect.Height}；窗口客户区高 {clientH}（下方剩余 {clientH - screenRect.Bottom}px）",
                        LogLevel.Info);
                }
            }
            catch
            {
                _immOk = false;
                Clear();
            }
            finally
            {
                if (himc != IntPtr.Zero)
                {
                    try { ImmReleaseContext(hwnd, himc); }
                    catch { /* ignore */ }
                }
            }
        }

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

        /// <summary>
        /// 游戏窗口客户区高度（物理像素，与传入的 <c>screenRect</c> 同一坐标系）。
        /// 拿不到就返回 0，调用方据此跳过「会不会超出屏幕」的避让判断。
        /// </summary>
        private static int WindowClientHeight(IntPtr hwnd)
        {
            try
            {
                if (hwnd != IntPtr.Zero && GetClientRect(hwnd, out NativeRect r))
                    return r.bottom - r.top;
            }
            catch
            {
                // ignore
            }
            return 0;
        }

        /// <summary>
        /// 找一个真正能拿到 IME 上下文的窗口句柄。
        /// MonoGame 的 <c>Window.Handle</c> 在 DesktopGL 上是 SDL_Window* 而非 HWND，
        /// 所以必须先用 IsWindow 过滤，再按「上次成功的 → 引擎给的 → 活动窗口 → 前台窗口」顺序试。
        /// </summary>
        private static IntPtr ResolveHwnd()
        {
            IntPtr engine;
            try { engine = Game1.game1?.Window?.Handle ?? IntPtr.Zero; }
            catch { engine = IntPtr.Zero; }

            if (TryHwnd(_hwnd, string.IsNullOrEmpty(_hwndSource) ? "上次成功" : _hwndSource)) return _hwnd;
            if (TryHwnd(engine, "Window.Handle")) return _hwnd;
            if (TryHwnd(GetActiveWindow(), "GetActiveWindow")) return _hwnd;
            if (TryHwnd(GetForegroundWindow(), "GetForegroundWindow")) return _hwnd;
            return IntPtr.Zero;
        }

        private static bool TryHwnd(IntPtr h, string src)
        {
            if (h == IntPtr.Zero || !IsWindow(h))
                return false;

            IntPtr himc = ImmGetContext(h);
            if (himc == IntPtr.Zero)
                return false;
            ImmReleaseContext(h, himc);

            if (_hwnd != h)
            {
                _hwnd = h;
                _hwndSource = src;
                ModEntry.Log?.Log($"AI 输入法：已定位游戏窗口（来源 {src}，hwnd=0x{h.ToInt64():X}）", LogLevel.Info);
            }
            return true;
        }

        /// <summary>读当前组合串（UTF-16；没在组合时返回空串）。</summary>
        private static string ReadComposition(IntPtr himc)
        {
            int bytes = ImmGetCompositionStringW(himc, GCS_COMPSTR, null, 0);
            if (bytes <= 0)
                return "";

            var buf = new byte[bytes];
            int got = ImmGetCompositionStringW(himc, GCS_COMPSTR, buf, bytes);
            string s = got > 0 ? Encoding.Unicode.GetString(buf, 0, got) : "";
            if (s.Length > 0 && !_loggedComp)
            {
                _loggedComp = true;
                ModEntry.Log?.Log("AI 输入法：可读取组合串（游戏内自绘拼音已生效）", LogLevel.Info);
            }
            return s;
        }

        /// <summary>
        /// 读当前候选词列表（CANDIDATELIST，TSF 输入法可能返回 0 —— 那就只能靠系统候选窗）。
        /// 布局：dwIndex(0) dwStyle(4) dwCount(8) dwSelection(12) dwPageStart(16) dwPageSize(20) dwOffset[](24...)
        /// </summary>
        private static void ReadCandidates(IntPtr himc)
        {
            _candidates.Clear();
            _selected = 0;
            _pageStart = 0;
            _pageSize = 0;

            int bytes = ImmGetCandidateListW(himc, 0, null, 0);
            if (bytes < 28)
                return;

            var buf = new byte[bytes];
            int got = ImmGetCandidateListW(himc, 0, buf, bytes);
            if (got < 28)
                return;

            int count = BitConverter.ToInt32(buf, 8);
            int sel = BitConverter.ToInt32(buf, 12);
            int pageStart = BitConverter.ToInt32(buf, 16);
            int pageSize = BitConverter.ToInt32(buf, 20);
            if (count <= 0 || count > 128)
                return;

            for (int i = 0; i < count; i++)
            {
                int offPos = 24 + i * 4;
                if (offPos + 4 > got)
                    break;
                int strOff = BitConverter.ToInt32(buf, offPos);
                if (strOff <= 0 || strOff >= got)
                    break;

                string s = Utf16At(buf, strOff, got);
                if (s.Length == 0)
                    break;
                _candidates.Add(s);
            }

            if (_candidates.Count == 0)
                return;

            _selected = sel;
            _pageStart = Math.Max(0, pageStart);
            _pageSize = Math.Max(0, pageSize);

            if (!_loggedCand)
            {
                _loggedCand = true;
                ModEntry.Log?.Log($"AI 输入法：可读取候选词列表（{_candidates.Count} 个，游戏内自绘候选条已生效）", LogLevel.Info);
            }
        }

        /// <summary>从字节缓冲里读以 null 结尾的 UTF-16 串。</summary>
        private static string Utf16At(byte[] buf, int off, int limit)
        {
            int end = off;
            while (end + 1 < limit && (buf[end] != 0 || buf[end + 1] != 0))
                end += 2;
            return end > off ? Encoding.Unicode.GetString(buf, off, end - off) : "";
        }
    }
}
