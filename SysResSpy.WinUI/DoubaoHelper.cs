using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SysResSpy.WinUI
{
    /// <summary>
    /// Sends a prompt to the local Doubao desktop client, mirroring the approach
    /// used by the DirSize project: find/focus (auto-launching if possible) the
    /// Doubao window, then type the text via clipboard paste (most reliable for
    /// Electron apps) with a SendInput fallback. No WPF UIA dependency is needed.
    /// </summary>
    internal static class DoubaoHelper
    {
        private static readonly string[] NameTokens = { "doubao", "豆包" };

        #region Win32 P/Invoke
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern bool EmptyClipboard();
        [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
        [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtra);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        private const int SW_RESTORE = 9;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint INPUT_KEYBOARD = 1;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint CF_UNICODETEXT = 13;
        private const byte VK_RETURN = 0x0D;
        private const byte KEY_V = 0x56;
        private const byte KEY_CTRL = 0x11;
        #endregion

        /// <summary>Send the prompt to Doubao and send Enter. Returns a user-facing status message.</summary>
        public static string SendToDoubao(string prompt)
        {
            var hwnd = EnsureWindow();
            if (hwnd == IntPtr.Zero)
                return "未能自动打开豆包，请手动打开豆包后，再点一次“问豆包”。";

            FocusWindow(hwnd);
            Thread.Sleep(300);

            // Click the bottom input area so the focused element is the text box.
            ClickInputArea(hwnd);

            if (TrySetClipboard(prompt))
            {
                SendCtrlV();
                Thread.Sleep(200);
                SendEnter();
                return "已把进程信息发送给豆包，请到豆包窗口查看回复。";
            }

            TypeText(prompt);
            SendEnter();
            return "已把进程信息发送给豆包，请到豆包窗口查看回复。";
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
               && AnyToken(name, StringComparison.OrdinalIgnoreCase);

        private static bool HasDoubaoTitle(Process p)
        {
            try { return AnyToken(p.MainWindowTitle, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static bool AnyToken(string s, StringComparison sc)
        {
            foreach (var t in NameTokens)
                if (s.IndexOf(t, sc) >= 0) return true;
            return false;
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
                dirs = new[]{
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                };
            }
            catch { return false; }

            foreach (var dir in dirs)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                IEnumerable<string> links;
                try { links = Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (var lnk in links)
                {
                    try
                    {
                        if (!AnyToken(Path.GetFileNameWithoutExtension(lnk), StringComparison.OrdinalIgnoreCase)) continue;
                        Process.Start(new ProcessStartInfo { FileName = lnk, UseShellExecute = true });
                        return true;
                    }
                    catch { }
                }
            }
            return false;
        }

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
                        {
                            Marshal.FreeHGlobal(hMem);
                            return false;
                        }
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
            keybd_event(KEY_CTRL, 0, 0, UIntPtr.Zero);
            keybd_event(KEY_V, 0, 0, UIntPtr.Zero);
            keybd_event(KEY_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(KEY_CTRL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private static void SendEnter()
        {
            keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
            keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private static void TypeText(string text)
        {
            var inputs = new List<INPUT>();
            foreach (var ch in text ?? "")
            {
                if (char.IsHighSurrogate(ch)) continue;
                if (ch == '\n') { inputs.Add(Key(VK_RETURN, false)); inputs.Add(Key(VK_RETURN, true)); continue; }
                inputs.Add(Key(ch, false));
                inputs.Add(Key(ch, true));
            }
            if (inputs.Count > 0)
                SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
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