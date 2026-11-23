// =============================================================================
//  BhopAssist - tro ly input ben ngoai (ban 2: chay duoc voi GAME THAT)
// -----------------------------------------------------------------------------
//  Cong cu chi lam 2 viec, deu o tang OS:
//     1. Nghe ban phim/chuot bang low-level hook (hoac GetAsyncKeyState)
//     2. Ban input tong hop bang SendInput
//  KHONG doc/ghi bo nho game, KHONG inject DLL, KHONG hook ham cua game,
//  va KHONG lam bat cu gi de an minh truoc anti-cheat.
//
//  CANH BAO: game online (Truy Kich, CrossFire, CS:GO/CS2 co VAC, ...) coi
//  macro nhay la gian lan. Tool nay khong che giau gi ca - chay o do la tu
//  chiu rui ro khoa tai khoan. Half-Life / CS 1.6 offline, LAN, server rieng,
//  game single-player, va testbed di kem thi thoai mai.
//
//  DIEM MOI SO VOI BAN 1 (day la ly do ban 1 khong an tren game that):
//     * Hai MODEL DOC INPUT: latch (GoldSrc/Source) va poll (DirectInput moi
//       frame). Model poll can hold_ms > 1 frame, khong phai 6 ms.
//     * Profile theo engine, tu nhan dien theo ten process dang focus.
//     * jitter chi an vao khoang NGHI, khong bao gio an vao thoi gian GIU.
//     * Trigger co the doc bang GetAsyncKeyState khi hook bi tu choi.
//     * Phat hien game chay Administrator + tu chay lai bang quyen admin.
//     * --watch  : do ten process/tieu de cua so game dang focus.
//     * --probe  : mo phong game doc input o N fps, bao thang la an hay truot.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace ExternalAssist
{
    // =========================================================================
    //  PHAN 1: Win32 interop
    // =========================================================================
    internal static class Native
    {
        public const int WH_KEYBOARD_LL = 13;
        public const int WH_MOUSE_LL = 14;

        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;

        public const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
        public const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
        public const int WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C;
        public const int WM_MOUSEWHEEL = 0x020A;

        // Bit bao "input nay do phan mem ban ra, khong phai nguoi go".
        // Ta dung no de tool khong tu kich hoat chinh minh (feedback loop).
        public const uint LLKHF_INJECTED = 0x00000010;
        public const uint LLMHF_INJECTED = 0x00000001;

        public const uint INPUT_MOUSE = 0;
        public const uint INPUT_KEYBOARD = 1;

        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_SCANCODE = 0x0008;

        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
        public const uint MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100;
        public const uint MOUSEEVENTF_WHEEL = 0x0800;

        public const uint MAPVK_VK_TO_VSC = 0;
        public const int WHEEL_DELTA = 120;

        // Chu ky rieng nhet vao dwExtraInfo -> nhan dien input do chinh ta ban ra.
        public static readonly IntPtr SIGNATURE = new IntPtr(0x1BADB002);

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int nExitCode);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        // Nang do phan giai timer he thong len 1ms - bat buoc neu muon nhip chinh xac.
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint uPeriod);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint uPeriod);
    }

    // =========================================================================
    //  PHAN 2: Mo ta mot "nut" (phim, nut chuot, hoac lan chuot)
    // =========================================================================
    internal enum BindKind { None, Key, MouseButton, Wheel }

    internal struct Bind
    {
        public BindKind Kind;
        public int Code;      // Key: VK code | MouseButton: 1..5 | Wheel: +1 len, -1 xuong
        public string Name;

        public bool IsNone { get { return Kind == BindKind.None; } }

        // VK tuong ung de doc bang GetAsyncKeyState (chi phim va nut chuot).
        public int PollVk
        {
            get
            {
                if (Kind == BindKind.Key) return Code;
                if (Kind == BindKind.MouseButton)
                {
                    switch (Code)
                    {
                        case 1: return 0x01;  // VK_LBUTTON
                        case 2: return 0x02;  // VK_RBUTTON
                        case 3: return 0x04;  // VK_MBUTTON
                        case 4: return 0x05;  // VK_XBUTTON1
                        case 5: return 0x06;  // VK_XBUTTON2
                    }
                }
                return 0;
            }
        }

        public override string ToString() { return Name == null ? "(khong)" : Name; }
    }

    internal static class Keys
    {
        private static readonly Dictionary<string, int> Vk = BuildTable();

        private static Dictionary<string, int> BuildTable()
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            d["SPACE"] = 0x20; d["ENTER"] = 0x0D; d["TAB"] = 0x09; d["ESC"] = 0x1B;
            d["BACKSPACE"] = 0x08; d["CAPSLOCK"] = 0x14;
            d["SHIFT"] = 0x10; d["CTRL"] = 0x11; d["ALT"] = 0x12;
            d["LSHIFT"] = 0xA0; d["RSHIFT"] = 0xA1;
            d["LCTRL"] = 0xA2; d["RCTRL"] = 0xA3;
            d["LALT"] = 0xA4; d["RALT"] = 0xA5;
            d["LEFT"] = 0x25; d["UP"] = 0x26; d["RIGHT"] = 0x27; d["DOWN"] = 0x28;
            d["INSERT"] = 0x2D; d["DELETE"] = 0x2E; d["HOME"] = 0x24; d["END"] = 0x23;
            d["PAGEUP"] = 0x21; d["PAGEDOWN"] = 0x22;
            for (int i = 1; i <= 12; i++) d["F" + i] = 0x6F + i;              // F1=0x70
            for (char c = 'A'; c <= 'Z'; c++) d[c.ToString()] = c;            // 'A'=0x41
            for (char c = '0'; c <= '9'; c++) d[c.ToString()] = c;            // '0'=0x30
            for (int i = 0; i <= 9; i++) d["NUM" + i] = 0x60 + i;
            return d;
        }

        public static Bind FromVk(int vk, string name)
        {
            var b = new Bind();
            b.Kind = BindKind.Key; b.Code = vk; b.Name = name;
            return b;
        }

        public static Bind Parse(string s)
        {
            var b = new Bind();
            b.Kind = BindKind.None;
            if (string.IsNullOrEmpty(s)) return b;
            s = s.Trim();
            b.Name = s.ToUpperInvariant();
            if (b.Name == "NONE" || b.Name == "-") { b.Kind = BindKind.None; return b; }

            if (b.Name == "MWHEELUP") { b.Kind = BindKind.Wheel; b.Code = +1; return b; }
            if (b.Name == "MWHEELDOWN") { b.Kind = BindKind.Wheel; b.Code = -1; return b; }

            if (b.Name.StartsWith("MOUSE"))
            {
                int n;
                if (int.TryParse(b.Name.Substring(5), out n) && n >= 1 && n <= 5)
                { b.Kind = BindKind.MouseButton; b.Code = n; return b; }
            }

            int vk;
            if (Vk.TryGetValue(b.Name, out vk)) { b.Kind = BindKind.Key; b.Code = vk; return b; }

            throw new FormatException("Khong hieu ten phim: '" + s + "'");
        }
    }

    // =========================================================================
    //  PHAN 3: PROFILE THEO ENGINE
    // -------------------------------------------------------------------------
    //  Day la mau chot cua ban 2. Hai kieu game hoan toan khac nhau:
    //
    //  LATCH  - GoldSrc (Half-Life, CS 1.6), Source (HL2, CS:S, CS:GO, CS2).
    //           Ban phim di qua WM_KEYDOWN/WM_KEYUP xep hang trong message
    //           queue. Engine dat bit "vua bam" (impulse) va bit do song den
    //           luc tao usercmd tiep theo moi bi xoa. => mot cu bam 6 ms nam
    //           tron giua 2 frame VAN duoc tinh. Ban nhanh la an.
    //
    //  POLL   - rat nhieu game FPS F2P (LithTech, mot so Unreal build, cac ban
    //           localize kieu CrossFire / Truy Kich) doc ban phim bang
    //           DirectInput8 GetDeviceState() mot lan moi frame. Do la anh
    //           chup TRANG THAI, khong phai hang doi su kien. Phim xuong roi
    //           len tron ven giua 2 lan poll = KHONG TON TAI.
    //           => hold_ms bat buoc phai > 1 frame, va khoang nghi cung vay
    //              (de engine kip thay phim da nha ra ma re-arm cu nhay sau).
    //
    //  Ban 1 chi cai dat duoc LATCH. Do la ly do no chay ngot tren testbed
    //  (testbed doc o tang message) ma cam nhu thoc tren cac game kia.
    // =========================================================================
    internal sealed class GameProfile
    {
        public string Key;          // ten dung trong config
        public string Label;        // hien ra man hinh
        public string[] Exe;        // ten process (khong .exe), so khop CHINH XAC
        public bool Polled;         // true = game doc input kieu polling
        public double GameFps;      // frame rate game dang chay (quan trong khi Polled)
        public double EmitHz;       // dung khi !Polled
        public double HoldMs;       // dung khi !Polled
        public double JitterPct;
        public bool Scancode;
        public string Hint;

        private static readonly GameProfile[] Table = BuildTable();

        private static GameProfile[] BuildTable()
        {
            return new GameProfile[]
            {
                new GameProfile {
                    Key = "testbed", Label = "BhopTestbed (game cua ban)",
                    Exe = new string[] { "bhoptestbed" },
                    Polled = false, GameFps = 64, EmitHz = 120, HoldMs = 5,
                    JitterPct = 12, Scancode = true,
                    Hint = "Doc input o tang message -> giong engine that. Dung de tune truoc."
                },
                new GameProfile {
                    Key = "goldsrc", Label = "GoldSrc (Half-Life, CS 1.6, DoD, Sven)",
                    Exe = new string[] { "hl", "cstrike", "czero", "dod", "ricochet", "svencoop", "hlds", "cs" },
                    Polled = false, GameFps = 100, EmitHz = 150, HoldMs = 4,
                    JitterPct = 8, Scancode = true,
                    Hint = "WM_KEYDOWN co bit impulse song den usercmd sau -> cu bam ngan van an. Chay fps_max 250? dat emit_hz = 250."
                },
                new GameProfile {
                    Key = "source", Label = "Source / Source 2 (HL2, CS:S, CS:GO, CS2, L4D2)",
                    Exe = new string[] { "hl2", "csgo", "cs2", "portal2", "left4dead2", "garrysmod", "bms", "tf_win64", "tf" },
                    Polled = false, GameFps = 120, EmitHz = 170, HoldMs = 3.5,
                    JitterPct = 8, Scancode = true,
                    Hint = "Giong GoldSrc. CHU Y: co VAC. Chi dung offline / server rieng."
                },
                new GameProfile {
                    Key = "poll60", Label = "Game doc DirectInput moi frame @ 60 fps",
                    Exe = new string[0],
                    Polled = true, GameFps = 60, EmitHz = 0, HoldMs = 0,
                    JitterPct = 6, Scancode = true,
                    Hint = "Kieu Truy Kich / CrossFire / LithTech. hold & nghi deu phai > 16.7 ms."
                },
                new GameProfile {
                    Key = "poll100", Label = "Game doc DirectInput moi frame @ 100 fps",
                    Exe = new string[0],
                    Polled = true, GameFps = 100, EmitHz = 0, HoldMs = 0,
                    JitterPct = 6, Scancode = true,
                    Hint = "Cung kieu tren nhung may chay 100 fps -> nhip nhanh hon."
                },
                new GameProfile {
                    Key = "poll144", Label = "Game doc DirectInput moi frame @ 144 fps",
                    Exe = new string[0],
                    Polled = true, GameFps = 144, EmitHz = 0, HoldMs = 0,
                    JitterPct = 6, Scancode = true,
                    Hint = "Cung kieu tren, may manh."
                },
            };
        }

        public static GameProfile ByKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            foreach (GameProfile p in Table)
                if (string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        // So khop CHINH XAC ten process, khong dung Contains: "hl" khong duoc
        // nuot mat "hl2", ma "cs" khong duoc nuot "csgo".
        public static GameProfile ByProcess(string procName)
        {
            if (string.IsNullOrEmpty(procName)) return null;
            foreach (GameProfile p in Table)
                foreach (string e in p.Exe)
                    if (string.Equals(e, procName, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        public static IEnumerable<GameProfile> All { get { return Table; } }

        public GameProfile Clone()
        {
            return (GameProfile)MemberwiseClone();
        }
    }

    // =========================================================================
    //  PHAN 4: NHIP BAN - tach ro thoi gian GIU va thoi gian NGHI
    // -------------------------------------------------------------------------
    //  Ban 1 chi co "emit_hz + hold_ms" roi nhan jitter len CA chu ky. O model
    //  poll, jitter am se keo hold xuong duoi 1 frame -> cu nhay bien mat.
    //  Ban 2: jitter CHI an vao khoang nghi. Thoi gian giu la bat kha xam pham.
    // =========================================================================
    internal sealed class Pattern
    {
        public double HoldMs;      // giu phim bao lau
        public double GapMs;       // nha phim bao lau truoc nhip sau
        public double JitterPct;   // chi ap len GapMs
        public bool Polled;
        public double GameFps;
        public string Why;         // giai thich ngan de in ra banner

        public bool WheelOnly;
        public double PeriodMs { get { return HoldMs + GapMs; } }
        public double Hz { get { return 1000.0 / PeriodMs; } }

        // Frame cua game (ms). 0 neu khong biet.
        public double FrameMs { get { return GameFps > 0 ? 1000.0 / GameFps : 0; } }

        public static Pattern Build(GameProfile p, double pollMargin,
                                    double overrideHz, double overrideHold, double overrideJitter)
        {
            var pat = new Pattern();
            pat.Polled = p.Polled;
            pat.GameFps = p.GameFps;
            pat.JitterPct = overrideJitter >= 0 ? overrideJitter : p.JitterPct;

            if (p.Polled)
            {
                // Dieu kien song con: hold > 1 frame VA nghi > 1 frame.
                // Nhan them bien do an toan vi frame time cua game khong deu.
                double frame = 1000.0 / Math.Max(15.0, p.GameFps);
                double unit = frame * pollMargin;
                pat.HoldMs = overrideHold > 0 ? Math.Max(overrideHold, frame * 1.05) : unit;
                pat.GapMs = unit;
                if (overrideHz > 0)
                {
                    // Nguoi dung ep nhip: van khong cho tut duoi nguong 1 frame.
                    double period = Math.Max(1000.0 / overrideHz, (frame * 1.05) * 2);
                    pat.GapMs = Math.Max(frame * 1.05, period - pat.HoldMs);
                }
                // Jitter khong duoc keo gap xuong duoi 1 frame.
                double worstGap = pat.GapMs * (1.0 - pat.JitterPct / 100.0);
                if (worstGap < frame * 1.05 && pat.JitterPct > 0)
                    pat.JitterPct = Math.Max(0, (1.0 - (frame * 1.05) / pat.GapMs) * 100.0);

                pat.Why = string.Format(CultureInfo.InvariantCulture,
                    "game poll moi {0:0.0} ms -> giu {1:0.0} ms va nghi {2:0.0} ms deu phai vuot 1 frame",
                    frame, pat.HoldMs, pat.GapMs);
            }
            else
            {
                double hz = overrideHz > 0 ? overrideHz : p.EmitHz;
                hz = Clamp(hz, 5, 400);
                double period = 1000.0 / hz;
                double hold = overrideHold > 0 ? overrideHold : p.HoldMs;
                if (hold > period * 0.6) hold = period * 0.6;   // phai kip nha ra
                pat.HoldMs = hold;
                pat.GapMs = period - hold;
                pat.Why = string.Format(CultureInfo.InvariantCulture,
                    "engine latch cu bam -> chi can nhip {0:0} Hz phu kin moi frame ({1:0.0} ms/frame)",
                    hz, p.GameFps > 0 ? 1000.0 / p.GameFps : 0);
            }
            return pat;
        }

        private static double Clamp(double v, double lo, double hi)
        { return v < lo ? lo : (v > hi ? hi : v); }

        public string Describe()
        {
            if (WheelOnly)
                return string.Format(CultureInfo.InvariantCulture,
                    "{0:0.0} xung lan/giay  (moi {1:0.0} ms mot xung), jitter {2:0}%" +
                    "   - xung roi rac, khong co pha giu nen khong bi tran boi frame time",
                    Hz, PeriodMs, JitterPct);
            return string.Format(CultureInfo.InvariantCulture,
                "giu {0:0.0} ms + nghi {1:0.0} ms = {2:0.0} ms/nhip ({3:0.0} Hz), jitter {4:0}% (chi len khoang nghi)",
                HoldMs, GapMs, PeriodMs, Hz, JitterPct);
        }
    }

    // =========================================================================
    //  PHIM KEM - ban them mot phim thu hai cung nhip voi phim nhay
    // -------------------------------------------------------------------------
    //  Off   khong dung
    //  Hold  bam MOT lan luc bat, giu suot ca phien, nha khi tat.
    //        Dung cho phim TRANG THAI: sprint/walk (SHIFT o hau het game Unity),
    //        vi nhap nhay no theo tung nhip nhay se bat/tat sprint lien tuc.
    //  With  xuong/len cung nhip voi phim nhay. Dung khi game doi hai phim phai
    //        cung o trang thai xuong tai dung thoi diem xu ly cu nhay.
    //  After bam SAU phim nhay combo_offset_ms mili giay.
    //        Day moi la CROUCH-JUMP that: nhay truoc, co chan sau -> len cao hon.
    //        GoldSrc/Source: combo_key = LCTRL, combo_offset_ms = 25.
    // =========================================================================
    internal enum ComboMode { Off, Hold, With, After }

    // =========================================================================
    //  PHAN 5: Config
    // =========================================================================
    internal sealed class Config
    {
        public Bind TriggerKey;
        public Bind PanicKey;
        public bool ToggleMode;
        public string TriggerSource;   // "hook" | "poll" | "auto"

        public string EmitMode;        // "key" | "wheel" | "both"
        public Bind EmitKey;
        public string SendMode;        // "scancode" | "vk" | "both"
        public string WheelDirMode;    // "up" | "down" | "both"

        public Bind ComboKey;
        public ComboMode Combo;
        public double ComboOffsetMs;
        public double ComboHoldMs;     // 0 = dung luon hold cua phim nhay

        public string ProfileKey;      // "auto" | ten profile | "custom"
        public double PollMargin;
        public double GameFpsOverride;
        public double EmitHzOverride;  // 0 = theo profile
        public double HoldMsOverride;  // 0 = theo profile
        public double JitterOverride;  // <0 = theo profile

        public string[] TargetProcess; // rong = moi cua so
        public bool RequireFocus;
        public bool Verbose;

        public int WheelDirValue(int n) // n = so lan da lan, dung cho che do "both"
        {
            if (WheelDirMode == "up") return +1;
            if (WheelDirMode == "down") return -1;
            return (n % 2 == 0) ? -1 : +1;
        }

        public static Config Load(string path)
        {
            var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(path))
            {
                foreach (string lineRaw in File.ReadAllLines(path))
                {
                    string line = lineRaw;
                    int hash = line.IndexOf('#');
                    if (hash >= 0) line = line.Substring(0, hash);
                    int semi = line.IndexOf(';');
                    if (semi >= 0) line = line.Substring(0, semi);
                    line = line.Trim();
                    if (line.Length == 0) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    raw[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }

            var c = new Config();
            c.TriggerKey = Keys.Parse(Str(raw, "trigger_key", "SPACE"));
            c.PanicKey = Keys.Parse(Str(raw, "panic_key", "F12"));
            c.ToggleMode = Str(raw, "trigger_mode", "hold").Equals("toggle", StringComparison.OrdinalIgnoreCase);
            c.TriggerSource = Str(raw, "trigger_source", "auto").ToLowerInvariant();

            c.EmitMode = Str(raw, "emit", "key").ToLowerInvariant();
            c.EmitKey = Keys.Parse(Str(raw, "emit_key", "SPACE"));
            c.SendMode = Str(raw, "send_mode", Bool(raw, "use_scancode", true) ? "scancode" : "vk").ToLowerInvariant();
            c.WheelDirMode = Str(raw, "wheel_dir", "down").ToLowerInvariant();

            c.ComboKey = Keys.Parse(Str(raw, "combo_key", "NONE"));
            string cm = Str(raw, "combo_mode", "hold").ToLowerInvariant();
            if (c.ComboKey.Kind == BindKind.Wheel || c.ComboKey.Kind == BindKind.MouseButton)
                throw new FormatException("combo_key chi nhan phim ban phim (vd LSHIFT, LCTRL). " +
                                          "Lan chuot la xung roi rac nen khong co trang thai giu, " +
                                          "con nut chuot thi phai ban bang duong MOUSEINPUT khac.");
            c.Combo = c.ComboKey.IsNone ? ComboMode.Off
                    : (cm == "with" ? ComboMode.With
                    : (cm == "after" ? ComboMode.After : ComboMode.Hold));
            c.ComboOffsetMs = Clamp(Num(raw, "combo_offset_ms", 25), 0, 500);
            c.ComboHoldMs = Clamp(Num(raw, "combo_hold_ms", 0), 0, 500);

            c.ProfileKey = Str(raw, "profile", "auto").ToLowerInvariant();
            c.PollMargin = Clamp(Num(raw, "poll_margin", 1.35), 1.05, 3.0);
            c.GameFpsOverride = Num(raw, "game_fps", 0);
            c.EmitHzOverride = Num(raw, "emit_hz", 0);
            c.HoldMsOverride = Num(raw, "hold_ms", 0);
            c.JitterOverride = Num(raw, "jitter_pct", -1);

            string tp = Str(raw, "target_process", "");
            var list = new List<string>();
            foreach (string s in tp.Split(','))
            {
                string t = s.Trim();
                if (t.Length > 0)
                {
                    if (t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) t = t.Substring(0, t.Length - 4);
                    list.Add(t);
                }
            }
            c.TargetProcess = list.ToArray();
            c.RequireFocus = Bool(raw, "require_focus", true);
            c.Verbose = Bool(raw, "verbose", true);
            return c;
        }

        private static string Str(Dictionary<string, string> d, string k, string dflt)
        { string v; return d.TryGetValue(k, out v) && v.Length > 0 ? v : dflt; }

        private static double Num(Dictionary<string, string> d, string k, double dflt)
        {
            string v; double r;
            if (d.TryGetValue(k, out v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return r;
            return dflt;
        }

        private static bool Bool(Dictionary<string, string> d, string k, bool dflt)
        {
            string v;
            if (!d.TryGetValue(k, out v)) return dflt;
            v = v.Trim().ToLowerInvariant();
            return v == "1" || v == "true" || v == "yes" || v == "on";
        }

        private static double Clamp(double v, double lo, double hi)
        { return v < lo ? lo : (v > hi ? hi : v); }

        // Profile khoi diem khi chua nhan dien duoc game nao.
        public GameProfile BaseProfile()
        {
            GameProfile p = GameProfile.ByKey(ProfileKey);
            if (p == null) p = GameProfile.ByKey("goldsrc");
            p = p.Clone();
            if (GameFpsOverride > 0) p.GameFps = GameFpsOverride;
            return AdaptForEmit(p);
        }

        // Luat "phai giu qua 1 frame" chi ap cho PHIM, vi phim co trang thai va
        // game chup trang thai do moi frame - bam xuong roi len gon giua hai lan
        // chup thi coi nhu chua bam.
        //
        // Lan chuot khac han: no la XUNG ROI RAC, khong co trang thai giu. Game
        // buoc phai cong don delta giua hai frame (Unity: Input.mouseScrollDelta),
        // nen mot xung ban ra o BAT KY thoi diem nao cung duoc doc o frame ke
        // tiep. Khong the "lot qua khe". => emit = wheel thi dung model LATCH,
        // va nhip khong con bi tran boi frame time cua game.
        public GameProfile AdaptForEmit(GameProfile p)
        {
            if (EmitMode != "wheel" || !p.Polled) return p;
            p.Polled = false;
            if (p.EmitHz <= 0) p.EmitHz = 200;   // profile poll* khong dat EmitHz
            if (p.HoldMs <= 0) p.HoldMs = 3;     // khong dung cho wheel, chi de Pattern hop le
            return p;
        }

        public bool AutoProfile { get { return ProfileKey == "auto"; } }
    }

    // =========================================================================
    //  PHAN 6: Ban input tong hop bang SendInput
    // =========================================================================
    internal sealed class InputEmitter
    {
        private readonly Config _cfg;
        private readonly Bind _key;
        private readonly ushort _scan;
        private readonly bool _extended;
        private int _wheelCount;

        public InputEmitter(Config cfg) : this(cfg, cfg.EmitKey) { }

        public InputEmitter(Config cfg, Bind key)
        {
            _cfg = cfg;
            _key = key;
            _scan = (ushort)Native.MapVirtualKey((uint)key.Code, Native.MAPVK_VK_TO_VSC);
            int vk = key.Code;
            _extended = vk == 0x25 || vk == 0x26 || vk == 0x27 || vk == 0x28 ||
                        vk == 0x2D || vk == 0x2E || vk == 0x24 || vk == 0x23 ||
                        vk == 0x21 || vk == 0x22 || vk == 0xA3 || vk == 0xA5;
        }

        public ushort ScanCode { get { return _scan; } }
        public Bind Key { get { return _key; } }

        private Native.INPUT KeyInput(bool up)
        {
            var inp = new Native.INPUT();
            inp.type = Native.INPUT_KEYBOARD;

            // scancode : wVk = 0, Windows tu suy VK tu scancode theo layout.
            //            Bat buoc voi game doc DirectInput / Raw Input.
            // vk       : gui virtual-key thuan, hop voi game doc WM_KEYDOWN.
            // both     : gui ca hai truong. Mot so engine doc wScan trong
            //            lParam nen can no dung, dong thoi van muon wVk.
            switch (_cfg.SendMode)
            {
                case "vk":
                    inp.U.ki.wVk = (ushort)_key.Code;
                    inp.U.ki.wScan = 0;
                    inp.U.ki.dwFlags = 0;
                    break;
                case "both":
                    inp.U.ki.wVk = (ushort)_key.Code;
                    inp.U.ki.wScan = _scan;
                    inp.U.ki.dwFlags = 0;
                    break;
                default: // scancode
                    inp.U.ki.wVk = 0;
                    inp.U.ki.wScan = _scan;
                    inp.U.ki.dwFlags = Native.KEYEVENTF_SCANCODE;
                    break;
            }
            if (_extended) inp.U.ki.dwFlags |= Native.KEYEVENTF_EXTENDEDKEY;
            if (up) inp.U.ki.dwFlags |= Native.KEYEVENTF_KEYUP;
            inp.U.ki.time = 0;
            inp.U.ki.dwExtraInfo = Native.SIGNATURE;
            return inp;
        }

        private Native.INPUT WheelInput(int dir)
        {
            var inp = new Native.INPUT();
            inp.type = Native.INPUT_MOUSE;
            inp.U.mi.mouseData = unchecked((uint)(dir * Native.WHEEL_DELTA));
            inp.U.mi.dwFlags = Native.MOUSEEVENTF_WHEEL;
            inp.U.mi.dwExtraInfo = Native.SIGNATURE;
            return inp;
        }

        public void KeyDown() { Send(KeyInput(false)); }
        public void KeyUp() { Send(KeyInput(true)); }
        public void Wheel() { Send(WheelInput(_cfg.WheelDirValue(_wheelCount++))); }

        public static long Failures;
        public static int LastError;

        private static void Send(Native.INPUT i)
        {
            var arr = new Native.INPUT[] { i };
            uint n = Native.SendInput(1, arr, Marshal.SizeOf(typeof(Native.INPUT)));
            if (n == 0)
            {
                int err = Marshal.GetLastWin32Error();
                long nth = Interlocked.Increment(ref Failures);
                LastError = err;
                // ERROR_ACCESS_DENIED (5) = UIPI: cua so dich chay o muc toan
                // ven cao hon tool. Gan nhu luon la "game chay Administrator".
                if (nth == 1)
                {
                    if (err == 5)
                        Program.Warn("SendInput bi UIPI chan (error 5). Game dang chay quyen cao hon tool. " +
                                     "Chay lai: BhopAssist.exe --admin");
                    else
                        Program.Warn("SendInput bi tu choi (win32 error " + err + ").");
                }
            }
        }
    }

    // =========================================================================
    //  PHAN 7: Runtime hien hanh (doi duoc nong khi doi game)
    // =========================================================================
    internal sealed class Runtime
    {
        public GameProfile Profile;
        public Pattern Pat;
        public string Source;   // "config" | "tu nhan dien"
    }

    // =========================================================================
    //  PHAN 8: Chuong trinh chinh
    // =========================================================================
    internal static class Program
    {
        private static Config _cfg;
        private static InputEmitter _emit;
        private static InputEmitter _combo;
        private static Runtime _rt;

        private static IntPtr _hKb = IntPtr.Zero, _hMs = IntPtr.Zero;
        // Giu tham chieu delegate o field static, neu khong GC se don mat -> crash ngau nhien.
        private static Native.HookProc _kbProc, _msProc;
        private static bool _hookOk;
        private static bool _pollTrigger;

        private static volatile bool _running = true;
        private static volatile bool _active = false;
        private static volatile bool _keyIsDown = false;
        private static volatile bool _comboIsDown = false;
        // Trang thai VAT LY cua trigger, de loc auto-repeat o che do toggle.
        private static bool _trigKeyDown = false;
        private static int _trigLastDownMs = 0;
        private static long _emitCount = 0;
        private static readonly object _con = new object();

        // Cache ket qua kiem tra focus, tranh goi Win32 lien tuc trong vong lap nhip.
        private static readonly Stopwatch _focusSw = Stopwatch.StartNew();
        private static double _focusCheckedAt = -1000;
        private static bool _focusOk = false;
        private static string _fgProc = "";
        private static int _selfPid;

        private static int Main(string[] args)
        {
            Console.Title = "BhopAssist - external input assist";
            _selfPid = Process.GetCurrentProcess().Id;

            string cfgPath = null;
            bool selftest = false, watch = false, probe = false, admin = false, listProfiles = false;
            double selftestMs = 3000, probeFps = 0;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)) selftest = true;
                else if (a.Equals("--watch", StringComparison.OrdinalIgnoreCase)) watch = true;
                else if (a.Equals("--probe", StringComparison.OrdinalIgnoreCase)) probe = true;
                else if (a.Equals("--admin", StringComparison.OrdinalIgnoreCase)) admin = true;
                else if (a.Equals("--profiles", StringComparison.OrdinalIgnoreCase)) listProfiles = true;
                else if (a.Equals("--help", StringComparison.OrdinalIgnoreCase) || a == "-h" || a == "/?")
                { Usage(); return 0; }
                else
                {
                    double v;
                    if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    {
                        if (selftest) selftestMs = v;
                        else if (probe) probeFps = v;
                    }
                    else if (cfgPath == null) cfgPath = a;
                }
            }

            if (admin && !IsElevated()) return Relaunch(args);

            if (cfgPath == null)
                cfgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");

            try { _cfg = Config.Load(cfgPath); }
            catch (Exception ex) { Console.Error.WriteLine("Config loi: " + ex.Message); return 2; }

            if (listProfiles) { ListProfiles(); return 0; }
            if (watch) return Watch();

            _rt = new Runtime();
            _rt.Profile = _cfg.BaseProfile();
            _rt.Pat = BuildPattern(_rt.Profile);
            _rt.Source = _cfg.AutoProfile ? "mac dinh (dang cho nhan dien)" : "config";

            _emit = new InputEmitter(_cfg);
            if (_cfg.Combo != ComboMode.Off) _combo = new InputEmitter(_cfg, _cfg.ComboKey);

            if (probe) return Probe(probeFps);
            if (selftest) return SelfTest(selftestMs);

            Banner(cfgPath);
            PreflightWarnings();

            _kbProc = KeyboardProc;
            _msProc = MouseProc;
            IntPtr hMod = Native.GetModuleHandle(null);

            _hKb = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbProc, hMod, 0);
            _hookOk = _hKb != IntPtr.Zero;
            if (!_hookOk)
                Warn("Khong dat duoc keyboard hook (" + Marshal.GetLastWin32Error() +
                     "). Se chuyen sang doc trigger bang GetAsyncKeyState.");

            bool needMouseHook = _cfg.TriggerKey.Kind == BindKind.Wheel ||
                                 _cfg.PanicKey.Kind == BindKind.MouseButton ||
                                 (_cfg.TriggerKey.Kind == BindKind.MouseButton && _cfg.TriggerSource != "poll");
            if (needMouseHook)
            {
                _hMs = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _msProc, hMod, 0);
                if (_hMs == IntPtr.Zero)
                    Warn("Khong dat duoc mouse hook: " + Marshal.GetLastWin32Error());
            }

            _pollTrigger = DecideTriggerSource();

            var emitThread = new Thread(EmitLoop);
            emitThread.IsBackground = true;
            emitThread.Priority = ThreadPriority.AboveNormal;
            emitThread.Start();

            if (_pollTrigger)
            {
                var trigThread = new Thread(TriggerPollLoop);
                trigThread.IsBackground = true;
                trigThread.Priority = ThreadPriority.AboveNormal;
                trigThread.Start();
            }

            var statThread = new Thread(StatLoop);
            statThread.IsBackground = true;
            statThread.Start();

            if (_hookOk || _hMs != IntPtr.Zero)
            {
                // Low-level hook chi duoc goi qua message pump cua thread da dat hook.
                Native.MSG msg;
                while (_running && Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
            }
            else
            {
                while (_running) Thread.Sleep(50);
            }

            _running = false;
            _active = false;
            SafeRelease();
            if (_hKb != IntPtr.Zero) Native.UnhookWindowsHookEx(_hKb);
            if (_hMs != IntPtr.Zero) Native.UnhookWindowsHookEx(_hMs);
            Console.WriteLine();
            Console.WriteLine("Da thoat. Tong so nhip da ban: " + Interlocked.Read(ref _emitCount));
            return 0;
        }

        // ---------------------------------------------------------------------
        private static void Usage()
        {
            Console.WriteLine("BhopAssist [config.ini] [che do]");
            Console.WriteLine();
            Console.WriteLine("  --watch            do ten process + tieu de cua so dang focus (tim ten exe cua game)");
            Console.WriteLine("  --probe [fps]      mo phong game doc input o fps nay, bao nhip hien tai an hay truot");
            Console.WriteLine("  --selftest [ms]    do do chinh xac cua nhip tren may nay (ban phim F24 vo hai)");
            Console.WriteLine("  --profiles         liet ke profile theo engine");
            Console.WriteLine("  --admin            chay lai bang quyen Administrator (khi game chay admin)");
            Console.WriteLine("  --help");
        }

        private static void ListProfiles()
        {
            Console.WriteLine("PROFILE THEO ENGINE");
            Console.WriteLine("===================================================================");
            foreach (GameProfile p in GameProfile.All)
            {
                Pattern pat = Pattern.Build(p, _cfg.PollMargin, 0, 0, -1);
                Console.WriteLine();
                Console.WriteLine("  " + p.Key + "   -   " + p.Label);
                Console.WriteLine("    model    : " + (p.Polled ? "POLL (doc trang thai moi frame)" : "LATCH (co hang doi su kien)"));
                Console.WriteLine("    nhip     : " + pat.Describe());
                if (p.Exe.Length > 0) Console.WriteLine("    exe      : " + string.Join(", ", p.Exe));
                Console.WriteLine("    ghi chu  : " + p.Hint);
            }
            Console.WriteLine();
            Console.WriteLine("Dat trong config:  profile = <ten>    (hoac  profile = auto  de tu nhan dien)");
        }

        private static Pattern BuildPattern(GameProfile p)
        {
            Pattern pat = Pattern.Build(p, _cfg.PollMargin, _cfg.EmitHzOverride, _cfg.HoldMsOverride, _cfg.JitterOverride);
            pat.WheelOnly = (_cfg.EmitMode == "wheel");
            return pat;
        }

        private static void Banner(string cfgPath)
        {
            Console.WriteLine("================================================================");
            Console.WriteLine(" BhopAssist - tro ly input ben ngoai (tang OS, khong dung vao game)");
            Console.WriteLine("================================================================");
            Console.WriteLine(" config : " + cfgPath + (File.Exists(cfgPath) ? "" : "  (khong thay -> dung mac dinh)"));
            Console.WriteLine(" quyen  : " + (IsElevated() ? "Administrator" : "nguoi dung thuong"));
            Console.WriteLine("----------------------------------------------------------------");
            Console.WriteLine(" profile: " + _rt.Profile.Key + "  (" + _rt.Profile.Label + ")");
            Console.WriteLine("          model " + (_rt.Profile.Polled ? "POLL" : "LATCH") + " - " + _rt.Pat.Why);
            Console.WriteLine(" nhip   : " + _rt.Pat.Describe());
            // O che do wheel khong co nguong gay - lan chuot la xung roi rac
            // nen khong the lot khe. Nhung van co TRAN HUU ICH: game doc scroll
            // mot lan moi frame va cong don delta, nen nhieu xung roi cung mot
            // frame se bi gop thanh mot lan doc. Ban nhanh hon fps = phi, chu
            // khong hong.
            if (_rt.Pat.WheelOnly && _rt.Profile.GameFps > 0)
            {
                double fps = _rt.Profile.GameFps;
                double eff = Math.Min(_rt.Pat.Hz, fps);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    " tran   : game doc scroll 1 lan moi frame -> o {0:0} fps, tran huu ich la {0:0} xung/giay",
                    fps));
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "          emit_hz {0:0} => hieu qua ~{1:0}/giay{2}",
                    _rt.Pat.Hz, eff,
                    _rt.Pat.Hz > fps * 1.15
                        ? "   (thua " + (_rt.Pat.Hz - fps).ToString("0", CultureInfo.InvariantCulture) +
                          " xung/giay bi gop mat - khong hong, chi phi)"
                        : (_rt.Pat.Hz < fps * 0.85 ? "   (con du cho de tang emit_hz)" : "   (vua khop)")));
            }

            if (_rt.Profile.Polled && !_rt.Pat.WheelOnly)
            {
                double mp = MinPhaseMs(_rt.Pat);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    " nguong : GAY neu fps trong game tut duoi {0:0}   (pha ngan nhat {1:0.00} ms)",
                    mp > 0 ? 1000.0 / mp : 0, mp));
            }
            Console.WriteLine(" trigger: " + _cfg.TriggerKey + (_cfg.ToggleMode ? "  (toggle)" : "  (giu)"));
            if (_cfg.EmitMode == "wheel")
                Console.WriteLine(" emit   : lan chuot  huong " + _cfg.WheelDirMode +
                                  "   (emit_key khong dung o che do nay)");
            else
                Console.WriteLine(" emit   : " + _cfg.EmitMode + " -> " + _cfg.EmitKey +
                                  "  send_mode=" + _cfg.SendMode +
                                  (_cfg.EmitMode == "both" ? ("  + lan chuot " + _cfg.WheelDirMode) : ""));
            if (_cfg.Combo != ComboMode.Off)
                Console.WriteLine(" kem    : " + _cfg.ComboKey + "  " + ComboDescribe());
            Console.WriteLine(" chi khi: " + (_cfg.RequireFocus
                                  ? ("cua so focus la " + (_cfg.TargetProcess.Length > 0
                                        ? "[" + string.Join(", ", _cfg.TargetProcess) + "]"
                                        : "bat ky (NEN dat target_process)"))
                                  : "luon luon (NGUY HIEM)"));
            Console.WriteLine("----------------------------------------------------------------");
            Console.WriteLine(" " + _cfg.PanicKey + " = thoat ngay lap tuc.");
            Console.WriteLine("----------------------------------------------------------------");
        }

        private static string ComboDescribe()
        {
            double hold = _cfg.ComboHoldMs > 0 ? _cfg.ComboHoldMs : _rt.Pat.HoldMs;
            switch (_cfg.Combo)
            {
                case ComboMode.Hold:
                    return "che do HOLD - bam mot lan luc bat, giu suot ca phien";
                case ComboMode.With:
                    return string.Format(CultureInfo.InvariantCulture,
                        "che do WITH - xuong cung luc voi phim nhay, giu {0:0.0} ms", hold);
                case ComboMode.After:
                    return string.Format(CultureInfo.InvariantCulture,
                        "che do AFTER - xuong sau phim nhay {0:0.0} ms, giu {1:0.0} ms (crouch-jump)",
                        _cfg.ComboOffsetMs, hold);
            }
            return "";
        }

        // Nhung cai khien nguoi ta ngoi ca buoi khong hieu vi sao khong an.
        private static void PreflightWarnings()
        {
            if (_cfg.Combo != ComboMode.Off)
            {
                int vk = _cfg.ComboKey.Code;
                bool isShift = vk == 0x10 || vk == 0xA0 || vk == 0xA1;
                bool isCtrl = vk == 0x11 || vk == 0xA2 || vk == 0xA3;

                if (isShift && !_rt.Profile.Polled)
                    Warn("Trong GoldSrc/Source, SHIFT mac dinh la +speed = DI BO CHAM. No khong lam nhay " +
                         "cao hon, ma con lam ban cham lai va MAT toc do bhop. Nhay cao hon o dong engine " +
                         "nay la crouch-jump:  combo_key = LCTRL, combo_mode = after, combo_offset_ms = 25");

                if (isShift && _cfg.Combo != ComboMode.Hold)
                    Warn("SHIFT hau nhu luon la phim TRANG THAI (sprint/walk). O che do " +
                         _cfg.Combo.ToString().ToUpperInvariant() + " no bi nhap nhay theo tung nhip nhay -> " +
                         "game bat/tat sprint lien tuc. Dung  combo_mode = hold.");

                if (isCtrl && _cfg.Combo == ComboMode.With)
                    Warn("Crouch-jump can co chan SAU khi da roi mat dat. Bam CTRL cung luc voi SPACE thi " +
                         "nhieu engine coi la dang ngoi -> chan luon cu nhay. Dung  combo_mode = after.");

                if (_cfg.Combo == ComboMode.After || _cfg.Combo == ComboMode.With)
                {
                    // Doc lai tu chinh ham ma vong lap ban dung, vi offset/hold
                    // co the da bi kep cho vua chu ky. Canh bao theo so THAT.
                    var ct = new double[4]; var cw = new int[4];
                    int n = BuildCycle(_rt.Pat, ct, cw);
                    double dn = 0, upT = 0;
                    for (int i = 0; i < n; i++)
                    {
                        if (cw[i] == 2) dn = ct[i];
                        else if (cw[i] == 3) upT = ct[i];
                    }
                    double effHold = upT - dn;

                    if (_cfg.Combo == ComboMode.After && dn < _cfg.ComboOffsetMs - 0.01)
                        Warn(string.Format(CultureInfo.InvariantCulture,
                            "combo_offset_ms = {0:0.0} khong lot vao mot chu ky {1:0.0} ms -> da bi kep xuong {2:0.0}. " +
                            "Giam offset, hoac ha nhip (profile cham hon).",
                            _cfg.ComboOffsetMs, _rt.Pat.PeriodMs, dn));

                    double want = _cfg.ComboHoldMs > 0 ? _cfg.ComboHoldMs : _rt.Pat.HoldMs;
                    if (effHold < want - 0.01)
                        Warn(string.Format(CultureInfo.InvariantCulture,
                            "phim kem dat giu {0:0.0} ms nhung khong lot chu ky -> da bi kep xuong {1:0.0} ms.",
                            want, effHold));

                    if (_rt.Profile.Polled && effHold <= _rt.Pat.FrameMs)
                        Warn(string.Format(CultureInfo.InvariantCulture,
                            "phim kem chi giu {0:0.0} ms, khong vuot 1 frame ({1:0.0} ms) cua game kieu POLL " +
                            "-> game se bo sot no y het cu nhay. Can offset + hold <= {2:0.0} ms, " +
                            "hoac ha poll_margin de chu ky rong ra.",
                            effHold, _rt.Pat.FrameMs, _rt.Pat.PeriodMs * 0.9));
                }
            }

            if (_cfg.RequireFocus && _cfg.TargetProcess.Length == 0)
                Warn("target_process de trong -> tool se ban vao BAT KY cua so nao dang focus. " +
                     "Chay 'BhopAssist.exe --watch' de lay ten exe cua game roi dien vao.");

            if (_cfg.TargetProcess.Length == 1 &&
                string.Equals(_cfg.TargetProcess[0], "BhopTestbed", StringComparison.OrdinalIgnoreCase))
                Warn("target_process van dang khoa vao BhopTestbed. Vao game that thi tool se im. " +
                     "Doi sang ten exe cua game.");

            if (!_rt.Profile.Polled && _rt.Pat.HoldMs < 10 && _cfg.ProfileKey == "auto")
                Say("   (dang o nhip LATCH. Neu game khong phan ung, gan nhu chac chan no doc kieu POLL" +
                    " -> dat  profile = poll60  trong config.)");

            if (!IsElevated())
                Say("   (chua chay quyen admin. Neu game chay Administrator, SendInput se bi chan im lang" +
                    " -> chay lai bang  BhopAssist.exe --admin)");
        }

        private static bool DecideTriggerSource()
        {
            // Vi sao KHONG mac dinh dung poll khi trigger_key trung emit_key:
            // cu KEYUP ta ban ra xoa luon trang thai phim trong Win32k, nen
            // GetAsyncKeyState se bao "da nha" du nguoi choi con dang giu.
            // Hook thi khong bi: no thay su kien vat ly va co co injected.
            bool sameKey = _cfg.TriggerKey.Kind == BindKind.Key &&
                           _cfg.EmitKey.Kind == BindKind.Key &&
                           _cfg.TriggerKey.Code == _cfg.EmitKey.Code &&
                           (_cfg.EmitMode == "key" || _cfg.EmitMode == "both");

            if (_cfg.TriggerSource == "hook") return false;
            if (_cfg.TriggerSource == "poll")
            {
                if (sameKey)
                    Warn("trigger_source=poll ma trigger_key trung emit_key. Cu KEYUP tool ban ra se xoa " +
                         "trang thai phim -> trigger tu tat. Doi trigger sang phim khac (vd MOUSE4/MOUSE5) " +
                         "hoac dung trigger_source=hook.");
                if (_cfg.TriggerKey.Kind == BindKind.Wheel)
                { Warn("Lan chuot khong doc duoc bang polling -> van phai dung hook."); return false; }
                return true;
            }
            // auto
            if (!_hookOk && _cfg.TriggerKey.Kind != BindKind.Wheel)
            {
                if (sameKey)
                    Warn("Hook that bai va trigger_key trung emit_key -> khong con cach doc trigger dang tin cay. " +
                         "Doi trigger_key sang phim khac (vd MOUSE5) roi chay lai.");
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------------
        //  Hook callbacks - chay tren main thread, phai TRA VE THAT NHANH.
        //  Cham hon ~300ms la Windows se go hook cua ban ra.
        // ---------------------------------------------------------------------
        private static IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var data = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                bool injected = (data.flags & Native.LLKHF_INJECTED) != 0 || data.dwExtraInfo == Native.SIGNATURE;
                if (!injected)
                {
                    int m = wParam.ToInt32();
                    bool down = (m == Native.WM_KEYDOWN || m == Native.WM_SYSKEYDOWN);
                    bool up = (m == Native.WM_KEYUP || m == Native.WM_SYSKEYUP);
                    int vk = (int)data.vkCode;

                    if (down && _cfg.PanicKey.Kind == BindKind.Key && vk == _cfg.PanicKey.Code)
                    {
                        Say("PANIC - dang thoat...");
                        _running = false; _active = false;
                        SafeRelease();
                        Native.PostQuitMessage(0);
                        return Native.CallNextHookEx(_hKb, nCode, wParam, lParam);
                    }

                    if (!_pollTrigger && _cfg.TriggerKey.Kind == BindKind.Key && vk == _cfg.TriggerKey.Code)
                    {
                        // Low-level hook nhan CA su kien auto-repeat cua Windows
                        // (~30 Hz sau 250 ms giu), va KBDLLHOOKSTRUCT khong co bit
                        // repeat de loc - khac voi lParam cua WM_KEYDOWN von co bit
                        // 30. Khong tu theo doi canh xuong thi o che do toggle, giu
                        // phim 1 giay se bat/tat khoang 20 lan roi dung o trang
                        // thai ngau nhien.
                        if (down)
                        {
                            // Auto-repeat cach nhau ~33 ms. Cach nhau qua 400 ms thi
                            // chac chan la cu bam moi -> van tinh la canh xuong, ke
                            // ca khi ta lo mat su kien keyup (hook bi go tam thoi
                            // luc alt-tab) va _trigKeyDown ket o true. Khong co chot
                            // nay thi toggle chet han cho toi khi khoi dong lai tool.
                            int nowMs = Environment.TickCount;
                            bool firstDown = !_trigKeyDown || (nowMs - _trigLastDownMs) > 400;
                            _trigKeyDown = true;
                            _trigLastDownMs = nowMs;
                            if (_cfg.ToggleMode) { if (firstDown) SetActive(!_active); }
                            else SetActive(true);
                        }
                        else if (up)
                        {
                            _trigKeyDown = false;
                            if (!_cfg.ToggleMode) SetActive(false);
                        }
                    }
                }
            }
            return Native.CallNextHookEx(_hKb, nCode, wParam, lParam);
        }

        private static IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var data = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                bool injected = (data.flags & Native.LLMHF_INJECTED) != 0 || data.dwExtraInfo == Native.SIGNATURE;
                if (!injected)
                {
                    int m = wParam.ToInt32();
                    var t = _cfg.TriggerKey;

                    if (_cfg.PanicKey.Kind == BindKind.MouseButton &&
                        ButtonOf(m, data.mouseData) == _cfg.PanicKey.Code && IsDown(m))
                    {
                        Say("PANIC - dang thoat...");
                        _running = false; _active = false;
                        SafeRelease();
                        Native.PostQuitMessage(0);
                        return Native.CallNextHookEx(_hMs, nCode, wParam, lParam);
                    }

                    if (!_pollTrigger && t.Kind == BindKind.MouseButton)
                    {
                        int btn = ButtonOf(m, data.mouseData);
                        bool down = IsDown(m);
                        if (btn == t.Code && btn != 0)
                        {
                            if (_cfg.ToggleMode) { if (down) SetActive(!_active); }
                            else SetActive(down);
                        }
                    }
                    else if (t.Kind == BindKind.Wheel && m == Native.WM_MOUSEWHEEL)
                    {
                        short delta = (short)((data.mouseData >> 16) & 0xFFFF);
                        int dir = delta > 0 ? +1 : -1;
                        // Lan chuot = xung roi rac. Mot cu lan bat assist trong 1 khoang ngan.
                        if (dir == t.Code) PulseActive(180);
                    }
                }
            }
            return Native.CallNextHookEx(_hMs, nCode, wParam, lParam);
        }

        private static int ButtonOf(int msg, uint mouseData)
        {
            switch (msg)
            {
                case Native.WM_LBUTTONDOWN: case Native.WM_LBUTTONUP: return 1;
                case Native.WM_RBUTTONDOWN: case Native.WM_RBUTTONUP: return 2;
                case Native.WM_MBUTTONDOWN: case Native.WM_MBUTTONUP: return 3;
                case Native.WM_XBUTTONDOWN:
                case Native.WM_XBUTTONUP:
                    return 3 + (int)((mouseData >> 16) & 0xFFFF); // XBUTTON1 -> 4, XBUTTON2 -> 5
            }
            return 0;
        }

        private static bool IsDown(int msg)
        {
            return msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN ||
                   msg == Native.WM_MBUTTONDOWN || msg == Native.WM_XBUTTONDOWN;
        }

        // ---------------------------------------------------------------------
        //  Doc trigger bang GetAsyncKeyState - loi thoat khi hook bi tu choi.
        //  Chi doc trong luc DANG NHA phim, de khong lan voi input ta ban ra.
        // ---------------------------------------------------------------------
        private static void TriggerPollLoop()
        {
            int vk = _cfg.TriggerKey.PollVk;
            int panicVk = _cfg.PanicKey.PollVk;
            if (vk == 0) return;
            bool prev = false;
            while (_running)
            {
                if (panicVk != 0 && (Native.GetAsyncKeyState(panicVk) & 0x8000) != 0)
                {
                    Say("PANIC - dang thoat...");
                    _running = false; _active = false; SafeRelease();
                    Native.PostQuitMessage(0);
                    return;
                }
                if (!_keyIsDown)
                {
                    bool now = (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
                    if (now != prev)
                    {
                        prev = now;
                        if (_cfg.ToggleMode) { if (now) SetActive(!_active); }
                        else SetActive(now);
                    }
                }
                Thread.Sleep(1);
            }
        }

        // ---------------------------------------------------------------------
        private static long _pulseUntilTicks = 0;

        private static void PulseActive(int ms)
        {
            Interlocked.Exchange(ref _pulseUntilTicks, Environment.TickCount + ms);
            SetActive(true);
        }

        private static void SetActive(bool on)
        {
            if (_active == on) return;
            _active = on;
            if (!on) SafeRelease();
            // O che do toggle, biet dang bat hay tat quan trong hon han che do giu
            // (khong co phim nao dang giu de ma biet). Tieu de cua so thay duoc
            // ngay tren taskbar ma khong can alt-tab ra doc log.
            try
            {
                Console.Title = (on ? "[BAT] " : "[tat] ") + "BhopAssist" +
                                (_cfg.ToggleMode ? "  -  bam " + _cfg.TriggerKey + " de doi" : "");
            }
            catch { }
            if (_cfg.Verbose) Say(on ? ">> ON" : "<< OFF");
        }

        // ---------------------------------------------------------------------
        //  Vong lap ban input - thread rieng, nhip chinh xac bang Stopwatch.
        // ---------------------------------------------------------------------
        private static void EmitLoop()
        {
            Native.timeBeginPeriod(1);
            var sw = Stopwatch.StartNew();
            var rnd = new Random(unchecked(Environment.TickCount * 397));
            var evT = new double[4];
            var evW = new int[4];

            while (_running)
            {
                long pulse = Interlocked.Read(ref _pulseUntilTicks);
                if (pulse != 0 && Environment.TickCount > pulse)
                {
                    Interlocked.Exchange(ref _pulseUntilTicks, 0);
                    SetActive(false);
                }

                if (!_active || (_cfg.RequireFocus && !FocusOk()))
                {
                    if (_keyIsDown || _comboIsDown) SafeRelease();
                    Thread.Sleep(2);
                    continue;
                }

                Pattern pat = _rt.Pat;
                double t0 = sw.Elapsed.TotalMilliseconds;

                // Phim kem che do HOLD: bam mot lan roi giu suot. Sprint/walk la
                // trang thai - nhap nhay no theo tung nhip nhay la pha game.
                if (_cfg.Combo == ComboMode.Hold && !_comboIsDown)
                { _combo.KeyDown(); _comboIsDown = true; }

                if (_cfg.EmitMode == "wheel" || _cfg.EmitMode == "both") _emit.Wheel();

                if (_cfg.EmitMode == "key" || _cfg.EmitMode == "both")
                {
                    int n = BuildCycle(pat, evT, evW);
                    for (int i = 0; i < n; i++)
                    {
                        SleepUntil(sw, t0 + evT[i]);
                        switch (evW[i])
                        {
                            case 0: _keyIsDown = true; _emit.KeyDown(); break;
                            case 1: _emit.KeyUp(); _keyIsDown = false; break;
                            case 2: _combo.KeyDown(); _comboIsDown = true; break;
                            case 3: _combo.KeyUp(); _comboIsDown = false; break;
                        }
                    }
                }

                Interlocked.Increment(ref _emitCount);

                // Jitter CHI an vao khoang nghi. Thoi gian giu la bat kha xam pham
                // vi o model POLL no chinh la thu quyet dinh game co thay phim khong.
                double gap = pat.GapMs;
                if (pat.JitterPct > 0)
                    gap *= 1.0 + (rnd.NextDouble() * 2.0 - 1.0) * (pat.JitterPct / 100.0);
                SleepUntil(sw, sw.Elapsed.TotalMilliseconds + gap);
            }

            Native.timeEndPeriod(1);
        }

        // ---------------------------------------------------------------------
        //  Dung lich cho MOT nhip, roi chay theo dung thu tu thoi gian.
        //  Ma su kien: 0 = nhay xuong, 1 = nhay len, 2 = kem xuong, 3 = kem len.
        //
        //  Phai lam kieu lich thay vi viet thang, vi o che do After thoi diem
        //  bam phim kem co the roi TRUOC hoac SAU luc nha phim nhay, tuy
        //  combo_offset_ms so voi hold_ms. Sap xep xong thi moi thu tu luon dung.
        // ---------------------------------------------------------------------
        // ---------------------------------------------------------------------
        //  Nhip chi song o game kieu POLL neu MOI pha deu dai hon 1 frame:
        //  phim giu, phim nha, va phim kem neu co. Pha NGAN NHAT quyet dinh tat
        //  ca -> tu no suy ra fps toi thieu ma nhip con an.
        //
        //  Day la con so can nhin khi chon game_fps. Khong phai "dien fps cua
        //  toi" - ma la "nhip nay chiu duoc fps tut toi dau". Dien theo fps luc
        //  vang thi luc dong nguoi fps tut xuong duoi nguong va macro ngung an
        //  ma khong bao gi ca.
        //
        //  Lan chuot khong co pha giu nen khong dinh luat nay -> khong in ra.
        // ---------------------------------------------------------------------
        private static double MinPhaseMs(Pattern pat)
        {
            double worstGap = pat.GapMs * (1.0 - pat.JitterPct / 100.0);
            double m = Math.Min(pat.HoldMs, worstGap);
            if (_cfg.Combo == ComboMode.With || _cfg.Combo == ComboMode.After)
            {
                var t = new double[4]; var w = new int[4];
                int n = BuildCycle(pat, t, w);
                double dn = 0, up = 0;
                for (int k = 0; k < n; k++)
                {
                    if (w[k] == 2) dn = t[k];
                    else if (w[k] == 3) up = t[k];
                }
                double ch = up - dn;
                if (ch > 0) m = Math.Min(m, Math.Min(ch, pat.PeriodMs - ch));
            }
            return m;
        }

        private static int BuildCycle(Pattern pat, double[] t, int[] w)
        {
            int n = 0;

            // Phim kem xep TRUOC trong mang: khi trung thoi diem, sap xep on
            // dinh se giu no xuong truoc phim nhay - dung thu tu ma game mong doi.
            if (_cfg.Combo == ComboMode.With || _cfg.Combo == ComboMode.After)
            {
                double off = _cfg.Combo == ComboMode.After ? _cfg.ComboOffsetMs : 0;
                double hold = _cfg.ComboHoldMs > 0 ? _cfg.ComboHoldMs : pat.HoldMs;

                // Ca cum phai gon trong mot chu ky, khong thi nhip sau bam de len
                // va phim kem khong bao gio duoc nha ra.
                double room = pat.PeriodMs * 0.9;
                if (off > room - 1.0) off = Math.Max(0, room - 1.0);
                if (off + hold > room) hold = Math.Max(1.0, room - off);

                t[n] = off; w[n++] = 2;
                t[n] = off + hold; w[n++] = 3;
            }

            t[n] = 0; w[n++] = 0;
            t[n] = pat.HoldMs; w[n++] = 1;

            // Sap xep chen, on dinh, 4 phan tu.
            for (int i = 1; i < n; i++)
            {
                double tv = t[i]; int wv = w[i]; int j = i - 1;
                while (j >= 0 && t[j] > tv) { t[j + 1] = t[j]; w[j + 1] = w[j]; j--; }
                t[j + 1] = tv; w[j + 1] = wv;
            }
            return n;
        }

        // Ngu chinh xac: Sleep(1) khi con xa, spin khi gan. Sleep thuan sai +-15ms.
        private static void SleepUntil(Stopwatch sw, double targetMs)
        {
            while (true)
            {
                double rem = targetMs - sw.Elapsed.TotalMilliseconds;
                if (rem <= 0) return;
                if (rem > 2.0) Thread.Sleep(1);
                else Thread.SpinWait(150);
            }
        }

        // Nha phim ra neu dang giu - tranh ket phim khi tat dot ngot / mat focus.
        // Phim kem quan trong hon ca: bo quen mot SHIFT dang giu la nguoi dung
        // go chu HOA khap noi ma khong hieu tai sao.
        private static void SafeRelease()
        {
            try
            {
                if (_cfg != null && _emit != null && _cfg.EmitMode != "wheel")
                {
                    _emit.KeyUp();
                    _keyIsDown = false;
                }
            }
            catch { }
            try
            {
                if (_combo != null)
                {
                    _combo.KeyUp();
                    _comboIsDown = false;
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------------
        //  Chot an toan: chi ban input khi dung cua so dang o truoc.
        //  Tien the: nhan dien game de doi profile neu profile = auto.
        // ---------------------------------------------------------------------
        private static bool FocusOk()
        {
            double now = _focusSw.Elapsed.TotalMilliseconds;
            if (now - _focusCheckedAt < 100) return _focusOk;
            _focusCheckedAt = now;

            IntPtr hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) { _focusOk = false; return false; }

            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if ((int)pid == _selfPid) { _focusOk = false; return false; }   // dung ban vao chinh minh

            string pname = SafeProcName(pid);
            if (pname != _fgProc)
            {
                _fgProc = pname;
                if (_cfg.AutoProfile) ApplyProfileFor(pname);
            }

            if (_cfg.TargetProcess.Length == 0) { _focusOk = true; return true; }

            foreach (string t in _cfg.TargetProcess)
                if (pname.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) { _focusOk = true; return true; }

            _focusOk = false;
            return false;
        }

        private static void ApplyProfileFor(string pname)
        {
            GameProfile p = GameProfile.ByProcess(pname);
            if (p == null) return;
            p = p.Clone();
            if (_cfg.GameFpsOverride > 0) p.GameFps = _cfg.GameFpsOverride;
            p = _cfg.AdaptForEmit(p);
            if (_rt.Profile != null && _rt.Profile.Key == p.Key) return;

            var rt = new Runtime();
            rt.Profile = p;
            rt.Pat = BuildPattern(p);
            rt.Source = "tu nhan dien tu '" + pname + "'";
            _rt = rt;
            Say("== nhan dien '" + pname + "' -> profile " + p.Key + " (" +
                (p.Polled ? "POLL" : "LATCH") + ")  " + rt.Pat.Describe());
        }

        private static string SafeProcName(uint pid)
        {
            try { return Process.GetProcessById((int)pid).ProcessName; }
            catch { return ""; }
        }

        // ---------------------------------------------------------------------
        private static void StatLoop()
        {
            long last = 0;
            var sw = Stopwatch.StartNew();
            double lastT = 0;
            while (_running)
            {
                Thread.Sleep(500);
                if (!_cfg.Verbose) continue;
                long cur = Interlocked.Read(ref _emitCount);
                double t = sw.Elapsed.TotalSeconds;
                double hz = (t - lastT) > 0 ? (cur - last) / (t - lastT) : 0;
                last = cur; lastT = t;
                if (_active)
                    Say(string.Format(CultureInfo.InvariantCulture,
                        "   dang ban: {0:0.0} nhip/s   tong {1}   [{2}]", hz, cur, _rt.Profile.Key));
            }
        }

        // =====================================================================
        //  --watch : tim ten exe cua game. Gan nhu ai cung can buoc nay.
        // =====================================================================
        private static int Watch()
        {
            Console.WriteLine("================================================================");
            Console.WriteLine(" WATCH - alt-tab sang game, ten exe se hien ra o day.");
            Console.WriteLine(" Chep ten do vao  target_process  trong config.ini. Ctrl+C de thoat.");
            Console.WriteLine("================================================================");
            string lastKey = "";
            var sb = new StringBuilder(512);
            while (true)
            {
                IntPtr hwnd = Native.GetForegroundWindow();
                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                string pname = SafeProcName(pid);
                sb.Length = 0;
                Native.GetWindowTextW(hwnd, sb, sb.Capacity);
                string title = sb.ToString();
                string key = pname + "|" + title;
                if (key != lastKey && pname.Length > 0)
                {
                    lastKey = key;
                    GameProfile p = GameProfile.ByProcess(pname);
                    Console.WriteLine();
                    Console.WriteLine("  process : " + pname + "   (pid " + pid + ")");
                    Console.WriteLine("  tieu de : " + title);
                    Console.WriteLine("  quyen   : " + (CanInspect(pid) ? "doc duoc (cung muc quyen)"
                                                     : "KHONG doc duoc -> nhieu kha nang chay Administrator, can --admin"));
                    Console.WriteLine("  profile : " + (p != null
                        ? (p.Key + " (" + (p.Polled ? "POLL" : "LATCH") + ") - tu nhan dien duoc")
                        : "khong co san -> tu chon: profile = poll60 neu game khong phan ung nhip nhanh"));
                    Console.WriteLine("  config  : target_process = " + pname);
                }
                Thread.Sleep(250);
            }
        }

        private static bool CanInspect(uint pid)
        {
            try
            {
                Process p = Process.GetProcessById((int)pid);
                IntPtr h = p.Handle;      // nem Win32Exception neu khong mo duoc
                return h != IntPtr.Zero;
            }
            catch { return false; }
        }

        // =====================================================================
        //  --probe : mo phong mot game doc input o N fps roi dem xem no THAY
        //  bao nhieu cu nhay. Day la cau tra loi thang cho "vi sao game khong an".
        // =====================================================================
        private static int Probe(double fps)
        {
            if (fps <= 0) fps = _rt.Profile.GameFps > 0 ? _rt.Profile.GameFps : 60;
            Pattern pat = _rt.Pat;
            const double DUR = 10000.0;   // 10 giay mo phong

            Console.WriteLine("================================================================");
            Console.WriteLine(" PROBE - game doc input o " + fps.ToString("0", CultureInfo.InvariantCulture) + " fps thi thay gi?");
            Console.WriteLine("================================================================");
            Console.WriteLine(" profile : " + _rt.Profile.Key + "  (" + (_rt.Profile.Polled ? "POLL" : "LATCH") + ")");
            Console.WriteLine(" nhip    : " + pat.Describe());
            Console.WriteLine("----------------------------------------------------------------");

            // Mot nhip trong thuc te, lay tu chinh ham ma vong lap ban dung.
            {
                var ct = new double[4]; var cw = new int[4];
                int n = BuildCycle(pat, ct, cw);
                string[] what = new string[] { "xuong", "len", "xuong", "len" };
                Console.WriteLine(" MOT NHIP - dung thu tu tool se ban ra:");
                for (int i = 0; i < n; i++)
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "   +{0,6:0.0} ms   {1,-8} {2}",
                        ct[i], cw[i] < 2 ? _cfg.EmitKey.ToString() : _cfg.ComboKey.ToString(), what[cw[i]]));
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "   +{0,6:0.0} ms   -- het chu ky, nhip sau bat dau --", pat.PeriodMs));
                if (_cfg.Combo == ComboMode.Hold)
                    Console.WriteLine("   (" + _cfg.ComboKey + " o che do HOLD: giu suot ca phien, khong nam trong nhip)");
                Console.WriteLine("----------------------------------------------------------------");
            }

            // Dung lai chinh xac chuoi thoi gian ma EmitLoop se sinh ra.
            var down = new List<double>();
            var up = new List<double>();
            var rnd = new Random(20240115);
            double t = 0;
            while (t < DUR)
            {
                down.Add(t);
                up.Add(t + pat.HoldMs);
                double gap = pat.GapMs;
                if (pat.JitterPct > 0)
                    gap *= 1.0 + (rnd.NextDouble() * 2.0 - 1.0) * (pat.JitterPct / 100.0);
                t += pat.HoldMs + gap;
            }

            // --- Nguoi doc kieu LATCH: hang doi su kien, khong bo sot cai nao ---
            int latchEdges = down.Count;

            // --- Nguoi doc kieu POLL: chup trang thai moi frame ---
            double frame = 1000.0 / fps;
            int pollEdges = 0, framesDown = 0, frames = 0;
            bool prevDown = false;
            int di = 0;
            for (double ft = 0; ft < DUR; ft += frame)
            {
                // Phim dang xuong tai thoi diem ft?
                while (di + 1 < down.Count && down[di + 1] <= ft) di++;
                bool isDown = ft >= down[di] && ft < up[di];
                frames++;
                if (isDown) framesDown++;
                if (isDown && !prevDown) pollEdges++;   // canh len = mot cu nhay moi
                prevDown = isDown;
            }

            double sec = DUR / 1000.0;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                " tool ban ra          : {0} cu nhan  ({1:0.0}/giay)", down.Count, down.Count / sec));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                " game LATCH se nhan   : {0} cu  ({1:0.0}/giay)   -> ty le bat {2:0.0} %",
                latchEdges, latchEdges / sec, 100.0));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                " game POLL {0:0} fps thay: {1} cu  ({2:0.0}/giay)   -> ty le bat {3:0.0} %",
                fps, pollEdges, pollEdges / sec, 100.0 * pollEdges / down.Count));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "   trong do {0}/{1} frame thay phim dang xuong", framesDown, frames));
            Console.WriteLine("----------------------------------------------------------------");

            double hit = 100.0 * pollEdges / down.Count;
            double jumpsPerSec = pollEdges / sec;
            if (pat.HoldMs <= frame)
            {
                Warn(string.Format(CultureInfo.InvariantCulture,
                    "hold {0:0.0} ms KHONG vuot 1 frame ({1:0.0} ms). Game kieu POLL se bo sot phan lon cu nhan. " +
                    "Day dung la trieu chung 'macro chay ma nhan vat khong nhay'.", pat.HoldMs, frame));
                Console.WriteLine("   Sua: dat  profile = poll" + (fps >= 130 ? "144" : (fps >= 90 ? "100" : "60")) +
                                  "   (hoac  game_fps = " + fps.ToString("0", CultureInfo.InvariantCulture) + ")");
            }
            else if (jumpsPerSec < 8)
            {
                Warn(string.Format(CultureInfo.InvariantCulture,
                    "chi {0:0.0} cu nhay/giay - hoi thua. Giam poll_margin (dang {1:0.00}) xuong 1.15-1.25.",
                    jumpsPerSec, _cfg.PollMargin));
            }
            else
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    " KET LUAN: {0:0.0} % cu nhan den duoc game, {1:0.0} cu nhay/giay. Nhip nay dung.",
                    hit, jumpsPerSec));
            }
            return 0;
        }

        // =====================================================================
        //  --selftest: khong dat hook, khong dung phim that.
        //  Ban phim F24 (VK 0x87) - khong ung dung nao dung - roi do xem nhip
        //  ma may nay thuc su giu duoc la bao nhieu.
        // =====================================================================
        private static int SelfTest(double durationMs)
        {
            _cfg.EmitMode = "key";
            _cfg.EmitKey = Keys.FromVk(0x87, "F24");
            _cfg.Combo = ComboMode.Off;   // khong bao gio ban SHIFT/CTRL that trong self-test
            _emit = new InputEmitter(_cfg);
            Pattern pat = _rt.Pat;

            Console.WriteLine("================================================================");
            Console.WriteLine(" SELF-TEST - do do chinh xac cua nhip tren may nay");
            Console.WriteLine("================================================================");
            Console.WriteLine(" profile  : " + _rt.Profile.Key + "  (" + (_rt.Profile.Polled ? "POLL" : "LATCH") + ")");
            Console.WriteLine(" muc tieu : " + pat.Describe());
            Console.WriteLine(" phim     : F24 (vo hai, khong ung dung nao dung)");
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, " thoi luong: {0:0} ms", durationMs));
            Console.WriteLine("----------------------------------------------------------------");

            int cap = (int)(durationMs / pat.PeriodMs) + 64;
            var stamps = new List<double>(cap);
            var holds = new List<double>(cap);

            Native.timeBeginPeriod(1);
            var sw = Stopwatch.StartNew();
            var rnd = new Random(12345);

            while (sw.Elapsed.TotalMilliseconds < durationMs)
            {
                double t0 = sw.Elapsed.TotalMilliseconds;
                stamps.Add(t0);
                _emit.KeyDown();
                SleepUntil(sw, t0 + pat.HoldMs);
                _emit.KeyUp();
                holds.Add(sw.Elapsed.TotalMilliseconds - t0);

                double gap = pat.GapMs;
                if (pat.JitterPct > 0)
                    gap *= 1.0 + (rnd.NextDouble() * 2.0 - 1.0) * (pat.JitterPct / 100.0);
                SleepUntil(sw, sw.Elapsed.TotalMilliseconds + gap);
            }
            double elapsed = sw.Elapsed.TotalMilliseconds;
            Native.timeEndPeriod(1);

            if (stamps.Count < 3) { Console.WriteLine("Khong du mau."); return 1; }

            var gaps = new List<double>(stamps.Count - 1);
            for (int i = 1; i < stamps.Count; i++) gaps.Add(stamps[i] - stamps[i - 1]);
            var sorted = new List<double>(gaps);
            sorted.Sort();

            double sum = 0; foreach (double v in gaps) sum += v;
            double mean = sum / gaps.Count;
            double var2 = 0; foreach (double v in gaps) var2 += (v - mean) * (v - mean);
            double sd = Math.Sqrt(var2 / gaps.Count);

            var hs = new List<double>(holds); hs.Sort();
            double hsum = 0; foreach (double v in holds) hsum += v;

            double achieved = stamps.Count * 1000.0 / elapsed;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                " da ban       : {0} nhip trong {1:0} ms", stamps.Count, elapsed));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                " nhip dat duoc: {0:0.0} Hz   (lech {1:+0.0;-0.0} Hz so voi muc tieu {2:0.0})",
                achieved, achieved - pat.Hz, pat.Hz));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                " khoang cach  : trung binh {0:0.00} ms   do lech chuan {1:0.00} ms", mean, sd));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "                min {0:0.00}   p50 {1:0.00}   p95 {2:0.00}   max {3:0.00}  (ms)",
                sorted[0], sorted[sorted.Count / 2], sorted[(int)(sorted.Count * 0.95)], sorted[sorted.Count - 1]));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                " thoi gian giu: trung binh {0:0.00} ms   min {1:0.00}   max {2:0.00}   (dat {3:0.00})",
                hsum / holds.Count, hs[0], hs[hs.Count - 1], pat.HoldMs));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                " SendInput    : {0} lan bi tu choi{1}",
                InputEmitter.Failures,
                InputEmitter.Failures > 0 ? ("  (win32 error " + InputEmitter.LastError + ")") : "  - OK"));
            Console.WriteLine("----------------------------------------------------------------");

            double err = Math.Abs(achieved - pat.Hz) / pat.Hz;
            if (InputEmitter.Failures > 0)
                Warn("SendInput bi chan. Thu chay lai: BhopAssist.exe --admin");
            else if (hs[0] < pat.HoldMs * 0.85)
                Warn("Co nhip giu ngan hon dat. May dang tai nang -> game kieu POLL co the bo sot.");
            else if (err > 0.10)
                Warn("Lech nhip > 10%. May dang tai nang, hoac nhip dat qua cao.");
            else
                Console.WriteLine(" KET LUAN: nhip on dinh, SendInput hoat dong binh thuong.");
            return 0;
        }

        // =====================================================================
        //  Quyen han
        // =====================================================================
        private static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static int Relaunch(string[] args)
        {
            var keep = new List<string>();
            foreach (string a in args)
                if (!a.Equals("--admin", StringComparison.OrdinalIgnoreCase)) keep.Add("\"" + a + "\"");
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = Process.GetCurrentProcess().MainModule.FileName;
                psi.Arguments = string.Join(" ", keep.ToArray());
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Khong chay lai bang quyen admin duoc: " + ex.Message);
                return 4;
            }
        }

        internal static void Say(string s) { lock (_con) Console.WriteLine(s); }
        internal static void Warn(string s)
        {
            lock (_con)
            {
                ConsoleColor old = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[!] " + s);
                Console.ForegroundColor = old;
            }
        }
    }
}
