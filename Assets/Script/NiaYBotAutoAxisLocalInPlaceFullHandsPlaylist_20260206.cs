using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NIA_YBOT_AUTOAXIS_LOCAL_INPLACE_FULLHANDS_PLAYLIST_20260206
{
    public class NiaYBotAutoAxisLocalInPlaceFullHandsPlaylist_20260206 : MonoBehaviour
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
        // Playlist
        // ---------------------------
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

        // ---------------------------
        // Keypoint conversion
        // ---------------------------
        [Header("Keypoint Conversion")]
        public float scale = 0.01f;

        [Tooltip("일반적으로 NIA/카메라 좌표는 위아래가 뒤집혀 있을 수 있어 true가 자주 필요")]
        public bool flipY = true;

        [Tooltip("NIA/카메라 좌표의 앞뒤가 Unity와 반대면 true")]
        public bool flipZ = true;

        [Header("Fix Mirror (Left/Right)")]
        [Tooltip("좌/우가 거울처럼 뒤집혀 들어올 때 X를 반전")]
        public bool flipX = false;

        [Tooltip("첫 프레임에서 어깨 x값 비교로 거울 여부 자동 판별 -> flipX 자동 ON")]
        public bool autoDetectMirror = true;

        [Tooltip("좌/우를 통째로 스왑해서 적용 (보조 옵션). flipX로 해결이 우선")]
        public bool swapLeftRight = false;

        [Header("Smoothing")]
        [Range(0f, 0.5f)]
        public float rotationSmoothing = 0.15f;

        // ---------------------------
        // Avatar / Animator
        // ---------------------------
        [Header("Avatar")]
        public Animator animator;
        public Transform avatarRoot; // 보통 animator.transform

        // ---------------------------
        // Internal playback state
        // ---------------------------
        private List<string[]> sequences = new();
        private List<string> loadedLabels = new();
        private int seqIndex = 0;
        private int frameIndex = 0;
        private float acc = 0f;

        private float holdTimer = 0f;
        private bool pendingEndAction = false;
        private EndActionType pendingEndActionType = EndActionType.None;
        private enum EndActionType { None, NextSequence, LoopSequence, StopAtEnd, StopPlaylistEnd }

        // ---------------------------
        // Keypoint indices (BODY25)
        // ---------------------------
        private const int PoseCount = 25;
        private const int HandCount = 21;

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

        // ---------------------------
        // Bone cache
        // ---------------------------
        private Transform tHips, tSpine, tChest, tUpperChest, tNeck, tHead;

        private Transform tLUpperArm, tLLowerArm, tLHand;
        private Transform tRUpperArm, tRLowerArm, tRHand;

        private Transform tLUpperLeg, tLLowerLeg, tLFoot;
        private Transform tRUpperLeg, tRLowerLeg, tRFoot;

        // Fingers left
        private Transform lThumb1, lThumb2, lThumb3;
        private Transform lIndex1, lIndex2, lIndex3;
        private Transform lMiddle1, lMiddle2, lMiddle3;
        private Transform lRing1, lRing2, lRing3;
        private Transform lLittle1, lLittle2, lLittle3;

        // Fingers right
        private Transform rThumb1, rThumb2, rThumb3;
        private Transform rIndex1, rIndex2, rIndex3;
        private Transform rMiddle1, rMiddle2, rMiddle3;
        private Transform rRing1, rRing2, rRing3;
        private Transform rLittle1, rLittle2, rLittle3;

        // ---------------------------
        // ✅ Auto Axis Calibration (LOCAL)
        // ---------------------------
        private struct BoneCalib
        {
            public Transform bone;
            public Transform child;

            public Quaternion baseLocalRot;   // 레스트 로컬 회전
            public Vector3 primaryAxisLocal;  // bone 로컬에서 자식 방향에 가장 가까운 축(±X/±Y/±Z)
            public Vector3 restAxisInParent;  // 레스트에서 그 축이 "부모공간"에서 향하던 방향

            public bool valid;
        }

        private readonly Dictionary<string, BoneCalib> C = new();

        private static readonly Vector3[] AxisCandidates =
        {
            Vector3.right, Vector3.left,
            Vector3.up, Vector3.down,
            Vector3.forward, Vector3.back
        };

        // ✅ mirror auto detect 1회만 (첫 프레임)
        private bool mirrorChecked = false;

        void Start()
        {
            if (!animator) animator = GetComponentInChildren<Animator>();
            if (!animator)
            {
                Debug.LogError("[AutoAxisLocal] Animator not found. Attach script to the character root (with Animator).");
                enabled = false;
                return;
            }

            if (animator.avatar == null)
            {
                Debug.LogError("[AutoAxisLocal] Animator.avatar is NULL. Set Avatar on Animator (Humanoid).");
                enabled = false;
                return;
            }

            if (!animator.isHuman)
            {
                Debug.LogError("[AutoAxisLocal] Animator is not Humanoid (isHuman=false). Set FBX Rig to Humanoid and Apply.");
                enabled = false;
                return;
            }

            if (!avatarRoot) avatarRoot = animator.transform;

            CacheBones();
            BuildAutoAxisCalibrationLocal();

            LoadAllSequences();
            if (sequences.Count == 0)
            {
                Debug.LogError("[AutoAxisLocal] No sequences loaded. Check StreamingAssets/SignJson/1,2,3,4 structure.");
                enabled = false;
                return;
            }

            Debug.Log($"[AutoAxisLocal] Ready. sequences={sequences.Count}, current={loadedLabels[seqIndex]}");
        }

        void Update()
        {
            if (sequences == null || sequences.Count == 0) return;

            if (enableHotkeys) HandleHotkeys();

            // End hold
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
        // Sequence loading / playback
        // ---------------------------
        void LoadAllSequences()
        {
            sequences.Clear();
            loadedLabels.Clear();

            string root = Path.Combine(Application.streamingAssetsPath, rootFolderInStreamingAssets);
            if (!Directory.Exists(root))
            {
                Debug.LogError($"[AutoAxisLocal] Root folder not found: {root}");
                return;
            }

            foreach (var seqName in sequenceFolders)
            {
                string folder = Path.Combine(root, seqName);
                if (!Directory.Exists(folder))
                {
                    Debug.LogWarning($"[AutoAxisLocal] Sequence folder not found: {folder}");
                    continue;
                }

                var files = Directory.GetFiles(folder, filePattern);
                Array.Sort(files);

                if (files.Length == 0)
                {
                    Debug.LogWarning($"[AutoAxisLocal] No files in: {folder} pattern={filePattern}");
                    continue;
                }

                sequences.Add(files);
                loadedLabels.Add(seqName);
                Debug.Log($"[AutoAxisLocal] Loaded '{seqName}' frames={files.Length}");
            }

            seqIndex = Mathf.Clamp(seqIndex, 0, sequences.Count - 1);
            frameIndex = 0;
            acc = 0f;

            holdTimer = 0f;
            pendingEndAction = false;
            pendingEndActionType = EndActionType.None;

            mirrorChecked = false;
        }

        void StepFrame()
        {
            var cur = sequences[seqIndex];
            if (cur == null || cur.Length == 0) return;

            ApplyFrameToAvatar(cur[frameIndex]);

            frameIndex++;
            if (frameIndex >= cur.Length)
            {
                frameIndex = Mathf.Clamp(cur.Length - 1, 0, cur.Length - 1);

                pendingEndAction = true;
                pendingEndActionType = DecideEndAction();

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
            switch (pendingEndActionType)
            {
                case EndActionType.NextSequence: GoNextSequence(); break;
                case EndActionType.LoopSequence: frameIndex = 0; break;
                case EndActionType.StopAtEnd: break;
                case EndActionType.StopPlaylistEnd:
                    Debug.Log("[AutoAxisLocal] Playlist finished. Stop.");
                    enabled = false;
                    break;
            }
            pendingEndActionType = EndActionType.None;
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

            mirrorChecked = false;
            Debug.Log($"[AutoAxisLocal] Switched -> {loadedLabels[seqIndex]}");
        }

        public void SwitchSequence(int index)
        {
            if (index < 0 || index >= sequences.Count) return;

            seqIndex = index;
            frameIndex = 0;
            acc = 0f;

            holdTimer = 0f;
            pendingEndAction = false;
            pendingEndActionType = EndActionType.None;

            mirrorChecked = false;

            Debug.Log($"[AutoAxisLocal] Manual switch -> {loadedLabels[seqIndex]}");
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

            // ✅ T: 좌/우 swap 토글
            if (Input.GetKeyDown(KeyCode.T))
            {
                swapLeftRight = !swapLeftRight;
                Debug.Log($"[AutoAxisLocal] swapLeftRight -> {swapLeftRight}");
            }

            // ✅ X: flipX 토글
            if (Input.GetKeyDown(KeyCode.X))
            {
                flipX = !flipX;
                mirrorChecked = true; // 수동 토글했으니 자동판별 막기
                Debug.Log($"[AutoAxisLocal] flipX -> {flipX}");
            }

            // ✅ R: 캘리브레이션 재생성
            if (Input.GetKeyDown(KeyCode.R))
            {
                BuildAutoAxisCalibrationLocal();
                mirrorChecked = false;
                Debug.Log("[AutoAxisLocal] Rebuilt calibration (R).");
            }
        }

        // ---------------------------
        // Core: JSON -> Avatar rotations (LOCAL, in-place)
        // ---------------------------
        void ApplyFrameToAvatar(string path)
        {
            string json = File.ReadAllText(path);
            var root = JsonUtility.FromJson<Root>(json);
            if (root?.people == null) return;

            var pose = root.people.pose_keypoints_3d;
            if (pose == null || pose.Count < PoseCount * 4) return;

            // pose points
            Vector3[] P = new Vector3[PoseCount];
            for (int i = 0; i < PoseCount; i++)
                P[i] = GetVec3(pose, i) * scale;

            // ✅ 자동 미러 판별 (첫 프레임 1회)
            if (autoDetectMirror && !mirrorChecked)
            {
                // 보통 정상이라면 RightShoulder.x > LeftShoulder.x 여야 함
                bool mirrored = (P[RShoulder].x < P[LShoulder].x);
                if (mirrored && !flipX)
                {
                    flipX = true;
                    Debug.Log("[AutoAxisLocal] Mirror detected -> flipX = true");
                }
                mirrorChecked = true;
            }

            // translation 제거: MidHip 기준
            Vector3 center = P[MidHip];
            for (int i = 0; i < PoseCount; i++)
                P[i] = P[i] - center;

            // hands
            Vector3[] LH = null;
            Vector3[] RH = null;

            var lh = root.people.hand_left_keypoints_3d;
            if (lh != null && lh.Count >= HandCount * 4)
            {
                LH = new Vector3[HandCount];
                for (int i = 0; i < HandCount; i++) LH[i] = GetVec3(lh, i) * scale;
                Vector3 w = LH[0];
                for (int i = 0; i < HandCount; i++) LH[i] -= w;
            }

            var rh = root.people.hand_right_keypoints_3d;
            if (rh != null && rh.Count >= HandCount * 4)
            {
                RH = new Vector3[HandCount];
                for (int i = 0; i < HandCount; i++) RH[i] = GetVec3(rh, i) * scale;
                Vector3 w = RH[0];
                for (int i = 0; i < HandCount; i++) RH[i] -= w;
            }

            // ✅ 손 데이터 swap(옵션)
            if (swapLeftRight)
            {
                var tmp = LH; LH = RH; RH = tmp;
            }

            // --------------------
            // Torso forward/up (로컬 적용)
            // --------------------
            int RS = swapLeftRight ? LShoulder : RShoulder;
            int LS = swapLeftRight ? RShoulder : LShoulder;

            Vector3 upTorso = SafeDir(P[Neck] - P[MidHip], Vector3.up);
            Vector3 rightLine = SafeDir(P[RS] - P[LS], Vector3.right);

            Vector3 fwdTorso = Vector3.Cross(upTorso, rightLine);
            if (fwdTorso.sqrMagnitude < 1e-8f) fwdTorso = Vector3.forward;
            fwdTorso.Normalize();

            Vector3 upW = avatarRoot.TransformDirection(upTorso);
            Vector3 fwdW = avatarRoot.TransformDirection(fwdTorso);

            ApplyLookLocal(tHips, upW, fwdW);
            ApplyLookLocal(tSpine, upW, fwdW);
            ApplyLookLocal(tChest, upW, fwdW);
            ApplyLookLocal(tUpperChest, upW, fwdW);
            ApplyLookLocal(tNeck, upW, fwdW);
            ApplyLookLocal(tHead, upW, fwdW);

            // --------------------
            // Arms/Legs indices (swap 반영)
            // --------------------
            int L_Sh = swapLeftRight ? RShoulder : LShoulder;
            int L_El = swapLeftRight ? RElbow : LElbow;
            int L_Wr = swapLeftRight ? RWrist : LWrist;

            int R_Sh = swapLeftRight ? LShoulder : RShoulder;
            int R_El = swapLeftRight ? LElbow : RElbow;
            int R_Wr = swapLeftRight ? LWrist : RWrist;

            int L_Hi = swapLeftRight ? RHip : LHip;
            int L_Kn = swapLeftRight ? RKnee : LKnee;
            int L_An = swapLeftRight ? RAnkle : LAnkle;

            int R_Hi = swapLeftRight ? LHip : RHip;
            int R_Kn = swapLeftRight ? LKnee : RKnee;
            int R_An = swapLeftRight ? LAnkle : RAnkle;

            // --------------------
            // ✅ Arms: localRotation + autoAxis (1축)
            // --------------------
            ApplyAutoAxisDirLocal("LUpperArm", SafeDir(P[L_El] - P[L_Sh], Vector3.down));
            ApplyAutoAxisDirLocal("LLowerArm", SafeDir(P[L_Wr] - P[L_El], Vector3.down));

            ApplyAutoAxisDirLocal("RUpperArm", SafeDir(P[R_El] - P[R_Sh], Vector3.down));
            ApplyAutoAxisDirLocal("RLowerArm", SafeDir(P[R_Wr] - P[R_El], Vector3.down));

            // --------------------
            // ✅ Legs: pole(굽힘 방향)까지 포함해서 꼬임 감소
            // --------------------
            ApplyLegWithPoleLocal(
                upperKey: "LUpperLeg", lowerKey: "LLowerLeg",
                hip: P[L_Hi], knee: P[L_Kn], ankle: P[L_An],
                torsoForwardLocal: fwdTorso
            );

            ApplyLegWithPoleLocal(
                upperKey: "RUpperLeg", lowerKey: "RLowerLeg",
                hip: P[R_Hi], knee: P[R_Kn], ankle: P[R_An],
                torsoForwardLocal: fwdTorso
            );

            // hands + fingers
            if (LH != null) ApplyHandAndFingersLocal(isLeft: true, H: LH);
            if (RH != null) ApplyHandAndFingersLocal(isLeft: false, H: RH);
        }

        // ---------------------------
        // ✅ Legs with Pole (knee bend direction fix)
        // ---------------------------
        void ApplyLegWithPoleLocal(
            string upperKey, string lowerKey,
            Vector3 hip, Vector3 knee, Vector3 ankle,
            Vector3 torsoForwardLocal
        )
        {
            Vector3 dirUpper = SafeDir(knee - hip, Vector3.down);
            Vector3 dirLower = SafeDir(ankle - knee, Vector3.down);

            // 무릎이 굽는 방향(정면) = torso forward
            Vector3 pole = SafeDir(torsoForwardLocal, Vector3.forward);

            ApplyAutoAxisLookLocal(upperKey, forwardLocal: dirUpper, upLocal: pole);
            ApplyAutoAxisLookLocal(lowerKey, forwardLocal: dirLower, upLocal: pole);
        }

        // ---------------------------
        // ✅ LOCAL apply helpers
        // ---------------------------
        void ApplyAutoAxisDirLocal(string key, Vector3 targetDirInAvatarLocal)
        {
            if (!C.TryGetValue(key, out var c) || !c.valid || c.bone == null) return;
            if (targetDirInAvatarLocal.sqrMagnitude < 1e-8f) return;

            // avatarRoot 로컬 -> 월드
            Vector3 targetDirWorld = avatarRoot.TransformDirection(targetDirInAvatarLocal.normalized);

            // 월드 -> bone.parent 공간
            Transform parent = c.bone.parent;
            Vector3 targetDirParent = parent ? parent.InverseTransformDirection(targetDirWorld) : targetDirWorld;
            if (targetDirParent.sqrMagnitude < 1e-8f) return;
            targetDirParent.Normalize();

            // 레스트에서 axis가 향하던 방향(restAxisInParent)을 targetDirParent로 회전시키는 delta
            Quaternion delta = Quaternion.FromToRotation(c.restAxisInParent, targetDirParent);

            // local 회전 적용
            Quaternion targetLocal = delta * c.baseLocalRot;

            c.bone.localRotation = SmoothLocal(c.bone.localRotation, targetLocal);
        }

        void ApplyAutoAxisLookLocal(string key, Vector3 forwardLocal, Vector3 upLocal)
        {
            if (!C.TryGetValue(key, out var c) || !c.valid || c.bone == null) return;

            // avatarRoot 로컬 -> 월드
            Vector3 fwdW = avatarRoot.TransformDirection(forwardLocal.normalized);
            Vector3 upW = avatarRoot.TransformDirection(upLocal.normalized);

            // 월드 -> parent 공간
            Transform parent = c.bone.parent;
            Vector3 fwdP = parent ? parent.InverseTransformDirection(fwdW) : fwdW;
            Vector3 upP = parent ? parent.InverseTransformDirection(upW) : upW;

            if (fwdP.sqrMagnitude < 1e-8f) return;
            if (upP.sqrMagnitude < 1e-8f) upP = Vector3.up;

            fwdP.Normalize();
            upP.Normalize();

            // parent공간에서 목표 forward
            Quaternion delta = Quaternion.FromToRotation(c.restAxisInParent, fwdP);
            Quaternion targetLocal = delta * c.baseLocalRot;

            c.bone.localRotation = SmoothLocal(c.bone.localRotation, targetLocal);
        }

        void ApplyLookLocal(Transform bone, Vector3 upWorld, Vector3 fwdWorld)
        {
            if (!bone) return;

            Quaternion targetWorld = Quaternion.LookRotation(fwdWorld, upWorld);

            Transform parent = bone.parent;
            Quaternion targetLocal = parent ? (Quaternion.Inverse(parent.rotation) * targetWorld) : targetWorld;

            bone.localRotation = SmoothLocal(bone.localRotation, targetLocal);
        }

        Quaternion SmoothLocal(Quaternion currentLocal, Quaternion targetLocal)
        {
            if (rotationSmoothing <= 0f) return targetLocal;
            float t = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-4f, rotationSmoothing));
            return Quaternion.Slerp(currentLocal, targetLocal, t);
        }

        // ---------------------------
        // Hands + Fingers (LOCAL)
        // ---------------------------
        void ApplyHandAndFingersLocal(bool isLeft, Vector3[] H)
        {
            // palm normal = cross(indexBase - wrist, pinkyBase - wrist)
            Vector3 v1 = SafeDir(H[5] - H[0], Vector3.right);
            Vector3 v2 = SafeDir(H[17] - H[0], Vector3.left);

            Vector3 palmNormal = Vector3.Cross(v1, v2);
            if (palmNormal.sqrMagnitude < 1e-8f) palmNormal = Vector3.up;
            palmNormal.Normalize();

            Vector3 palmForward = SafeDir(H[9] - H[0], Vector3.forward);

            // avatarRoot 로컬 -> 월드
            Vector3 upW = avatarRoot.TransformDirection(palmNormal);
            Vector3 fwdW = avatarRoot.TransformDirection(palmForward);

            if (isLeft && tLHand) ApplyLookLocal(tLHand, upW, fwdW);
            if (!isLeft && tRHand) ApplyLookLocal(tRHand, upW, fwdW);

            ApplyFingerChain(isLeft, "Thumb", 1, 2, 3, 4);
            ApplyFingerChain(isLeft, "Index", 5, 6, 7, 8);
            ApplyFingerChain(isLeft, "Middle", 9, 10, 11, 12);
            ApplyFingerChain(isLeft, "Ring", 13, 14, 15, 16);
            ApplyFingerChain(isLeft, "Little", 17, 18, 19, 20);

            void ApplyFingerChain(bool left, string name, int a, int b, int c, int d)
            {
                ApplyFingerBoneLocal($"{(left ? "L" : "R")}{name}1", GetFingerTransform(left, name, 1), H, a, b);
                ApplyFingerBoneLocal($"{(left ? "L" : "R")}{name}2", GetFingerTransform(left, name, 2), H, b, c);
                ApplyFingerBoneLocal($"{(left ? "L" : "R")}{name}3", GetFingerTransform(left, name, 3), H, c, d);
            }
        }

        void ApplyFingerBoneLocal(string key, Transform bone, Vector3[] H, int a, int b)
        {
            if (!bone) return;
            if (!C.TryGetValue(key, out var c) || !c.valid) return;

            Vector3 dirLocal = SafeDir(H[b] - H[a], Vector3.forward);

            Vector3 dirWorld = avatarRoot.TransformDirection(dirLocal);

            Transform parent = bone.parent;
            Vector3 dirParent = parent ? parent.InverseTransformDirection(dirWorld) : dirWorld;
            if (dirParent.sqrMagnitude < 1e-8f) return;
            dirParent.Normalize();

            Quaternion delta = Quaternion.FromToRotation(c.restAxisInParent, dirParent);
            Quaternion targetLocal = delta * c.baseLocalRot;

            bone.localRotation = SmoothLocal(bone.localRotation, targetLocal);
        }

        Transform GetFingerTransform(bool isLeft, string finger, int idx)
        {
            if (isLeft)
            {
                return finger switch
                {
                    "Thumb" => idx == 1 ? lThumb1 : idx == 2 ? lThumb2 : lThumb3,
                    "Index" => idx == 1 ? lIndex1 : idx == 2 ? lIndex2 : lIndex3,
                    "Middle" => idx == 1 ? lMiddle1 : idx == 2 ? lMiddle2 : lMiddle3,
                    "Ring" => idx == 1 ? lRing1 : idx == 2 ? lRing2 : lRing3,
                    "Little" => idx == 1 ? lLittle1 : idx == 2 ? lLittle2 : lLittle3,
                    _ => null
                };
            }
            else
            {
                return finger switch
                {
                    "Thumb" => idx == 1 ? rThumb1 : idx == 2 ? rThumb2 : rThumb3,
                    "Index" => idx == 1 ? rIndex1 : idx == 2 ? rIndex2 : rIndex3,
                    "Middle" => idx == 1 ? rMiddle1 : idx == 2 ? rMiddle2 : rMiddle3,
                    "Ring" => idx == 1 ? rRing1 : idx == 2 ? rRing2 : rRing3,
                    "Little" => idx == 1 ? rLittle1 : idx == 2 ? rLittle2 : rLittle3,
                    _ => null
                };
            }
        }

        // ---------------------------
        // Calibration build (AUTO, LOCAL)
        // ---------------------------
        void BuildAutoAxisCalibrationLocal()
        {
            C.Clear();

            AddAutoLocal("Hips", tHips, tSpine);
            AddAutoLocal("Spine", tSpine, tChest ? tChest : tNeck);
            AddAutoLocal("Chest", tChest, tUpperChest ? tUpperChest : tNeck);
            AddAutoLocal("UpperChest", tUpperChest, tNeck);
            AddAutoLocal("Neck", tNeck, tHead);
            AddAutoLocal("Head", tHead, null);

            AddAutoLocal("LUpperArm", tLUpperArm, tLLowerArm);
            AddAutoLocal("LLowerArm", tLLowerArm, tLHand);
            AddAutoLocal("RUpperArm", tRUpperArm, tRLowerArm);
            AddAutoLocal("RLowerArm", tRLowerArm, tRHand);

            AddAutoLocal("LUpperLeg", tLUpperLeg, tLLowerLeg);
            AddAutoLocal("LLowerLeg", tLLowerLeg, tLFoot);
            AddAutoLocal("RUpperLeg", tRUpperLeg, tRLowerLeg);
            AddAutoLocal("RLowerLeg", tRLowerLeg, tRFoot);

            AddAutoLocal("LHand", tLHand, lMiddle1);
            AddAutoLocal("RHand", tRHand, rMiddle1);

            AddAutoLocal("LThumb1", lThumb1, lThumb2);
            AddAutoLocal("LThumb2", lThumb2, lThumb3);
            AddAutoLocal("LThumb3", lThumb3, null);

            AddAutoLocal("LIndex1", lIndex1, lIndex2);
            AddAutoLocal("LIndex2", lIndex2, lIndex3);
            AddAutoLocal("LIndex3", lIndex3, null);

            AddAutoLocal("LMiddle1", lMiddle1, lMiddle2);
            AddAutoLocal("LMiddle2", lMiddle2, lMiddle3);
            AddAutoLocal("LMiddle3", lMiddle3, null);

            AddAutoLocal("LRing1", lRing1, lRing2);
            AddAutoLocal("LRing2", lRing2, lRing3);
            AddAutoLocal("LRing3", lRing3, null);

            AddAutoLocal("LLittle1", lLittle1, lLittle2);
            AddAutoLocal("LLittle2", lLittle2, lLittle3);
            AddAutoLocal("LLittle3", lLittle3, null);

            AddAutoLocal("RThumb1", rThumb1, rThumb2);
            AddAutoLocal("RThumb2", rThumb2, rThumb3);
            AddAutoLocal("RThumb3", rThumb3, null);

            AddAutoLocal("RIndex1", rIndex1, rIndex2);
            AddAutoLocal("RIndex2", rIndex2, rIndex3);
            AddAutoLocal("RIndex3", rIndex3, null);

            AddAutoLocal("RMiddle1", rMiddle1, rMiddle2);
            AddAutoLocal("RMiddle2", rMiddle2, rMiddle3);
            AddAutoLocal("RMiddle3", rMiddle3, null);

            AddAutoLocal("RRing1", rRing1, rRing2);
            AddAutoLocal("RRing2", rRing2, rRing3);
            AddAutoLocal("RRing3", rRing3, null);

            AddAutoLocal("RLittle1", rLittle1, rLittle2);
            AddAutoLocal("RLittle2", rLittle2, rLittle3);
            AddAutoLocal("RLittle3", rLittle3, null);

            Debug.Log($"[AutoAxisLocal] Calibration built. count={C.Count}");
        }

        void AddAutoLocal(string key, Transform bone, Transform child)
        {
            var bc = new BoneCalib
            {
                bone = bone,
                child = child,
                baseLocalRot = bone ? bone.localRotation : Quaternion.identity,
                primaryAxisLocal = Vector3.forward,
                restAxisInParent = Vector3.forward,
                valid = false
            };

            if (!bone)
            {
                C[key] = bc;
                return;
            }

            if (child)
            {
                Vector3 dirWorld = child.position - bone.position;
                if (dirWorld.sqrMagnitude > 1e-8f)
                {
                    Vector3 dirLocal = bone.InverseTransformDirection(dirWorld.normalized);

                    float best = -999f;
                    Vector3 bestAxis = Vector3.forward;
                    foreach (var a in AxisCandidates)
                    {
                        float d = Vector3.Dot(dirLocal, a);
                        if (d > best) { best = d; bestAxis = a; }
                    }

                    bc.primaryAxisLocal = bestAxis;

                    // 레스트 로컬회전이 로컬축을 부모공간으로 보냄
                    bc.restAxisInParent = (bc.baseLocalRot * bestAxis).normalized;

                    bc.valid = true;
                }
            }
            else
            {
                bc.primaryAxisLocal = Vector3.forward;
                bc.restAxisInParent = (bc.baseLocalRot * Vector3.forward).normalized;
                bc.valid = true;
            }

            C[key] = bc;
        }

        // ---------------------------
        // Bone caching
        // ---------------------------
        void CacheBones()
        {
            // torso
            tHips = animator.GetBoneTransform(HumanBodyBones.Hips);
            tSpine = animator.GetBoneTransform(HumanBodyBones.Spine);
            tChest = animator.GetBoneTransform(HumanBodyBones.Chest);
            tUpperChest = animator.GetBoneTransform(HumanBodyBones.UpperChest);
            tNeck = animator.GetBoneTransform(HumanBodyBones.Neck);
            tHead = animator.GetBoneTransform(HumanBodyBones.Head);

            // arms
            tLUpperArm = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            tLLowerArm = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            tLHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);

            tRUpperArm = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            tRLowerArm = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
            tRHand = animator.GetBoneTransform(HumanBodyBones.RightHand);

            // legs
            tLUpperLeg = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            tLLowerLeg = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            tLFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);

            tRUpperLeg = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            tRLowerLeg = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
            tRFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);

            // left fingers
            lThumb1 = animator.GetBoneTransform(HumanBodyBones.LeftThumbProximal);
            lThumb2 = animator.GetBoneTransform(HumanBodyBones.LeftThumbIntermediate);
            lThumb3 = animator.GetBoneTransform(HumanBodyBones.LeftThumbDistal);

            lIndex1 = animator.GetBoneTransform(HumanBodyBones.LeftIndexProximal);
            lIndex2 = animator.GetBoneTransform(HumanBodyBones.LeftIndexIntermediate);
            lIndex3 = animator.GetBoneTransform(HumanBodyBones.LeftIndexDistal);

            lMiddle1 = animator.GetBoneTransform(HumanBodyBones.LeftMiddleProximal);
            lMiddle2 = animator.GetBoneTransform(HumanBodyBones.LeftMiddleIntermediate);
            lMiddle3 = animator.GetBoneTransform(HumanBodyBones.LeftMiddleDistal);

            lRing1 = animator.GetBoneTransform(HumanBodyBones.LeftRingProximal);
            lRing2 = animator.GetBoneTransform(HumanBodyBones.LeftRingIntermediate);
            lRing3 = animator.GetBoneTransform(HumanBodyBones.LeftRingDistal);

            lLittle1 = animator.GetBoneTransform(HumanBodyBones.LeftLittleProximal);
            lLittle2 = animator.GetBoneTransform(HumanBodyBones.LeftLittleIntermediate);
            lLittle3 = animator.GetBoneTransform(HumanBodyBones.LeftLittleDistal);

            // right fingers
            rThumb1 = animator.GetBoneTransform(HumanBodyBones.RightThumbProximal);
            rThumb2 = animator.GetBoneTransform(HumanBodyBones.RightThumbIntermediate);
            rThumb3 = animator.GetBoneTransform(HumanBodyBones.RightThumbDistal);

            rIndex1 = animator.GetBoneTransform(HumanBodyBones.RightIndexProximal);
            rIndex2 = animator.GetBoneTransform(HumanBodyBones.RightIndexIntermediate);
            rIndex3 = animator.GetBoneTransform(HumanBodyBones.RightIndexDistal);

            rMiddle1 = animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
            rMiddle2 = animator.GetBoneTransform(HumanBodyBones.RightMiddleIntermediate);
            rMiddle3 = animator.GetBoneTransform(HumanBodyBones.RightMiddleDistal);

            rRing1 = animator.GetBoneTransform(HumanBodyBones.RightRingProximal);
            rRing2 = animator.GetBoneTransform(HumanBodyBones.RightRingIntermediate);
            rRing3 = animator.GetBoneTransform(HumanBodyBones.RightRingDistal);

            rLittle1 = animator.GetBoneTransform(HumanBodyBones.RightLittleProximal);
            rLittle2 = animator.GetBoneTransform(HumanBodyBones.RightLittleIntermediate);
            rLittle3 = animator.GetBoneTransform(HumanBodyBones.RightLittleDistal);
        }

        // ---------------------------
        // Math helpers
        // ---------------------------
        Vector3 SafeDir(Vector3 v, Vector3 fallback)
        {
            if (v.sqrMagnitude < 1e-8f) return fallback.normalized;
            return v.normalized;
        }

        Vector3 GetVec3(List<float> list, int idx)
        {
            int b = idx * 4;
            float x = list[b + 0];
            float y = list[b + 1];
            float z = list[b + 2];

            if (flipX) x = -x;   // ✅ mirror fix
            if (flipY) y = -y;
            if (flipZ) z = -z;

            return new Vector3(x, y, z);
        }
    }
}
