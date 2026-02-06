using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NIA_STICKMAN_FULLHANDS_PLAYLIST_20260206
{
    public class NiaStickmanFullHandsPlaylist_20260206 : MonoBehaviour
    {
        // ---------------------------
        // ✅ JSON DTO (중첩 클래스) : Unity가 컴포넌트로 오해하는 이슈 방지
        // ---------------------------
        [Serializable]
        private class Root
        {
            public People people;
        }

        [Serializable]
        private class People
        {
            public List<float> pose_keypoints_3d;
            public List<float> hand_left_keypoints_3d;
            public List<float> hand_right_keypoints_3d;
        }

        [Header("Source (StreamingAssets)")]
        [Tooltip("예: StreamingAssets/SignJson/1,2,3,4 ...")]
        public string rootFolderInStreamingAssets = "SignJson";

        [Tooltip("동작 폴더 이름들 (순서 재생). 예: 1,2,3,4")]
        public List<string> sequenceFolders = new List<string> { "1", "2", "3", "4" };

        [Tooltip("프레임 파일 패턴")]
        public string filePattern = "*_keypoints.json";

        [Header("Playback")]
        public float fps = 30f;

        [Tooltip("동작 끝에서 포즈 유지(초)")]
        public float endHoldSeconds = 0.5f;

        [Tooltip("각 동작(폴더) 끝나면 다음 동작으로 자동 이동")]
        public bool autoNextSequence = true;

        [Tooltip("마지막 동작까지 끝나면 다시 첫 동작으로")]
        public bool loopPlaylist = true;

        [Tooltip("autoNextSequence=false일 때 동작 내부 반복")]
        public bool loopSequence = true;

        [Tooltip("키보드 1~9로 동작 전환")]
        public bool enableHotkeys = true;

        [Header("Coordinate Conversion")]
        public float scale = 0.01f;
        public bool flipY = true;
        public bool flipZ = true;

        [Header("Visual")]
        public float jointSize = 0.03f;
        public float handJointSize = 0.02f;
        public Vector3 worldOffset = new Vector3(0, 1.0f, 0);

        [Header("Lines")]
        public bool drawBones = true;
        public float boneWidth = 0.01f;
        public float handBoneWidth = 0.006f;

        // ---------------------------
        // Internal Playback State
        // ---------------------------
        private List<string[]> sequences = new();      // 각 동작(폴더)별 프레임 파일 목록
        private List<string> loadedLabels = new();     // 실제 로드된 폴더명(없는 폴더 스킵 대비)

        private int seqIndex = 0;
        private int frameIndex = 0;
        private float acc = 0f;

        // ✅ end hold 상태
        private float holdTimer = 0f;
        private bool pendingEndAction = false;
        private EndActionType pendingActionType = EndActionType.None;

        private enum EndActionType { None, NextSequence, LoopSequence, StopAtEnd, StopPlaylistEnd }

        // ---------------------------
        // Skeleton Setup
        // ---------------------------
        private const int PoseCount = 25;
        private const int HandCount = 21;
        private const int TotalJoints = PoseCount + HandCount + HandCount; // 67

        private const int LeftHandStart = PoseCount;              // 25
        private const int RightHandStart = PoseCount + HandCount; // 46

        private GameObject[] joints;

        // BODY_25 연결(스틱맨)
        private readonly (int a, int b)[] poseBones = new (int, int)[]
        {
            (0,1), (1,8),
            (1,2), (2,3), (3,4),
            (1,5), (5,6), (6,7),
            (8,9), (9,10), (10,11),
            (8,12), (12,13), (13,14),
            (14,19), (19,20), (14,21),
            (11,22), (22,23), (11,24),
            (0,15), (0,16), (15,17), (16,18),
        };

        // Hand(21) 연결: MediaPipe Hands 인덱스 구조
        private readonly (int a, int b)[] handBonesLocal = new (int, int)[]
        {
            // thumb
            (0,1),(1,2),(2,3),(3,4),
            // index
            (0,5),(5,6),(6,7),(7,8),
            // middle
            (0,9),(9,10),(10,11),(11,12),
            // ring
            (0,13),(13,14),(14,15),(15,16),
            // pinky
            (0,17),(17,18),(18,19),(19,20),
            // palm cross
            (5,9),(9,13),(13,17)
        };

        private LineRenderer[] poseLines;
        private LineRenderer[] leftHandLines;
        private LineRenderer[] rightHandLines;

        void Start()
        {
            LoadAllSequences();

            if (sequences.Count == 0)
            {
                Debug.LogError("[FullHandsPlaylist] No sequences loaded. Check StreamingAssets folder structure.");
                enabled = false;
                return;
            }

            // 관절 생성
            joints = new GameObject[TotalJoints];

            for (int i = 0; i < PoseCount; i++)
                joints[i] = CreatePoseJoint(i);

            for (int i = 0; i < HandCount; i++)
                joints[LeftHandStart + i] = CreateHandJoint(isLeft: true, handLocalIdx: i);

            for (int i = 0; i < HandCount; i++)
                joints[RightHandStart + i] = CreateHandJoint(isLeft: false, handLocalIdx: i);

            // 라인 생성
            if (drawBones)
            {
                poseLines = CreateLines("PoseBone_", poseBones.Length, boneWidth);
                leftHandLines = CreateLines("LeftHandBone_", handBonesLocal.Length, handBoneWidth);
                rightHandLines = CreateLines("RightHandBone_", handBonesLocal.Length, handBoneWidth);
            }

            Debug.Log($"[FullHandsPlaylist] Ready. sequences={sequences.Count}, joints={TotalJoints}");
            Debug.Log($"[FullHandsPlaylist] Current sequence = {loadedLabels[seqIndex]}");
        }

        void Update()
        {
            if (sequences == null || sequences.Count == 0) return;

            if (enableHotkeys)
                HandleHotkeys();

            // ✅ End Hold 처리: 이 동안은 프레임 진행 안 하고 "마지막 포즈 유지"
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
        // Playlist / Sequence Loading
        // ---------------------------
        void LoadAllSequences()
        {
            sequences.Clear();
            loadedLabels.Clear();

            string root = Path.Combine(Application.streamingAssetsPath, rootFolderInStreamingAssets);

            if (!Directory.Exists(root))
            {
                Debug.LogError($"[FullHandsPlaylist] Root folder not found: {root}");
                return;
            }

            for (int i = 0; i < sequenceFolders.Count; i++)
            {
                string seqName = sequenceFolders[i];
                string folder = Path.Combine(root, seqName);

                if (!Directory.Exists(folder))
                {
                    Debug.LogWarning($"[FullHandsPlaylist] Sequence folder not found: {folder}");
                    continue;
                }

                var files = Directory.GetFiles(folder, filePattern);
                Array.Sort(files);

                if (files.Length == 0)
                {
                    Debug.LogWarning($"[FullHandsPlaylist] No files in: {folder} pattern={filePattern}");
                    continue;
                }

                sequences.Add(files);
                loadedLabels.Add(seqName);

                Debug.Log($"[FullHandsPlaylist] Loaded sequence '{seqName}' frames={files.Length}");
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

            // 현재 프레임 적용
            ApplyFrame(curSeq[frameIndex]);

            frameIndex++;

            // 시퀀스 종료 처리
            if (frameIndex >= curSeq.Length)
            {
                // 마지막 포즈 유지: 방금 마지막 프레임을 적용했으니 "홀드"만 걸면 됨
                frameIndex = Mathf.Clamp(curSeq.Length - 1, 0, curSeq.Length - 1);

                // end hold 후 할 행동 예약
                pendingEndAction = true;
                pendingActionType = DecideEndAction();

                // ✅ 0.5초 멈춤(포즈 유지)
                holdTimer = Mathf.Max(0f, endHoldSeconds);
                if (holdTimer <= 0f)
                {
                    // hold 0이면 즉시 실행
                    pendingEndAction = false;
                    DoPendingEndAction();
                }
            }
        }

        EndActionType DecideEndAction()
        {
            if (autoNextSequence)
            {
                // 다음 시퀀스로 이동 or 플레이리스트 종료
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
                    // 마지막 포즈 유지(정지)
                    enabled = true; // 계속 살아있어도 프레임 진행 안 하게 하려면 autoNextSequence/loopSequence를 false로 유지
                    break;

                case EndActionType.StopPlaylistEnd:
                    Debug.Log("[FullHandsPlaylist] Playlist finished. Stop.");
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
                else
                {
                    seqIndex = sequences.Count - 1;
                    enabled = false;
                    return;
                }
            }

            Debug.Log($"[FullHandsPlaylist] Switched sequence -> {loadedLabels[seqIndex]} (seqIndex={seqIndex})");
        }

        public void SwitchSequence(int index)
        {
            if (index < 0 || index >= sequences.Count) return;

            seqIndex = index;
            frameIndex = 0;
            acc = 0f;

            // hold 상태 초기화
            holdTimer = 0f;
            pendingEndAction = false;
            pendingActionType = EndActionType.None;

            Debug.Log($"[FullHandsPlaylist] Manual switch -> {loadedLabels[seqIndex]} (seqIndex={seqIndex})");
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
        // Frame Apply (FullHands)
        // ---------------------------
        void ApplyFrame(string path)
        {
            string json = File.ReadAllText(path);
            var root = JsonUtility.FromJson<Root>(json);

            if (root?.people == null) return;

            // Pose(25)
            var pose = root.people.pose_keypoints_3d;
            if (pose != null && pose.Count >= PoseCount * 4)
            {
                for (int i = 0; i < PoseCount; i++)
                {
                    Vector3 p = GetVec3(pose, i) * scale + worldOffset;
                    joints[i].transform.position = p;
                }
            }

            // Left Hand(21)
            var lh = root.people.hand_left_keypoints_3d;
            if (lh != null && lh.Count >= HandCount * 4)
            {
                for (int i = 0; i < HandCount; i++)
                {
                    Vector3 p = GetVec3(lh, i) * scale + worldOffset;
                    joints[LeftHandStart + i].transform.position = p;
                    joints[LeftHandStart + i].SetActive(true);
                }
            }
            else
            {
                for (int i = 0; i < HandCount; i++) joints[LeftHandStart + i].SetActive(false);
            }

            // Right Hand(21)
            var rh = root.people.hand_right_keypoints_3d;
            if (rh != null && rh.Count >= HandCount * 4)
            {
                for (int i = 0; i < HandCount; i++)
                {
                    Vector3 p = GetVec3(rh, i) * scale + worldOffset;
                    joints[RightHandStart + i].transform.position = p;
                    joints[RightHandStart + i].SetActive(true);
                }
            }
            else
            {
                for (int i = 0; i < HandCount; i++) joints[RightHandStart + i].SetActive(false);
            }

            // Lines update
            if (drawBones)
            {
                // pose
                if (poseLines != null)
                {
                    for (int i = 0; i < poseBones.Length; i++)
                    {
                        var (a, b) = poseBones[i];
                        poseLines[i].SetPosition(0, joints[a].transform.position);
                        poseLines[i].SetPosition(1, joints[b].transform.position);
                    }
                }

                // left hand
                if (leftHandLines != null)
                {
                    for (int i = 0; i < handBonesLocal.Length; i++)
                    {
                        var (a, b) = handBonesLocal[i];
                        int A = LeftHandStart + a;
                        int B = LeftHandStart + b;

                        if (!joints[A].activeSelf || !joints[B].activeSelf)
                        {
                            leftHandLines[i].enabled = false;
                            continue;
                        }

                        leftHandLines[i].enabled = true;
                        leftHandLines[i].SetPosition(0, joints[A].transform.position);
                        leftHandLines[i].SetPosition(1, joints[B].transform.position);
                    }
                }

                // right hand
                if (rightHandLines != null)
                {
                    for (int i = 0; i < handBonesLocal.Length; i++)
                    {
                        var (a, b) = handBonesLocal[i];
                        int A = RightHandStart + a;
                        int B = RightHandStart + b;

                        if (!joints[A].activeSelf || !joints[B].activeSelf)
                        {
                            rightHandLines[i].enabled = false;
                            continue;
                        }

                        rightHandLines[i].enabled = true;
                        rightHandLines[i].SetPosition(0, joints[A].transform.position);
                        rightHandLines[i].SetPosition(1, joints[B].transform.position);
                    }
                }
            }
        }

        // ---------------------------
        // Visual helpers
        // ---------------------------
        GameObject CreatePoseJoint(int idx)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = $"P{idx}";
            go.transform.SetParent(transform, true);
            go.transform.localScale = Vector3.one * jointSize;

            var r = go.GetComponent<Renderer>();
            if (r)
            {
                r.material = new Material(Shader.Find("Standard"));
                r.material.color = Color.white;
            }
            return go;
        }

        GameObject CreateHandJoint(bool isLeft, int handLocalIdx)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = $"{(isLeft ? "LH" : "RH")}_{handLocalIdx}";
            go.transform.SetParent(transform, true);

            float mul = (handLocalIdx == 4 || handLocalIdx == 8 || handLocalIdx == 12 || handLocalIdx == 16 || handLocalIdx == 20) ? 1.25f : 1f;
            go.transform.localScale = Vector3.one * handJointSize * mul;

            var r = go.GetComponent<Renderer>();
            if (r)
            {
                r.material = new Material(Shader.Find("Standard"));
                r.material.color = isLeft ? new Color(0.2f, 1f, 0.6f) : new Color(0.3f, 0.7f, 1f);
            }
            return go;
        }

        LineRenderer[] CreateLines(string prefix, int count, float width)
        {
            var arr = new LineRenderer[count];
            for (int i = 0; i < count; i++)
            {
                var go = new GameObject(prefix + i);
                go.transform.SetParent(transform, true);

                var lr = go.AddComponent<LineRenderer>();
                lr.positionCount = 2;
                lr.startWidth = width;
                lr.endWidth = width;
                lr.useWorldSpace = true;
                lr.material = new Material(Shader.Find("Sprites/Default"));

                arr[i] = lr;
            }
            return arr;
        }

        // ---------------------------
        // Data helpers
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
    }
}
