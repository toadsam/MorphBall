using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NIA_YBOT_IKPLAYLIST_TARGETONLY_20260206
{
    public class NiaYBotIKPlaylist_TargetOnly_20260206 : MonoBehaviour
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

        // ---------------------------
        // Source / Playlist
        // ---------------------------
        [Header("Source (StreamingAssets)")]
        [Tooltip("StreamingAssets/SignJson")]
        public string rootFolderInStreamingAssets = "SignJson";

        [Tooltip("동작 폴더 (순서대로 재생)")]
        public List<string> sequenceFolders = new List<string> { "1", "2", "3", "4" };

        [Tooltip("프레임 파일 패턴")]
        public string filePattern = "*_keypoints.json";

        [Header("Playback")]
        public float fps = 30f;
        public float endHoldSeconds = 0.5f;
        public bool autoNextSequence = true;
        public bool loopPlaylist = true;
        public bool loopSequence = true;
        public bool enableHotkeys = true;

        // ---------------------------
        // Coordinate Conversion (same as stickman)
        // ---------------------------
        [Header("Coordinate Conversion")]
        public float scale = 0.01f;
        public bool flipY = true;
        public bool flipZ = true;

        [Tooltip("좌/우가 뒤바뀌면 체크")]
        public bool swapLeftRight = false;

        [Header("In-Place / World Offset")]
        public bool inPlace = true;

        [Tooltip("아바타 기준 오프셋(보통 Y만 0.9~1.1 정도)")]
        public Vector3 worldOffset = new Vector3(0, 0.9f, 0);

        // ---------------------------
        // IK Targets (Animation Rigging)
        // ---------------------------
        [Header("IK Targets (Animation Rigging)")]
        public Transform leftHandTarget;
        public Transform leftArmHint;

        public Transform rightHandTarget;
        public Transform rightArmHint;

        public Transform leftFootTarget;
        public Transform leftLegHint;

        public Transform rightFootTarget;
        public Transform rightLegHint;

        [Header("Optional: Head/Chest target (있으면 더 안정)")]
        public Transform headTarget;
        public Transform chestTarget;

        // ---------------------------
        // Tuning
        // ---------------------------
        [Header("Tuning")]
        [Range(0f, 1f)] public float posLerp = 0.6f; // 0=즉시, 1=느리게
        [Tooltip("팔꿈치/무릎 힌트가 옆으로 얼마나 빠질지")]
        public float hintOut = 0.25f;

        [Tooltip("팔꿈치/무릎 힌트가 앞쪽으로 얼마나 나올지")]
        public float hintForward = 0.15f;

        // ---------------------------
        // Internal state
        // ---------------------------
        private List<string[]> sequences = new();
        private List<string> loadedLabels = new();
        private int seqIndex = 0;
        private int frameIndex = 0;
        private float acc = 0f;

        private float holdTimer = 0f;
        private bool pendingEnd = false;
        private EndAction pendingAction = EndAction.None;
        private enum EndAction { None, NextSeq, LoopSeq, StopAtEnd, StopPlaylistEnd }

        // BODY25 indices (OpenPose BODY_25 형태 가정)
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
                Debug.LogError("[IK TargetOnly] No sequences loaded. Check StreamingAssets/SignJson/1,2,3...");
                enabled = false;
                return;
            }

            // 최소 타겟 체크(손/발은 필수)
            if (!leftHandTarget || !rightHandTarget || !leftFootTarget || !rightFootTarget)
            {
                Debug.LogError("[IK TargetOnly] Missing IK Targets. Assign Hand/Foot Targets in Inspector.");
                enabled = false;
                return;
            }

            Debug.Log($"[IK TargetOnly] Ready. sequences={sequences.Count}, current={loadedLabels[seqIndex]}");
        }

        void Update()
        {
            if (sequences == null || sequences.Count == 0) return;
            if (enableHotkeys) HandleHotkeys();

            // end hold (마지막 포즈 유지)
            if (holdTimer > 0f)
            {
                holdTimer -= Time.deltaTime;
                if (holdTimer <= 0f && pendingEnd)
                {
                    pendingEnd = false;
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
        // Playlist
        // ---------------------------
        void LoadAllSequences()
        {
            sequences.Clear();
            loadedLabels.Clear();

            string root = Path.Combine(Application.streamingAssetsPath, rootFolderInStreamingAssets);
            if (!Directory.Exists(root))
            {
                Debug.LogError($"[IK TargetOnly] Root folder not found: {root}");
                return;
            }

            foreach (var seqName in sequenceFolders)
            {
                string folder = Path.Combine(root, seqName);
                if (!Directory.Exists(folder))
                {
                    Debug.LogWarning($"[IK TargetOnly] Sequence folder not found: {folder}");
                    continue;
                }

                var files = Directory.GetFiles(folder, filePattern);
                Array.Sort(files);

                if (files.Length == 0)
                {
                    Debug.LogWarning($"[IK TargetOnly] No files in: {folder} pattern={filePattern}");
                    continue;
                }

                sequences.Add(files);
                loadedLabels.Add(seqName);

                Debug.Log($"[IK TargetOnly] Loaded '{seqName}' frames={files.Length}");
            }

            seqIndex = Mathf.Clamp(seqIndex, 0, sequences.Count - 1);
            frameIndex = 0;
            acc = 0f;
            holdTimer = 0f;
            pendingEnd = false;
            pendingAction = EndAction.None;
        }

        void StepFrame()
        {
            var cur = sequences[seqIndex];
            if (cur == null || cur.Length == 0) return;

            ApplyFrameToTargets(cur[frameIndex]);

            frameIndex++;
            if (frameIndex >= cur.Length)
            {
                frameIndex = Mathf.Clamp(cur.Length - 1, 0, cur.Length - 1);

                pendingEnd = true;
                pendingAction = DecideEndAction();

                holdTimer = Mathf.Max(0f, endHoldSeconds);
                if (holdTimer <= 0f)
                {
                    pendingEnd = false;
                    DoPendingEndAction();
                }
            }
        }

        EndAction DecideEndAction()
        {
            if (autoNextSequence)
            {
                if (seqIndex + 1 < sequences.Count) return EndAction.NextSeq;
                return loopPlaylist ? EndAction.NextSeq : EndAction.StopPlaylistEnd;
            }
            else
            {
                if (loopSequence) return EndAction.LoopSeq;
                return EndAction.StopAtEnd;
            }
        }

        void DoPendingEndAction()
        {
            switch (pendingAction)
            {
                case EndAction.NextSeq:
                    frameIndex = 0;
                    acc = 0f;
                    seqIndex++;
                    if (seqIndex >= sequences.Count)
                    {
                        if (loopPlaylist) seqIndex = 0;
                        else { enabled = false; return; }
                    }
                    Debug.Log($"[IK TargetOnly] Switched -> {loadedLabels[seqIndex]}");
                    break;

                case EndAction.LoopSeq:
                    frameIndex = 0;
                    break;

                case EndAction.StopAtEnd:
                    // 마지막 포즈 유지(아무것도 안 함)
                    break;

                case EndAction.StopPlaylistEnd:
                    Debug.Log("[IK TargetOnly] Playlist finished. Stop.");
                    enabled = false;
                    break;
            }

            pendingAction = EndAction.None;
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
        }

        public void SwitchSequence(int index)
        {
            if (index < 0 || index >= sequences.Count) return;
            seqIndex = index;
            frameIndex = 0;
            acc = 0f;
            holdTimer = 0f;
            pendingEnd = false;
            pendingAction = EndAction.None;

            Debug.Log($"[IK TargetOnly] Manual switch -> {loadedLabels[seqIndex]}");
        }

        // ---------------------------
        // Core: JSON -> IK Target positions only
        // ---------------------------
        void ApplyFrameToTargets(string path)
        {
            string json = File.ReadAllText(path);
            var root = JsonUtility.FromJson<Root>(json);
            if (root?.people == null) return;

            var pose = root.people.pose_keypoints_3d;
            if (pose == null || pose.Count < PoseCount * 4) return;

            // read pose points
            Vector3[] P = new Vector3[PoseCount];
            for (int i = 0; i < PoseCount; i++)
                P[i] = GetVec3(pose, i) * scale;

            // in-place: MidHip 기준으로 원점 정렬
            if (inPlace)
            {
                Vector3 center = P[MidHip];
                for (int i = 0; i < PoseCount; i++) P[i] -= center;
            }

            // 좌우 스왑 옵션
            if (swapLeftRight)
            {
                Swap(ref P[LShoulder], ref P[RShoulder]);
                Swap(ref P[LElbow], ref P[RElbow]);
                Swap(ref P[LWrist], ref P[RWrist]);

                Swap(ref P[LHip], ref P[RHip]);
                Swap(ref P[LKnee], ref P[RKnee]);
                Swap(ref P[LAnkle], ref P[RAnkle]);
            }

            // 월드 기준 변환(= "이 캐릭터 주변"에서 움직이게)
            // 핵심: Target은 "월드 위치"를 받으니, 캐릭터 transform 기준으로 TransformPoint 해준다.
            Vector3 baseWorld = transform.TransformPoint(worldOffset);

            Vector3 LHandW = transform.TransformPoint(P[LWrist] + worldOffset);
            Vector3 RHandW = transform.TransformPoint(P[RWrist] + worldOffset);
            Vector3 LFootW = transform.TransformPoint(P[LAnkle] + worldOffset);
            Vector3 RFootW = transform.TransformPoint(P[RAnkle] + worldOffset);

            // 타겟 포지션 적용(부드럽게)
            SetPos(leftHandTarget, LHandW);
            SetPos(rightHandTarget, RHandW);
            SetPos(leftFootTarget, LFootW);
            SetPos(rightFootTarget, RFootW);

            // 힌트(팔꿈치/무릎 방향 안정화) - 없으면 스킵
            // 힌트는 "관절 위치 + 옆/앞 오프셋" 방식이 가장 단순하고 잘 먹음
            Vector3 torsoUp = SafeDir(P[Neck] - P[MidHip], Vector3.up);
            Vector3 torsoRight = SafeDir(P[RShoulder] - P[LShoulder], Vector3.right);
            Vector3 torsoFwd = Vector3.Cross(torsoUp, torsoRight);
            if (torsoFwd.sqrMagnitude < 1e-8f) torsoFwd = Vector3.forward;
            torsoFwd.Normalize();

            // elbow/knee world
            Vector3 LElbowW = transform.TransformPoint(P[LElbow] + worldOffset);
            Vector3 RElbowW = transform.TransformPoint(P[RElbow] + worldOffset);
            Vector3 LKneeW = transform.TransformPoint(P[LKnee] + worldOffset);
            Vector3 RKneeW = transform.TransformPoint(P[RKnee] + worldOffset);

            // hint offsets (왼쪽은 -right, 오른쪽은 +right)
            Vector3 rightW = transform.TransformDirection(torsoRight);
            Vector3 fwdW = transform.TransformDirection(torsoFwd);

            if (leftArmHint) SetPos(leftArmHint, LElbowW + (-rightW * hintOut) + (fwdW * hintForward));
            if (rightArmHint) SetPos(rightArmHint, RElbowW + (rightW * hintOut) + (fwdW * hintForward));

            if (leftLegHint) SetPos(leftLegHint, LKneeW + (-rightW * hintOut) + (fwdW * hintForward));
            if (rightLegHint) SetPos(rightLegHint, RKneeW + (rightW * hintOut) + (fwdW * hintForward));

            // (옵션) 머리/가슴 타겟
            if (headTarget)
            {
                Vector3 headApproxW = transform.TransformPoint(P[Neck] + torsoUp * 0.25f + worldOffset);
                SetPos(headTarget, headApproxW);
            }
            if (chestTarget)
            {
                Vector3 chestApproxW = transform.TransformPoint((P[LShoulder] + P[RShoulder]) * 0.5f + worldOffset);
                SetPos(chestTarget, chestApproxW);
            }
        }

        void SetPos(Transform t, Vector3 worldPos)
        {
            if (!t) return;
            if (posLerp <= 0f)
            {
                t.position = worldPos;
                return;
            }
            float k = 1f - Mathf.Pow(1f - posLerp, Time.deltaTime * 60f); // 프레임레이트 독립
            t.position = Vector3.Lerp(t.position, worldPos, k);
        }

        // ---------------------------
        // Helpers
        // ---------------------------
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

        Vector3 SafeDir(Vector3 v, Vector3 fallback)
        {
            if (v.sqrMagnitude < 1e-8f) return fallback.normalized;
            return v.normalized;
        }

        void Swap(ref Vector3 a, ref Vector3 b)
        {
            var t = a; a = b; b = t;
        }
    }
}
