// =============================================================================
//  Kich ban kiem tra FollowUpGate - thuan C#, khong phu thuoc Unity.
//  Trong Unity: Tools > Knife Follow-Up > Chay self-test (ket qua o Console).
// =============================================================================
using System;
using System.Collections.Generic;

namespace KnifeCombo.EditorTools
{
    public static class FollowUpGateSelfTest
    {
        private sealed class Probe
        {
            public readonly FollowUpGate Gate = new FollowUpGate();
            public readonly List<FollowUpReason> Fired = new List<FollowUpReason>();
            public float EndedSeconds = -1f;
            public bool EndedFired;

            public Probe()
            {
                Gate.Fired += delegate (FollowUpReason r) { Fired.Add(r); };
                Gate.SlashEnded += delegate (float s, bool f) { EndedSeconds = s; EndedFired = f; };
            }

            public bool Only(FollowUpReason r) { return Fired.Count == 1 && Fired[0] == r; }
            public bool None { get { return Fired.Count == 0; } }
        }

        public static bool Run(Action<string> log)
        {
            int pass = 0, fail = 0;
            Action<string, bool> check = delegate (string name, bool ok)
            {
                if (ok) pass++; else fail++;
                log((ok ? "  DUNG  " : "  SAI   ") + name);
            };

            Probe p;

            p = new Probe();
            check("khong chem: bam V chay ngay", p.Gate.PressV(0f) && p.Only(FollowUpReason.Immediate));

            p = new Probe();
            p.Gate.BeginSlash(0f, 0f);
            bool ranNow = p.Gate.PressV(0.2f);
            bool silentWhileSlashing = !ranNow && p.None && p.Gate.HasBufferedPress;
            p.Gate.EndSlash(0.68f);
            check("dang chem: V duoc nho, chem xong moi chay", silentWhileSlashing && p.Only(FollowUpReason.Buffered));
            p.Gate.EndSlash(0.70f);
            check("EndSlash lan hai: khong chay them", p.Fired.Count == 1);

            p = new Probe();
            p.Gate.BeginSlash(0f, 0f);
            p.Gate.PressV(0.1f); p.Gate.PressV(0.3f); p.Gate.PressV(0.5f);
            p.Gate.EndSlash(0.68f);
            check("bam V 3 lan trong luc chem: chi chay 1 lan", p.Only(FollowUpReason.Buffered));

            p = new Probe();
            p.Gate.BeginSlash(0f, 0f);
            p.Gate.PressV(0.3f);
            p.Gate.CancelSlash();
            p.Gate.EndSlash(0.68f);
            check("bi ngat (doi vu khi / trung don): bo V dang nho", p.None && !p.Gate.IsSlashing && !p.Gate.HasBufferedPress);

            p = new Probe();
            p.Gate.EndSlash(1f);
            p.Gate.Tick(2f);
            check("EndSlash / Tick khi khong chem: khong lam gi", p.None);

            p = new Probe();
            p.Gate.AutoFollowUp = true;
            p.Gate.BeginSlash(0f, 0f);
            p.Gate.EndSlash(0.68f);
            check("AutoFollowUp: chem xong tu chay V", p.Only(FollowUpReason.Auto));

            p = new Probe();
            p.Gate.AutoFollowUp = true;
            p.Gate.BeginSlash(0f, 0f);
            p.Gate.PressV(0.4f);
            p.Gate.EndSlash(0.68f);
            check("AutoFollowUp + bam V: van chi 1 lan", p.Only(FollowUpReason.Buffered));

            p = new Probe();
            p.Gate.AutoFollowUp = true;
            p.Gate.BeginSlash(0f, 0f);
            p.Gate.CancelSlash();
            check("AutoFollowUp + bi ngat: khong chay", p.None);

            p = new Probe();
            p.Gate.BeginSlash(10f, 0.68f);
            p.Gate.PressV(10.1f);
            p.Gate.Tick(10.5f);
            bool notEarly = p.None;
            p.Gate.Tick(10.67f);
            notEarly = notEarly && p.None;
            p.Gate.Tick(10.69f);
            check("timer 0.68 s: khong chay som, chay o frame dau tien sau moc",
                  notEarly && p.Only(FollowUpReason.Buffered) && Math.Abs(p.Gate.LastSlashDuration - 0.69f) < 1e-4f);

            p = new Probe();
            p.Gate.BufferSeconds = 0.2f;
            p.Gate.BeginSlash(0f, 0f);
            p.Gate.PressV(0.1f);
            p.Gate.EndSlash(0.68f);
            bool tooEarlyDropped = p.None;
            p.Gate.BeginSlash(1f, 0f);
            p.Gate.PressV(1.55f);
            p.Gate.EndSlash(1.68f);
            check("BufferSeconds 0.2: bam som qua bi bo, bam sat cuoi thi chay",
                  tooEarlyDropped && p.Only(FollowUpReason.Buffered));

            p = new Probe();
            p.Gate.BeginSlash(0f, 0f);
            p.Gate.PressV(0.3f);
            p.Gate.BeginSlash(0.5f, 0f);   // chem lien tiep, nhat 1 chua ket thuc
            bool keptOverChain = p.None && p.Gate.HasBufferedPress;
            p.Gate.EndSlash(1.2f);
            check("chem lien tiep: V nho tu nhat 1 chay sau nhat 2", keptOverChain && p.Only(FollowUpReason.Buffered));

            p = new Probe();
            p.Gate.BeginSlash(3f, 0f);
            p.Gate.EndSlash(3.7f);
            check("SlashEnded bao dung thoi gian chem that",
                  Math.Abs(p.EndedSeconds - 0.7f) < 1e-4f && !p.EndedFired && p.None);

            log(string.Format("[FollowUpGate] {0} dung, {1} sai", pass, fail));
            return fail == 0;
        }
    }
}
