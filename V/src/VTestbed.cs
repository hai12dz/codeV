// =============================================================================
//  VTestbed - "game cua ban" de thu VAssist truoc khi vao game that
// -----------------------------------------------------------------------------
//  Mo phong dung MOT co che: nhat chem nang khoa nhan vat trong slash_ms.
//
//     chuot phai           -> dao bat dau chem, khoa slash_ms
//     V trong luc chem     -> BI NUOT. Game that cung vay: dang animation thi
//                             lenh khac bi bo qua (hoac cat ngang nhat chem).
//     V sau khi chem xong  -> AN. Tre bao nhieu ms = thoi gian phi.
//     chuot phai trong luc dang chem -> game bo qua (dao dang ban).
//
//  Phim:
//     chuot phai  chem               V            lenh can do
//     UP / DOWN   slash_ms +-10      PGUP / PGDN  slash_ms +-50
//     F5  doc input: LATCH (moi message) <-> POLL (chup trang thai moi frame)
//     F7  fps khi o POLL: 30 / 60 / 100 / 144
//     F9  xoa so lieu
//
//  O POLL, click va V chi duoc thay o frame KE TIEP -> sai so +-1 frame, giong
//  het game Unity doc Input.GetKey moi frame. V giu ngan hon 1 frame -> BO SOT.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace VTestbed
{
    internal enum Verdict { Waiting, Ok, Early, Missed, NoV }

    internal sealed class Attempt
    {
        public double ClickAt;            // dong ho that, luc testbed nhan click
        public double SlashStart;         // = ClickAt (LATCH) | frame thay click (POLL)
        public double SlashEnd;
        public double VDownAt = double.NaN, VUpAt = double.NaN, VSeenAt = double.NaN;
        public Verdict Result = Verdict.Waiting;
        public int BusyClicks;            // click them trong luc dang chem - game bo qua
        public bool Poll;
        public int Fps;
        public int SlashMs;

        public bool HasV { get { return !double.IsNaN(VDownAt); } }
        public double Interval { get { return VDownAt - ClickAt; } }   // click -> V, dong ho that
        public double Late { get { return VSeenAt - SlashEnd; } }      // > 0 tre, < 0 som
        public double Hold { get { return VUpAt - VDownAt; } }
    }

    internal sealed class BenchForm : Form
    {
        private const int VK_V = 0x56;

        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly System.Windows.Forms.Timer _timer;
        private readonly List<Attempt> _hist = new List<Attempt>();
        private Attempt _cur;
        private int _slashMs = 680;
        private bool _poll;
        private int _fps = 60;
        private int _loneV;

        private static readonly Color Bg = Color.FromArgb(16, 18, 24);
        private static readonly Color PanelC = Color.FromArgb(22, 25, 33);
        private static readonly Color EdgeC = Color.FromArgb(38, 43, 55);
        private static readonly Color Fg = Color.FromArgb(225, 230, 240);
        private static readonly Color Dim = Color.FromArgb(120, 130, 148);
        private static readonly Color SlashC = Color.FromArgb(249, 115, 22);
        private static readonly Color OkC = Color.FromArgb(52, 211, 153);
        private static readonly Color BadC = Color.FromArgb(232, 86, 86);
        private static readonly Color WarnC = Color.FromArgb(250, 204, 21);

        private readonly Font _f = new Font("Consolas", 9.5f);
        private readonly Font _fMid = new Font("Consolas", 11f, FontStyle.Bold);
        private readonly Font _fBig = new Font("Consolas", 30f, FontStyle.Bold);

        public BenchForm()
        {
            Text = "VTestbed - thu VAssist";
            ClientSize = new Size(960, 624);
            BackColor = Bg;
            DoubleBuffered = true;
            KeyPreview = true;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 30;
            _timer.Tick += OnTick;
            _timer.Start();
        }

        private double Now { get { return _sw.Elapsed.TotalMilliseconds; } }

        // Frame dau tien >= t. Game POLL chi thay su kien o frame ke tiep.
        private static double NextFrame(double t, int fps)
        {
            double f = 1000.0 / fps;
            return Math.Ceiling(t / f) * f;
        }

        private static string F(string fmt, params object[] a)
        {
            return string.Format(CultureInfo.InvariantCulture, fmt, a);
        }

        // ---------------------------------------------------------------------
        //  Input - doc o tang message, lay gio NGAY khi message toi.
        // ---------------------------------------------------------------------
        protected override void WndProc(ref Message m)
        {
            double t = Now;
            const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
            const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONDBLCLK = 0x0206;
            switch (m.Msg)
            {
                case WM_RBUTTONDOWN:
                case WM_RBUTTONDBLCLK:     // click nhanh lan hai toi duoi dang DBLCLK
                    HandleClick(t);
                    break;
                case WM_KEYDOWN:
                    if (m.WParam.ToInt32() == VK_V && (m.LParam.ToInt64() & 0x40000000L) == 0) HandleVDown(t);
                    break;
                case WM_KEYUP:
                    if (m.WParam.ToInt32() == VK_V) HandleVUp(t);
                    break;
            }
            base.WndProc(ref m);
        }

        private void HandleClick(double t)
        {
            bool poll = _poll;
            int fps = _fps;
            double seen = poll ? NextFrame(t, fps) : t;

            // Dao dang chem: game bo qua click, khong chem chong len.
            if (_cur != null && seen < _cur.SlashEnd) { _cur.BusyClicks++; Invalidate(); return; }

            if (_cur != null && _cur.Result == Verdict.Waiting)
            {
                if (_cur.HasV) Resolve(_cur, _cur.Poll ? NextFrame(_cur.VDownAt, _cur.Fps) : _cur.VDownAt);
                else { _cur.Result = Verdict.NoV; Archive(_cur); }
            }

            var a = new Attempt();
            a.ClickAt = t;
            a.Poll = poll;
            a.Fps = fps;
            a.SlashMs = _slashMs;
            a.SlashStart = seen;
            a.SlashEnd = seen + _slashMs;
            _cur = a;
            Invalidate();
        }

        private void HandleVDown(double t)
        {
            if (_cur == null || _cur.Result != Verdict.Waiting || _cur.HasV) { _loneV++; Invalidate(); return; }
            _cur.VDownAt = t;
            if (!_cur.Poll) Resolve(_cur, t);
            Invalidate();
        }

        private void HandleVUp(double t)
        {
            Attempt a = _cur;
            if (a == null || !a.HasV || !double.IsNaN(a.VUpAt)) return;
            a.VUpAt = t;
            if (a.Poll && a.Result == Verdict.Waiting)
            {
                // Frame dau tien sau luc V xuong. V da nha truoc frame do = game khong thay.
                double f = NextFrame(a.VDownAt, a.Fps);
                if (f < t) Resolve(a, f);
                else { a.Result = Verdict.Missed; Archive(a); }
            }
            Invalidate();
        }

        private void Resolve(Attempt a, double seen)
        {
            a.VSeenAt = seen;
            a.Result = seen >= a.SlashEnd ? Verdict.Ok : Verdict.Early;
            Archive(a);
        }

        private void Archive(Attempt a)
        {
            _hist.Add(a);
            if (_hist.Count > 500) _hist.RemoveAt(0);
        }

        private void OnTick(object sender, EventArgs e)
        {
            // Chem xong 3 giay ma khong co V: VAssist dang tat, sai focus, hoac bi UIPI chan.
            if (_cur != null && _cur.Result == Verdict.Waiting && !_cur.HasV && Now > _cur.SlashEnd + 3000)
            {
                _cur.Result = Verdict.NoV;
                Archive(_cur);
            }
            Invalidate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Up: SetSlash(_slashMs + 10); return true;
                case Keys.Down: SetSlash(_slashMs - 10); return true;
                case Keys.PageUp: SetSlash(_slashMs + 50); return true;
                case Keys.PageDown: SetSlash(_slashMs - 50); return true;
                case Keys.F5: _poll = !_poll; Invalidate(); return true;
                case Keys.F7:
                    {
                        int[] steps = new int[] { 30, 60, 100, 144 };
                        int i = Array.IndexOf(steps, _fps);
                        _fps = steps[(i + 1) % steps.Length];
                        Invalidate();
                        return true;
                    }
                case Keys.F9:
                    _hist.Clear(); _cur = null; _loneV = 0;
                    Invalidate();
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void SetSlash(int v)
        {
            _slashMs = Math.Max(50, Math.Min(3000, v));
            Invalidate();
        }

        // ---------------------------------------------------------------------
        //  Ve
        // ---------------------------------------------------------------------
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            DrawHeader(g, new Rectangle(14, 14, 932, 62));
            DrawTimeline(g, new Rectangle(14, 88, 932, 180), _cur);
            DrawVerdict(g, new Rectangle(14, 280, 460, 160), _cur);
            DrawStats(g, new Rectangle(486, 280, 460, 160));
            DrawHistory(g, new Rectangle(14, 452, 932, 136));
            Put(g, "chuot phai = chem   V = lenh   UP/DOWN +-10 ms   PGUP/PGDN +-50 ms   " +
                   "F5 LATCH<->POLL   F7 fps   F9 xoa so lieu", _f, Dim, 16, 598);
        }

        private static void Box(Graphics g, Rectangle r)
        {
            using (var b = new SolidBrush(PanelC)) g.FillRectangle(b, r);
            using (var p = new Pen(EdgeC)) g.DrawRectangle(p, r);
        }

        private static void Put(Graphics g, string s, Font f, Color c, float x, float y)
        {
            using (var b = new SolidBrush(c)) g.DrawString(s, f, b, x, y);
        }

        private static Color ColorOf(Verdict v)
        {
            switch (v)
            {
                case Verdict.Ok: return OkC;
                case Verdict.Early: return BadC;
                case Verdict.Missed: return WarnC;
                case Verdict.NoV: return Dim;
            }
            return Fg;
        }

        private void DrawHeader(Graphics g, Rectangle r)
        {
            Box(g, r);
            bool focused = Form.ActiveForm == this;
            Put(g, "VTestbed - thu VAssist truoc khi vao game", _fMid, OkC, r.X + 12, r.Y + 8);
            Put(g, F("nhat chem {0} ms", _slashMs), _fMid, SlashC, r.X + 12, r.Y + 34);
            Put(g, "doc input: " + (_poll ? F("POLL {0} fps ({1:0.0} ms/frame)", _fps, 1000.0 / _fps)
                                          : "LATCH (moi message, khong tre)"),
                _f, _poll ? WarnC : Fg, r.X + 230, r.Y + 37);
            Put(g, focused ? "focus: CO" : "focus: KHONG - click vao day", _f, focused ? OkC : BadC,
                r.Right - 240, r.Y + 37);
        }

        private void DrawTimeline(Graphics g, Rectangle r, Attempt a)
        {
            Box(g, r);
            Put(g, "DONG THOI GIAN - tinh tu luc click", _f, Dim, r.X + 12, r.Y + 8);
            if (a == null)
            {
                Put(g, "Click CHUOT PHAI vao cua so nay de chem.", _fMid, Fg, r.X + 12, r.Y + 64);
                Put(g, "VAssist dang chay (target_process co VTestbed) se bam V sau delay_ms.", _f, Dim, r.X + 12, r.Y + 92);
                Put(g, "Tu bam V bang tay cung duoc - de so tay ban voi tool.", _f, Dim, r.X + 12, r.Y + 112);
                return;
            }

            double now = Now;
            double end = a.SlashEnd - a.ClickAt;
            if (a.HasV) end = Math.Max(end, (double.IsNaN(a.VUpAt) ? now : a.VUpAt) - a.ClickAt);
            else if (a.Result == Verdict.Waiting) end = Math.Max(end, now - a.ClickAt);
            double span = Math.Max(300, end * 1.15);

            float x0 = r.X + 28, x1 = r.Right - 28, w = x1 - x0;
            float yBar = r.Y + 66, hBar = 40;
            Func<double, float> X = delegate (double ms)
            {
                return x0 + (float)(Math.Max(0, Math.Min(span, ms)) / span) * w;
            };

            using (var b = new SolidBrush(Color.FromArgb(30, 34, 44))) g.FillRectangle(b, x0, yBar, w, hBar);

            // Nhat chem: V roi vao vung nay = bi nuot.
            float sx = X(a.SlashStart - a.ClickAt), ex = X(a.SlashEnd - a.ClickAt);
            using (var b = new SolidBrush(Color.FromArgb(60, SlashC))) g.FillRectangle(b, sx, yBar, ex - sx, hBar);
            if (now < a.SlashEnd)
            {
                float px = X(now - a.ClickAt);
                using (var b = new SolidBrush(Color.FromArgb(160, SlashC)))
                    g.FillRectangle(b, sx, yBar, Math.Max(0, px - sx), hBar);
            }
            if (ex - sx > 260) Put(g, "DAO DANG CHEM - V vao day la bi nuot", _f, SlashC, sx + 8, yBar + 12);

            // Vach frame (POLL): game chi nhin input o nhung vach nay.
            if (a.Poll)
            {
                double fr = 1000.0 / a.Fps;
                using (var p = new Pen(Color.FromArgb(80, 255, 255, 255)))
                    for (double ft = Math.Ceiling(a.ClickAt / fr) * fr; ft - a.ClickAt <= span; ft += fr)
                    {
                        float fx = X(ft - a.ClickAt);
                        g.DrawLine(p, fx, yBar + hBar + 2, fx, yBar + hBar + 7);
                    }
                Put(g, F("vach nho = frame game doc input ({0} fps)", a.Fps), _f, Dim, r.X + 12, r.Bottom - 22);
            }

            // Click
            using (var p = new Pen(Fg, 2f)) g.DrawLine(p, x0, yBar - 12, x0, yBar + hBar + 4);
            Put(g, "click", _f, Fg, x0 - 18, yBar - 30);

            // Chem xong
            using (var p = new Pen(SlashC, 2f)) g.DrawLine(p, ex, yBar - 6, ex, yBar + hBar);
            Put(g, F("chem xong +{0:0.0}", a.SlashEnd - a.ClickAt), _f, SlashC, ex - 70, yBar - 30);

            // V
            if (a.HasV)
            {
                Color c = ColorOf(a.Result);
                float vx = X(a.VDownAt - a.ClickAt);
                using (var p = new Pen(c, 3f)) g.DrawLine(p, vx, yBar - 4, vx, yBar + hBar + 10);
                if (!double.IsNaN(a.VUpAt))
                {
                    float ux = X(a.VUpAt - a.ClickAt);
                    using (var b = new SolidBrush(Color.FromArgb(110, c)))
                        g.FillRectangle(b, vx, yBar + hBar - 10, Math.Max(2, ux - vx), 10);
                }
                if (a.Poll && !double.IsNaN(a.VSeenAt))
                {
                    float qx = X(a.VSeenAt - a.ClickAt);
                    using (var p = new Pen(c, 1.5f))
                    {
                        p.DashStyle = DashStyle.Dash;
                        g.DrawLine(p, qx, yBar - 4, qx, yBar + hBar + 10);
                    }
                }
                Put(g, F("V +{0:0.0}", a.VDownAt - a.ClickAt), _fMid, c, vx - 34, yBar + hBar + 14);
            }
            else if (a.Result == Verdict.Waiting)
            {
                float nx = X(now - a.ClickAt);
                using (var p = new Pen(Color.FromArgb(120, Fg))) g.DrawLine(p, nx, yBar - 4, nx, yBar + hBar + 4);
            }
        }

        private void DrawVerdict(Graphics g, Rectangle r, Attempt a)
        {
            Box(g, r);
            Put(g, "LAN NAY", _f, Dim, r.X + 12, r.Y + 8);
            if (a == null) return;

            double now = Now;
            string big, l1 = "", l2 = "";
            Color c;
            switch (a.Result)
            {
                case Verdict.Ok:
                    big = "AN"; c = OkC;
                    l1 = F("V den sau khi chem xong {0:0.0} ms", a.Late);
                    l2 = a.Late > 25 ? "con du -> co the GIAM delay_ms (NUMMINUS)" : "vua khit";
                    break;
                case Verdict.Early:
                    big = "SOM"; c = BadC;
                    l1 = F("V den TRUOC khi chem xong {0:0.0} ms", -a.Late);
                    l2 = "game NUOT mat V -> TANG delay_ms (NUMPLUS)";
                    break;
                case Verdict.Missed:
                    big = "BO SOT"; c = WarnC;
                    l1 = F("V chi giu {0:0.0} ms, chua toi 1 frame ({1:0.0} ms)", a.Hold, 1000.0 / a.Fps);
                    l2 = "game khong he thay -> tang hold_ms";
                    break;
                case Verdict.NoV:
                    big = "KHONG CO V"; c = Dim;
                    l1 = "VAssist co dang chay va dang [BAT]?";
                    l2 = "target_process co VTestbed? game admin -> --admin";
                    break;
                default:
                    if (now < a.SlashEnd) { big = "DANG CHEM"; c = SlashC; l1 = F("con {0:0} ms", a.SlashEnd - now); }
                    else { big = "CHO V..."; c = Fg; l1 = F("dao da chem xong {0:0} ms", now - a.SlashEnd); }
                    break;
            }
            Put(g, big, _fBig, c, r.X + 10, r.Y + 24);
            Put(g, l1, _f, Fg, r.X + 14, r.Y + 82);
            Put(g, l2, _f, c, r.X + 14, r.Y + 100);
            if (a.HasV)
                Put(g, F("click -> V {0:0.0} ms{1}", a.Interval,
                         double.IsNaN(a.VUpAt) ? "" : F("    giu V {0:0.0} ms", a.Hold)), _f, Dim, r.X + 14, r.Y + 120);
            if (a.BusyClicks > 0)
                Put(g, F("+{0} click trong luc chem (game bo qua)", a.BusyClicks), _f, Dim, r.X + 14, r.Y + 138);
        }

        private void DrawStats(Graphics g, Rectangle r)
        {
            Box(g, r);
            Put(g, "TONG KET", _f, Dim, r.X + 12, r.Y + 8);
            int ok = 0, early = 0, miss = 0, nov = 0;
            double lateSum = 0, s = 0, s2 = 0, mn = double.MaxValue, mx = double.MinValue;
            int nIv = 0;
            foreach (Attempt a in _hist)
            {
                switch (a.Result)
                {
                    case Verdict.Ok: ok++; lateSum += a.Late; break;
                    case Verdict.Early: early++; break;
                    case Verdict.Missed: miss++; break;
                    case Verdict.NoV: nov++; break;
                }
                if (a.HasV)
                {
                    double v = a.Interval;
                    s += v; s2 += v * v; nIv++;
                    if (v < mn) mn = v;
                    if (v > mx) mx = v;
                }
            }

            int y = r.Y + 28;
            Put(g, F("AN {0}", ok), _fMid, ok > 0 ? OkC : Dim, r.X + 14, y);
            Put(g, F("SOM {0}", early), _fMid, early > 0 ? BadC : Dim, r.X + 104, y);
            Put(g, F("BO SOT {0}", miss), _fMid, miss > 0 ? WarnC : Dim, r.X + 204, y);
            Put(g, F("khong V {0}", nov), _fMid, Dim, r.X + 334, y);
            y += 28;
            if (nIv > 0)
            {
                double mean = s / nIv;
                double sd = Math.Sqrt(Math.Max(0, s2 / nIv - mean * mean));
                Put(g, F("click -> V : tb {0:0.0} ms   sd {1:0.0}   min {2:0.0}   max {3:0.0}", mean, sd, mn, mx),
                    _f, Fg, r.X + 14, y);
                y += 20;
            }
            if (ok > 0)
            {
                Put(g, F("khi AN     : tre tb {0:0.0} ms sau khi chem xong", lateSum / ok), _f, Fg, r.X + 14, y);
                y += 20;
            }
            if (_loneV > 0) Put(g, F("V khong kem click (bo qua): {0}", _loneV), _f, Dim, r.X + 14, y);

            string tip;
            Color tc;
            if (early > 0) { tip = "Co V den som -> TANG delay_ms cua VAssist."; tc = BadC; }
            else if (miss > 0) { tip = "Co V bi bo sot -> tang hold_ms."; tc = WarnC; }
            else if (ok > 0 && lateSum / ok > 25) { tip = F("An het, du tb {0:0} ms -> co the GIAM delay_ms.", lateSum / ok); tc = OkC; }
            else if (ok > 0) { tip = "An het, vua khit."; tc = OkC; }
            else { tip = "Chua co so lieu."; tc = Dim; }
            Put(g, tip, _f, tc, r.X + 14, r.Bottom - 24);
        }

        private void DrawHistory(Graphics g, Rectangle r)
        {
            Box(g, r);
            Put(g, "LICH SU - moi nhat o tren", _f, Dim, r.X + 12, r.Y + 8);
            int y = r.Y + 28;
            int n = 0;
            for (int i = _hist.Count - 1; i >= 0 && n < 5; i--, n++)
            {
                Attempt a = _hist[i];
                string what;
                switch (a.Result)
                {
                    case Verdict.Ok: what = F("AN       tre +{0:0.0} ms", a.Late); break;
                    case Verdict.Early: what = F("SOM      {0:0.0} ms truoc khi chem xong", -a.Late); break;
                    case Verdict.Missed: what = F("BO SOT   V giu {0:0.0} ms", a.Hold); break;
                    default: what = "khong co V"; break;
                }
                string iv = a.HasV ? F("click -> V {0,7:0.0} ms", a.Interval) : new string(' ', 21);
                string mode = a.Poll ? F("POLL {0}", a.Fps) : "LATCH";
                Put(g, F("#{0,-4} chem {1,4} ms   {2,-8}   {3}    {4}", i + 1, a.SlashMs, mode, iv, what),
                    _f, ColorOf(a.Result), r.X + 14, y);
                y += 20;
            }
        }
    }

    internal static class Entry
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new BenchForm());
        }
    }
}
