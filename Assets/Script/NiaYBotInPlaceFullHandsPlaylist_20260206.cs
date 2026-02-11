using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NIA_YBOT_INPLACE_FULLHANDS_PLAYLIST_20260206
{
    public class NiaYBotInPlaceFullHandsPlaylist_20260206 : MonoBehaviour
    {
        // ---------------------------
        // ✅ JSON DTO (중첩) - Unity 오해 방지
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
        [Tooltip("예: StreamingAssets/SignJson/1,2,3,4 ...")]
        public string rootFolderInStreamingAssets = "SignJson";
        public List<string> sequenceFolders = new List<string> { "1", "2", "3", "4" };
        public string filePattern = "*_keypoints.json";

        [Header("Playback")]
        public float fps = 30f;
        [Tooltip("동작 끝에서 포즈 유지(초)")]
        public float endHoldSeconds = 0.5f;
        public bool autoNextSequence = true;
        public bool loopPlaylist = true;
        public bool loopSequence = true;
        public bool enableHotkeys = true;

        // ---------------------------
        // Keypoint coordinate tweak
        // ---------------------------
        [Header("Keypoint Conversion")]
        [Tooltip("키포인트 스케일(회전만 쓰지만, 방향 벡터 계산 안정용)")]
        public float scale = 0.01f;
        public bool flipY = true;
        public bool flipZ = true;

        [Header("Smoothing")]
        [Tooltip("0이면 즉시 적용, 0.1~0.3 정도 추천")]
        [Range(0f, 0.5f)]
        public float rotationSmoothing = 0.15f;

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
        private EndActionType pendingActionType = EndActionType.None;
        private enum EndActionType { None, NextSequence, LoopSequence, StopAtEnd, StopPlaylistEnd }

        // ---------------------------
        // Skeleton mapping (BODY25 + HAND21+21)
        // ---------------------------
        private const int PoseCount = 25;
        private const int HandCount = 21;

        // BODY_25 indices (너가 쓰던 스틱맨 기준)
        private const int Nose = 0;
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
        // Humanoid bones we will drive
        // ---------------------------
        [Header("Avatar")]
        [Tooltip("비워두면 자동으로 같은 오브젝트에서 Animator 찾음")]
        public Animator animator;

        [Tooltip("In-place 회전 기준(보통 캐릭터 루트). 비워두면 animator.transform 사용")]
        public Transform avatarRoot;

        private Transform tHips, tSpine, tChest, tUpperChest, tNeck, tHead;

        private Transform tLUpperArm, tLLowerArm, tLHand;
        private Transform tRUpperArm, tRLowerArm, tRHand;

        private Transform tLUpperLeg, tLLowerLeg, tLFoot;
        private Transform tRUpperLeg, tRLowerLeg, tRFoot;

        // Fingers (Humanoid)
        private Transform lThumb1, lThumb2, lThumb3;
        private Transform lIndex1, lIndex2, lIndex3;
        private Transform lMiddle1, lMiddle2, lMiddle3;
        private Transform lRing1, lRing2, lRing3;
        private Transform lLittle1, lLittle2, lLittle3;

        private Transform rThumb1, rThumb2, rThumb3;
        private Transform rIndex1, rIndex2, rIndex3;
        private Transform rMiddle1, rMiddle2, rMiddle3;
        private Transform rRing1, rRing2, rRing3;
        private Transform rLittle1, rLittle2, rLittle3;

        // ---------------------------
        // Calibration (T-pose baseline)
        // ---------------------------
        private struct BoneCalib
        {
            public Transform bone;
            public Transform child;
            public Quaternion baseRot;
            public Vector3 baseDirWorld; // bone -> child in world at calibration
            public bool valid;
        }

        private readonly Dictionary<string, BoneCalib> calib = new();

        void Start()
        {
            if (!animator) animator = GetComponentInChildren<Animator>();

            if (!animator)
            {
                Debug.LogError("[YBotInPlace] Animator not found. Attach this script to the Y Bot character root (with Animator).");
                enabled = false;
                return;
            }

            // ✅ Avatar null 방지: 런타임에 가장 흔한 원인
            if (animator.avatar == null)
            {
                // 1) 같은 오브젝트/자식에서 Avatar를 가진 Animator를 찾는 시도 (혹시 스크립트를 다른 곳에 붙였을 때 대비)
                var anims = GetComponentsInChildren<Animator>(true);
                foreach (var a in anims)
                {
                    if (a != null && a.avatar != null)
                    {
                        animator = a;
                        break;
                    }
                }

                // 그래도 없으면 종료
                if (animator.avatar == null)
                {
                    Debug.LogError(
                        "[YBotInPlace] Animator.avatar is NULL.\n" +
                        "Fix: Select the Y Bot in Scene -> Animator component -> set Avatar to 'Y Bot Avatar'.\n" +
                        "Also confirm FBX Rig is Humanoid and applied."
                    );
                    enabled = false;
                    return;
                }
            }

            // Humanoid 아니면 GetBoneTransform이 제대로 안 나올 수 있음
            if (!animator.isHuman)
            {
                Debug.LogError("[YBotInPlace] Animator is not Humanoid (isHuman=false). Set FBX Rig to Humanoid and Apply.");
                enabled = false;
                return;
            }

            if (!avatarRoot) avatarRoot = animator.transform;

            CacheBones();
            BuildCalibration();

            LoadAllSequences();
            if (sequences.Count == 0)
            {
                Debug.LogError("[YBotInPlace] No sequences loaded. Check StreamingAssets/SignJson/1,2,3,4 folders.");
                enabled = false;
                return;
            }

            Debug.Log($"[YBotInPlace] Ready. sequences={sequences.Count}, current={loadedLabels[seqIndex]}");
        }


        void Update()
        {
            if (sequences == null || sequences.Count == 0) return;

            if (enableHotkeys) HandleHotkeys();

            // hold time (포즈 유지)
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
                Debug.LogError($"[YBotInPlace] Root folder not found: {root}");
                return;
            }

            foreach (var seqName in sequenceFolders)
            {
                string folder = Path.Combine(root, seqName);
                if (!Directory.Exists(folder))
                {
                    Debug.LogWarning($"[YBotInPlace] Sequence folder not found: {folder}");
                    continue;
                }

                var files = Directory.GetFiles(folder, filePattern);
                Array.Sort(files);

                if (files.Length == 0)
                {
                    Debug.LogWarning($"[YBotInPlace] No files in: {folder} pattern={filePattern}");
                    continue;
                }

                sequences.Add(files);
                loadedLabels.Add(seqName);
                Debug.Log($"[YBotInPlace] Loaded '{seqName}' frames={files.Length}");
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
            var cur = sequences[seqIndex];
            if (cur == null || cur.Length == 0) return;

            ApplyFrameToAvatar(cur[frameIndex]);

            frameIndex++;

            if (frameIndex >= cur.Length)
            {
                frameIndex = Mathf.Clamp(cur.Length - 1, 0, cur.Length - 1);

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
                    // 마지막 포즈에서 유지 (업데이트는 계속되지만 frame 진행은 없음)
                    break;
                case EndActionType.StopPlaylistEnd:
                    Debug.Log("[YBotInPlace] Playlist finished. Stop.");
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

            Debug.Log($"[YBotInPlace] Switched -> {loadedLabels[seqIndex]}");
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

            Debug.Log($"[YBotInPlace] Manual switch -> {loadedLabels[seqIndex]}");
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

        // ---------------------------
        // Core: JSON -> Avatar rotations (in-place)
        // ---------------------------
        void ApplyFrameToAvatar(string path)
        {
            string json = File.ReadAllText(path);
            var root = JsonUtility.FromJson<Root>(json);
            if (root?.people == null) return;

            var pose = root.people.pose_keypoints_3d;
            if (pose == null || pose.Count < PoseCount * 4) return;

            // 키포인트를 "MidHip 기준 로컬(translation 제거)"로 만들면 in-place 회전에 안정적
            Vector3[] P = new Vector3[PoseCount];
            for (int i = 0; i < PoseCount; i++)
                P[i] = GetVec3(pose, i) * scale;

            Vector3 center = P[MidHip];
            for (int i = 0; i < PoseCount; i++)
                P[i] = P[i] - center;

            // 손
            Vector3[] LH = null;
            Vector3[] RH = null;

            var lh = root.people.hand_left_keypoints_3d;
            if (lh != null && lh.Count >= HandCount * 4)
            {
                LH = new Vector3[HandCount];
                for (int i = 0; i < HandCount; i++)
                    LH[i] = GetVec3(lh, i) * scale;
                // 손도 translation 제거(손목 기준)
                Vector3 w = LH[0];
                for (int i = 0; i < HandCount; i++) LH[i] -= w;
            }

            var rh = root.people.hand_right_keypoints_3d;
            if (rh != null && rh.Count >= HandCount * 4)
            {
                RH = new Vector3[HandCount];
                for (int i = 0; i < HandCount; i++)
                    RH[i] = GetVec3(rh, i) * scale;
                Vector3 w = RH[0];
                for (int i = 0; i < HandCount; i++) RH[i] -= w;
            }

            // --------------------
            // 몸통 방향(앞/위) 추정
            // up = (Neck - MidHip)
            // right = (RShoulder - LShoulder) or (RHip - LHip)
            // forward = cross(right, up)
            // --------------------
            Vector3 upTorso = SafeDir(P[Neck] - P[MidHip], Vector3.up);
            Vector3 rightShoulder = SafeDir(P[RShoulder] - P[LShoulder], Vector3.right);
            Vector3 fwdTorso = Vector3.Cross(rightShoulder, upTorso);
            if (fwdTorso.sqrMagnitude < 1e-6f) fwdTorso = Vector3.forward;
            fwdTorso.Normalize();

            // 이 “데이터 공간” 방향들을 아바타 월드 방향으로 변환
            Vector3 UpW = avatarRoot.TransformDirection(upTorso);
            Vector3 FwdW = avatarRoot.TransformDirection(fwdTorso);

            // --------------------
            // Hips/Spine/Chest/Neck/Head
            // --------------------
            ApplyLook("Hips", tHips, UpW, FwdW);
            ApplyLook("Spine", tSpine, UpW, FwdW);
            ApplyLook("Chest", tChest, UpW, FwdW);
            ApplyLook("UpperChest", tUpperChest, UpW, FwdW);
            ApplyLook("Neck", tNeck, UpW, FwdW);
            ApplyLook("Head", tHead, UpW, FwdW);

            // --------------------
            // Arms (UpperArm -> LowerArm -> Hand)
            // 방향은 (Shoulder->Elbow), (Elbow->Wrist)
            // --------------------
            ApplyBoneDir("LUpperArm", tLUpperArm, avatarRoot.TransformDirection(SafeDir(P[LElbow] - P[LShoulder], Vector3.down)));
            ApplyBoneDir("LLowerArm", tLLowerArm, avatarRoot.TransformDirection(SafeDir(P[LWrist] - P[LElbow], Vector3.down)));

            ApplyBoneDir("RUpperArm", tRUpperArm, avatarRoot.TransformDirection(SafeDir(P[RElbow] - P[RShoulder], Vector3.down)));
            ApplyBoneDir("RLowerArm", tRLowerArm, avatarRoot.TransformDirection(SafeDir(P[RWrist] - P[RElbow], Vector3.down)));

            // 손목 회전(대충): 손(0)→중지기저(9) 방향을 사용
            if (LH != null)
                ApplyBoneDir("LHand", tLHand, avatarRoot.TransformDirection(SafeDir(LH[9] - LH[0], Vector3.forward)));
            if (RH != null)
                ApplyBoneDir("RHand", tRHand, avatarRoot.TransformDirection(SafeDir(RH[9] - RH[0], Vector3.forward)));

            // --------------------
            // Legs (UpperLeg -> LowerLeg -> Foot)
            // 방향은 (Hip->Knee), (Knee->Ankle)
            // --------------------
            ApplyBoneDir("LUpperLeg", tLUpperLeg, avatarRoot.TransformDirection(SafeDir(P[LKnee] - P[LHip], Vector3.down)));
            ApplyBoneDir("LLowerLeg", tLLowerLeg, avatarRoot.TransformDirection(SafeDir(P[LAnkle] - P[LKnee], Vector3.down)));

            ApplyBoneDir("RUpperLeg", tRUpperLeg, avatarRoot.TransformDirection(SafeDir(P[RKnee] - P[RHip], Vector3.down)));
            ApplyBoneDir("RLowerLeg", tRLowerLeg, avatarRoot.TransformDirection(SafeDir(P[RAnkle] - P[RKnee], Vector3.down)));

            // 발은 ankle 기준이 없어서(발끝 데이터가 있긴 하지만 BODY25는 발 관절이 별도)
            // 여기서는 LowerLeg까지로도 충분히 "제자리 동작" 느낌이 남.
            // 필요하면 BODY25의 발가락(19~24)을 이용해 Foot 방향도 추가 가능.

            // --------------------
            // Fingers (if hand data exists)
            // 각 손 21 인덱스 기준:
            // thumb: 1-4, index: 5-8, middle: 9-12, ring: 13-16, little: 17-20
            // --------------------
            if (LH != null) ApplyFingers(isLeft: true, H: LH);
            if (RH != null) ApplyFingers(isLeft: false, H: RH);
        }

        // ---------------------------
        // Apply helpers
        // ---------------------------
        void ApplyLook(string key, Transform bone, Vector3 upW, Vector3 fwdW)
        {
            if (!bone) return;

            // T-pose에서의 base rotation을 유지하면서, 월드에서 (forward, up) 방향을 맞춰줌
            Quaternion target = Quaternion.LookRotation(fwdW, upW);

            // baseDir 기반 회전 대신, torso류는 LookRotation이 더 안정적
            // 하지만 "기본 포즈 편차"가 있으면 baseRot로 보정
            if (calib.TryGetValue(key, out var c) && c.valid)
            {
                // baseRot -> target을 섞어서 적용(큰 틀은 target, 기본은 baseRot)
                target = target * Quaternion.Inverse(Quaternion.LookRotation(c.baseDirWorld, upW)) * c.baseRot;
            }

            bone.rotation = Smooth(bone.rotation, target);
        }

        void ApplyBoneDir(string key, Transform bone, Vector3 targetDirWorld)
        {
            if (!bone) return;

            if (!calib.TryGetValue(key, out var c) || !c.valid) return;
            if (targetDirWorld.sqrMagnitude < 1e-6f) return;
            targetDirWorld.Normalize();

            // baseDirWorld(캘리브레이션 때 bone->child 방향)을 targetDirWorld로 돌리는 회전
            Quaternion delta = Quaternion.FromToRotation(c.baseDirWorld, targetDirWorld);
            Quaternion target = delta * c.baseRot;

            bone.rotation = Smooth(bone.rotation, target);
        }

        void ApplyFingers(bool isLeft, Vector3[] H)
        {
            // thumb
            ApplyFingerBone(isLeft ? "LThumb1" : "RThumb1", isLeft ? lThumb1 : rThumb1, H, 1, 2);
            ApplyFingerBone(isLeft ? "LThumb2" : "RThumb2", isLeft ? lThumb2 : rThumb2, H, 2, 3);
            ApplyFingerBone(isLeft ? "LThumb3" : "RThumb3", isLeft ? lThumb3 : rThumb3, H, 3, 4);

            // index
            ApplyFingerBone(isLeft ? "LIndex1" : "RIndex1", isLeft ? lIndex1 : rIndex1, H, 5, 6);
            ApplyFingerBone(isLeft ? "LIndex2" : "RIndex2", isLeft ? lIndex2 : rIndex2, H, 6, 7);
            ApplyFingerBone(isLeft ? "LIndex3" : "RIndex3", isLeft ? lIndex3 : rIndex3, H, 7, 8);

            // middle
            ApplyFingerBone(isLeft ? "LMiddle1" : "RMiddle1", isLeft ? lMiddle1 : rMiddle1, H, 9, 10);
            ApplyFingerBone(isLeft ? "LMiddle2" : "RMiddle2", isLeft ? lMiddle2 : rMiddle2, H, 10, 11);
            ApplyFingerBone(isLeft ? "LMiddle3" : "RMiddle3", isLeft ? lMiddle3 : rMiddle3, H, 11, 12);

            // ring
            ApplyFingerBone(isLeft ? "LRing1" : "RRing1", isLeft ? lRing1 : rRing1, H, 13, 14);
            ApplyFingerBone(isLeft ? "LRing2" : "RRing2", isLeft ? lRing2 : rRing2, H, 14, 15);
            ApplyFingerBone(isLeft ? "LRing3" : "RRing3", isLeft ? lRing3 : rRing3, H, 15, 16);

            // little
            ApplyFingerBone(isLeft ? "LLittle1" : "RLittle1", isLeft ? lLittle1 : rLittle1, H, 17, 18);
            ApplyFingerBone(isLeft ? "LLittle2" : "RLittle2", isLeft ? lLittle2 : rLittle2, H, 18, 19);
            ApplyFingerBone(isLeft ? "LLittle3" : "RLittle3", isLeft ? lLittle3 : rLittle3, H, 19, 20);
        }

        void ApplyFingerBone(string key, Transform bone, Vector3[] H, int a, int b)
        {
            if (!bone) return;
            Vector3 dirLocal = H[b] - H[a];
            Vector3 dirWorld = avatarRoot.TransformDirection(SafeDir(dirLocal, Vector3.forward));
            ApplyBoneDir(key, bone, dirWorld);
        }

        Quaternion Smooth(Quaternion current, Quaternion target)
        {
            if (rotationSmoothing <= 0f) return target;

            // 프레임레이트에 덜 민감하게: exp smoothing
            float t = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-4f, rotationSmoothing));
            return Quaternion.Slerp(current, target, t);
        }

        // ---------------------------
        // Bone caching / calibration
        // ---------------------------
        void CacheBones()
        {
            // body
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

            // fingers left
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

            // fingers right
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

        void BuildCalibration()
        {
            calib.Clear();

            // 팔/다리/손가락은 "bone->child"가 있는 경우가 대부분이라 FromToRotation 캘리브가 안정적
            AddCalib("LUpperArm", tLUpperArm, tLLowerArm);
            AddCalib("LLowerArm", tLLowerArm, tLHand);
            AddCalib("RUpperArm", tRUpperArm, tRLowerArm);
            AddCalib("RLowerArm", tRLowerArm, tRHand);

            AddCalib("LUpperLeg", tLUpperLeg, tLLowerLeg);
            AddCalib("LLowerLeg", tLLowerLeg, tLFoot);
            AddCalib("RUpperLeg", tRUpperLeg, tRLowerLeg);
            AddCalib("RLowerLeg", tRLowerLeg, tRFoot);

            AddCalib("LHand", tLHand, lMiddle1 ? lMiddle1 : null);
            AddCalib("RHand", tRHand, rMiddle1 ? rMiddle1 : null);

            // fingers left
            AddCalib("LThumb1", lThumb1, lThumb2);
            AddCalib("LThumb2", lThumb2, lThumb3);
            AddCalib("LThumb3", lThumb3, null);

            AddCalib("LIndex1", lIndex1, lIndex2);
            AddCalib("LIndex2", lIndex2, lIndex3);
            AddCalib("LIndex3", lIndex3, null);

            AddCalib("LMiddle1", lMiddle1, lMiddle2);
            AddCalib("LMiddle2", lMiddle2, lMiddle3);
            AddCalib("LMiddle3", lMiddle3, null);

            AddCalib("LRing1", lRing1, lRing2);
            AddCalib("LRing2", lRing2, lRing3);
            AddCalib("LRing3", lRing3, null);

            AddCalib("LLittle1", lLittle1, lLittle2);
            AddCalib("LLittle2", lLittle2, lLittle3);
            AddCalib("LLittle3", lLittle3, null);

            // fingers right
            AddCalib("RThumb1", rThumb1, rThumb2);
            AddCalib("RThumb2", rThumb2, rThumb3);
            AddCalib("RThumb3", rThumb3, null);

            AddCalib("RIndex1", rIndex1, rIndex2);
            AddCalib("RIndex2", rIndex2, rIndex3);
            AddCalib("RIndex3", rIndex3, null);

            AddCalib("RMiddle1", rMiddle1, rMiddle2);
            AddCalib("RMiddle2", rMiddle2, rMiddle3);
            AddCalib("RMiddle3", rMiddle3, null);

            AddCalib("RRing1", rRing1, rRing2);
            AddCalib("RRing2", rRing2, rRing3);
            AddCalib("RRing3", rRing3, null);

            AddCalib("RLittle1", rLittle1, rLittle2);
            AddCalib("RLittle2", rLittle2, rLittle3);
            AddCalib("RLittle3", rLittle3, null);

            // torso류는 LookRotation 기반이라 baseDir만 있으면 좋긴 한데,
            // 여기서는 optional로 hips 방향(hips->spine)을 저장해두자.
            AddCalib("Hips", tHips, tSpine);
            AddCalib("Spine", tSpine, tChest ? tChest : tNeck);
            AddCalib("Chest", tChest, tUpperChest ? tUpperChest : tNeck);
            AddCalib("UpperChest", tUpperChest, tNeck);
            AddCalib("Neck", tNeck, tHead);
            AddCalib("Head", tHead, null);
        }

        void AddCalib(string key, Transform bone, Transform child)
        {
            BoneCalib c = new BoneCalib
            {
                bone = bone,
                child = child,
                baseRot = bone ? bone.rotation : Quaternion.identity,
                baseDirWorld = Vector3.forward,
                valid = false
            };

            if (bone && child)
            {
                Vector3 d = (child.position - bone.position);
                if (d.sqrMagnitude > 1e-8f)
                {
                    c.baseDirWorld = d.normalized;
                    c.valid = true;
                }
            }
            else if (bone)
            {
                // child가 없으면 "현재 forward"를 기준 방향으로 잡아둠(손끝/머리끝 등)
                c.baseDirWorld = bone.forward;
                c.valid = true;
            }

            calib[key] = c;
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

            if (flipY) y = -y;
            if (flipZ) z = -z;

            return new Vector3(x, y, z);
        }
    }
}
