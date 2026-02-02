using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// ✅ 절대 겹치기 힘든 네임스페이스/클래스명 사용
namespace NIA_STICKMAN_DEBUG_20260203
{
    // ---------------------------
    // JSON DTO (unique names)
    // ---------------------------
    [Serializable]
    public class NIA20260203_Root
    {
        public NIA20260203_People people;
    }

    [Serializable]
    public class NIA20260203_People
    {
        public List<float> pose_keypoints_3d;
    }

    // ---------------------------
    // Main Debugger
    // ---------------------------
    public class NiaStickmanDbg_20260203 : MonoBehaviour
    {
        [Header("Source")]
        public string folderInStreamingAssets = "SignJson";
        public float fps = 30f;

        [Header("Coordinate Conversion")]
        public float scale = 0.01f;
        public bool flipY = true;
        public bool flipZ = true;

        [Header("Joint Visual")]
        public float jointSize = 0.03f;
        public Vector3 worldOffset = new Vector3(0, 1.0f, 0);

        [Header("Bone Visual")]
        public bool drawBones = true;
        public float boneWidth = 0.01f;

        private string[] files = Array.Empty<string>();
        private int frame = 0;
        private float acc = 0f;

        private const int jointCount = 25;
        private GameObject[] joints;

        // BODY_25 연결(스틱맨)
        private readonly (int a, int b)[] bones = new (int, int)[]
        {
            // head/torso
            (0,1), (1,8),

            // right arm
            (1,2), (2,3), (3,4),

            // left arm
            (1,5), (5,6), (6,7),

            // right leg
            (8,9), (9,10), (10,11),

            // left leg
            (8,12), (12,13), (13,14),

            // feet (optional)
            (14,19), (19,20), (14,21),
            (11,22), (22,23), (11,24),

            // face extras (optional)
            (0,15), (0,16), (15,17), (16,18),
        };

        private LineRenderer[] boneLines;

        void Start()
        {
            string folder = Path.Combine(Application.streamingAssetsPath, folderInStreamingAssets);
            if (!Directory.Exists(folder))
            {
                Debug.LogError($"[NiaStickmanDbg] Folder not found: {folder}");
                return;
            }

            files = Directory.GetFiles(folder, "*_keypoints.json");
            Array.Sort(files);

            if (files.Length == 0)
            {
                Debug.LogError($"[NiaStickmanDbg] No *_keypoints.json found in: {folder}");
                return;
            }

            joints = new GameObject[jointCount];
            for (int i = 0; i < jointCount; i++)
            {
                joints[i] = CreateJointVisual(i);
            }

            if (drawBones)
            {
                boneLines = new LineRenderer[bones.Length];
                for (int i = 0; i < bones.Length; i++)
                {
                    boneLines[i] = CreateBoneLine($"Bone_{bones[i].a}_{bones[i].b}");
                }
            }

            Debug.Log($"[NiaStickmanDbg] Loaded {files.Length} frames. jointCount={jointCount}");
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
            var root = JsonUtility.FromJson<NIA20260203_Root>(json);

            // ✅ 모호성 방지: 단순 null 체크만
            if (root == null) return;
            if (root.people == null) return;
            if (root.people.pose_keypoints_3d == null) return;

            var pose = root.people.pose_keypoints_3d;
            if (pose.Count < jointCount * 4) return;

            // joints update
            for (int i = 0; i < jointCount; i++)
            {
                Vector3 p = GetPose3D(pose, i) * scale + worldOffset;
                joints[i].transform.position = p;
            }

            // bones update
            if (drawBones && boneLines != null)
            {
                for (int i = 0; i < bones.Length; i++)
                {
                    int a = bones[i].a;
                    int b = bones[i].b;
                    boneLines[i].SetPosition(0, joints[a].transform.position);
                    boneLines[i].SetPosition(1, joints[b].transform.position);
                }
            }
        }

