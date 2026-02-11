using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NIA_DEBUG_25 // ✅ 기존과 절대 겹치지 않게 완전 새 네임스페이스
{
    [Serializable]
    public class NiaDbgRoot
    {
        public NiaDbgPerson people;
    }

    [Serializable]
    public class NiaDbgPerson
    {
        public List<float> pose_keypoints_3d;
    }

    public class JointDebugger25 : MonoBehaviour
    {
        [Header("Source")]
        public string folderInStreamingAssets = "SignJson";
        public float fps = 30f;

        [Header("Coordinate Conversion")]
        public float scale = 0.01f;
        public bool flipY = true;
        public bool flipZ = true;

        [Header("Debug Visual")]
        public float sphereSize = 0.03f;
        public Vector3 worldOffset = new Vector3(0, 1.0f, 0);

        private string[] files = Array.Empty<string>();
        private int frame = 0;
        private float acc = 0f;

        private const int jointCount = 25; // ✅ 너 로그에서 25 확정
        private GameObject[] spheres;

        void Start()
        {
            string folder = Path.Combine(Application.streamingAssetsPath, folderInStreamingAssets);
            if (!Directory.Exists(folder))
            {
                Debug.LogError($"[JointDebugger25] Folder not found: {folder}");
                return;
            }

            files = Directory.GetFiles(folder, "*_keypoints.json");
            Array.Sort(files);

            if (files.Length == 0)
            {
                Debug.LogError($"[JointDebugger25] No *_keypoints.json found in: {folder}");
                return;
            }

            spheres = new GameObject[jointCount];
            for (int i = 0; i < jointCount; i++)
            {
                var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                s.name = $"J{i}";
                s.transform.localScale = Vector3.one * sphereSize;
                spheres[i] = s;
            }

            Debug.Log($"[JointDebugger25] Loaded {files.Length} frames. jointCount={jointCount}");
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
            var root = JsonUtility.FromJson<NiaDbgRoot>(json);
            if (root == null || root.people == null || root.people.pose_keypoints_3d == null) return;

            var pose = root.people.pose_keypoints_3d;
            if (pose.Count < jointCount * 4) return;

            for (int i = 0; i < jointCount; i++)
            {
                Vector3 p = GetPose3D(pose, i) * scale + worldOffset;
                spheres[i].transform.position = p;
            }
        }

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
