using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NIA_STICKMAN_FULLHANDS_20260206
{
    [Serializable]
    public class NIA20260206_Root
    {
        public NIA20260206_People people;
    }

    [Serializable]
    public class NIA20260206_People
    {
        public List<float> pose_keypoints_3d;

        // ✅ 손가락까지: 21개 관절(각 관절: x,y,z,score) => 21*4 floats
        public List<float> hand_left_keypoints_3d;
        public List<float> hand_right_keypoints_3d;
    }

    public class NiaStickmanFullHandsDbg_20260206 : MonoBehaviour
    {
        [Header("Source")]
        public string folderInStreamingAssets = "SignJson";
        public float fps = 30f;

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

        private string[] files = Array.Empty<string>();
        private int frame = 0;
        private float acc = 0f;

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
        // 0: wrist
        // thumb: 1-4, index: 5-8, middle: 9-12, ring: 13-16, pinky: 17-20
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

            // (선택) 손바닥 “가로” 연결(좀 더 손처럼 보이게)
            (5,9),(9,13),(13,17)
        };

        private LineRenderer[] poseLines;
        private LineRenderer[] leftHandLines;
        private LineRenderer[] rightHandLines;

        void Start()
        {
            string folder = Path.Combine(Application.streamingAssetsPath, folderInStreamingAssets);
            if (!Directory.Exists(folder))
            {
                Debug.LogError($"[FullHandsDbg] Folder not found: {folder}");
                return;
            }

            files = Directory.GetFiles(folder, "*_keypoints.json");
            Array.Sort(files);

            if (files.Length == 0)
            {
                Debug.LogError($"[FullHandsDbg] No *_keypoints.json found in: {folder}");
                return;
            }

            joints = new GameObject[TotalJoints];

            // pose joints (0..24)
            for (int i = 0; i < PoseCount; i++)
                joints[i] = CreatePoseJoint(i);

            // left hand joints (25..45)
            for (int i = 0; i < HandCount; i++)
                joints[LeftHandStart + i] = CreateHandJoint(LeftHandStart + i, isLeft: true, handLocalIdx: i);

            // right hand joints (46..66)
            for (int i = 0; i < HandCount; i++)
                joints[RightHandStart + i] = CreateHandJoint(RightHandStart + i, isLeft: false, handLocalIdx: i);

            if (drawBones)
            {
                poseLines = CreateLines("PoseBone_", poseBones.Length, boneWidth);
                leftHandLines = CreateLines("LeftHandBone_", handBonesLocal.Length, handBoneWidth);
                rightHandLines = CreateLines("RightHandBone_", handBonesLocal.Length, handBoneWidth);
            }

            Debug.Log($"[FullHandsDbg] Loaded {files.Length} frames. TotalJoints={TotalJoints} (pose 25 + hands 21+21)");
        }

        void Update()
        {
            if (files == null || files.Length == 0) return;

            acc += Time.deltaTime;
            float spf = 1f / Mathf.Max(1f, fps);

            while (acc >= spf)
            {
                acc -= spf;
                ApplyFrame(files[frame]);
                frame = (frame + 1) % files.Length;
            }
        }

        void ApplyFrame(string path)
        {
            string json = File.ReadAllText(path);
            var root = JsonUtility.FromJson<NIA20260206_Root>(json);

            if (root?.people == null) return;

            // --------------------
            // Pose(25)
            // --------------------
            var pose = root.people.pose_keypoints_3d;
            if (pose != null && pose.Count >= PoseCount * 4)
            {
                for (int i = 0; i < PoseCount; i++)
                {
                    Vector3 p = GetVec3(pose, i) * scale + worldOffset;
                    joints[i].transform.position = p;
                }
            }

            // --------------------
            // Left Hand(21)
            // --------------------
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
                // 프레임에 손 데이터 없으면 숨김(깨끗하게)
                for (int i = 0; i < HandCount; i++) joints[LeftHandStart + i].SetActive(false);
            }

            // --------------------
            // Right Hand(21)
            // --------------------
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

            // --------------------
            // Lines update
            // --------------------
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
            // 네 기존 스타일 그대로(간단히)
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

        GameObject CreateHandJoint(int globalIdx, bool isLeft, int handLocalIdx)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = $"{(isLeft ? "LH" : "RH")}_{handLocalIdx}";
            go.transform.SetParent(transform, true);

            // 손가락 끝(4,8,12,16,20)은 살짝 크게
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
