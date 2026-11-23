// =============================================================================
//  FollowUpGate - quyet dinh LUC NAO hanh dong V duoc chay quanh nhat chem nang
// -----------------------------------------------------------------------------
//  Day la cach "lam trong game", thay cho VAssist (tool ngoai). Tool ngoai phai
//  DOAN dao chem xong luc nao (delay_ms, lech +-1 frame, moi loai dao mot so,
//  va bi anti-cheat chan). Trong game thi khong can doan: game BIET chinh xac
//  luc khoa hanh dong duoc go, chi can noi V vao dung luc do.
//
//  Ba quy tac:
//    1. Khong dang chem     -> bam V la chay V ngay (khong khac gi hien tai).
//    2. Dang chem, bam V    -> NHO LAI, chem xong moi chay. V khong bao gio bi nuot.
//    3. AutoFollowUp = true -> chem xong la TU chay V, khong can bam (dung cai
//                              macro ngoai co lam, nhung dung tung frame).
//  Bi ngat giua chung (doi vu khi, trung don, chet) -> CancelSlash(), bo V dang nho.
//
//  Logic thuan, KHONG phu thuoc Unity: moi ham nhan thoi gian hien tai (giay)
//  tu ben ngoai -> test duoc ngoai Unity, dung duoc voi Time.time, unscaledTime
//  hay tick cua server.
// =============================================================================
using System;

namespace KnifeCombo
{
    public enum FollowUpReason
    {
        Immediate,   // bam V luc khong chem -> chay ngay
        Buffered,    // bam V trong luc chem -> chay dung luc chem xong
        Auto         // AutoFollowUp: chem xong tu chay, khong ai bam
    }

    public sealed class FollowUpGate
    {
        /// <summary>
        /// Bam V trong luc dang chem thi nho lai bao lau. 0 = nho suot nhat chem.
        /// Lon hon 0 = chi nho neu bam trong N giay cuoi truoc khi chem xong
        /// (tranh truong hop bam vo y tu dau nhat chem ma V van chay).
        /// </summary>
        public float BufferSeconds;

        /// <summary>Chem nang xong la tu chay V, khong can bam.</summary>
        public bool AutoFollowUp;

        /// <summary>Hanh dong V can chay. Game noi DUNG ham ma phim V that dang goi.</summary>
        public event Action<FollowUpReason> Fired;

        /// <summary>Moi lan chem ket thuc: (thoi gian chem that tinh bang giay, co chay V hay khong).</summary>
        public event Action<float, bool> SlashEnded;

        private bool _slashing;
        private float _start;
        private float _endAt = -1f;     // am = khong biet truoc, cho EndSlash() goi vao
        private bool _buffered;
        private float _pressAt;
        private float _lastDuration;

        public bool IsSlashing { get { return _slashing; } }
        public bool HasBufferedPress { get { return _buffered; } }
        public float LastSlashDuration { get { return _lastDuration; } }

        /// <summary>
        /// Bat dau mot nhat chem nang.
        /// lockSeconds lon hon 0: game biet truoc thoi gian khoa (tu data vu khi) -> Tick() tu ket thuc.
        /// lockSeconds = 0: cho code game / animation goi EndSlash() dung luc go khoa.
        /// Goi lai trong luc dang chem (chem lien tiep) = bat dau nhat moi, V dang nho van giu.
        /// </summary>
        public void BeginSlash(float now, float lockSeconds)
        {
            _slashing = true;
            _start = now;
            _endAt = lockSeconds > 0f ? now + lockSeconds : -1f;
        }

        /// <summary>Khoa hanh dong vua duoc go. Chay V neu dang nho, hoac neu AutoFollowUp.</summary>
        public void EndSlash(float now)
        {
            if (!_slashing) return;
            _slashing = false;
            _endAt = -1f;
            _lastDuration = now - _start;

            bool buffered = _buffered && (BufferSeconds <= 0f || now - _pressAt <= BufferSeconds);
            _buffered = false;
            bool fire = buffered || AutoFollowUp;

            Action<float, bool> ended = SlashEnded;
            if (ended != null) ended(_lastDuration, fire);

            if (buffered) Raise(FollowUpReason.Buffered);
            else if (AutoFollowUp) Raise(FollowUpReason.Auto);
        }

        /// <summary>Nhat chem bi ngat (doi vu khi, trung don, chet...): bo V dang nho, khong chay gi.</summary>
        public void CancelSlash()
        {
            _slashing = false;
            _endAt = -1f;
            _buffered = false;
        }

        /// <summary>
        /// Nguoi choi bam V. Tra ve true neu V chay NGAY; false neu dang chem va V
        /// duoc nho lai, se chay dung luc chem xong.
        /// </summary>
        public bool PressV(float now)
        {
            if (_slashing)
            {
                _buffered = true;
                _pressAt = now;      // bam nhieu lan: tinh lan moi nhat
                return false;
            }
            Raise(FollowUpReason.Immediate);
            return true;
        }

        /// <summary>Goi moi frame. Chi co tac dung khi BeginSlash co lockSeconds lon hon 0.</summary>
        public void Tick(float now)
        {
            if (_slashing && _endAt >= 0f && now >= _endAt) EndSlash(now);
        }

        private void Raise(FollowUpReason reason)
        {
            Action<FollowUpReason> h = Fired;
            if (h != null) h(reason);
        }
    }
}
