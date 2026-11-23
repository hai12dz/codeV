// Menu trong Unity Editor: Tools > Knife Follow-Up > Chay self-test
using UnityEditor;
using UnityEngine;

namespace KnifeCombo.EditorTools
{
    public static class KnifeFollowUpMenu
    {
        [MenuItem("Tools/Knife Follow-Up/Chay self-test")]
        private static void RunSelfTest()
        {
            bool ok = FollowUpGateSelfTest.Run(delegate (string s) { Debug.Log(s); });
            if (ok) Debug.Log("[KnifeFollowUp] self-test: DUNG het.");
            else Debug.LogError("[KnifeFollowUp] self-test: co truong hop SAI - xem cac dong o tren.");
        }
    }
}
