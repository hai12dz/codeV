// =============================================================================
//  VAssist - chuot phai -> CHO dao chem xong -> moi bam V
// -----------------------------------------------------------------------------
//  Cung kien truc voi BhopAssist (..\..\src\BhopAssist.cs), chi khac viec lam:
//
//     BhopAssist : bat trigger -> ban phim nhay theo NHIP lap lai
//     VAssist    : moi cu click chuot phai -> DUNG MOT lan bam V, tre delay_ms
//
//        t = 0           ban bam chuot phai (tay that). Dao bat dau chem.
//        t = delay_ms    tool bam V xuong   <- luc nay dao da chem xong
//        t = + hold_ms   tool nha V
//
//  Van la "lam o ngoai": nghe chuot/phim bang low-level hook, bam V bang
//  SendInput. KHONG doc/ghi bo nho game, KHONG inject DLL, KHONG hook ham game.
//  Vi vay tool KHONG BIET dao chem xong luc nao - no chi dem dong ho tu luc
//  ban click. delay_ms la con so ban tune (NUMPLUS / NUMMINUS ngay trong game).
//
//  CANH BAO: game online coi macro la gian lan. Tool nay khong che giau gi ca,
//  dung o dau la tu chiu rui ro khoa tai khoan.
//
//  Nhung cho de sai, da xu ly:
//    * Click o cua so khac (Discord, desktop...) khong sinh ra V.
//    * Alt-tab ra trong luc V dang cho -> huy, khong bam V vao cua so khac.
//    * Click lai giua chung -> dem lai tu click moi nhat (reclick = restart)
//      -> V khong bao gio den SOM hon delay_ms sau click cuoi.
//    * Thread hook KHONG ghi console. Ghi console bi treo khi ban boi den chu
//      trong cua so console, ma hook treo qua ~300 ms la Windows go hook ra.
//      Moi dong log di qua hang doi, mot thread rieng ghi.
//    * Hook chuot bo qua WM_MOUSEMOVE ngay tu ma message: chuot gaming bao vi
//      tri 1000 lan/giay, hook cham la chuot trong game tre theo.
//    * Thoat (F12, Ctrl+C) dung luc dang giu V -> go hook, cho thread ban nha V
//      ra roi moi thoat. Process chet giua chung thi V ket o trang thai giu.
// =============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace VAssist
{
    // =========================================================================
    //  PHAN 1: Win32 interop
    // =========================================================================
    internal static class Native
    {
        public const int WH_KEYBOARD_LL = 13;
        public const int WH_MOUSE_LL = 14;

        public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
        public const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
        public const int WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C;
        public const uint WM_QUIT = 0x0012;

        // Bit "input nay do phan mem ban ra". Dung de chi tin click/phim tu tay that.
        public const uint LLKHF_INJECTED = 0x10;
        public const uint LLMHF_INJECTED = 0x01;

        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_SCANCODE = 0x0008;
        public const uint MAPVK_VK_TO_VSC = 0;

        // Chu ky nhet vao dwExtraInfo de nhan ra V do chinh tool ban.
        // KHAC BhopAssist (0x1BADB002) -> chay hai tool cung luc van phan biet duoc.
        public static readonly IntPtr SIGNATURE = new IntPtr(0x56A55E57);

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

        // MOUSEINPUT phai co mat trong union du ta chi ban phim: no lon nhat, quyet
        // dinh sizeof(INPUT). Thieu no thi cbSize sai va SendInput tu choi het.
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

        [DllImport("user32.dll")]
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
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        // Nang do phan giai timer he thong len 1 ms - bat buoc neu muon dem gio chinh xac.
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint uPeriod);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint uPeriod);
    }

    // =========================================================================
    //  PHAN 2: Mot "nut" - phim ban phim hoac nut chuot
    // =========================================================================
    internal enum BindKind { None, Key, MouseButton }

    internal struct Bind
    {
        public BindKind Kind;
        public int Code;      // Key: ma VK | MouseButton: 1..5
        public string Name;

        public bool IsNone { get { return Kind == BindKind.None; } }
        public bool IsMouse { get { return Kind == BindKind.MouseButton; } }

        // Mot ma VK chung cho ca phim lan nut chuot (chuot phai = VK_RBUTTON 0x02).
        // Hook va polling deu quy ve ma nay, nen logic phia sau chi so mot so nguyen.
        public int Vk
        {
            get
            {
                if (Kind == BindKind.Key) return Code;
                if (Kind == BindKind.MouseButton) return Keys.MouseVk(Code);
                return 0;
            }
        }

        public bool Is(int vk) { return Kind != BindKind.None && Vk == vk; }

        public override string ToString() { return IsNone ? "(khong)" : Name; }
    }

    internal static class Keys
    {
        private static readonly Dictionary<string, int> Table = BuildTable();

        private static Dictionary<string, int> BuildTable()
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            d["SPACE"] = 0x20; d["ENTER"] = 0x0D; d["TAB"] = 0x09; d["ESC"] = 0x1B;
            d["BACKSPACE"] = 0x08; d["CAPSLOCK"] = 0x14; d["PAUSE"] = 0x13; d["SCROLLLOCK"] = 0x91;
            d["SHIFT"] = 0x10; d["CTRL"] = 0x11; d["ALT"] = 0x12;
            d["LSHIFT"] = 0xA0; d["RSHIFT"] = 0xA1;
            d["LCTRL"] = 0xA2; d["RCTRL"] = 0xA3;
            d["LALT"] = 0xA4; d["RALT"] = 0xA5;
            d["LEFT"] = 0x25; d["UP"] = 0x26; d["RIGHT"] = 0x27; d["DOWN"] = 0x28;
            d["INSERT"] = 0x2D; d["DELETE"] = 0x2E; d["HOME"] = 0x24; d["END"] = 0x23;
            d["PAGEUP"] = 0x21; d["PAGEDOWN"] = 0x22;
            d["NUMPLUS"] = 0x6B; d["NUMMINUS"] = 0x6D; d["NUMSTAR"] = 0x6A;
            d["NUMSLASH"] = 0x6F; d["NUMDOT"] = 0x6E;
            d["TILDE"] = 0xC0;                                                   // phim ` ~
            for (int i = 1; i <= 24; i++) d["F" + i] = 0x6F + i;               // F1 = 0x70 .. F24 = 0x87
            for (char c = 'A'; c <= 'Z'; c++) d[c.ToString()] = c;             // 'V' = 0x56
            for (char c = '0'; c <= '9'; c++) d[c.ToString()] = c;
            for (int i = 0; i <= 9; i++) d["NUM" + i] = 0x60 + i;
            return d;
        }

        public static int MouseVk(int button)
        {
            switch (button)
            {
                case 1: return 0x01;   // VK_LBUTTON
                case 2: return 0x02;   // VK_RBUTTON
                case 3: return 0x04;   // VK_MBUTTON
                case 4: return 0x05;   // VK_XBUTTON1
                case 5: return 0x06;   // VK_XBUTTON2
            }
            return 0;
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
            if (s == null) return b;
            s = s.Trim();
            b.Name = s.ToUpperInvariant();
            if (b.Name.Length == 0 || b.Name == "NONE" || b.Name == "-") { b.Kind = BindKind.None; return b; }

            if (b.Name.StartsWith("MOUSE", StringComparison.Ordinal))
            {
                int n;
                if (int.TryParse(b.Name.Substring(5), out n) && n >= 1 && n <= 5)
                { b.Kind = BindKind.MouseButton; b.Code = n; return b; }
            }

            int vk;
            if (Table.TryGetValue(b.Name, out vk)) { b.Kind = BindKind.Key; b.Code = vk; return b; }
            throw new FormatException("Khong hieu ten phim: '" + s + "'");
        }

        public static Bind[] ParseList(string s)
        {
            var list = new List<Bind>();
            if (string.IsNullOrEmpty(s)) return list.ToArray();
            foreach (string part in s.Split(','))
            {
                Bind b = Parse(part);
                if (!b.IsNone) list.Add(b);
            }
            return list.ToArray();
        }

        public static string Names(Bind[] list)
        {
            if (list.Length == 0) return "(khong)";
            var parts = new string[list.Length];
            for (int i = 0; i < list.Length; i++) parts[i] = list[i].Name;
            return string.Join(", ", parts);
        }
    }

    // =========================================================================
    //  PHAN 3: Config
    // =========================================================================
    internal enum StartFrom { Down, Up }
    internal enum ReclickMode { Restart, Ignore }

    internal sealed class Config
    {
        public const int DELAY_MAX = 5000;

        public Bind TriggerKey;        // MOUSE2 = chuot phai
        public StartFrom From;         // dem gio tu luc nhan xuong hay luc nha ra
        public int DelayMs;            // cho dao chem xong
        public ReclickMode Reclick;    // click lai trong luc V dang cho

        public Bind EmitKey;           // V
        public double HoldMs;
        public int PressCount;
        public double PressGapMs;
        public string SendMode;        // scancode | vk | both

        public Bind ToggleKey;
        public bool StartArmed;
        public Bind[] ArmKeys;
        public Bind[] DisarmKeys;
        public Bind PanicKey;

        public Bind DelayUpKey;
        public Bind DelayDownKey;
        public int DelayStepMs;
        public bool SaveTuning;

        public double GameFps;         // fps THAP NHAT trong game - de canh bao va --probe
        public string[] TargetProcess; // rong = moi cua so
        public bool RequireFocus;
        public string TriggerSource;   // auto | hook | poll
        public bool AcceptInjected;
        public bool Verbose;

        public double FrameMs { get { return 1000.0 / GameFps; } }

        // Ca chuoi bam (neu press_count > 1) keo dai bao lau.
        public double BurstMs { get { return PressCount * HoldMs + (PressCount - 1) * PressGapMs; } }

        public static Config Load(string path)
        {
            Dictionary<string, string> raw = ReadIni(path);
            var c = new Config();

            c.TriggerKey = Keys.Parse(Str(raw, "trigger_key", "MOUSE2"));
            if (c.TriggerKey.IsNone) throw new FormatException("trigger_key khong duoc de NONE.");
            string sf = Str(raw, "start_from", "down").ToLowerInvariant();
            if (sf != "down" && sf != "up")
                throw new FormatException("start_from chi nhan down | up (dang la '" + sf + "').");
            c.From = sf == "up" ? StartFrom.Up : StartFrom.Down;
            c.DelayMs = (int)Math.Round(Clamp(Num(raw, "delay_ms", 700), 0, DELAY_MAX));
            string rc = Str(raw, "reclick", "restart").ToLowerInvariant();
            if (rc != "restart" && rc != "ignore")
                throw new FormatException("reclick chi nhan restart | ignore (dang la '" + rc + "').");
            c.Reclick = rc == "ignore" ? ReclickMode.Ignore : ReclickMode.Restart;

            c.EmitKey = Keys.Parse(Str(raw, "emit_key", "V"));
            if (c.EmitKey.Kind != BindKind.Key)
                throw new FormatException("emit_key phai la phim ban phim (vd V), khong phai nut chuot / NONE.");
            c.HoldMs = Clamp(Num(raw, "hold_ms", 50), 1, 1000);
            c.PressCount = (int)Clamp(Num(raw, "press_count", 1), 1, 10);
            c.PressGapMs = Clamp(Num(raw, "press_gap_ms", 50), 1, 1000);
            c.SendMode = Str(raw, "send_mode", "scancode").ToLowerInvariant();
            if (c.SendMode != "scancode" && c.SendMode != "vk" && c.SendMode != "both")
                throw new FormatException("send_mode chi nhan scancode | vk | both.");

            c.ToggleKey = Keys.Parse(Str(raw, "toggle_key", "F8"));
            c.StartArmed = Bool(raw, "start_armed", true);
            c.ArmKeys = Keys.ParseList(Str(raw, "arm_keys", ""));
            c.DisarmKeys = Keys.ParseList(Str(raw, "disarm_keys", ""));
            c.PanicKey = Keys.Parse(Str(raw, "panic_key", "F12"));

            c.DelayUpKey = Keys.Parse(Str(raw, "delay_up_key", "NUMPLUS"));
            c.DelayDownKey = Keys.Parse(Str(raw, "delay_down_key", "NUMMINUS"));
            c.DelayStepMs = (int)Clamp(Num(raw, "delay_step_ms", 10), 1, 500);
            c.SaveTuning = Bool(raw, "save_tuning", true);

            c.GameFps = Clamp(Num(raw, "game_fps", 60), 10, 1000);
            c.TargetProcess = ParseTargets(Str(raw, "target_process", ""));
            c.RequireFocus = Bool(raw, "require_focus", true);
            c.TriggerSource = Str(raw, "trigger_source", "auto").ToLowerInvariant();
            if (c.TriggerSource != "auto" && c.TriggerSource != "hook" && c.TriggerSource != "poll")
                throw new FormatException("trigger_source chi nhan auto | hook | poll.");
            c.AcceptInjected = Bool(raw, "accept_injected", false);
            c.Verbose = Bool(raw, "verbose", true);
            return c;
        }

        private static Dictionary<string, string> ReadIni(string path)
        {
            var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return raw;
            foreach (string lineRaw in File.ReadAllLines(path))
            {
                string line = StripComment(lineRaw).Trim();
                if (line.Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                raw[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return raw;
        }

        private static readonly char[] CommentChars = new char[] { '#', ';' };

        private static string StripComment(string line)
        {
            int cut = line.IndexOfAny(CommentChars);
            return cut >= 0 ? line.Substring(0, cut) : line;
        }

        // Ghi lai gia tri cua MOT khoa. Giu nguyen moi dong khac, moi chu thich, ca
        // khoang trang canh cot. Khong co dong do thi them vao cuoi file.
        public static void SaveValue(string path, string key, string value)
        {
            var lines = new List<string>(File.Exists(path) ? File.ReadAllLines(path) : new string[0]);
            bool found = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string l = lines[i];
                int cut = l.IndexOfAny(CommentChars);
                string body = cut >= 0 ? l.Substring(0, cut) : l;
                string comment = cut >= 0 ? l.Substring(cut) : "";
                int eq = body.IndexOf('=');
                if (eq <= 0) continue;
                if (!body.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                lines[i] = body.Substring(0, eq + 1) + " " + value + (comment.Length > 0 ? "   " + comment : "");
                found = true;
            }
            if (!found) lines.Add(key + " = " + value);
            File.WriteAllLines(path, lines.ToArray());
        }

        private static string[] ParseTargets(string s)
        {
            var list = new List<string>();
            foreach (string part in s.Split(','))
            {
                string t = part.Trim();
                if (t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) t = t.Substring(0, t.Length - 4);
                if (t.Length > 0) list.Add(t);
            }
            return list.ToArray();
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
            if (!d.TryGetValue(k, out v) || v.Length == 0) return dflt;
            v = v.Trim().ToLowerInvariant();
            return v == "1" || v == "true" || v == "yes" || v == "on";
        }

        private static double Clamp(double v, double lo, double hi)
        { return v < lo ? lo : (v > hi ? hi : v); }
    }

    // =========================================================================
    //  PHAN 4: Log - KHONG BAO GIO block nguoi goi
    // -------------------------------------------------------------------------
    //  Console.WriteLine co the treo: chi can ban boi den chu trong cua so
    //  console (QuickEdit) la moi lenh ghi dung lai cho toi khi bo chon. Neu cai
    //  dang ghi la thread hook, Windows go hook sau ~300 ms; neu la thread ban,
    //  V den tre. Vi vay sau khi khoi dong xong, moi dong log, doi tieu de va ghi
    //  file deu xep hang cho mot thread rieng lam.
    // =========================================================================
    internal static class Log
    {
        private static readonly object _con = new object();
        private static BlockingCollection<Action> _q;
        private static volatile bool _async;

        public static void StartAsync()
        {
            _q = new BlockingCollection<Action>(new ConcurrentQueue<Action>(), 4096);
            var t = new Thread(Pump);
            t.IsBackground = true;
            t.Name = "log";
            t.Start();
            _async = true;
        }

        private static void Pump()
        {
            foreach (Action a in _q.GetConsumingEnumerable())
            {
                try { a(); } catch { }
            }
        }

        public static void Post(Action a)
        {
            if (_async) _q.TryAdd(a);          // hang doi day -> bo, khong bao gio cho
            else { try { a(); } catch { } }
        }

        public static void Say(string s) { Post(delegate { Write(s, null); }); }
        public static void Warn(string s) { Post(delegate { Write("[!] " + s, ConsoleColor.Yellow); }); }
        public static void Title(string s) { Post(delegate { try { Console.Title = s; } catch { } }); }

        private static void Write(string s, ConsoleColor? color)
        {
            lock (_con)
            {
                if (color.HasValue)
                {
                    ConsoleColor old = Console.ForegroundColor;
                    Console.ForegroundColor = color.Value;
                    Console.WriteLine(s);
                    Console.ForegroundColor = old;
                }
                else Console.WriteLine(s);
            }
        }

        // Cho hang doi ghi het (luc thoat).
        public static void Flush(int timeoutMs)
        {
            if (!_async) return;
            var done = new ManualResetEvent(false);
            if (_q.TryAdd(delegate { done.Set(); })) done.WaitOne(timeoutMs);
        }
    }

    // =========================================================================
    //  PHAN 5: Bam phim bang SendInput
    // =========================================================================
    internal sealed class KeyEmitter
    {
        private readonly Bind _key;
        private readonly ushort _scan;
        private readonly Native.INPUT[] _down;
        private readonly Native.INPUT[] _up;
        private static readonly int Size = Marshal.SizeOf(typeof(Native.INPUT));

        public static long Failures;
        public static int LastError;

        public KeyEmitter(Bind key, string sendMode)
        {
            _key = key;
            _scan = (ushort)Native.MapVirtualKey((uint)key.Code, Native.MAPVK_VK_TO_VSC);
            int vk = key.Code;
            bool extended = vk == 0x25 || vk == 0x26 || vk == 0x27 || vk == 0x28 ||
                            vk == 0x2D || vk == 0x2E || vk == 0x24 || vk == 0x23 ||
                            vk == 0x21 || vk == 0x22 || vk == 0xA3 || vk == 0xA5 || vk == 0x6F;
            // Dung san hai goi tin, khong cap phat gi moi lan bam.
            _down = new Native.INPUT[] { Make(sendMode, extended, false) };
            _up = new Native.INPUT[] { Make(sendMode, extended, true) };
        }

        public Bind Key { get { return _key; } }
        public ushort ScanCode { get { return _scan; } }

        private Native.INPUT Make(string mode, bool extended, bool up)
        {
            var inp = new Native.INPUT();
            inp.type = Native.INPUT_KEYBOARD;
            // scancode : wVk = 0, Windows tu suy VK tu scancode theo layout.
            //            Bat buoc voi game doc DirectInput / Raw Input.
            // vk       : virtual-key thuan, hop voi game doc WM_KEYDOWN.
            // both     : gui ca hai truong.
            switch (mode)
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
                default:
                    inp.U.ki.wVk = 0;
                    inp.U.ki.wScan = _scan;
                    inp.U.ki.dwFlags = Native.KEYEVENTF_SCANCODE;
                    break;
            }
            if (extended) inp.U.ki.dwFlags |= Native.KEYEVENTF_EXTENDEDKEY;
            if (up) inp.U.ki.dwFlags |= Native.KEYEVENTF_KEYUP;
            inp.U.ki.time = 0;
            inp.U.ki.dwExtraInfo = Native.SIGNATURE;
            return inp;
        }

        public void Down() { Send(_down); }
        public void Up() { Send(_up); }

        private static void Send(Native.INPUT[] arr)
        {
            uint n = Native.SendInput(1, arr, Size);
            if (n != 0) return;
            int err = Marshal.GetLastWin32Error();
            long nth = Interlocked.Increment(ref Failures);
            LastError = err;
            // ERROR_ACCESS_DENIED (5) = UIPI: cua so dich chay o muc toan ven cao
            // hon tool. Gan nhu luon la "game chay Administrator".
            if (nth == 1)
                Log.Warn(err == 5
                    ? "SendInput bi UIPI chan (error 5): game dang chay quyen cao hon tool. Chay lai: VAssist.exe --admin"
                    : "SendInput bi tu choi (win32 error " + err + ").");
        }
    }

    internal sealed class FireRec
    {
        public double ClickMs;
        public double DownMs;
        public double UpMs;
        public int Presses;
    }

    // =========================================================================
    //  PHAN 6: Chuong trinh chinh
    // =========================================================================
    internal static class Program
    {
        private static Config _cfg;
        private static string _cfgPath;
        private static KeyEmitter _emit;

        private static IntPtr _hKb = IntPtr.Zero, _hMs = IntPtr.Zero;
        // Delegate hook PHAI giu o field static. De bien cuc bo thi GC don mat,
        // Windows goi vao con tro chet -> crash ngau nhien sau vai phut.
        private static Native.HookProc _kbProc, _msProc;
        private static uint _mainThreadId;
        private static int _selfPid;
        private static Thread _emitThread;
        private static bool _pollKeys, _pollMouse;

        private static volatile bool _running = true;
        private static volatile bool _armed;
        private static volatile bool _vDown;
        private static int _delayMs;          // doc bang Thread.VolatileRead, ghi bang Interlocked

        // --- Lich: hook ghi vao, thread ban doc ra. Moi thay doi deu tang _seq,
        //     nen thread ban chi can so _seq la biet co gi moi hay khong.
        private static readonly object _gate = new object();
        private static bool _pending;
        private static long _pendingTs;
        private static int _pendingClicks;
        private static volatile int _seq;
        private static readonly AutoResetEvent _wake = new AutoResetEvent(false);

        // --- So lieu
        private static long _nClicks, _nFired, _nRestart, _nIgnored, _nOffFocus, _nCanceled;

        // --- Self-test ghi lai tung lan ban
        private static volatile bool _record;
        private static readonly List<FireRec> _fires = new List<FireRec>();

        // --- Offset cac field trong struct cua hook, tinh mot lan luc khoi dong.
        //     Doc thang bang Marshal.ReadInt32 -> callback khong cap phat gi.
        private static int _kbVkOff, _kbFlagsOff, _kbExtraOff, _msDataOff, _msFlagsOff, _msExtraOff;

        // --- Loc auto-repeat ban phim (chi thread hook dung)
        private static readonly bool[] _physDown = new bool[256];
        private static readonly int[] _physLastMs = new int[256];

        // --- Dong ho chung cho moi thread
        private static readonly long _t0 = Stopwatch.GetTimestamp();
        private static double Ms(long ts) { return (ts - _t0) * 1000.0 / Stopwatch.Frequency; }
        private static double NowMs() { return Ms(Stopwatch.GetTimestamp()); }

        // --- Focus: chi thread ban goi FocusOk(); hook chi doc _gameFocused.
        private static volatile bool _gameFocused;
        private static double _focusAt = -1e9;
        private static uint _fgPid;
        private static double _fgPidAt = -1e9;
        private static string _fgProc = "";

        private static int Main(string[] args)
        {
            try { Console.Title = "VAssist"; } catch { }
            _selfPid = Process.GetCurrentProcess().Id;

            string cfgPath = null;
            bool selftest = false, watch = false, probe = false, admin = false;
            var nums = new List<double>();
            foreach (string a in args)
            {
                string l = a.ToLowerInvariant();
                if (l == "--selftest") selftest = true;
                else if (l == "--watch") watch = true;
                else if (l == "--probe") probe = true;
                else if (l == "--admin") admin = true;
                else if (l == "--help" || l == "-h" || l == "/?") { Usage(); return 0; }
                else
                {
                    double v;
                    if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) nums.Add(v);
                    else if (cfgPath == null) cfgPath = a;
                }
            }

            if (admin && !IsElevated()) return Relaunch(args);

            if (cfgPath == null) cfgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");
            _cfgPath = Path.GetFullPath(cfgPath);
            try { _cfg = Config.Load(_cfgPath); }
            catch (Exception ex) { Console.Error.WriteLine("Config loi: " + ex.Message); return 2; }

            _delayMs = _cfg.DelayMs;
            _armed = _cfg.StartArmed;

            if (watch) return Watch();
            if (probe) return Probe(nums.Count > 0 ? nums[0] : _cfg.GameFps, nums.Count > 1 ? nums[1] : 0);
            if (selftest) return SelfTest(nums.Count > 0 ? (int)nums[0] : 6);
            return Run();
        }

        // ---------------------------------------------------------------------
        private static int Run()
        {
            _emit = new KeyEmitter(_cfg.EmitKey, _cfg.SendMode);
            Banner();
            Preflight();
            Log.StartAsync();

            // Hook chay tren thread nay. Game an CPU thi thread hook van phai duoc
            // goi kip, khong thi ca chuot trong game cung tre theo.
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            _mainThreadId = Native.GetCurrentThreadId();
            ResolveOffsets();
            _kbProc = KeyboardProc;
            _msProc = MouseProc;
            IntPtr hMod = Native.GetModuleHandle(null);

            bool needKb = AnyBind(BindKind.Key), needMs = AnyBind(BindKind.MouseButton);
            if (_cfg.TriggerSource != "poll")
            {
                if (needKb)
                {
                    _hKb = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbProc, hMod, 0);
                    if (_hKb == IntPtr.Zero)
                        Log.Warn("Khong dat duoc keyboard hook (win32 error " + Marshal.GetLastWin32Error() +
                                 ") -> doc phim bang GetAsyncKeyState.");
                }
                if (needMs)
                {
                    _hMs = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _msProc, hMod, 0);
                    if (_hMs == IntPtr.Zero)
                        Log.Warn("Khong dat duoc mouse hook (win32 error " + Marshal.GetLastWin32Error() +
                                 ") -> doc chuot bang GetAsyncKeyState.");
                }
            }
            _pollKeys = needKb && _hKb == IntPtr.Zero;
            _pollMouse = needMs && _hMs == IntPtr.Zero;
            if (_pollKeys || _pollMouse)
            {
                Log.Say("   (dang doc bang polling: khong phan biet duoc click tay voi click do phan mem sinh ra)");
                var pt = new Thread(PollLoop);
                pt.IsBackground = true;
                pt.Priority = ThreadPriority.AboveNormal;
                pt.Name = "poll";
                pt.Start();
            }

            Console.CancelKeyPress += delegate (object s, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                Shutdown("Ctrl+C");
            };

            _emitThread = new Thread(EmitLoop);
            _emitThread.IsBackground = true;
            _emitThread.Priority = ThreadPriority.AboveNormal;
            _emitThread.Name = "emit";
            _emitThread.Start();

            UpdateTitle();
            Log.Say(_armed
                ? ">> dang BAT. Vao game, click " + _cfg.TriggerKey + " de thu."
                : "<< dang TAT. Bam " + _cfg.ToggleKey + " de bat.");

            if (_hKb != IntPtr.Zero || _hMs != IntPtr.Zero)
            {
                // Low-level hook chi duoc goi khi thread da dat hook dang bom message.
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

            // Thu tu thoat quan trong: go hook TRUOC (de chuot/phim cua he thong
            // khong phai cho thread nay nua), roi moi cho thread ban nha V ra.
            _running = false;
            if (_hKb != IntPtr.Zero) Native.UnhookWindowsHookEx(_hKb);
            if (_hMs != IntPtr.Zero) Native.UnhookWindowsHookEx(_hMs);
            CancelPending(null);
            _wake.Set();
            _emitThread.Join(2000);
            SafeRelease();

            Log.Say("");
            Log.Say(F("Da thoat.  click {0}   {1} da bam {2}   dem lai {3}   bo qua {4}   ngoai game {5}   huy {6}",
                Interlocked.Read(ref _nClicks), _cfg.EmitKey, Interlocked.Read(ref _nFired),
                Interlocked.Read(ref _nRestart), Interlocked.Read(ref _nIgnored),
                Interlocked.Read(ref _nOffFocus), Interlocked.Read(ref _nCanceled)));
            int d = Thread.VolatileRead(ref _delayMs);
            if (d != _cfg.DelayMs)
                Log.Say("delay_ms: " + _cfg.DelayMs + " -> " + d +
                        (_cfg.SaveTuning ? "   (da luu vao config)" : "   (CHUA luu: save_tuning = false)"));
            Log.Flush(1000);
            return 0;
        }

        private static void Shutdown(string why)
        {
            if (!_running) return;
            _running = false;
            if (why != null) Log.Say(why + " - dang thoat...");
            CancelPending(null);
            _wake.Set();
            if (_mainThreadId != 0)
                Native.PostThreadMessage(_mainThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        private static IEnumerable<Bind> AllBinds()
        {
            yield return _cfg.TriggerKey;
            yield return _cfg.ToggleKey;
            yield return _cfg.PanicKey;
            yield return _cfg.DelayUpKey;
            yield return _cfg.DelayDownKey;
            foreach (Bind b in _cfg.ArmKeys) yield return b;
            foreach (Bind b in _cfg.DisarmKeys) yield return b;
        }

        private static bool AnyBind(BindKind k)
        {
            foreach (Bind b in AllBinds()) if (b.Kind == k) return true;
            return false;
        }

        private static void ResolveOffsets()
        {
            Type kb = typeof(Native.KBDLLHOOKSTRUCT), ms = typeof(Native.MSLLHOOKSTRUCT);
            _kbVkOff = Marshal.OffsetOf(kb, "vkCode").ToInt32();
            _kbFlagsOff = Marshal.OffsetOf(kb, "flags").ToInt32();
            _kbExtraOff = Marshal.OffsetOf(kb, "dwExtraInfo").ToInt32();
            _msDataOff = Marshal.OffsetOf(ms, "mouseData").ToInt32();
            _msFlagsOff = Marshal.OffsetOf(ms, "flags").ToInt32();
            _msExtraOff = Marshal.OffsetOf(ms, "dwExtraInfo").ToInt32();
        }

        // ---------------------------------------------------------------------
        //  Hook callbacks - chay tren main thread, phai TRA VE THAT NHANH.
        //  Khong ghi console, khong doc file, khong hoi ten process o day.
        // ---------------------------------------------------------------------
        private static IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    int m = wParam.ToInt32();
                    bool down = m == Native.WM_KEYDOWN || m == Native.WM_SYSKEYDOWN;
                    bool up = m == Native.WM_KEYUP || m == Native.WM_SYSKEYUP;
                    if ((down || up) && Trusted((uint)Marshal.ReadInt32(lParam, _kbFlagsOff), Native.LLKHF_INJECTED,
                                                Marshal.ReadIntPtr(lParam, _kbExtraOff)))
                    {
                        int vk = Marshal.ReadInt32(lParam, _kbVkOff) & 0xFF;
                        long ts = Stopwatch.GetTimestamp();
                        if (down)
                        {
                            bool fresh = FreshPress(vk);
                            OnInput(vk, true, ts, !fresh);
                        }
                        else
                        {
                            _physDown[vk] = false;
                            OnInput(vk, false, ts, false);
                        }
                    }
                }
                catch { }   // ngoai le lot ra khoi callback native = crash ca process
            }
            return Native.CallNextHookEx(_hKb, nCode, wParam, lParam);
        }

        private static IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int m = wParam.ToInt32();
                // Chuot gaming bao vi tri 1000 lan/giay. Loc ngay tu ma message,
                // khong doc struct, khong cap phat -> hook khong lam chuot tre.
                if (m != Native.WM_MOUSEMOVE)
                {
                    try
                    {
                        int btn = 0;
                        bool down = false;
                        switch (m)
                        {
                            case Native.WM_LBUTTONDOWN: btn = 1; down = true; break;
                            case Native.WM_LBUTTONUP: btn = 1; break;
                            case Native.WM_RBUTTONDOWN: btn = 2; down = true; break;
                            case Native.WM_RBUTTONUP: btn = 2; break;
                            case Native.WM_MBUTTONDOWN: btn = 3; down = true; break;
                            case Native.WM_MBUTTONUP: btn = 3; break;
                            case Native.WM_XBUTTONDOWN:
                            case Native.WM_XBUTTONUP:
                                int x = (Marshal.ReadInt32(lParam, _msDataOff) >> 16) & 0xFFFF;
                                btn = x == 1 ? 4 : (x == 2 ? 5 : 0);
                                down = m == Native.WM_XBUTTONDOWN;
                                break;
                        }
                        if (btn != 0 && Trusted((uint)Marshal.ReadInt32(lParam, _msFlagsOff), Native.LLMHF_INJECTED,
                                                Marshal.ReadIntPtr(lParam, _msExtraOff)))
                            OnInput(Keys.MouseVk(btn), down, Stopwatch.GetTimestamp(), false);
                    }
                    catch { }
                }
            }
            return Native.CallNextHookEx(_hMs, nCode, wParam, lParam);
        }

        // V cua chinh tool thi luon bo qua. Input do phan mem khac sinh ra chi
        // duoc tin khi accept_injected = true.
        private static bool Trusted(uint flags, uint injectedBit, IntPtr extra)
        {
            if (extra == Native.SIGNATURE) return false;
            return _cfg.AcceptInjected || (flags & injectedBit) == 0;
        }

        // Low-level hook nhan ca auto-repeat (~30 lan/giay khi giu phim) ma khong
        // co bit repeat de loc -> tu theo doi canh xuong. Chot 1100 ms: lo mat
        // keyup (hook bi treo luc alt-tab, man hinh UAC...) thi sau 1.1 s phim van
        // song lai. Lau hon do tre lap toi da cua Windows (1000 ms) nen khong bao
        // gio nham voi repeat.
        private static bool FreshPress(int vk)
        {
            int now = Environment.TickCount;
            bool fresh = !_physDown[vk] || unchecked(now - _physLastMs[vk]) > 1100;
            _physDown[vk] = true;
            _physLastMs[vk] = now;
            return fresh;
        }

        // ---------------------------------------------------------------------
        //  Doc bang GetAsyncKeyState - loi thoat khi hook bi tu choi.
        //  Chi doc nhung nut ma hook khong lo duoc.
        // ---------------------------------------------------------------------
        private static void PollLoop()
        {
            var vks = new List<int>();
            foreach (Bind b in AllBinds())
            {
                if (b.IsNone) continue;
                bool mine = b.IsMouse ? _pollMouse : _pollKeys;
                if (mine && !vks.Contains(b.Vk)) vks.Add(b.Vk);
            }
            var prev = new bool[256];
            foreach (int vk in vks) prev[vk] = (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

            Native.timeBeginPeriod(1);
            try
            {
                while (_running)
                {
                    long ts = Stopwatch.GetTimestamp();
                    for (int i = 0; i < vks.Count; i++)
                    {
                        int vk = vks[i];
                        bool now = (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
                        if (now != prev[vk]) { prev[vk] = now; OnInput(vk, now, ts, false); }
                    }
                    Thread.Sleep(1);
                }
            }
            finally { Native.timeEndPeriod(1); }
        }

        // ---------------------------------------------------------------------
        //  Moi nguon input (hook phim, hook chuot, polling) deu do vao day.
        // ---------------------------------------------------------------------
        private static void OnInput(int vk, bool down, long ts, bool repeat)
        {
            if (repeat)
            {
                // Auto-repeat chi co nghia voi phim tune: giu NUMMINUS cho no chay.
                if (_cfg.DelayUpKey.Is(vk)) AdjustDelay(+_cfg.DelayStepMs);
                else if (_cfg.DelayDownKey.Is(vk)) AdjustDelay(-_cfg.DelayStepMs);
                return;
            }

            if (down && _cfg.PanicKey.Is(vk)) { Shutdown("PANIC (" + _cfg.PanicKey + ")"); return; }

            if (_cfg.TriggerKey.Is(vk) && down == (_cfg.From == StartFrom.Down)) Trigger(ts);
            if (!down) return;

            if (_cfg.ToggleKey.Is(vk)) SetArmed(!_armed, _cfg.ToggleKey.Name);

            // Phim doi vu khi chi tinh khi dang o trong game: go "2" trong Discord
            // khong duoc tat tool.
            if (!_cfg.RequireFocus || _gameFocused)
            {
                for (int i = 0; i < _cfg.ArmKeys.Length; i++)
                    if (_cfg.ArmKeys[i].Is(vk)) SetArmed(true, _cfg.ArmKeys[i].Name);
                for (int i = 0; i < _cfg.DisarmKeys.Length; i++)
                    if (_cfg.DisarmKeys[i].Is(vk)) SetArmed(false, _cfg.DisarmKeys[i].Name);
            }

            if (_cfg.DelayUpKey.Is(vk)) AdjustDelay(+_cfg.DelayStepMs);
            else if (_cfg.DelayDownKey.Is(vk)) AdjustDelay(-_cfg.DelayStepMs);
        }

        // ---------------------------------------------------------------------
        //  Mot cu click duoc nhan. Chi ghi lai thoi diem va danh thuc thread ban;
        //  moi viec con lai (focus, cho, bam) la cua thread ban.
        // ---------------------------------------------------------------------
        private static void Trigger(long ts)
        {
            if (!_armed) return;
            bool restarted = false, ignored = false;
            lock (_gate)
            {
                _nClicks++;
                if (_pending && _cfg.Reclick == ReclickMode.Ignore)
                {
                    _nIgnored++;
                    _pendingClicks++;
                    ignored = true;
                }
                else
                {
                    if (_pending) { _nRestart++; _pendingClicks++; restarted = true; }
                    else _pendingClicks = 1;
                    _pending = true;
                    _pendingTs = ts;
                    _seq++;
                }
            }
            if (!ignored) _wake.Set();
            if (_cfg.Verbose && (restarted || ignored))
                Log.Say(restarted ? "   click lai -> dem lai tu dau" : "   click lai -> bo qua, giu moc click dau");
        }

        private static void SetArmed(bool on, string why)
        {
            if (_armed == on) return;
            _armed = on;
            if (!on) CancelPending("tat");
            UpdateTitle();
            Log.Say(on ? ">> BAT  (" + why + ")" : "<< TAT  (" + why + ")");
        }

        private static void CancelPending(string why)
        {
            bool had;
            lock (_gate)
            {
                had = _pending;
                if (had) { _pending = false; _seq++; _nCanceled++; }
            }
            if (!had) return;
            _wake.Set();
            if (why != null && _cfg.Verbose) Log.Say("   huy " + _cfg.EmitKey + " dang cho (" + why + ")");
        }

        private static void AdjustDelay(int delta)
        {
            int old = Thread.VolatileRead(ref _delayMs);
            int v = old + delta;
            if (v < 0) v = 0;
            if (v > Config.DELAY_MAX) v = Config.DELAY_MAX;
            if (v == old) { Log.Say("   delay_ms da cham gioi han: " + v); return; }
            Interlocked.Exchange(ref _delayMs, v);
            _wake.Set();                    // V dang cho -> tinh lai moc ban theo so moi
            UpdateTitle();
            Log.Say(F("   delay_ms = {0}   ({1:0.0} frame o {2:0} fps)", v, v / _cfg.FrameMs, _cfg.GameFps));
            if (_cfg.SaveTuning)
            {
                // Ghi file tren thread log, khong bao gio tren thread hook.
                string path = _cfgPath;
                Log.Post(delegate
                {
                    try { Config.SaveValue(path, "delay_ms", v.ToString(CultureInfo.InvariantCulture)); }
                    catch (Exception ex) { Log.Warn("khong luu duoc delay_ms vao config: " + ex.Message); }
                });
            }
        }

        // ---------------------------------------------------------------------
        //  Thread ban - ngu cho toi moc click + delay_ms, roi bam V.
        // ---------------------------------------------------------------------
        private static void EmitLoop()
        {
            Native.timeBeginPeriod(1);
            try
            {
                int validated = int.MinValue;
                while (_running)
                {
                    bool has; long ts; int seq, clicks;
                    lock (_gate) { has = _pending; ts = _pendingTs; seq = _seq; clicks = _pendingClicks; }

                    if (!has)
                    {
                        _wake.WaitOne(100);
                        FocusOk(false);          // giu _gameFocused tuoi cho arm_keys / disarm_keys
                        continue;
                    }

                    // Click vua toi - thread nay thuc day trong ~1 ms sau click, nen
                    // kiem tra focus luc nay = kiem tra luc click. Click o Discord,
                    // desktop... khong duoc sinh ra V.
                    if (seq != validated)
                    {
                        validated = seq;
                        if (_cfg.RequireFocus && !FocusOk(true))
                        {
                            if (Claim(seq))
                            {
                                Interlocked.Increment(ref _nOffFocus);
                                if (_cfg.Verbose) Log.Say("   bo qua click: dang focus '" + _fgProc + "', khong phai game");
                            }
                            continue;
                        }
                    }

                    if (!WaitUntil(ts, seq)) continue;   // co click moi / bi huy -> tinh lai tu dau
                    if (!Claim(seq)) continue;           // click moi chen vao dung luc nay

                    // Ngay truoc khi bam: van phai dung cua so game. Alt-tab giua chung = huy.
                    if (_cfg.RequireFocus && !FocusOk(true))
                    {
                        Interlocked.Increment(ref _nOffFocus);
                        Log.Say("   huy " + _cfg.EmitKey + ": da roi khoi game (dang focus '" + _fgProc + "')");
                        continue;
                    }
                    Fire(ts, clicks);
                }
            }
            finally { Native.timeEndPeriod(1); }
        }

        // Ngu toi moc ban. Tra ve false neu giua chung co click moi hoac bi huy.
        // Moc tinh lai moi vong vi delay_ms co the vua bi tune nong. Ngu dai bang
        // WaitOne (click moi danh thuc ngay), 2 ms cuoi thi spin cho chinh xac.
        private static bool WaitUntil(long clickTs, int seq)
        {
            while (_running)
            {
                if (_seq != seq) return false;
                double rem = Ms(clickTs) + Thread.VolatileRead(ref _delayMs) - NowMs();
                if (rem <= 0) return true;
                if (rem > 4.0) _wake.WaitOne((int)(rem - 3.0));
                else if (rem > 2.0) Thread.Sleep(1);
                else Thread.SpinWait(100);
            }
            return false;
        }

        // Nhan lan ban nay ve minh. Thua neu click moi vua chen vao.
        private static bool Claim(int seq)
        {
            lock (_gate)
            {
                if (!_pending || _seq != seq) return false;
                _pending = false;
                return true;
            }
        }

        private static void Fire(long clickTs, int clicks)
        {
            double click = Ms(clickTs);
            double firstDown = 0, firstUp = 0, lastUp = 0;
            int n = 0;
            for (int i = 0; i < _cfg.PressCount; i++)
            {
                if (i > 0)
                {
                    SleepUntil(lastUp + _cfg.PressGapMs);
                    if (!_running || !_armed || (_cfg.RequireFocus && !FocusOk(true))) break;
                }
                double t0 = NowMs();
                _vDown = true;
                _emit.Down();
                SleepUntil(t0 + _cfg.HoldMs);   // khong kiem tra _running: da xuong thi phai nha
                double t1 = NowMs();            // do TRUOC lenh nha, giong t0 do truoc lenh xuong
                _emit.Up();
                _vDown = false;
                if (i == 0) { firstDown = t0; firstUp = t1; }
                lastUp = t1;
                n++;
            }
            Interlocked.Increment(ref _nFired);

            if (_record)
            {
                var r = new FireRec();
                r.ClickMs = click; r.DownMs = firstDown; r.UpMs = firstUp; r.Presses = n;
                lock (_fires) _fires.Add(r);
            }
            if (_cfg.Verbose)
            {
                string from = clicks <= 1 ? "click"
                            : (_cfg.Reclick == ReclickMode.Restart ? "click cuoi" : "click dau");
                Log.Say(F("   {0}  +{1:0.0} ms sau {2}   giu {3:0.0} ms{4}{5}",
                    _cfg.EmitKey, firstDown - click, from, firstUp - firstDown,
                    n > 1 ? "   x" + n : "",
                    clicks > 1 ? "   (" + clicks + " click -> 1 lan)" : ""));
            }
        }

        // Ngu chinh xac: Sleep(1) khi con xa, spin khi gan. Sleep thuan sai +-15 ms.
        private static void SleepUntil(double targetMs)
        {
            while (true)
            {
                double rem = targetMs - NowMs();
                if (rem <= 0) return;
                if (rem > 2.0) Thread.Sleep(1);
                else Thread.SpinWait(150);
            }
        }

        // ---------------------------------------------------------------------
        //  Chot an toan: chi bam V khi dung cua so game dang o truoc.
        // ---------------------------------------------------------------------
        private static bool FocusOk(bool fresh)
        {
            double now = NowMs();
            if (!fresh && now - _focusAt < 100) return _gameFocused;
            _focusAt = now;
            bool ok = CheckForeground(now);
            _gameFocused = ok;
            return ok;
        }

        private static bool CheckForeground(double now)
        {
            IntPtr hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) { _fgProc = "(khong co cua so)"; return false; }
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if ((int)pid == _selfPid) { _fgProc = "VAssist"; return false; }   // dung bam vao chinh minh
            // Ten process cache theo pid; lam moi moi 2 s phong khi pid bi tai su dung.
            if (pid != _fgPid || now - _fgPidAt > 2000)
            {
                _fgPid = pid;
                _fgPidAt = now;
                _fgProc = SafeProcName(pid);
            }
            if (_cfg.TargetProcess.Length == 0) return true;
            foreach (string t in _cfg.TargetProcess)
                if (_fgProc.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static string SafeProcName(uint pid)
        {
            try { return Process.GetProcessById((int)pid).ProcessName; }
            catch { return ""; }
        }

        // Nha V neu dang giu - tranh ket phim khi thoat dot ngot.
        private static void SafeRelease()
        {
            try
            {
                if (_emit != null && _vDown) { _emit.Up(); _vDown = false; }
            }
            catch { }
        }

        // O che do bat/tat, biet dang BAT hay TAT la quan trong nhat. Tieu de cua
        // so thay ngay tren taskbar ma khong can alt-tab ra doc log.
        private static void UpdateTitle()
        {
            Log.Title((_armed ? "[BAT] " : "[tat] ") + "VAssist   " + _cfg.TriggerKey + " -> " +
                      Thread.VolatileRead(ref _delayMs) + " ms -> " + _cfg.EmitKey +
                      (_cfg.ToggleKey.IsNone ? "" : "     (" + _cfg.ToggleKey + " = bat/tat)"));
        }

        // ---------------------------------------------------------------------
        private static void Usage()
        {
            Console.WriteLine("VAssist [config.ini] [che do]");
            Console.WriteLine();
            Console.WriteLine("  (khong co gi)        chay: chuot phai -> cho delay_ms -> bam V");
            Console.WriteLine("  --watch              do ten process cua so dang focus (tim ten exe cua game)");
            Console.WriteLine("  --probe [fps] [ms]   game chay o fps nay thay lich the nao;");
            Console.WriteLine("                       them so ms nhat chem de tinh ty le V den som");
            Console.WriteLine("  --selftest [n]       chay bo lich that voi F24 (vo hai), do do chinh xac");
            Console.WriteLine("  --admin              chay lai bang quyen Administrator (khi game chay admin)");
            Console.WriteLine("  --help");
        }

        private static void Banner()
        {
            int d = Thread.VolatileRead(ref _delayMs);
            Console.WriteLine("================================================================");
            Console.WriteLine(" VAssist - chuot phai -> CHO dao chem xong -> moi bam " + _cfg.EmitKey);
            Console.WriteLine("           tang OS: hook + SendInput, khong dung vao game");
            Console.WriteLine("================================================================");
            Console.WriteLine(" config   : " + _cfgPath + (File.Exists(_cfgPath) ? "" : "  (khong thay -> dung mac dinh)"));
            Console.WriteLine(" quyen    : " + (IsElevated() ? "Administrator" : "nguoi dung thuong"));
            Console.WriteLine("----------------------------------------------------------------");
            Console.WriteLine(F(" lich     : {0} {1}  --( {2} ms )-->  {3} xuong, giu {4:0} ms, nha{5}",
                _cfg.TriggerKey, _cfg.From == StartFrom.Down ? "xuong" : "nha ra", d, _cfg.EmitKey, _cfg.HoldMs,
                _cfg.PressCount > 1 ? F("   (x{0}, nghi {1:0} ms)", _cfg.PressCount, _cfg.PressGapMs) : ""));
            Console.WriteLine(F("            {0} ms = {1:0.0} frame o {2:0} fps", d, d / _cfg.FrameMs, _cfg.GameFps));
            Console.WriteLine(" click lai: " + (_cfg.Reclick == ReclickMode.Restart
                ? "restart - dem lai tu click MOI NHAT -> V khong bao gio den som"
                : "ignore  - giu moc click DAU, bo qua click trong luc cho"));
            Console.WriteLine(" bat/tat  : " + (_cfg.ToggleKey.IsNone ? "(khong co phim)" : _cfg.ToggleKey.Name) +
                              "    luc khoi dong: " + (_armed ? "BAT" : "TAT"));
            if (_cfg.ArmKeys.Length > 0 || _cfg.DisarmKeys.Length > 0)
                Console.WriteLine(" tu dong  : bam " + Keys.Names(_cfg.ArmKeys) + " -> BAT     bam " +
                                  Keys.Names(_cfg.DisarmKeys) + " -> TAT   (chi khi dang o trong game)");
            if (!_cfg.DelayUpKey.IsNone || !_cfg.DelayDownKey.IsNone)
                Console.WriteLine(F(" tune nong: {0} / {1} = +/- {2} ms{3}", _cfg.DelayUpKey, _cfg.DelayDownKey,
                    _cfg.DelayStepMs, _cfg.SaveTuning ? "   (tu luu vao config)" : ""));
            Console.WriteLine(F(" gui      : {0}   ({1}: vk 0x{2:X2}, scancode 0x{3:X2})",
                _cfg.SendMode, _cfg.EmitKey, _cfg.EmitKey.Code, _emit.ScanCode));
            Console.WriteLine(" chi khi  : " + (_cfg.RequireFocus
                ? "cua so focus la " + (_cfg.TargetProcess.Length > 0
                    ? "[" + string.Join(", ", _cfg.TargetProcess) + "]"
                    : "BAT KY (nen dat target_process)")
                : "LUON LUON (require_focus = false)"));
            Console.WriteLine("----------------------------------------------------------------");
            Console.WriteLine(" " + _cfg.PanicKey + " = thoat ngay, nha het phim.");
            Console.WriteLine("----------------------------------------------------------------");
        }

        // Nhung cai khien nguoi ta ngoi ca buoi khong hieu vi sao khong an.
        private static void Preflight()
        {
            double frame = _cfg.FrameMs;
            int d = Thread.VolatileRead(ref _delayMs);

            if (_cfg.HoldMs <= frame)
                Warn(F("hold_ms = {0:0.0} khong vuot 1 frame ({1:0.0} ms o {2:0} fps). Game doc phim MOT lan moi " +
                       "frame (Unity Input.GetKey, DirectInput) se bo sot {3} - dung trieu chung 'tool bam ma game " +
                       "khong nhan'. Dat hold_ms >= {4:0}.",
                       _cfg.HoldMs, frame, _cfg.GameFps, _cfg.EmitKey, Math.Ceiling(frame * 1.5)));

            if (_cfg.PressCount > 1 && _cfg.PressGapMs <= frame)
                Warn(F("press_gap_ms = {0:0.0} khong vuot 1 frame ({1:0.0} ms): game se thay {2} lan bam dinh " +
                       "thanh MOT lan giu dai. Dat press_gap_ms >= {3:0}.",
                       _cfg.PressGapMs, frame, _cfg.PressCount, Math.Ceiling(frame * 1.5)));

            if (d < 100)
                Warn(F("delay_ms = {0}: gan nhu bam {1} ngay luc click, dao chua kip chem. " +
                       "Day dung la cai ban muon tranh.", d, _cfg.EmitKey));

            if (_cfg.RequireFocus && _cfg.TargetProcess.Length == 0)
                Warn("target_process de trong -> tool bam " + _cfg.EmitKey + " vao BAT KY cua so nao dang focus. " +
                     "Chay 'VAssist.exe --watch' lay ten exe cua game roi dien vao.");
            if (!_cfg.RequireFocus)
                Warn("require_focus = false: click " + _cfg.TriggerKey + " o BAT CU DAU (desktop, trinh duyet...) " +
                     "cung sinh ra " + _cfg.EmitKey + ".");

            if (_cfg.TriggerKey.Is(Keys.MouseVk(1)))
                Warn("trigger_key = MOUSE1 (chuot trai): moi phat ban / nhat chem thuong deu sinh ra " + _cfg.EmitKey +
                     ". Chem nang thuong la MOUSE2.");

            if (_emit.ScanCode == 0 && _cfg.SendMode != "vk")
                Warn("emit_key khong co scancode tren layout ban phim nay -> dat send_mode = vk.");

            // Trung phim: moi nut chi nen lam mot viec.
            var named = new List<KeyValuePair<string, Bind>>();
            named.Add(new KeyValuePair<string, Bind>("trigger_key", _cfg.TriggerKey));
            named.Add(new KeyValuePair<string, Bind>("toggle_key", _cfg.ToggleKey));
            named.Add(new KeyValuePair<string, Bind>("panic_key", _cfg.PanicKey));
            named.Add(new KeyValuePair<string, Bind>("delay_up_key", _cfg.DelayUpKey));
            named.Add(new KeyValuePair<string, Bind>("delay_down_key", _cfg.DelayDownKey));
            foreach (Bind b in _cfg.ArmKeys) named.Add(new KeyValuePair<string, Bind>("arm_keys", b));
            foreach (Bind b in _cfg.DisarmKeys) named.Add(new KeyValuePair<string, Bind>("disarm_keys", b));
            for (int i = 0; i < named.Count; i++)
            {
                Bind a = named[i].Value;
                if (a.IsNone) continue;
                if (a.Kind == BindKind.Key && a.Code == _cfg.EmitKey.Code)
                    Warn(named[i].Key + " = " + a.Name + " trung emit_key. Hook tu loc " + _cfg.EmitKey +
                         " cua chinh tool, nhung o che do poll thi KHONG -> tool tu kich hoat chinh no.");
                for (int j = i + 1; j < named.Count; j++)
                {
                    Bind b = named[j].Value;
                    if (b.IsNone || a.Vk != b.Vk || named[i].Key == named[j].Key) continue;
                    Warn(named[i].Key + " va " + named[j].Key + " cung la " + a.Name + " -> mot cu bam lam hai viec.");
                }
            }

            if (!IsElevated())
                Log.Say("   (chua chay quyen admin. Neu game chay Administrator thi hook khong nghe duoc chuot trong game" +
                        " va SendInput bi chan im lang -> VAssist.exe --admin)");
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
            Console.WriteLine(" target_process hien tai: " +
                              (_cfg.TargetProcess.Length > 0 ? string.Join(", ", _cfg.TargetProcess) : "(trong)"));
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
                    bool match = false;
                    foreach (string t in _cfg.TargetProcess)
                        if (pname.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) match = true;
                    Console.WriteLine();
                    Console.WriteLine("  process : " + pname + "   (pid " + pid + ")");
                    Console.WriteLine("  tieu de : " + title);
                    Console.WriteLine("  quyen   : " + (CanInspect(pid)
                        ? "doc duoc (cung muc quyen)"
                        : "KHONG doc duoc -> nhieu kha nang chay Administrator, can --admin"));
                    Console.WriteLine("  khop    : " + (match
                        ? "CO - tool se bam " + _cfg.EmitKey + " o cua so nay"
                        : "khong - tool se im o cua so nay"));
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
        //  --probe : game chay N fps thi THAY lich nay the nao.
        //  Tool dem gio chinh xac toi 0.1 ms, nhung game chi nhin dong ho qua
        //  tung frame: click va V deu chi duoc thay o frame ke tiep. Day la sai
        //  so that su, va la ly do delay_ms can them ~1 frame bien an toan.
        // =====================================================================
        private const double PROBE_JITTER = 0.15;   // frame time dao dong +-15 %
        private const int PROBE_N = 20000;

        private static int Probe(double fps, double slashMs)
        {
            if (fps < 10) fps = 10;
            if (fps > 1000) fps = 1000;
            double fr = 1000.0 / fps;
            double D = Thread.VolatileRead(ref _delayMs), H = _cfg.HoldMs;

            Console.WriteLine("================================================================");
            Console.WriteLine(F(" PROBE - game chay {0:0} fps ({1:0.0} ms/frame) thay lich nay the nao?", fps, fr));
            Console.WriteLine("================================================================");
            Console.WriteLine(" LICH CUA TOOL (dong ho that):");
            Console.WriteLine(F("   +{0,7:0.0} ms   {1,-8} {2,-6}  <- ban click, dao bat dau chem",
                0.0, _cfg.TriggerKey, _cfg.From == StartFrom.Down ? "xuong" : "nha"));
            double t = D;
            for (int i = 0; i < _cfg.PressCount; i++)
            {
                if (i > 0) t += _cfg.PressGapMs;
                Console.WriteLine(F("   +{0,7:0.0} ms   {1,-8} xuong {2}", t, _cfg.EmitKey, i == 0 ? "  <- delay_ms" : ""));
                t += H;
                Console.WriteLine(F("   +{0,7:0.0} ms   {1,-8} len   {2}", t, _cfg.EmitKey, i == 0 ? "  <- hold_ms" : ""));
            }
            if (_cfg.From == StartFrom.Up)
                Console.WriteLine("   (start_from = up: dem tu luc NHA chuot. Nhat chem thuong bat dau tu luc NHAN,\n" +
                                  "    nen thuc te V con tre hon bang thoi gian ban giu chuot.)");
            Console.WriteLine("----------------------------------------------------------------");

            Console.WriteLine(" 1) Game co THAY cu bam " + _cfg.EmitKey + " khong?");
            Console.WriteLine("    Game Unity / DirectInput doc phim MOT lan moi frame. Bam xuong roi nha");
            Console.WriteLine("    gon giua hai lan doc = game khong he biet.");
            if (H > fr * (1 + PROBE_JITTER))
                Console.WriteLine(F("    giu {0:0.0} ms = {1:0.0} frame  -> CO, moi lan. Chiu duoc fps tut toi {2:0} fps.",
                    H, H / fr, 1000.0 / H));
            else
                Console.WriteLine(F("    giu {0:0.0} ms ~ 1 frame -> co lan khong thay. Tang hold_ms len >= {1:0}.",
                    H, Math.Ceiling(fr * 1.5)));
            Console.WriteLine("----------------------------------------------------------------");

            // Chia khoang theo ms THAT ma game thay (frame time dao dong nen
            // "so frame x 16.7" khong phai so ms that).
            double bw = Math.Max(1.0, Math.Round(fr / 3.0));
            var rnd = new Random(20261123);
            var hist = new SortedDictionary<int, int>();
            int miss = 0;
            double minI = double.MaxValue, maxI = 0;
            for (int k = 0; k < PROBE_N; k++)
            {
                double tc, tv; int frames;
                if (!SimOnce(rnd, fr, D, H, out tc, out tv, out frames)) { miss++; continue; }
                double iv = tv - tc;
                int bucket = (int)Math.Floor(iv / bw);
                int c;
                hist.TryGetValue(bucket, out c);
                hist[bucket] = c + 1;
                if (iv < minI) minI = iv;
                if (iv > maxI) maxI = iv;
            }
            Console.WriteLine(" 2) Game thay " + _cfg.EmitKey + " cach click bao lau?");
            Console.WriteLine("    Click va " + _cfg.EmitKey + " deu chi duoc thay o frame KE TIEP, nen game khong thay");
            Console.WriteLine(F("    dung {0:0} ms (mo phong {1} lan, frame time dao dong +-{2:0} %):",
                D, PROBE_N, PROBE_JITTER * 100));
            foreach (KeyValuePair<int, int> kv in hist)
            {
                double pct = 100.0 * kv.Value / PROBE_N;
                if (pct < 0.05) continue;
                string range = F("{0:0}..{1:0} ms", kv.Key * bw, (kv.Key + 1) * bw);
                Console.WriteLine(F("       {0,-14} {1,-25} {2,5:0.0} %",
                    range, new string('#', (int)Math.Ceiling(pct / 2)), pct));
            }
            if (miss > 0)
                Console.WriteLine(F("       BO SOT              {0,5:0.0} %   (nha {1} truoc khi frame ke tiep kip doc)",
                    100.0 * miss / PROBE_N, _cfg.EmitKey));
            if (miss < PROBE_N)
                Console.WriteLine(F("    => som nhat {0:0.0} ms, tre nhat {1:0.0} ms: sai so ~ +-1 frame.", minI, maxI));
            Console.WriteLine(F("       Chinh delay_ms tung buoc nho hon 1 frame ({0:0.0} ms) gan nhu khong khac gi.", fr));
            Console.WriteLine("----------------------------------------------------------------");

            if (slashMs > 0)
            {
                int early = 0, lateN = 0, missed = 0;
                double lateSum = 0;
                rnd = new Random(7);
                for (int k = 0; k < PROBE_N; k++)
                {
                    double tc, tv; int frames;
                    if (!SimOnce(rnd, fr, D, H, out tc, out tv, out frames)) { missed++; continue; }
                    if (tv < tc + slashMs) early++;
                    else { lateSum += tv - (tc + slashMs); lateN++; }
                }
                Console.WriteLine(F(" 3) Neu nhat chem dai {0:0} ms:", slashMs));
                Console.WriteLine(F("    {0} den TRUOC khi chem xong : {1,5:0.0} %    <- game nuot mat / cat ngang nhat chem",
                    _cfg.EmitKey, 100.0 * early / PROBE_N));
                if (lateN > 0)
                    Console.WriteLine(F("    {0} den sau, tre trung binh : {1,5:0.0} ms   <- thoi gian phi",
                        _cfg.EmitKey, lateSum / lateN));
                int safe = SafeDelay(fr, H, slashMs);
                Console.WriteLine(F("    => delay_ms an toan nho nhat: ~{0} ms  (= chem + {1:0} ms, ~{2:0.0} frame)",
                    safe, safe - slashMs, (safe - slashMs) / fr));
                if (D < safe)
                    Warn(F("delay_ms hien tai ({0:0}) nho hon muc an toan -> se co lan {1} den som.", D, _cfg.EmitKey));
                else if (D > safe + 2 * fr)
                    Console.WriteLine(F("    (delay_ms hien tai {0:0} con du ~{1:0} ms -> co the ha bot)", D, D - safe));
            }
            else
            {
                Console.WriteLine(" 3) Biet (hoac doan) nhat chem dai bao nhieu ms? Them vao lenh de tinh ty le");
                Console.WriteLine(F("    {0} den som:   VAssist.exe --probe {1:0} {2:0}",
                    _cfg.EmitKey, fps, Math.Max(0, D - 20)));
            }
            return 0;
        }

        // Mot lan mo phong. Frame dau tien sau click = luc game thay click (nhat
        // chem bat dau); frame dau tien sau luc V xuong = luc game thay V. V da nha
        // truoc frame do thi game khong thay (tra ve false).
        private static bool SimOnce(Random rnd, double fr, double D, double H,
                                    out double tc, out double tv, out int frames)
        {
            double t = -rnd.NextDouble() * fr;   // frame gan nhat TRUOC luc click
            tc = double.NaN; tv = double.NaN; frames = 0;
            int ic = -1, i = 0;
            while (true)
            {
                t += fr * (1.0 + (rnd.NextDouble() * 2.0 - 1.0) * PROBE_JITTER);
                i++;
                if (ic < 0 && t >= 0) { ic = i; tc = t; }
                if (t >= D)
                {
                    if (t >= D + H) return false;
                    tv = t;
                    frames = i - ic;
                    return true;
                }
            }
        }

        // delay_ms nho nhat ma 4000 lan mo phong khong co lan nao V den som.
        private static int SafeDelay(double fr, double H, double slash)
        {
            int lo = (int)Math.Floor(slash), hi = (int)Math.Ceiling(slash + 6 * fr);
            for (int d = lo; d <= hi; d++)
            {
                var rnd = new Random(99);
                bool bad = false;
                for (int k = 0; k < 4000 && !bad; k++)
                {
                    double tc, tv; int frames;
                    if (!SimOnce(rnd, fr, d, H, out tc, out tv, out frames)) bad = true;
                    else if (tv < tc + slash) bad = true;
                }
                if (!bad) return d;
            }
            return hi;
        }

        // =====================================================================
        //  --selftest : chay DUNG thread ban that, dung bo lich that, nhung tu
        //  goi Trigger() thay cho click va bam F24 (VK 0x87) thay cho V.
        //  Khong dat hook, khong dung chuot that, khong ghi config.
        // =====================================================================
        private static int SelfTest(int trials)
        {
            if (trials < 2) trials = 2;
            if (trials > 50) trials = 50;
            _cfg.RequireFocus = false;
            _cfg.Verbose = false;
            _cfg.SaveTuning = false;
            _armed = true;
            _emit = new KeyEmitter(Keys.FromVk(0x87, "F24"), _cfg.SendMode);
            int D = Thread.VolatileRead(ref _delayMs);
            double H = _cfg.HoldMs;
            int tail = (int)Math.Ceiling(_cfg.BurstMs) + 150;

            Console.WriteLine("================================================================");
            Console.WriteLine(" SELF-TEST - chay DUNG bo lich that, nhung bam F24 thay cho " + _cfg.EmitKey);
            Console.WriteLine("================================================================");
            Console.WriteLine(F(" lich     : click --( {0} ms )--> F24 giu {1:0} ms{2}", D, H,
                _cfg.PressCount > 1 ? F("   x{0}, nghi {1:0} ms", _cfg.PressCount, _cfg.PressGapMs) : ""));
            Console.WriteLine(" phim     : F24 - vo hai, khong ung dung nao dung");
            Console.WriteLine(" click    : self-test tu goi vao bo lich - khong hook, khong dung chuot that");
            Console.WriteLine("----------------------------------------------------------------");

            _record = true;
            _emitThread = new Thread(EmitLoop);
            _emitThread.IsBackground = true;
            _emitThread.Priority = ThreadPriority.AboveNormal;
            _emitThread.Start();
            Thread.Sleep(50);

            var rnd = new Random(7);
            bool ok = true;

            // [1] click don
            Console.WriteLine(F(" [1] {0} click don, cach nhau ngau nhien", trials));
            var lat = new List<double>();
            var hold = new List<double>();
            for (int i = 0; i < trials; i++)
            {
                ClearFires();
                long ts = Stopwatch.GetTimestamp();
                Trigger(ts);
                Thread.Sleep(D + tail + rnd.Next(0, 150));
                List<FireRec> f = TakeFires();
                if (f.Count != 1)
                {
                    ok = false;
                    Console.WriteLine(F("     lan {0}: ban {1} lan (phai la 1)  -> SAI", i + 1, f.Count));
                    continue;
                }
                lat.Add(f[0].DownMs - Ms(ts));
                hold.Add(f[0].UpMs - f[0].DownMs);
            }
            if (lat.Count > 0)
            {
                double m, sd, mn, mx, hm, hsd, hmn, hmx;
                Stats(lat, out m, out sd, out mn, out mx);
                Stats(hold, out hm, out hsd, out hmn, out hmx);
                Console.WriteLine(F("     F24 sau click : tb {0:0.00} ms   sd {1:0.00}   min {2:0.00}   max {3:0.00}   (dat {4})",
                    m, sd, mn, mx, D));
                Console.WriteLine(F("     giu F24       : tb {0:0.00} ms   min {1:0.00}   max {2:0.00}   (dat {3:0})",
                    hm, hmn, hmx, H));
                bool good = mn >= D - 0.01 && mx <= D + 2.0 && hmn >= H - 0.01;
                if (!good) ok = false;
                Console.WriteLine(good ? "     -> DUNG: khong lan nao som, tre nhat duoi 2 ms"
                                       : "     -> LECH: co lan som hoac tre qua 2 ms");
            }

            if (D < 60)
            {
                Console.WriteLine(" [2..4] bo qua: delay_ms < 60 ms, qua ngan de thu click lai / tat / tune giua chung.");
            }
            else
            {
                // [2] click lai giua chung, ca hai che do
                ReclickMode keep = _cfg.Reclick;
                ReclickMode[] modes = new ReclickMode[] { ReclickMode.Restart, ReclickMode.Ignore };
                foreach (ReclickMode mode in modes)
                {
                    _cfg.Reclick = mode;
                    ClearFires();
                    long t1 = Stopwatch.GetTimestamp();
                    Trigger(t1);
                    Thread.Sleep(D / 2);
                    long t2 = Stopwatch.GetTimestamp();
                    Trigger(t2);
                    Thread.Sleep(D + tail);
                    List<FireRec> f = TakeFires();
                    long refTs = mode == ReclickMode.Restart ? t2 : t1;
                    double at = f.Count == 1 ? f[0].DownMs - Ms(refTs) : double.NaN;
                    bool good = f.Count == 1 && at >= D - 0.01 && at <= D + 2.0;
                    if (!good) ok = false;
                    Console.WriteLine(F(" [2] click lai sau {0} ms, reclick = {1}", D / 2,
                        mode == ReclickMode.Restart ? "restart" : "ignore"));
                    Console.WriteLine(good
                        ? F("     ban 1 lan, +{0:0.00} ms sau click {1}  -> DUNG", at,
                            mode == ReclickMode.Restart ? "THU HAI" : "DAU TIEN")
                        : F("     ban {0} lan  -> SAI", f.Count));
                }
                _cfg.Reclick = keep;

                // [3] tat tool trong luc dang cho
                {
                    ClearFires();
                    Trigger(Stopwatch.GetTimestamp());
                    Thread.Sleep(D / 3);
                    _armed = false;
                    CancelPending(null);
                    Thread.Sleep(D + tail);
                    List<FireRec> f = TakeFires();
                    _armed = true;
                    bool good = f.Count == 0;
                    if (!good) ok = false;
                    Console.WriteLine(" [3] tat tool trong luc " + _cfg.EmitKey + " dang cho");
                    Console.WriteLine(good ? "     khong ban gi  -> DUNG" : F("     van ban {0} lan  -> SAI", f.Count));
                }

                // [4] tune nong trong luc dang cho
                {
                    int step = Math.Max(_cfg.DelayStepMs, 10) * 3;
                    ClearFires();
                    long ts = Stopwatch.GetTimestamp();
                    Trigger(ts);
                    Thread.Sleep(D / 3);
                    Interlocked.Exchange(ref _delayMs, D + step);
                    _wake.Set();
                    Thread.Sleep(D + step - D / 3 + tail);
                    List<FireRec> f = TakeFires();
                    Interlocked.Exchange(ref _delayMs, D);
                    double at = f.Count == 1 ? f[0].DownMs - Ms(ts) : double.NaN;
                    bool good = f.Count == 1 && at >= D + step - 0.01 && at <= D + step + 2.0;
                    if (!good) ok = false;
                    Console.WriteLine(F(" [4] doi delay_ms {0} -> {1} trong luc dang cho (tune nong)", D, D + step));
                    Console.WriteLine(good
                        ? F("     ban o +{0:0.00} ms  -> DUNG, theo so moi", at)
                        : F("     ban {0} lan{1}  -> SAI", f.Count, f.Count == 1 ? F(" o +{0:0.00} ms", at) : ""));
                }
            }

            _running = false;
            _wake.Set();
            _emitThread.Join(2000);
            SafeRelease();

            Console.WriteLine("----------------------------------------------------------------");
            Console.WriteLine(F(" SendInput : {0} lan bi tu choi{1}", KeyEmitter.Failures,
                KeyEmitter.Failures > 0 ? "  (win32 error " + KeyEmitter.LastError + ")" : "  - OK"));
            if (KeyEmitter.Failures > 0) { Warn("SendInput bi chan. Thu: VAssist.exe --admin"); ok = false; }
            else if (ok) Console.WriteLine(" KET LUAN: lich dung moi truong hop, chinh xac duoi 1 ms.\n" +
                                           "           Sai so con lai nam o frame cua game -> xem --probe.");
            else Warn("Co truong hop SAI/LECH. May dang tai nang? Dong bot ung dung roi chay lai.");
            return ok ? 0 : 1;
        }

        private static void ClearFires() { lock (_fires) _fires.Clear(); }

        private static List<FireRec> TakeFires()
        {
            lock (_fires)
            {
                var l = new List<FireRec>(_fires);
                _fires.Clear();
                return l;
            }
        }

        private static void Stats(List<double> v, out double mean, out double sd, out double min, out double max)
        {
            double s = 0;
            min = double.MaxValue; max = double.MinValue;
            foreach (double x in v) { s += x; if (x < min) min = x; if (x > max) max = x; }
            mean = s / v.Count;
            double q = 0;
            foreach (double x in v) q += (x - mean) * (x - mean);
            sd = Math.Sqrt(q / v.Count);
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
            // Process chay bang runas co the bat dau o System32 -> doi moi duong
            // dan file sang tuyet doi truoc khi truyen qua.
            var keep = new List<string>();
            foreach (string a in args)
            {
                if (a.Equals("--admin", StringComparison.OrdinalIgnoreCase)) continue;
                string v = File.Exists(a) ? Path.GetFullPath(a) : a;
                keep.Add("\"" + v + "\"");
            }
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = Process.GetCurrentProcess().MainModule.FileName;
                psi.Arguments = string.Join(" ", keep.ToArray());
                psi.WorkingDirectory = Environment.CurrentDirectory;
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

        private static string F(string fmt, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, fmt, args);
        }

        private static void Warn(string s) { Log.Warn(s); }
    }
}
