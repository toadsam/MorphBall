using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NIA_YBOT_AUTOAXIS_INPLACE_FULLHANDS_PLAYLIST_20260206
{
    public class NiaYBotAutoAxisInPlaceFullHandsPlaylist_20260206 : MonoBehaviour
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
        public bool flipY = true;
        public bool flipZ = true;

        [Header("Fix")]
        [Tooltip("켜면 좌/우를 통째로 스왑해서 적용 (왼손 움직일 때 오른손이 움직이는 현상 해결용)")]
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
        // Auto Axis Calibration
        // ---------------------------
        private struct BoneCalib
        {
            public Transform bone;
            public Transform child;

            public Quaternion baseWorldRot;
            public Vector3 primaryAxisLocal;
            public Vector3 primaryAxisWorldRest;

            public bool valid;
        }

        private readonly Dictionary<string, BoneCalib> C = new();

        private static readonly Vector3[] AxisCandidates =
        {
            Vector3.right, Vector3.left,
            Vector3.up, Vector3.down,
            Vector3.forward, Vector3.back
        };

        void Start()
        {
            if (!animator) animator = GetComponentInChildren<Animator>();
            if (!animator)
            {
                Debug.LogError("[AutoAxis] Animator not found. Attach script to the character root (with Animator).");
                enabled = false;
                return;
            }

            if (animator.avatar == null)
            {
                Debug.LogError("[AutoAxis] Animator.avatar is NULL. Set Avatar on Animator (Humanoid).");
                enabled = false;
                return;
            }

            if (!animator.isHuman)
            {
                Debug.LogError("[AutoAxis] Animator is not Humanoid (isHuman=false). Set FBX Rig to Humanoid and Apply.");
                enabled = false;
                return;
            }

            if (!avatarRoot) avatarRoot = animator.transform;

            CacheBones();
            BuildAutoAxisCalibration();

            LoadAllSequences();
            if (sequences.Count == 0)
            {
                Debug.LogError("[AutoAxis] No sequences loaded. Check StreamingAssets/SignJson/1,2,3,4 structure.");
                enabled = false;
                return;
            }

            Debug.Log($"[AutoAxis] Ready. sequences={sequences.Count}, current={loadedLabels[seqIndex]}, swapLR={swapLeftRight}");
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
                Debug.LogError($"[AutoAxis] Root folder not found: {root}");
                return;
            }

            foreach (var seqName in sequenceFolders)
            {
                string folder = Path.Combine(root, seqName);
                if (!Directory.Exists(folder))
                {
                    Debug.LogWarning($"[AutoAxis] Sequence folder not found: {folder}");
                    continue;
                }

                var files = Directory.GetFiles(folder, filePattern);
                Array.Sort(files);

                if (files.Length == 0)
                {
                    Debug.LogWarning($"[AutoAxis] No files in: {folder} pattern={filePattern}");
                    continue;
                }

                sequences.Add(files);
                loadedLabels.Add(seqName);
                Debug.Log($"[AutoAxis] Loaded '{seqName}' frames={files.Length}");
            }

            seqIndex = Mathf.Clamp(seqIndex, 0, sequences.Count - 1);
            frameIndex = 0;
            acc = 0f;

            holdTimer = 0f;
            pendingEndAction = false;
            pendingEndActionType = EndActionType.None;
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
                    Debug.Log("[AutoAxis] Playlist finished. Stop.");
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

            Debug.Log($"[AutoAxis] Switched -> {loadedLabels[seqIndex]}");
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

            Debug.Log($"[AutoAxis] Manual switch -> {loadedLabels[seqIndex]}");
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

            // toggle swap
            if (Input.GetKeyDown(KeyCode.T))
            {
                swapLeftRight = !swapLeftRight;
                Debug.Log($"[AutoAxis] swapLeftRight toggled -> {swapLeftRight}");
            }

            // rebuild calib
            if (Input.GetKeyDown(KeyCode.R))
            {
                BuildAutoAxisCalibration();
                Debug.Log("[AutoAxis] Rebuilt calibration (R).");
            }
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

            // pose points (translation 제거: MidHip 기준)
            Vector3[] P = new Vector3[PoseCount];
            for (int i = 0; i < PoseCount; i++)
                P[i] = GetVec3(pose, i) * scale;

            Vector3 center = P[MidHip];
            for (int i = 0; i < PoseCount; i++)
                P[i] = P[i] - center;

            // hands (optional) - 손목 기준 translation 제거
            Vector3[] LH = null;
            Vector3[] RH = null;

            var lh = root.people.hand_left_keypoints_3d;
            if (lh != null && lh.Count >= HandCount * 4)
            {
                LH = new Vector3[HandCount];
                for (int i = 0; i < HandCount; i++)
                    LH[i] = GetVec3(lh, i) * scale;
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

            // ✅ swapLeftRight가 켜져 있으면 손 데이터도 통째로 교환
            if (swapLeftRight)
            {
                var tmp = LH;
                LH = RH;
                RH = tmp;
            }

            // --------------------
            // Torso forward/up
            // --------------------
            int RS = swapLeftRight ? LShoulder : RShoulder;
            int LS = swapLeftRight ? RShoulder : LShoulder;

            Vector3 upTorso = SafeDir(P[Neck] - P[MidHip], Vector3.up);
            Vector3 rightLine = SafeDir(P[RS] - P[LS], Vector3.right);

            Vector3 fwdTorso = Vector3.Cross(upTorso, rightLine);
            if (fwdTorso.sqrMagnitude < 1e-8f) fwdTorso = Vector3.forward;
            fwdTorso.Normalize();

            Vector3 UpW = avatarRoot.TransformDirection(upTorso);
            Vector3 FwdW = avatarRoot.TransformDirection(fwdTorso);

            ApplyLook(tHips, UpW, FwdW);
            ApplyLook(tSpine, UpW, FwdW);
            ApplyLook(tChest, UpW, FwdW);
            ApplyLook(tUpperChest, UpW, FwdW);
            ApplyLook(tNeck, UpW, FwdW);
            ApplyLook(tHead, UpW, FwdW);

            // --------------------
            // Arms / Legs with swapped indices
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

            // arms
            ApplyAutoAxisDir("LUpperArm", avatarRoot.TransformDirection(SafeDir(P[L_El] - P[L_Sh], Vector3.down)));
            ApplyAutoAxisDir("LLowerArm", avatarRoot.TransformDirection(SafeDir(P[L_Wr] - P[L_El], Vector3.down)));

            ApplyAutoAxisDir("RUpperArm", avatarRoot.TransformDirection(SafeDir(P[R_El] - P[R_Sh], Vector3.down)));
            ApplyAutoAxisDir("RLowerArm", avatarRoot.TransformDirection(SafeDir(P[R_Wr] - P[R_El], Vector3.down)));

            // legs
            ApplyAutoAxisDir("LUpperLeg", avatarRoot.TransformDirection(SafeDir(P[L_Kn] - P[L_Hi], Vector3.down)));
            ApplyAutoAxisDir("LLowerLeg", avatarRoot.TransformDirection(SafeDir(P[L_An] - P[L_Kn], Vector3.down)));

            ApplyAutoAxisDir("RUpperLeg", avatarRoot.TransformDirection(SafeDir(P[R_Kn] - P[R_Hi], Vector3.down)));
            ApplyAutoAxisDir("RLowerLeg", avatarRoot.TransformDirection(SafeDir(P[R_An] - P[R_Kn], Vector3.down)));

            // hands: palm plane 기반
            if (LH != null) ApplyHandAndFingers(isLeft: true, H: LH);
            if (RH != null) ApplyHandAndFingers(isLeft: false, H: RH);
        }

        // ---------------------------
        // AutoAxis apply helpers
        // ---------------------------
        void ApplyAutoAxisDir(string key, Vector3 targetDirWorld)
        {
            if (!C.TryGetValue(key, out var c) || !c.valid || c.bone == null) return;

            if (targetDirWorld.sqrMagnitude < 1e-8f) return;
            targetDirWorld.Normalize();

            Quaternion delta = Quaternion.FromToRotation(c.primaryAxisWorldRest, targetDirWorld);
            Quaternion targetRot = delta * c.baseWorldRot;

            c.bone.rotation = Smooth(c.bone.rotation, targetRot);
        }

        void ApplyLook(Transform bone, Vector3 upW, Vector3 fwdW)
        {
            if (!bone) return;
            Quaternion target = Quaternion.LookRotation(fwdW, upW);
            bone.rotation = Smooth(bone.rotation, target);
        }

        Quaternion Smooth(Quaternion current, Quaternion target)
        {
            if (rotationSmoothing <= 0f) return target;
            float t = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-4f, rotationSmoothing));
            return Quaternion.Slerp(current, target, t);
        }

        // 손 + 손가락
        void ApplyHandAndFingers(bool isLeft, Vector3[] H)
        {
            Vector3 v1 = SafeDir(H[5] - H[0], Vector3.right);
            Vector3 v2 = SafeDir(H[17] - H[0], Vector3.left);

            Vector3 palmNormal = Vector3.Cross(v1, v2);
            if (palmNormal.sqrMagnitude < 1e-8f) palmNormal = Vector3.up;
            palmNormal.Normalize();

            Vector3 palmForward = SafeDir(H[9] - H[0], Vector3.forward);

            Vector3 upW = avatarRoot.TransformDirection(palmNormal);
            Vector3 fwdW = avatarRoot.TransformDirection(palmForward);

            if (isLeft && tLHand) tLHand.rotation = Smooth(tLHand.rotation, Quaternion.LookRotation(fwdW, upW));
            if (!isLeft && tRHand) tRHand.rotation = Smooth(tRHand.rotation, Quaternion.LookRotation(fwdW, upW));

            ApplyFingerChain(isLeft, "Thumb", 1, 2, 3, 4);
            ApplyFingerChain(isLeft, "Index", 5, 6, 7, 8);
            ApplyFingerChain(isLeft, "Middle", 9, 10, 11, 12);
            ApplyFingerChain(isLeft, "Ring", 13, 14, 15, 16);
            ApplyFingerChain(isLeft, "Little", 17, 18, 19, 20);

            void ApplyFingerChain(bool left, string name, int a, int b, int c, int d)
            {
                ApplyFingerBone(left, $"{(left ? "L" : "R")}{name}1", GetFingerTransform(left, name, 1), H, a, b);
                ApplyFingerBone(left, $"{(left ? "L" : "R")}{name}2", GetFingerTransform(left, name, 2), H, b, c);
                ApplyFingerBone(left, $"{(left ? "L" : "R")}{name}3", GetFingerTransform(left, name, 3), H, c, d);
            }
        }

        void ApplyFingerBone(bool isLeft, string key, Transform bone, Vector3[] H, int a, int b)
        {
            if (!bone) return;
            if (!C.TryGetValue(key, out var c) || !c.valid) return;

            Vector3 dirLocal = SafeDir(H[b] - H[a], Vector3.forward);
            Vector3 dirWorld = avatarRoot.TransformDirection(dirLocal);

            Quaternion delta = Quaternion.FromToRotation(c.primaryAxisWorldRest, dirWorld);
            Quaternion targetRot = delta * c.baseWorldRot;

            bone.rotation = Smooth(bone.rotation, targetRot);
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
        // Calibration build (AUTO)
        // ---------------------------
        void BuildAutoAxisCalibration()
        {
            C.Clear();

            AddAuto("Hips", tHips, tSpine);
            AddAuto("Spine", tSpine, tChest ? tChest : tNeck);
            AddAuto("Chest", tChest, tUpperChest ? tUpperChest : tNeck);
            AddAuto("UpperChest", tUpperChest, tNeck);
            AddAuto("Neck", tNeck, tHead);
            AddAuto("Head", tHead, null);

            AddAuto("LUpperArm", tLUpperArm, tLLowerArm);
            AddAuto("LLowerArm", tLLowerArm, tLHand);
            AddAuto("RUpperArm", tRUpperArm, tRLowerArm);
            AddAuto("RLowerArm", tRLowerArm, tRHand);

            AddAuto("LUpperLeg", tLUpperLeg, tLLowerLeg);
            AddAuto("LLowerLeg", tLLowerLeg, tLFoot);
            AddAuto("RUpperLeg", tRUpperLeg, tRLowerLeg);
            AddAuto("RLowerLeg", tRLowerLeg, tRFoot);

            AddAuto("LHand", tLHand, lMiddle1);
            AddAuto("RHand", tRHand, rMiddle1);

            // left fingers
            AddAuto("LThumb1", lThumb1, lThumb2);
            AddAuto("LThumb2", lThumb2, lThumb3);
            AddAuto("LThumb3", lThumb3, null);

            AddAuto("LIndex1", lIndex1, lIndex2);
            AddAuto("LIndex2", lIndex2, lIndex3);
            AddAuto("LIndex3", lIndex3, null);

            AddAuto("LMiddle1", lMiddle1, lMiddle2);
            AddAuto("LMiddle2", lMiddle2, lMiddle3);
            AddAuto("LMiddle3", lMiddle3, null);

            AddAuto("LRing1", lRing1, lRing2);
            AddAuto("LRing2", lRing2, lRing3);
            AddAuto("LRing3", lRing3, null);

            AddAuto("LLittle1", lLittle1, lLittle2);
            AddAuto("LLittle2", lLittle2, lLittle3);
            AddAuto("LLittle3", lLittle3, null);

            // right fingers
            AddAuto("RThumb1", rThumb1, rThumb2);
            AddAuto("RThumb2", rThumb2, rThumb3);
            AddAuto("RThumb3", rThumb3, null);

            AddAuto("RIndex1", rIndex1, rIndex2);
            AddAuto("RIndex2", rIndex2, rIndex3);
            AddAuto("RIndex3", rIndex3, null);

            AddAuto("RMiddle1", rMiddle1, rMiddle2);
            AddAuto("RMiddle2", rMiddle2, rMiddle3);
            AddAuto("RMiddle3", rMiddle3, null);

            AddAuto("RRing1", rRing1, rRing2);
            AddAuto("RRing2", rRing2, rRing3);
            AddAuto("RRing3", rRing3, null);

            AddAuto("RLittle1", rLittle1, rLittle2);
            AddAuto("RLittle2", rLittle2, rLittle3);
            AddAuto("RLittle3", rLittle3, null);

            Debug.Log($"[AutoAxis] Calibration built. count={C.Count}");
        }

        void AddAuto(string key, Transform bone, Transform child)
        {
            var bc = new BoneCalib
            {
                bone = bone,
                child = child,
                baseWorldRot = bone ? bone.rotation : Quaternion.identity,
                primaryAxisLocal = Vector3.forward,
                primaryAxisWorldRest = Vector3.forward,
                valid = false
            };

            if (!bone) { C[key] = bc; return; }

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
                        if (d > best)
                        {
                            best = d;
                            bestAxis = a;
                        }
                    }

                    bc.primaryAxisLocal = bestAxis;
                    bc.primaryAxisWorldRest = bone.TransformDirection(bestAxis).normalized;
                    bc.valid = true;
                }
            }
            else
            {
                bc.primaryAxisLocal = Vector3.forward;
                bc.primaryAxisWorldRest = bone.forward.normalized;
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

            if (flipY) y = -y;
            if (flipZ) z = -z;

            return new Vector3(x, y, z);
        }
    }
}
