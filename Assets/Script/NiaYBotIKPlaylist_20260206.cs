using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NIA_YBOT_IK_PLAYLIST_MATCH_STICKMAN_20260206
{
    public class NiaYBotIKPlaylist_20260206 : MonoBehaviour
    {
        // ---------------------------
        // JSON DTO (nested)
        // ---------------------------
        [Serializable] private class Root { public People people; }
        [Serializable]
        private class People
        {
            public List<float> pose_keypoints_3d;
            public List<float> hand_left_keypoints_3d;
            public List<float> hand_right_keypoints_3d;
        }

        [Header("Source (StreamingAssets)")]
        public string rootFolderInStreamingAssets = "SignJson";
        public List<string> sequenceFolders = new List<string> { "1", "2", "3", "4" };
        public string filePattern = "*_keypoints.json";

        [Header("Playback")]
        public float fps = 30f;
        public float endHoldSeconds = 0.5f;
        public bool autoNextSequence = true;
        public bool loopPlaylist = true;
        public bool loopSequence = true;
        public bool enableHotkeys = true;

        [Header("Coordinate Conversion (same as stickman)")]
        public float scale = 0.01f;
        public bool flipY = true;
        public bool flipZ = true;

        [Tooltip("좌/우가 바뀌어 보이면 이거 ON (한 번에 해결되는 경우 많음)")]
        public bool swapLeftRight = false;

        [Header("In-Place")]
        [Tooltip("골반(hips) 기준으로 인플레이스. 켜면 캐릭터가 앞으로 이동하지 않고 제자리에서 따라함")]
        public bool inPlace = true;

        [Tooltip("월드에서 캐릭터를 띄우는 오프셋(스틱맨 worldOffset 같은 역할)")]
        public Vector3 worldOffset = new Vector3(0, 0.9f, 0);

        [Header("IK Targets (Animation Rigging)")]
        public Transform leftHandTarget;
        public Transform leftArmHint;
        public Transform rightHandTarget;
        public Transform rightArmHint;

        public Transform leftFootTarget;
        public Transform leftLegHint;
        public Transform rightFootTarget;
        public Transform rightLegHint;

        [Header("Optional: Head/Chest target (있으면 더 비슷해짐)")]
        public Transform headTarget;
        public Transform chestTarget;

        [Header("Tuning")]
        [Tooltip("힌트(팔꿈치/무릎)를 얼마나 바깥으로 밀지")]
        public float hintOut = 0.25f;

        [Tooltip("힌트(팔꿈치/무릎)를 얼마나 앞으로/뒤로 밀지")]
        public float hintForward = 0.15f;

        // ---------------------------
        // Playback State
        // ---------------------------
        private List<string[]> sequences = new();
        private List<string> loadedLabels = new();
        private int seqIndex = 0;
        private int frameIndex = 0;
        private float acc = 0f;

        private float holdTimer = 0f;
        private bool pendingEndAction = false;
        private EndActionType pendingActionType = EndActionType.None;
        private enum EndActionType { None, NextSequence, LoopSequence, StopAtEnd, StopPlaylistEnd }

        // ---------------------------
        // BODY25 indices
        // ---------------------------
        private const int PoseCount = 25;
        private const int Neck = 1;

        private const int RShoulder = 2;
        private const int RElbow = 3;
        private const int RWrist = 4;

        private const int LShoulder = 5;
        private const int LElbow = 6;
        private const int LWrist = 7;

        private const int MidHip = 8;

        private const int RHip = 9;
        private const int RKnee = 10;
        private const int RAnkle = 11;

        private const int LHip = 12;
        private const int LKnee = 13;
        private const int LAnkle = 14;

        void Start()
        {
            LoadAllSequences();

            if (sequences.Count == 0)
            {
                Debug.LogError("[YBotIKPlaylist] No sequences loaded. Check StreamingAssets/SignJson/1,2,3,4");
                enabled = false;
                return;
            }

            Debug.Log($"[YBotIKPlaylist] Ready. sequences={sequences.Count}, current={loadedLabels[seqIndex]}");
        }

        void Update()
        {
            if (sequences == null || sequences.Count == 0) return;

            if (enableHotkeys) HandleHotkeys();

            // End Hold: 마지막 포즈 유지
            if (holdTimer > 0f)
            {
                holdTimer -= Time.deltaTime;
                if (holdTimer <= 0f && pendingEndAction)
                {
                    pendingEndAction = false;
                    DoPendingEndAction();
                }
                return;
            }

            acc += Time.deltaTime;
            float spf = 1f / Mathf.Max(1f, fps);

            while (acc >= spf)
            {
                acc -= spf;
                StepFrame();
            }
        }

        // ---------------------------
        // Load sequences
        // ---------------------------
        void LoadAllSequences()
        {
            sequences.Clear();
            loadedLabels.Clear();

            string root = Path.Combine(Application.streamingAssetsPath, rootFolderInStreamingAssets);
            if (!Directory.Exists(root))
            {
                Debug.LogError($"[YBotIKPlaylist] Root folder not found: {root}");
                return;
            }

            foreach (var seqName in sequenceFolders)
            {
                string folder = Path.Combine(root, seqName);
                if (!Directory.Exists(folder))
                {
                    Debug.LogWarning($"[YBotIKPlaylist] Sequence folder not found: {folder}");
                    continue;
                }

                var files = Directory.GetFiles(folder, filePattern);
                Array.Sort(files);

                if (files.Length == 0)
                {
                    Debug.LogWarning($"[YBotIKPlaylist] No files in: {folder}");
                    continue;
                }

                sequences.Add(files);
                loadedLabels.Add(seqName);
                Debug.Log($"[YBotIKPlaylist] Loaded '{seqName}' frames={files.Length}");
            }

            seqIndex = Mathf.Clamp(seqIndex, 0, sequences.Count - 1);
            frameIndex = 0;
            acc = 0f;

            holdTimer = 0f;
            pendingEndAction = false;
            pendingActionType = EndActionType.None;
        }

        void StepFrame()
        {
            var curSeq = sequences[seqIndex];
            if (curSeq == null || curSeq.Length == 0) return;

            ApplyFrame(curSeq[frameIndex]);

            frameIndex++;
            if (frameIndex >= curSeq.Length)
            {
                frameIndex = Mathf.Clamp(curSeq.Length - 1, 0, curSeq.Length - 1);

                pendingEndAction = true;
                pendingActionType = DecideEndAction();

                holdTimer = Mathf.Max(0f, endHoldSeconds);
                if (holdTimer <= 0f)
                {
                    pendingEndAction = false;
                    DoPendingEndAction();
                }
            }
        }

        EndActionType DecideEndAction()
        {
            if (autoNextSequence)
            {
                if (seqIndex + 1 < sequences.Count) return EndActionType.NextSequence;
                return loopPlaylist ? EndActionType.NextSequence : EndActionType.StopPlaylistEnd;
            }
            else
            {
                if (loopSequence) return EndActionType.LoopSequence;
                return EndActionType.StopAtEnd;
            }
        }

        void DoPendingEndAction()
        {
            switch (pendingActionType)
            {
                case EndActionType.NextSequence:
                    GoNextSequence();
                    break;
                case EndActionType.LoopSequence:
                    frameIndex = 0;
                    break;
                case EndActionType.StopAtEnd:
                    // 마지막 포즈 유지
                    break;
                case EndActionType.StopPlaylistEnd:
                    Debug.Log("[YBotIKPlaylist] Playlist finished. Stop.");
                    enabled = false;
                    break;
            }

            pendingActionType = EndActionType.None;
        }

        void GoNextSequence()
        {
            frameIndex = 0;
            acc = 0f;

            seqIndex++;
            if (seqIndex >= sequences.Count)
            {
                if (loopPlaylist) seqIndex = 0;
                else { enabled = false; return; }
            }

            Debug.Log($"[YBotIKPlaylist] Switched -> {loadedLabels[seqIndex]}");
        }

        public void SwitchSequence(int index)
        {
            if (index < 0 || index >= sequences.Count) return;

            seqIndex = index;
            frameIndex = 0;
            acc = 0f;

            holdTimer = 0f;
            pendingEndAction = false;
            pendingActionType = EndActionType.None;

            Debug.Log($"[YBotIKPlaylist] Manual switch -> {loadedLabels[seqIndex]}");
        }

        void HandleHotkeys()
        {
            if (Input.GetKeyDown(KeyCode.Alpha1)) SwitchSequence(0);
            if (Input.GetKeyDown(KeyCode.Alpha2)) SwitchSequence(1);
            if (Input.GetKeyDown(KeyCode.Alpha3)) SwitchSequence(2);
            if (Input.GetKeyDown(KeyCode.Alpha4)) SwitchSequence(3);
            if (Input.GetKeyDown(KeyCode.Alpha5)) SwitchSequence(4);
            if (Input.GetKeyDown(KeyCode.Alpha6)) SwitchSequence(5);
            if (Input.GetKeyDown(KeyCode.Alpha7)) SwitchSequence(6);
            if (Input.GetKeyDown(KeyCode.Alpha8)) SwitchSequence(7);
            if (Input.GetKeyDown(KeyCode.Alpha9)) SwitchSequence(8);

            // 좌/우 스왑 토글
            if (Input.GetKeyDown(KeyCode.T))
            {
                swapLeftRight = !swapLeftRight;
                Debug.Log($"[YBotIKPlaylist] swapLeftRight -> {swapLeftRight}");
            }
        }

        // ---------------------------
        // ✅ 핵심: 스틱맨 포즈(관절 위치) -> IK Targets
        // ---------------------------
        void ApplyFrame(string path)
        {
            if (!leftHandTarget || !rightHandTarget || !leftFootTarget || !rightFootTarget)
            {
                // 타겟을 안 꽂으면 아무것도 못함
                return;
            }

            string json = File.ReadAllText(path);
            var root = JsonUtility.FromJson<Root>(json);
            if (root?.people == null) return;

            var pose = root.people.pose_keypoints_3d;
            if (pose == null || pose.Count < PoseCount * 4) return;

            Vector3[] P = new Vector3[PoseCount];
            for (int i = 0; i < PoseCount; i++)
                P[i] = GetVec3(pose, i) * scale;

            // inPlace: MidHip 기준으로 빼기
            Vector3 origin = inPlace ? P[MidHip] : Vector3.zero;
            for (int i = 0; i < PoseCount; i++)
                P[i] = P[i] - origin;

            // 좌/우 스왑(필요하면)
            int LS = swapLeftRight ? RShoulder : LShoulder;
            int LE = swapLeftRight ? RElbow : LElbow;
            int LW = swapLeftRight ? RWrist : LWrist;

            int RS = swapLeftRight ? LShoulder : RShoulder;
            int RE = swapLeftRight ? LElbow : RElbow;
            int RW = swapLeftRight ? LWrist : RWrist;

            int LH = swapLeftRight ? RHip : LHip;
            int LK = swapLeftRight ? RKnee : LKnee;
            int LA = swapLeftRight ? RAnkle : LAnkle;

            int RH = swapLeftRight ? LHip : RHip;
            int RK = swapLeftRight ? LKnee : RKnee;
            int RA = swapLeftRight ? LAnkle : RAnkle;

            // 월드 목표 위치(스틱맨처럼 worldOffset 적용)
            Vector3 W(Vector3 v) => transform.TransformPoint(v + worldOffset);

            // 손/발 타겟 = 손목/발목
            leftHandTarget.position = W(P[LW]);
            rightHandTarget.position = W(P[RW]);
            leftFootTarget.position = W(P[LA]);
            rightFootTarget.position = W(P[RA]);

            // 팔꿈치/무릎 힌트 = 관절 위치 + 바깥/앞쪽으로 조금 밀기
            // (힌트 없으면 IK가 반대로 꺾이거나 꼬임 심해짐)
            if (leftArmHint)
                leftArmHint.position = MakeHintPos(W(P[LE]), W(P[LS]), W(P[LW]), isLeft: true);

            if (rightArmHint)
                rightArmHint.position = MakeHintPos(W(P[RE]), W(P[RS]), W(P[RW]), isLeft: false);

            if (leftLegHint)
                leftLegHint.position = MakeHintPos(W(P[LK]), W(P[LH]), W(P[LA]), isLeft: true);

            if (rightLegHint)
                rightLegHint.position = MakeHintPos(W(P[RK]), W(P[RH]), W(P[RA]), isLeft: false);

            // 옵션: 머리/가슴 타겟
            if (headTarget) headTarget.position = W(P[Neck] + (P[0] - P[Neck]) * 0.7f);
            if (chestTarget) chestTarget.position = W((P[Neck] + P[MidHip]) * 0.5f);
        }

        Vector3 MakeHintPos(Vector3 joint, Vector3 a, Vector3 b, bool isLeft)
        {
            // 힌트 방향: (a->b) 뼈 방향과 수직인 방향(바깥쪽) + 약간 앞쪽
            Vector3 boneDir = (b - a);
            if (boneDir.sqrMagnitude < 1e-8f) boneDir = Vector3.down;
            boneDir.Normalize();

            // 바깥쪽: 캐릭터 기준 좌/우로
            Vector3 outDir = isLeft ? -transform.right : transform.right;

            // 앞쪽
            Vector3 fwd = transform.forward;

            return joint + outDir * hintOut + fwd * hintForward;
        }

        Vector3 GetVec3(List<float> list, int idx)
        {
            int b = idx * 4;
            float x = list[b + 0];
            float y = list[b + 1];
            float z = list[b + 2];

            if (flipY) y = -y;
            if (flipZ) z = -z;

            return new Vector3(x, y, z);
        }
    }
}
