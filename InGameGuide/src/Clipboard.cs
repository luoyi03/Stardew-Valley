using System;
using System.Runtime.InteropServices;
using System.Text;

namespace InGameGuide
{
    /// <summary>写入系统剪贴板（Windows Win32），用于把物品代码复制到命名框粘贴。</summary>
    internal static class WinClipboard
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;

        public static bool SetText(string text)
        {
            if (text == null)
                text = "";
            if (!OpenClipboard(IntPtr.Zero))
                return false;

            IntPtr mem = IntPtr.Zero;
            try
            {
                if (!EmptyClipboard())
                    return false;

                byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
                mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
                if (mem == IntPtr.Zero)
                    return false;

                IntPtr ptr = GlobalLock(mem);
                if (ptr == IntPtr.Zero)
                {
                    GlobalFree(mem);
                    mem = IntPtr.Zero;
                    return false;
                }
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                GlobalUnlock(mem);

                if (SetClipboardData(CF_UNICODETEXT, mem) == IntPtr.Zero)
                {
                    GlobalFree(mem);
                    mem = IntPtr.Zero;
                    return false;
                }
                mem = IntPtr.Zero;   // 交给系统接管，不能释放
                return true;
            }
            finally
            {
                if (mem != IntPtr.Zero)
                    GlobalFree(mem);
                CloseClipboard();
            }
        }
    }
}
