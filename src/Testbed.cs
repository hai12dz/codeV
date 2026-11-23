// =============================================================================
//  BhopTestbed - "game cua ban" de test tro ly input ben ngoai
// -----------------------------------------------------------------------------
//  Mo phong vat ly di chuyen kieu Source (CS/Quake): friction tren dat,
//  air-acceleration co gioi han 30 u/s. Bhop = nhay dung tick vua cham dat,
//  friction chua kip an vao -> giu nguyen toc do, con air-strafe thi cong them.
//
//  Muc dich: co mot muc tieu HOP PHAP de tune BhopAssist.exe, va de nhin thay
//  chinh xac cai cua so 1-tick ma macro dang nham vao.
//
//  Phim:
//    W/A/S/D      di chuyen        Chuot / mui ten trai-phai   xoay
//    SPACE        nhay             Chuot trai  bat chuot vao   ESC  nha ra
//    F1  jump buffer (coyote)  - cach LAM DUNG: game tu ho tro
//    F2  auto-hop              - cach LAM DUNG: game tu ho tro
//    F5  che do doc input: LATCH (hang doi su kien) <-> POLL (chup trang thai)
//    F6  doi tickrate  64 / 128 / 32
//    F7  doi tan so poll (= fps cua game) khi dang o che do POLL
//    F9  reset thong ke        F10 reset vi tri
//
//  Che do POLL o day mo phong dung cach rat nhieu game FPS F2P doc ban phim:
//  DirectInput8 GetDeviceState() mot lan moi frame. Do la ANH CHUP TRANG THAI.
//  Phim xuong roi len tron ven giua hai lan chup = khong ton tai. Do la ly do
//  macro hold_ms = 6 chay ngon o LATCH ma cam nhu thoc o POLL.
// =============================================================================
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace BhopTestbed
{
    internal static class Win
    {
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();

        public const int VK_W = 0x57, VK_A = 0x41, VK_S = 0x53, VK_D = 0x44;
        public const int VK_SPACE = 0x20, VK_LEFT = 0x25, VK_RIGHT = 0x27;

        public static bool Down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
    }

    // Trang thai duoc render doc ra (ban sao, khong dung chung voi thread vat ly).
    internal struct Snapshot
    {
        public double X, Y, Z, Vx, Vy, Vz, Yaw, Speed, MaxSpeed;
        public bool Grounded;
        public long Tick;
        public long JumpInputs, RepeatInputs, JumpsConsumed, Landings, PerfectHops, GroundTicks;
        public long PollEdges;
        public int LastLandingGroundTicks;
        public double InputHz;
    }

    internal sealed class Sim
    {
        // --- hang so vat ly kieu Source ---
        public const double GRAVITY = 800.0;
        public const double JUMP_VEL = 268.3216;
        public const double MAX_SPEED = 320.0;
        public const double ACCEL = 10.0;
        public const double AIR_ACCEL = 100.0;
        public const double AIR_CAP = 30.0;    // <- linh hon cua bhop
        public const double FRICTION = 4.0;
        public const double STOP_SPEED = 100.0;

        public readonly object Gate = new object();

        public int TickRate = 64;
        public bool LatchMode = true;      // true = bat moi cu go (giong engine that)
        public int PollFps = 60;           // F7 - tan so chup trang thai o che do POLL
        public int AssistBufferTicks = 0;  // F1 - jump buffer do GAME cung cap
        public bool AutoHop = false;       // F2 - auto-hop do GAME cung cap

        private double _x, _y, _z, _vx, _vy, _vz;
        private double _yaw;
        private bool _grounded = true;
        private long _tick;
        private int _jumpBuffer;
        private int _groundRun;            // so tick lien tiep dang cham dat

        private long _jumpInputs, _repeatInputs, _jumpsConsumed, _landings, _perfectHops, _groundTicks;
        private long _pollEdges;
        private int _lastLandingGroundTicks;
        private double _maxSpeed;

        private int _pendingJumps;
        private int _pendingPollJumps;
        private long _inputStampTick;
        private double _inputHz;

        // Vong lich su de ve
        public const int HIST = 240;
        public readonly byte[] TickKind = new byte[HIST];  // 0 = air, 1 = ground, 2 = jump
        public int HistIdx;
        public const int SPEEDHIST = 320;
        public readonly float[] SpeedHist = new float[SPEEDHIST];
        public int SpeedIdx;

        // Duong LATCH: goi tu WndProc, moi WM_KEYDOWN mot lan. Khong bo sot gi.
        public void QueueJump(bool isRepeat)
        {
            if (isRepeat) { Interlocked.Increment(ref _repeatInputs); return; }
            Interlocked.Increment(ref _pendingJumps);
            Interlocked.Increment(ref _jumpInputs);
        }

        // Duong POLL: goi tu PhysLoop moi khi mot lan CHUP TRANG THAI thay phim
        // vua chuyen tu len sang xuong. Cu bam nam gon giua 2 lan chup thi
        // ham nay khong bao gio duoc goi - do chinh la cai game that bo sot.
        public void QueuePollEdge()
        {
            Interlocked.Increment(ref _pendingPollJumps);
            Interlocked.Increment(ref _pollEdges);
        }

        public void AddYaw(double d) { lock (Gate) { _yaw += d; } }

        public void ResetStats()
        {
            lock (Gate)
            {
                _jumpInputs = _repeatInputs = _jumpsConsumed = _landings = _perfectHops = _groundTicks = 0;
                _pollEdges = 0;
                _maxSpeed = 0; _lastLandingGroundTicks = 0;
                Array.Clear(TickKind, 0, HIST);
                Array.Clear(SpeedHist, 0, SPEEDHIST);
            }
        }

        public void ResetPos()
        {
            lock (Gate) { _x = _y = _z = _vx = _vy = _vz = 0; _grounded = true; _groundRun = 0; }
        }

        public void Step(double dt, bool focused)
        {
            bool w = false, a = false, s = false, d = false, space = false, kl = false, kr = false;
            if (focused)
            {
                w = Win.Down(Win.VK_W); a = Win.Down(Win.VK_A);
                s = Win.Down(Win.VK_S); d = Win.Down(Win.VK_D);
                space = Win.Down(Win.VK_SPACE);
                kl = Win.Down(Win.VK_LEFT); kr = Win.Down(Win.VK_RIGHT);
            }

            int queued = Interlocked.Exchange(ref _pendingJumps, 0);
            int queuedPoll = Interlocked.Exchange(ref _pendingPollJumps, 0);

            lock (Gate)
            {
                _tick++;
                if (kl) _yaw -= 2.6 * dt;
                if (kr) _yaw += 2.6 * dt;

                // --- 1. Nhan y dinh nhay -------------------------------------
                // LATCH: moi WM_KEYDOWN deu duoc tinh (GoldSrc/Source lam vay).
                // POLL : chi tinh nhung canh len ma mot lan CHUP TRANG THAI o
                //        PollFps bat duoc (DirectInput GetDeviceState moi frame).
                bool edge = LatchMode ? (queued > 0) : (queuedPoll > 0);
                if (edge) _jumpBuffer = 1 + AssistBufferTicks;
                if (AutoHop && space && _jumpBuffer <= 0) _jumpBuffer = 1;

                // --- 2. Kiem tra nhay TRUOC friction -------------------------
                // Day la toan bo bi mat cua bhop: nhay dung tick cham dat thi
                // friction o buoc 3 khong bao gio chay.
                bool jumped = false;
                if (_grounded && _jumpBuffer > 0)
                {
                    _vz = JUMP_VEL;
                    _grounded = false;
                    _jumpBuffer = 0;
                    _jumpsConsumed++;
                    jumped = true;
                    if (_groundRun == 1) _perfectHops++;   // roi -> nhay ngay tick sau
                    _lastLandingGroundTicks = _groundRun;
                    _groundRun = 0;
                }
                else if (_jumpBuffer > 0) _jumpBuffer--;

                // --- 3. Friction: chi khi cham dat va khong nhay --------------
                if (_grounded)
                {
                    _groundTicks++;
                    _groundRun++;
                    ApplyFriction(dt);
                }

                // --- 4. Gia toc ----------------------------------------------
                double fmove = (w ? 1 : 0) - (s ? 1 : 0);
                double smove = (d ? 1 : 0) - (a ? 1 : 0);
                double fx = Math.Cos(_yaw), fy = Math.Sin(_yaw);
                double rx = -Math.Sin(_yaw), ry = Math.Cos(_yaw);
                double wx = fx * fmove + rx * smove;
                double wy = fy * fmove + ry * smove;
                double wlen = Math.Sqrt(wx * wx + wy * wy);
                if (wlen > 1e-6)
                {
                    wx /= wlen; wy /= wlen;
                    if (_grounded) Accelerate(wx, wy, MAX_SPEED, ACCEL, dt);
                    else AirAccelerate(wx, wy, MAX_SPEED, dt);
                }

                // --- 5. Trong luc + di chuyen --------------------------------
                if (!_grounded)
                {
                    _vz -= GRAVITY * dt;
                    _z += _vz * dt;
                    if (_z <= 0.0)
                    {
                        _z = 0.0; _vz = 0.0; _grounded = true; _landings++; _groundRun = 0;
                    }
                }
                _x += _vx * dt;
                _y += _vy * dt;

                double sp = Math.Sqrt(_vx * _vx + _vy * _vy);
                if (sp > _maxSpeed) _maxSpeed = sp;

                // --- 6. Lich su de ve ----------------------------------------
                TickKind[HistIdx] = jumped ? (byte)2 : (_grounded ? (byte)1 : (byte)0);
                HistIdx = (HistIdx + 1) % HIST;
                SpeedHist[SpeedIdx] = (float)sp;
                SpeedIdx = (SpeedIdx + 1) % SPEEDHIST;

                // Do tan so input thuc te dang toi
                if (_tick - _inputStampTick >= TickRate)
                {
                    _inputHz = (_jumpInputs - _inputHzBase) * (double)TickRate / (_tick - _inputStampTick);
                    _inputHzBase = _jumpInputs;
                    _inputStampTick = _tick;
                }
            }
        }

        private long _inputHzBase;

        private void ApplyFriction(double dt)
        {
            double speed = Math.Sqrt(_vx * _vx + _vy * _vy);
            if (speed < 0.1) { _vx = 0; _vy = 0; return; }
            double control = speed < STOP_SPEED ? STOP_SPEED : speed;
            double drop = control * FRICTION * dt;
            double newspeed = speed - drop;
            if (newspeed < 0) newspeed = 0;
            newspeed /= speed;
            _vx *= newspeed; _vy *= newspeed;
        }

        private void Accelerate(double wx, double wy, double wishspeed, double accel, double dt)
        {
            double current = _vx * wx + _vy * wy;
            double add = wishspeed - current;
            if (add <= 0) return;
            double accelspeed = accel * wishspeed * dt;
            if (accelspeed > add) accelspeed = add;
            _vx += accelspeed * wx; _vy += accelspeed * wy;
        }

        private void AirAccelerate(double wx, double wy, double wishspeed, double dt)
        {
            // Chot chan: tren khong chi duoc "voi toi" 30 u/s theo huong nhin.
            // Vector cong them gan vuong goc voi van toc -> do dai tong tang len.
            double wishspd = wishspeed > AIR_CAP ? AIR_CAP : wishspeed;
            double current = _vx * wx + _vy * wy;
            double add = wishspd - current;
            if (add <= 0) return;
            double accelspeed = AIR_ACCEL * wishspeed * dt;
            if (accelspeed > add) accelspeed = add;
            _vx += accelspeed * wx; _vy += accelspeed * wy;
        }

        public Snapshot Read()
        {
            var s = new Snapshot();
            lock (Gate)
            {
                s.X = _x; s.Y = _y; s.Z = _z; s.Vx = _vx; s.Vy = _vy; s.Vz = _vz; s.Yaw = _yaw;
                s.Speed = Math.Sqrt(_vx * _vx + _vy * _vy);
                s.MaxSpeed = _maxSpeed; s.Grounded = _grounded; s.Tick = _tick;
                s.JumpInputs = _jumpInputs; s.RepeatInputs = _repeatInputs;
                s.JumpsConsumed = _jumpsConsumed; s.Landings = _landings;
                s.PerfectHops = _perfectHops; s.GroundTicks = _groundTicks;
                s.PollEdges = _pollEdges;
                s.LastLandingGroundTicks = _lastLandingGroundTicks;
                s.InputHz = _inputHz;
            }
            return s;
        }
    }

    // =========================================================================
    internal sealed class GameForm : Form
    {
        private readonly Sim _sim = new Sim();
        private Thread _phys;
        private volatile bool _run = true;
        private bool _captured;
        private readonly System.Windows.Forms.Timer _render;

        private static readonly Color Bg = Color.FromArgb(16, 18, 24);
        private static readonly Color Fg = Color.FromArgb(225, 230, 240);
        private static readonly Color Dim = Color.FromArgb(120, 130, 148);
        private static readonly Color AirC = Color.FromArgb(56, 130, 246);
        private static readonly Color GroundC = Color.FromArgb(232, 86, 86);
        private static readonly Color JumpC = Color.FromArgb(250, 204, 21);
        private static readonly Color Accent = Color.FromArgb(52, 211, 153);

        private readonly Font _f = new Font("Consolas", 9.5f);
        private readonly Font _fBig = new Font("Consolas", 34f, FontStyle.Bold);
        private readonly Font _fMid = new Font("Consolas", 11f, FontStyle.Bold);

        public GameForm()
        {
            Text = "BhopTestbed - game cua ban";
            ClientSize = new Size(1020, 752);
            BackColor = Bg;
            DoubleBuffered = true;
            KeyPreview = true;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            _phys = new Thread(PhysLoop);
            _phys.IsBackground = true;
            _phys.Priority = ThreadPriority.AboveNormal;
            _phys.Start();

            _render = new System.Windows.Forms.Timer();
            _render.Interval = 15;
            _render.Tick += OnRenderTick;
            _render.Start();

            MouseDown += delegate { Capture2(true); };
            FormClosing += delegate { _run = false; Capture2(false); };
        }

        // --- doc input nhay o tang message: bat duoc ca nhung cu go rat ngan ----
        protected override void WndProc(ref Message m)
        {
            const int WM_KEYDOWN = 0x0100;
            if (m.Msg == WM_KEYDOWN && m.WParam.ToInt32() == Win.VK_SPACE)
            {
                bool repeat = (m.LParam.ToInt64() & 0x40000000L) != 0;  // bit 30 = trang thai truoc
                _sim.QueueJump(repeat);
            }
            base.WndProc(ref m);
        }

        protected override bool ProcessCmdKey(ref Message msg, System.Windows.Forms.Keys keyData)
        {
            switch (keyData)
            {
                case System.Windows.Forms.Keys.F1:
                    lock (_sim.Gate) _sim.AssistBufferTicks = _sim.AssistBufferTicks == 0 ? 4 : 0;
                    return true;
                case System.Windows.Forms.Keys.F2:
                    lock (_sim.Gate) _sim.AutoHop = !_sim.AutoHop;
                    return true;
                case System.Windows.Forms.Keys.F5:
                    lock (_sim.Gate) _sim.LatchMode = !_sim.LatchMode;
                    return true;
                case System.Windows.Forms.Keys.F6:
                    lock (_sim.Gate)
                        _sim.TickRate = _sim.TickRate == 64 ? 128 : (_sim.TickRate == 128 ? 32 : 64);
                    return true;
                case System.Windows.Forms.Keys.F7:
                    lock (_sim.Gate)
                    {
                        int[] steps = new int[] { 30, 60, 100, 144, 250 };
                        int i = Array.IndexOf(steps, _sim.PollFps);
                        _sim.PollFps = steps[(i + 1) % steps.Length];
                    }
                    return true;
                case System.Windows.Forms.Keys.F9: _sim.ResetStats(); return true;
                case System.Windows.Forms.Keys.F10: _sim.ResetPos(); return true;
                case System.Windows.Forms.Keys.Escape: Capture2(false); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void Capture2(bool on)
        {
            if (on == _captured) return;
            _captured = on;
            if (on) Cursor.Hide(); else Cursor.Show();
        }

        private Point ScreenCenter()
        {
            Rectangle r = RectangleToScreen(ClientRectangle);
            return new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
        }

        private void OnRenderTick(object sender, EventArgs e)
        {
            if (_captured && Form.ActiveForm == this)
            {
                Point c = ScreenCenter();
                Point p = Cursor.Position;
                int dx = p.X - c.X;
                if (dx != 0) _sim.AddYaw(dx * 0.0022);
                Cursor.Position = c;
            }
            Invalidate();
        }

        private void PhysLoop()
        {
            var sw = Stopwatch.StartNew();
            double acc = 0, last = sw.Elapsed.TotalSeconds;
            double pollAcc = 0;
            bool pollPrev = false;

            while (_run)
            {
                double now = sw.Elapsed.TotalSeconds;
                double frame = now - last;
                last = now;
                if (frame > 0.25) frame = 0.25;
                acc += frame;

                int rate, pollFps; bool latch;
                lock (_sim.Gate) { rate = _sim.TickRate; pollFps = _sim.PollFps; latch = _sim.LatchMode; }
                double dt = 1.0 / rate;

                bool focused = Win.GetForegroundWindow() == Handle;

                // --- Bo lay mau kieu POLL ------------------------------------
                // Chay o THOI GIAN THUC, khong theo tick, vi day chinh la cach
                // game that lam: moi frame render goi GetDeviceState() mot lan.
                // Vong lap nay ngu 1 ms nen do phan giai du toi 250 fps.
                if (!latch)
                {
                    pollAcc += frame;
                    double pi = 1.0 / pollFps;
                    int guard = 0;
                    while (pollAcc >= pi && guard++ < 64)
                    {
                        pollAcc -= pi;
                        bool d = focused && Win.Down(Win.VK_SPACE);
                        if (d && !pollPrev) _sim.QueuePollEdge();
                        pollPrev = d;
                    }
                }
                else { pollAcc = 0; pollPrev = false; }

                while (acc >= dt)
                {
                    _sim.Step(dt, focused);
                    acc -= dt;
                }
                Thread.Sleep(1);
            }
        }

        // ---------------------------------------------------------------------
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Snapshot s = _sim.Read();

            int rate, pollFps; bool latch, autohop; int buf;
            lock (_sim.Gate)
            {
                rate = _sim.TickRate; pollFps = _sim.PollFps;
                latch = _sim.LatchMode; autohop = _sim.AutoHop; buf = _sim.AssistBufferTicks;
            }

            DrawArena(g, new Rectangle(14, 14, 640, 472), s);
            DrawPanel(g, new Rectangle(668, 14, 338, 472), s, rate, pollFps, latch, autohop, buf);
            DrawSpeedGraph(g, new Rectangle(14, 498, 992, 130));
            DrawTickStrip(g, new Rectangle(14, 640, 992, 46));
            DrawHelp(g, new Rectangle(14, 694, 992, 50), latch);
        }

        private void DrawArena(Graphics g, Rectangle r, Snapshot s)
        {
            using (var b = new SolidBrush(Color.FromArgb(22, 25, 33))) g.FillRectangle(b, r);
            using (var p = new Pen(Color.FromArgb(38, 43, 55))) g.DrawRectangle(p, r);

            var old = g.Clip;
            g.SetClip(r);

            // Luoi cuon theo vi tri -> cam nhan duoc toc do
            const double GRID = 128.0;
            double sc = 0.55;
            double step = GRID * sc;
            double ox = r.X + r.Width / 2.0, oy = r.Y + r.Height / 2.0;
            using (var gp = new Pen(Color.FromArgb(34, 39, 50)))
            {
                // Luoi truot nguoc chieu di chuyen; phase luon nam trong [-step, 0).
                double px = -((s.X * sc) % step); if (px > 0) px -= step;
                for (double x = px; x < r.Width; x += step)
                    g.DrawLine(gp, (float)(r.X + x), r.Y, (float)(r.X + x), r.Bottom);
                double py = -((s.Y * sc) % step); if (py > 0) py -= step;
                for (double y = py; y < r.Height; y += step)
                    g.DrawLine(gp, r.X, (float)(r.Y + y), r.Right, (float)(r.Y + y));
            }

            // Bong do theo do cao -> thay ro dang bay hay dang cham dat
            float shadow = (float)(10 + s.Z * 0.04);
            using (var sb = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
                g.FillEllipse(sb, (float)ox - shadow, (float)oy - shadow * 0.6f, shadow * 2, shadow * 1.2f);

            // Vector van toc
            if (s.Speed > 1)
            {
                double vlen = Math.Min(150, s.Speed * 0.22);
                double ux = s.Vx / s.Speed, uy = s.Vy / s.Speed;
                using (var vp = new Pen(Accent, 2.5f))
                {
                    vp.EndCap = LineCap.ArrowAnchor;
                    g.DrawLine(vp, (float)ox, (float)oy, (float)(ox + ux * vlen), (float)(oy + uy * vlen));
                }
            }

            // Huong nhin
            using (var yp = new Pen(Color.FromArgb(150, 255, 255, 255), 1.5f))
                g.DrawLine(yp, (float)ox, (float)oy,
                           (float)(ox + Math.Cos(s.Yaw) * 42), (float)(oy + Math.Sin(s.Yaw) * 42));

            // Nguoi choi
            using (var pb = new SolidBrush(s.Grounded ? GroundC : AirC))
                g.FillEllipse(pb, (float)ox - 7, (float)(oy - 7 - s.Z * 0.12), 14, 14);

            g.Clip = old;

            using (var tb = new SolidBrush(Fg))
            {
                g.DrawString(s.Speed.ToString("0", CultureInfo.InvariantCulture), _fBig, tb, r.X + 12, r.Y + 6);
                g.DrawString("u/s", _f, new SolidBrush(Dim), r.X + 14 + g.MeasureString(s.Speed.ToString("0"), _fBig).Width, r.Y + 34);
            }
            using (var tb = new SolidBrush(s.Grounded ? GroundC : AirC))
                g.DrawString(s.Grounded ? "CHAM DAT (friction dang an toc do)" : "TREN KHONG (air-strafe duoc)",
                             _fMid, tb, r.X + 14, r.Y + 62);
            using (var tb = new SolidBrush(Dim))
                g.DrawString("cao " + s.Z.ToString("0") + "   max " + s.MaxSpeed.ToString("0"),
                             _f, tb, r.X + 14, r.Bottom - 22);
        }

        private void DrawPanel(Graphics g, Rectangle r, Snapshot s, int rate, int pollFps, bool latch, bool autohop, int buf)
        {
            using (var b = new SolidBrush(Color.FromArgb(22, 25, 33))) g.FillRectangle(b, r);
            using (var p = new Pen(Color.FromArgb(38, 43, 55))) g.DrawRectangle(p, r);

            int y = r.Y + 12;
            Action<string, string, Color> row = delegate (string k, string v, Color c)
            {
                using (var db = new SolidBrush(Dim)) g.DrawString(k, _f, db, r.X + 14, y);
                using (var vb = new SolidBrush(c))
                {
                    SizeF sz = g.MeasureString(v, _f);
                    g.DrawString(v, _f, vb, r.Right - 14 - sz.Width, y);
                }
                y += 19;
            };

            using (var hb = new SolidBrush(Accent)) g.DrawString("INPUT", _fMid, hb, r.X + 14, y);
            y += 24;
            row("che do doc", latch ? "LATCH (hang doi su kien)" : "POLL (chup trang thai)", latch ? Accent : JumpC);
            row("tickrate", rate + " Hz   (" + (1000.0 / rate).ToString("0.0") + " ms/tick)", Fg);
            if (!latch)
                row("F7 poll rate (fps game)", pollFps + " Hz   (" + (1000.0 / pollFps).ToString("0.0") + " ms/frame)", JumpC);
            row("cu nhan nhay (that)", s.JumpInputs.ToString(), Fg);
            row("  trong do auto-repeat", s.RepeatInputs.ToString(), Dim);
            if (!latch)
            {
                row("  poll chup duoc", s.PollEdges.ToString(), s.PollEdges > 0 ? Fg : GroundC);
                double miss = s.JumpInputs > 0 ? 100.0 * (s.JumpInputs - s.PollEdges) / s.JumpInputs : 0;
                if (miss < 0) miss = 0;
                row("  BO SOT", miss.ToString("0.0") + " %", miss > 25 ? GroundC : (miss > 5 ? JumpC : Accent));
            }
            row("tan so nhan toi", s.InputHz.ToString("0.0") + " Hz", Fg);
            y += 10;

            using (var hb = new SolidBrush(Accent)) g.DrawString("KET QUA", _fMid, hb, r.X + 14, y);
            y += 24;
            row("nhay thuc su xay ra", s.JumpsConsumed.ToString(), Fg);
            double eff = s.JumpInputs > 0 ? 100.0 * s.JumpsConsumed / s.JumpInputs : 0;
            row("  ty le trung", eff.ToString("0.0") + " %", eff > 20 ? Accent : Dim);
            row("so lan tiep dat", s.Landings.ToString(), Fg);
            row("hop hoan hao (1 tick)", s.PerfectHops.ToString(), s.PerfectHops > 0 ? Accent : Dim);
            double pq = s.Landings > 0 ? 100.0 * s.PerfectHops / s.Landings : 0;
            row("  chat luong hop", pq.ToString("0.0") + " %", pq > 60 ? Accent : (pq > 25 ? JumpC : GroundC));
            row("tick dinh mat dat", s.GroundTicks.ToString(), s.GroundTicks > 0 ? GroundC : Accent);
            row("lan cham dat gan nhat", s.LastLandingGroundTicks + " tick", Fg);
            y += 10;

            using (var hb = new SolidBrush(Accent)) g.DrawString("HO TRO TU CHINH GAME", _fMid, hb, r.X + 14, y);
            y += 24;
            row("F1  jump buffer", buf > 0 ? (buf + " tick  BAT") : "TAT", buf > 0 ? Accent : Dim);
            row("F2  auto-hop", autohop ? "BAT" : "TAT", autohop ? Accent : Dim);

            y += 12;
            using (var db = new SolidBrush(Dim))
            {
                if (latch)
                {
                    g.DrawString("F5 -> POLL de xem game kieu Truy Kich /", _f, db, r.X + 14, y); y += 16;
                    g.DrawString("CrossFire bo sot cu nhan ngan the nao.", _f, db, r.X + 14, y);
                }
                else
                {
                    g.DrawString("Muon 'BO SOT' ve 0: macro phai giu phim", _f, db, r.X + 14, y); y += 16;
                    g.DrawString("lau hon 1 frame (" + (1000.0 / pollFps).ToString("0.0") + " ms) VA nghi cung vay.", _f, db, r.X + 14, y);
                }
            }
        }

        private void DrawSpeedGraph(Graphics g, Rectangle r)
        {
            using (var b = new SolidBrush(Color.FromArgb(22, 25, 33))) g.FillRectangle(b, r);
            using (var p = new Pen(Color.FromArgb(38, 43, 55))) g.DrawRectangle(p, r);

            float max = 400;
            lock (_sim.Gate)
                for (int i = 0; i < Sim.SPEEDHIST; i++) if (_sim.SpeedHist[i] > max) max = _sim.SpeedHist[i];
            max *= 1.1f;

            // Duong 320 u/s = tran toc do chay bo. Vuot no = dang bhop that su.
            float yRun = r.Bottom - (float)(320.0 / max) * r.Height;
            using (var p = new Pen(Color.FromArgb(70, 232, 86, 86)))
            {
                p.DashStyle = DashStyle.Dash;
                g.DrawLine(p, r.X, yRun, r.Right, yRun);
            }
            using (var db = new SolidBrush(Color.FromArgb(140, 232, 86, 86)))
                g.DrawString("320 u/s = toc do chay toi da", _f, db, r.X + 8, yRun - 17);

            lock (_sim.Gate)
            {
                var pts = new PointF[Sim.SPEEDHIST];
                for (int i = 0; i < Sim.SPEEDHIST; i++)
                {
                    int idx = (_sim.SpeedIdx + i) % Sim.SPEEDHIST;
                    float x = r.X + (float)i / (Sim.SPEEDHIST - 1) * r.Width;
                    float yy = r.Bottom - (_sim.SpeedHist[idx] / max) * r.Height;
                    pts[i] = new PointF(x, yy);
                }
                using (var p = new Pen(Accent, 1.8f)) g.DrawLines(p, pts);
            }
            using (var db = new SolidBrush(Dim))
                g.DrawString("toc do ngang theo thoi gian", _f, db, r.X + 8, r.Y + 6);
        }

        private void DrawTickStrip(Graphics g, Rectangle r)
        {
            using (var b = new SolidBrush(Color.FromArgb(22, 25, 33))) g.FillRectangle(b, r);
            float w = (float)r.Width / Sim.HIST;
            lock (_sim.Gate)
            {
                for (int i = 0; i < Sim.HIST; i++)
                {
                    int idx = (_sim.HistIdx + i) % Sim.HIST;
                    byte k = _sim.TickKind[idx];
                    Color c = k == 2 ? JumpC : (k == 1 ? GroundC : Color.FromArgb(40, 56, 130, 246));
                    float h = k == 0 ? r.Height * 0.45f : r.Height * 0.9f;
                    using (var b2 = new SolidBrush(c))
                        g.FillRectangle(b2, r.X + i * w, r.Bottom - h - 2, Math.Max(1f, w - 0.6f), h);
                }
            }
            using (var db = new SolidBrush(Dim))
                g.DrawString("moi cot = 1 tick    xanh mo: tren khong    do: dinh dat (mat toc do)    vang: nhay",
                             _f, db, r.X + 6, r.Y + 2);
        }

        private void DrawHelp(Graphics g, Rectangle r, bool latch)
        {
            using (var db = new SolidBrush(Dim))
            {
                g.DrawString("WASD di chuyen | chuot hoac mui ten = xoay | SPACE nhay | click de khoa chuot, ESC nha",
                             _f, db, r.X + 2, r.Y + 2);
                g.DrawString("F1 jump buffer  F2 auto-hop  F5 LATCH<->POLL  F6 tickrate  F7 poll rate (fps game)  F9 reset so lieu  F10 reset vi tri"
                             + (latch ? "" : "   <- dang o POLL: giong game F2P doc DirectInput moi frame"),
                             _f, db, r.X + 2, r.Y + 20);
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
            Application.Run(new GameForm());
        }
    }
}
