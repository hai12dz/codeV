// =============================================================================
//  HeavySlashFollowUp - gan len nhan vat cua NGUOI CHOI LOCAL
// -----------------------------------------------------------------------------
//  Noi FollowUpGate voi Unity: dong ho (Time), Inspector, UnityEvent, Animation
//  Event. Game chi can bao 3 dieu:
//     BeginHeavySlash()   nhat chem nang bat dau
//     EndHeavySlash()     khoa hanh dong duoc go (goi o DUNG dong code go khoa)
//     PressV()            nguoi choi bam V (thay cho goi thang hanh dong V)
//  roi noi hanh dong V that vao onFollowUp (Inspector) hoac event FollowUp (code).
//
//  CHI gan cho nhan vat local. Nhan vat cua nguoi khac cung choi animation chem;
//  gan component cho ho thi AutoFollowUp se chay V thay ho tren may minh.
// =============================================================================
using System;
using UnityEngine;
using UnityEngine.Events;

namespace KnifeCombo
{
    [DisallowMultipleComponent]
    public sealed class HeavySlashFollowUp : MonoBehaviour
    {
        [Header("Hanh vi")]
        [Tooltip("Chem nang xong la TU chay hanh dong V, khong can bam. Co the dua ra menu setting cho nguoi choi bat/tat.")]
        [SerializeField] private bool autoFollowUp = false;

        [Tooltip("Bam V trong luc dang chem: chi nho lai neu bam trong N giay cuoi truoc khi chem xong. 0 = nho suot nhat chem.")]
        [SerializeField] private float bufferSeconds = 0f;

        [Tooltip("Bat neu code khoa hanh dong cua game go khoa SAU EndHeavySlash() trong cung frame (vd thu tu cac Animation Event). V se chay o Update ke tiep thay vi ngay lap tuc.")]
        [SerializeField] private bool deferToNextFrame = false;

        [Tooltip("Dung Time.unscaledTime thay cho Time.time (game co slow-motion / pause bang timeScale).")]
        [SerializeField] private bool useUnscaledTime = false;

        [Tooltip("In thoi gian moi nhat chem ra Console - de biet tung loai dao chem bao lau.")]
        [SerializeField] private bool logTiming = false;

        [Header("Hanh dong V")]
        [Tooltip("Noi vao DUNG ham ma phim V that dang goi, de duong mang / kiem tra phia server khong doi.")]
        [SerializeField] private UnityEvent onFollowUp = new UnityEvent();

        /// <summary>Cho code C# dang ky thay vi keo tha UnityEvent trong Inspector.</summary>
        public event Action<FollowUpReason> FollowUp;

        private readonly FollowUpGate _gate = new FollowUpGate();
        private bool _wired;
        private bool _deferred;
        private FollowUpReason _deferredReason;
        private int _deferredFrame;

        public bool IsSlashing { get { return _gate.IsSlashing; } }
        public bool HasBufferedPress { get { return _gate.HasBufferedPress; } }
        public float LastSlashDuration { get { return _gate.LastSlashDuration; } }

        /// <summary>Bat/tat che do tu chay V luc runtime (vd tu menu setting).</summary>
        public bool AutoFollowUp
        {
            get { return autoFollowUp; }
            set { autoFollowUp = value; SyncSettings(); }
        }

        private float Now { get { return useUnscaledTime ? Time.unscaledTime : Time.time; } }

        private void Awake()
        {
            Wire();
        }

        // Noi gate voi component. Goi ca tu Awake lan tu moi ham public: GameObject
        // con inactive thi Awake chua chay, ma code game van co the goi vao.
        private void Wire()
        {
            if (_wired) return;
            _wired = true;
            _gate.Fired += OnGateFired;
            _gate.SlashEnded += OnSlashEnded;
            SyncSettings();
        }

        private void OnValidate()
        {
            SyncSettings();
        }

        private void OnDisable()
        {
            // Tat component / doi nhan vat giua nhat chem -> khong de V treo lai.
            CancelHeavySlash();
        }

        private void Update()
        {
            _gate.Tick(Now);
            if (_deferred && Time.frameCount > _deferredFrame)
            {
                _deferred = false;
                RunFollowUp(_deferredReason);
            }
        }

        private void SyncSettings()
        {
            _gate.AutoFollowUp = autoFollowUp;
            _gate.BufferSeconds = Mathf.Max(0f, bufferSeconds);
        }

        // ---------------------------------------------------------------------
        //  Goi tu code game
        // ---------------------------------------------------------------------

        /// <summary>
        /// Nhat chem nang bat dau. Truyen lockSeconds neu code game biet truoc thoi
        /// gian khoa (vd tu data cua tung loai dao): component tu ket thuc dung luc.
        /// De 0 neu se goi EndHeavySlash() (hoac dung HeavySlashStateHook / Animation Event).
        /// </summary>
        public void BeginHeavySlash(float lockSeconds = 0f)
        {
            Wire();
            _gate.BeginSlash(Now, lockSeconds);
        }

        /// <summary>Khoa hanh dong vua duoc go. Goi o DUNG dong code game go khoa.</summary>
        public void EndHeavySlash()
        {
            Wire();
            _gate.EndSlash(Now);
        }

        /// <summary>Nhat chem bi ngat (doi vu khi, trung don, chet...). Bo V dang nho.</summary>
        public void CancelHeavySlash()
        {
            _gate.CancelSlash();
            _deferred = false;
        }

        /// <summary>
        /// Goi thay cho viec chay thang hanh dong V khi nguoi choi bam V.
        /// true = V chay ngay; false = dang chem, V duoc nho va chay luc chem xong.
        /// </summary>
        public bool PressV()
        {
            Wire();
            return _gate.PressV(Now);
        }

        // ---------------------------------------------------------------------
        //  Animation Event - chon ten ham nay trong clip chem nang. Component phai
        //  nam CUNG GameObject voi Animator thi clip moi goi toi duoc.
        // ---------------------------------------------------------------------
        public void AnimEvent_HeavySlashBegin() { BeginHeavySlash(0f); }
        public void AnimEvent_HeavySlashEnd() { EndHeavySlash(); }

        // ---------------------------------------------------------------------
        private void OnGateFired(FollowUpReason reason)
        {
            if (deferToNextFrame && reason != FollowUpReason.Immediate)
            {
                _deferred = true;
                _deferredReason = reason;
                _deferredFrame = Time.frameCount;
                return;
            }
            RunFollowUp(reason);
        }

        private void RunFollowUp(FollowUpReason reason)
        {
            onFollowUp.Invoke();
            Action<FollowUpReason> h = FollowUp;
            if (h != null) h(reason);
        }

        private void OnSlashEnded(float seconds, bool fired)
        {
            if (!logTiming) return;
            Debug.Log(string.Format("[HeavySlashFollowUp] {0}: chem nang {1:0.000} s -> {2}",
                name, seconds, fired ? "chay V" : "khong co V"), this);
        }
    }
}
