// =============================================================================
//  HeavySlashStateHook - gan vao state CHEM NANG trong Animator Controller
// -----------------------------------------------------------------------------
//  Cach noi KHONG can sua code game, khong can sua clip: chon state chem nang
//  trong cua so Animator -> Add Behaviour -> HeavySlashStateHook.
//
//     vao state                   -> BeginHeavySlash()
//     toi endAtNormalizedTime     -> EndHeavySlash()   (V chay dung frame nay)
//     roi state truoc moc do      -> exitCountsAsEnd ? EndHeavySlash() : CancelHeavySlash()
//
//  Dung cach nay khi khoa hanh dong cua game di theo animation. Neu game khoa
//  bang timer trong code vu khi, goi BeginHeavySlash / EndHeavySlash o do se
//  chinh xac hon.
//
//  Han che: khong dung voi transition tu state chem ve CHINH NO (chem lien tiep
//  bang self-transition) - Unity goi Enter cua lan moi truoc Exit cua lan cu.
//  Chem lien tiep thi tach thanh hai state.
// =============================================================================
using UnityEngine;

namespace KnifeCombo
{
    public sealed class HeavySlashStateHook : StateMachineBehaviour
    {
        [Tooltip("Khoa hanh dong ket thuc o dau trong clip (0..1). 1 = het clip. Nen dat bang Exit Time cua transition ra khoi state nay, hoac som hon neu game cho huy phan thu dao.")]
        [Range(0.05f, 1f)]
        public float endAtNormalizedTime = 1f;

        [Tooltip("Roi state truoc khi toi moc tren: true = coi nhu chem xong (chay V dang nho), false = coi nhu bi ngat (bo V). Bi ngat that (doi vu khi, trung don) thi code game nen goi CancelHeavySlash() truoc.")]
        public bool exitCountsAsEnd = true;

        private HeavySlashFollowUp _target;
        private bool _active;

        public override void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
        {
            if (_target == null)
            {
                // Component thuong o goc nhan vat, Animator o model con - tim ca hai phia.
                _target = animator.GetComponentInParent<HeavySlashFollowUp>();
                if (_target == null) _target = animator.GetComponentInChildren<HeavySlashFollowUp>();
            }
            // Khong tim thay = nhan vat cua nguoi khac (chi local moi gan component) -> im lang bo qua.
            _active = _target != null;
            if (_active) _target.BeginHeavySlash(0f);
        }

        public override void OnStateUpdate(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
        {
            if (!_active) return;
            if (_target == null) { _active = false; return; }   // nhan vat bi huy giua nhat chem
            if (stateInfo.normalizedTime >= endAtNormalizedTime)
            {
                _active = false;
                _target.EndHeavySlash();
            }
        }

        public override void OnStateExit(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
        {
            if (!_active) return;
            _active = false;
            if (_target == null) return;
            if (exitCountsAsEnd) _target.EndHeavySlash();
            else _target.CancelHeavySlash();
        }
    }
}