        // ---------------------------
        // Visual helpers
        // ---------------------------
        GameObject CreateJointVisual(int idx)
        {
            PrimitiveType type = PrimitiveType.Sphere;
            float sizeMul = 1f;

            // Head/Face
            if (idx == 0 || idx == 15 || idx == 16 || idx == 17 || idx == 18)
            {
                type = PrimitiveType.Sphere;
                sizeMul = 1.2f;
            }
            // Neck / MidHip
            else if (idx == 1 || idx == 8)
            {
                type = PrimitiveType.Cube;
                sizeMul = 1.3f;
            }
            // Shoulders / Hips
            else if (idx == 2 || idx == 5 || idx == 9 || idx == 12)
            {
                type = PrimitiveType.Capsule;
                sizeMul = 1.15f;
            }
            // Elbows / Knees
            else if (idx == 3 || idx == 6 || idx == 10 || idx == 13)
            {
                type = PrimitiveType.Cylinder;
                sizeMul = 1.05f;
            }
            // Wrists / Ankles
            else if (idx == 4 || idx == 7 || idx == 11 || idx == 14)
            {
                type = PrimitiveType.Sphere;
                sizeMul = 1.0f;
            }
            // Feet
            else if (idx == 19 || idx == 20 || idx == 21 || idx == 22 || idx == 23 || idx == 24)
            {
                type = PrimitiveType.Cube;
                sizeMul = 0.9f;
            }

            var go = GameObject.CreatePrimitive(type);
            go.name = $"J{idx}_{JointName(idx)}";
            go.transform.SetParent(transform, worldPositionStays: true);
            go.transform.localScale = Vector3.one * jointSize * sizeMul;

            // (선택) 부위별 색상
            var r = go.GetComponent<Renderer>();
            if (r)
            {
                r.material = new Material(Shader.Find("Standard"));
                r.material.color = JointColor(idx);
            }

            return go;
        }

        LineRenderer CreateBoneLine(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, worldPositionStays: true);

            var lr = go.AddComponent<LineRenderer>();
            lr.positionCount = 2;
            lr.startWidth = boneWidth;
            lr.endWidth = boneWidth;
            lr.useWorldSpace = true;
            lr.material = new Material(Shader.Find("Sprites/Default"));
            return lr;
        }

        string JointName(int idx)
        {
            return idx switch
            {
                0 => "Nose",
                1 => "Neck",
                2 => "RShoulder",
                3 => "RElbow",
                4 => "RWrist",
                5 => "LShoulder",
                6 => "LElbow",
                7 => "LWrist",
                8 => "MidHip",
                9 => "RHip",
                10 => "RKnee",
                11 => "RAnkle",
                12 => "LHip",
                13 => "LKnee",
                14 => "LAnkle",
                15 => "REye",
                16 => "LEye",
                17 => "REar",
                18 => "LEar",
                19 => "LBigToe",
                20 => "LSmallToe",
                21 => "LHeel",
                22 => "RBigToe",
                23 => "RSmallToe",
                24 => "RHeel",
                _ => "J"
            };
        }

        Color JointColor(int idx)
        {
            if (idx == 0 || idx == 1 || idx == 15 || idx == 16 || idx == 17 || idx == 18)
                return new Color(1f, 0.8f, 0.2f); // head
            if (idx == 8)
                return new Color(0.2f, 1f, 0.6f); // torso
            if (idx == 2 || idx == 3 || idx == 4)
                return new Color(0.3f, 0.7f, 1f); // right arm
            if (idx == 5 || idx == 6 || idx == 7)
                return new Color(1f, 0.4f, 0.7f); // left arm
            if (idx == 9 || idx == 10 || idx == 11)
                return new Color(0.7f, 0.7f, 1f); // right leg
            if (idx == 12 || idx == 13 || idx == 14)
                return new Color(1f, 0.7f, 0.7f); // left leg
            return new Color(0.9f, 0.9f, 0.9f);
        }

        // ---------------------------
        // Data helpers
        // ---------------------------
        Vector3 GetPose3D(List<float> pose, int idx)
        {
            int b = idx * 4;
            float x = pose[b + 0];
            float y = pose[b + 1];
            float z = pose[b + 2];

            if (flipY) y = -y;
            if (flipZ) z = -z;

            return new Vector3(x, y, z);
        }
    }
}
