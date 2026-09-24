using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace SysResSpy.WinUI
{
    /// <summary>
    /// 调用本机豆包桌面客户端（UI 自动化），逻辑直接取自 DirSize 项目最新版：
    /// 1) 检测豆包进程/窗口；没有则尝试从开始菜单/桌面快捷方式自动打开。
    /// 2) 窗口在则聚焦，并【先定位真实输入框】再把文字送入：
    ///    优先 UIA 定位输入控件（支持 ValuePattern 则直接填入）；
    ///    否则点击窗口底部输入区聚焦 → 剪贴板粘贴(Ctrl+V，带重试)；
    ///    最后兜底 SendInput 逐字输入。
    /// </summary>
    internal static class DoubaoHelper
    {
        private static readonly string[] NameTokens = { "doubao", "豆包" };

        #region Win32 P/Invoke
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtra);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern bool EmptyClipboard();
        [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        private const int SW_RESTORE = 9;
        private const uint KEYEVENTF_KEYUP = 0x02;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint CF_UNICODETEXT = 13;
        #endregion

        /// <summary>Send the prompt to Doubao and send Enter. Returns a user-facing status message.</summary>
        public static string SendToDoubao(string prompt)
        {
            IntPtr hwnd = EnsureWindow();
            if (hwnd == IntPtr.Zero)
                return "未能自动打开豆包，请手动打开豆包后，再点一次“问豆包”。";

            FocusWindow(hwnd);

            // 首选：UIA 定位真实输入框后聚焦/填入（不依赖计算的像素坐标）。
            if (InputViaUia(hwnd, prompt))
            {
                Thread.Sleep(250);
                SendEnter();
                return "已把进程信息发送给豆包，请到豆包窗口查看回复。";
            }

            // 兜底：点击窗口底部输入区聚焦 → 剪贴板粘贴(Ctrl+V，带重试)。
            ClickInputArea(hwnd);
            if (TrySetClipboard(prompt))
            {
                SendCtrlV();
                SendEnter();
                return "已把进程信息发送给豆包，请到豆包窗口查看回复。";
            }

            // 最后兜底：SendInput 逐字输入。
            TypeText(prompt);
            SendEnter();
            return "已把进程信息发送给豆包，请到豆包窗口查看回复。";
        }

        /// <summary>用 UIA 在豆包窗口内定位输入框并把文本送入：优先 ValuePattern 直写，
        /// 否则对输入框 SetFocus 后再剪贴板粘贴。失败返回 false，交给兜底路径。</summary>
        private static bool InputViaUia(IntPtr hwnd, string text)
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                var input = FindInputElement(root);
                if (input == null) return false;

                if (TrySetValue(input, text)) return true;

                input.SetFocus();
                Thread.Sleep(400);
                if (!TrySetClipboard(text)) return false;
                SendCtrlV();
                return true;
            }
            catch { return false; }
        }

        /// <summary>在窗口 UIA 树中搜索真实聊天输入框。
        /// 豆包用 Chromium + tiptap 富文本，输入框暴露为 ControlType.Group、ClassName 含 “ProseMirror”，
        /// 且 web 层无法用 FindFirst(Descendants) 枚举，必须逐层 Children 递归过滤。</summary>
        private static AutomationElement FindInputElement(AutomationElement root)
        {
            // 优先 tiptap / ProseMirror（豆包聊天输入框）。
            var byProse = FindByClass(root, "ProseMirror", 0);
            if (byProse != null) return byProse;

            // 次优先 Edit（可编辑文本框），最后 Document（Chromium 富文本根）。
            var byEdit = FindByControl(ControlType.Edit, root, 0);
            if (byEdit != null) return byEdit;
            return FindByControl(ControlType.Document, root, 0);
        }

        /// <summary>递归（逐层 Children）查找 ClassName 含关键字、且有可见绘制区域的可聚焦元素。
        /// Chromium 的 web 树只能这样枚举。</summary>
        private static AutomationElement FindByClass(AutomationElement root, string keyword, int depth)
        {
            if (depth > 60) return null;
            try
            {
                string cn = root.Current.ClassName;
                if (!string.IsNullOrEmpty(cn)
                    && cn.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0
                    && !cn.Contains("trailingBreak")
                    && HasVisibleRect(root))
                    return root;
            }
            catch { return null; }
            foreach (AutomationElement child in root.FindAll(TreeScope.Children, Condition.TrueCondition))
            {
                var hit = FindByClass(child, keyword, depth + 1);
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>排除越界（Infinity）或空白绘制的隐式元素。</summary>
        private static bool HasVisibleRect(AutomationElement el)
        {
            try
            {
                var r = el.Current.BoundingRectangle;
                return !r.IsEmpty && !double.IsInfinity(r.Width) && r.Width > 0 && !double.IsInfinity(r.Height) && r.Height > 0;
            }
            catch { return false; }
        }

        /// <summary>递归查找指定 ControlType 的元素（作为无 ProseMirror 时的兜底）。</summary>
        private static AutomationElement FindByControl(ControlType type, AutomationElement root, int depth)
        {
            if (depth > 60) return null;
            if (root.Current.ControlType == type) return root;
            foreach (AutomationElement child in root.FindAll(TreeScope.Children, Condition.TrueCondition))
            {
                var hit = FindByControl(type, child, depth + 1);
                if (hit != null) return hit;
            }
            return null;
        }

        private static bool TrySetValue(AutomationElement el, string text)
        {
            try
            {
                if (!el.TryGetCurrentPattern(ValuePattern.Pattern, out object obj)) return false;
                var value = (ValuePattern)obj;
                if (value.Current.IsReadOnly) return false;
                value.SetValue(text);
                return true;
            }
            catch { return false; }
        }

        private static IntPtr EnsureWindow()
        {
            var hwnd = FindWindow();
            if (hwnd != IntPtr.Zero) return hwnd;

            if (TryLaunch())
                for (int i = 0; i < 24 && (hwnd = FindWindow()) == IntPtr.Zero; i++)
                    Thread.Sleep(500);
            return hwnd;
        }

        private static IntPtr FindWindow()
        {
            foreach (var p in Process.GetProcesses())
                if (IsDoubaoName(p.ProcessName) && p.MainWindowHandle != IntPtr.Zero)
                    return p.MainWindowHandle;
            foreach (var p in Process.GetProcesses())
                if (IsDoubaoName(p.ProcessName) && HasDoubaoTitle(p))
                    return p.MainWindowHandle;
            return IntPtr.Zero;
        }

        private static bool IsDoubaoName(string name)
            => !string.IsNullOrWhiteSpace(name)
               && NameTokens.Any(t => name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);

        private static bool HasDoubaoTitle(Process p)
        {
            try { return NameTokens.Any(t => p.MainWindowTitle.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0); }
            catch { return false; }
        }

        private static void FocusWindow(IntPtr hwnd)
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            Thread.Sleep(300);
            SetForegroundWindow(hwnd);
        }

        private static bool TryLaunch()
        {
            string[] dirs;
            try
            {
                dirs = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                }.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).Distinct().ToArray();
            }
            catch { return false; }

            foreach (var dir in dirs)
            {
                IEnumerable<string> links;
                try { links = Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (var lnk in links)
                {
                    if (!NameTokens.Any(t => Path.GetFileNameWithoutExtension(lnk).IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    try { Process.Start(new ProcessStartInfo { FileName = lnk, UseShellExecute = true }); return true; }
                    catch { }
                }
            }
            return false;
        }

        /// <summary>点击窗口底部输入区以获得焦点（不依赖 UIA）。</summary>
        private static void ClickInputArea(IntPtr hwnd)
        {
            if (!GetWindowRect(hwnd, out var r)) return;
            int x = r.Left + (r.Right - r.Left) / 2;
            int y = r.Top + Math.Max((r.Bottom - r.Top) - 40, r.Top + (r.Bottom - r.Top) * 3 / 5);
            try
            {
                SetCursorPos(x, y);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(250);
            }
            catch { }
        }

        /// <summary>带重试地把文本设到剪贴板，避免 OpenClipboard 竞争（0x800401D0）。</summary>
        private static bool TrySetClipboard(string s)
        {
            for (int i = 0; i < 6; i++)
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        EmptyClipboard();
                        var hMem = Marshal.StringToHGlobalUni(s);
                        if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero)
                        { Marshal.FreeHGlobal(hMem); return false; }
                        return true;
                    }
                    catch { return false; }
                    finally { CloseClipboard(); }
                }
                Thread.Sleep(120);
            }
            return false;
        }

        private static void SendCtrlV()
        {
            keybd_event(0x11, 0, 0, UIntPtr.Zero);
            keybd_event(0x56, 0, 0, UIntPtr.Zero);
            keybd_event(0x56, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(0x11, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private static void SendEnter()
        {
            keybd_event(0x0D, 0, 0, UIntPtr.Zero);
            keybd_event(0x0D, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        /// <summary>SendInput UNICODE 逐字输入（不经过剪贴板），最终兜底。BMP 内字符（含中文）均可。</summary>
        private static void TypeText(string text)
        {
            var inputs = new List<INPUT>();
            foreach (char ch in text)
            {
                if (char.IsHighSurrogate(ch)) continue;
                if (ch == '\n') { inputs.Add(Key(VK_RETURN, false)); inputs.Add(Key(VK_RETURN, true)); continue; }
                inputs.Add(Key(ch, false));
                inputs.Add(Key(ch, true));
            }
            if (inputs.Count > 0) SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        }

        private static INPUT Key(char ch, bool keyUp)
        {
            uint flags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0);
            return new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = flags } } };
        }

        private static INPUT Key(ushort k, bool keyUp)
        {
            uint flags = keyUp ? KEYEVENTF_KEYUP : 0;
            return new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = k, dwFlags = flags } } };
        }

        private const ushort VK_RETURN = 0x0D;
        private const uint KEYEVENTF_UNICODE = 0x04;
        private const uint INPUT_KEYBOARD = 1;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public INPUTUNION u; }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }
    }
}