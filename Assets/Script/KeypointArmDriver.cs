using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// ===== JSON 구조 (네 파일 구조에 맞춤) =====
[Serializable]
public class FrameRoot
{
    public People people;
}

[Serializable]
public class People
{
    public int person_id;

    // (x,y,z,conf) 반복
    public List<float> pose_keypoints_3d;
}

public class KeypointArmDriver : MonoBehaviour
{
    [Header("Source")]
    public Animator animator;
    public string folderInStreamingAssets = "SignJson";
    public float fps = 30f;

    [Header("Coordinate Fix")]
    public float scale = 1.0f;
    public bool flipY = false;
    public bool flipZ = true;   // 보통 카메라 깊이축 반대라 Z flip이 필요할 때가 많음

    [Header("Smoothing")]
    [Range(0f, 1f)] public float rotSmooth = 0.5f; // 0=즉시, 1=매우 부드럽게

    [Header("Pose Index Mapping (IMPORTANT)")]
    // pose_keypoints_3d 안에서 "몇 번 관절"이 어깨/팔꿈치/손목인지
    // (여기 값이 맞아야 팔이 제대로 움직임)
    public int LShoulder = 5;
    public int LElbow = 6;
    public int LWrist = 7;

    public int RShoulder = 2;
    public int RElbow = 3;
    public int RWrist = 4;

    // ===== runtime =====
    private string[] files;
    private int frame;
    private float acc;

    // Bones
    private Transform lUpper, lLower, rUpper, rLower;

    // Bind pose offset: "본이 바라보는 축" 보정
    private Quaternion lUpperOffset, lLowerOffset, rUpperOffset, rLowerOffset;
    private bool offsetsReady = false;

    void Awake()
    {
        if (!animator) animator = GetComponentInChildren<Animator>();
    }

    void Start()
    {
        // bones
        lUpper = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        lLower = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        rUpper = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        rLower = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);

        // frame files
        string folder = Path.Combine(Application.streamingAssetsPath, folderInStreamingAssets);
        if (!Directory.Exists(folder))
        {
            Debug.LogError($"StreamingAssets folder not found: {folder}");
            return;
        }

        files = Directory.GetFiles(folder, "*_keypoints.json");
        Array.Sort(files);
        if (files.Length == 0)
        {
            Debug.LogError($"No *_keypoints.json in: {folder}");
            return;
        }

        frame = 0;
        acc = 0f;

        // offsets will be computed on first valid frame
        offsetsReady = false;
    }

    void Update()
    {
        if (files == null || files.Length == 0) return;
        if (!lUpper || !lLower || !rUpper || !rLower) return;

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
        FrameRoot root = JsonUtility.FromJson<FrameRoot>(json);
        if (root == null || root.people == null) return;

        var pose = root.people.pose_keypoints_3d;
        if (pose == null) return;

        // 필요한 인덱스 범위 체크
        int maxIdx = Mathf.Max(LWrist, RWrist);
        if (pose.Count < (maxIdx + 1) * 4) return;

        Vector3 LS = GetPose3D(pose, LShoulder);
        Vector3 LE = GetPose3D(pose, LElbow);
        Vector3 LW = GetPose3D(pose, LWrist);

        Vector3 RS = GetPose3D(pose, RShoulder);
        Vector3 RE = GetPose3D(pose, RElbow);
        Vector3 RW = GetPose3D(pose, RWrist);

        // 방향 벡터 (어깨->팔꿈치, 팔꿈치->손목)
        Vector3 lUpperDir = (LS - LE);
        Vector3 lLowerDir = (LE - LW);

        Vector3 rUpperDir = (RS - RE);
        Vector3 rLowerDir = (RE - RW);

        if (lUpperDir.sqrMagnitude < 1e-8f || lLowerDir.sqrMagnitude < 1e-8f) return;
        if (rUpperDir.sqrMagnitude < 1e-8f || rLowerDir.sqrMagnitude < 1e-8f) return;

        // 기준 회전(좌표 방향이 보는 방향)
        // up 벡터는 임시로 world up 사용 (나중에 몸통/가슴 방향으로 개선 가능)
        Quaternion lUpperLook = Quaternion.LookRotation(lUpperDir.normalized, Vector3.up);
        Quaternion lLowerLook = Quaternion.LookRotation(lLowerDir.normalized, Vector3.up);
        Quaternion rUpperLook = Quaternion.LookRotation(rUpperDir.normalized, Vector3.up);
        Quaternion rLowerLook = Quaternion.LookRotation(rLowerDir.normalized, Vector3.up);

        // 첫 프레임에서 offset 캘리브레이션
        if (!offsetsReady)
        {
            // "현재 본 회전"이 "LookRotation 결과"와 맞도록 offset을 잡는다.
            // (본의 forward 축이 모델마다 다르기 때문에 필요한 과정)
            lUpperOffset = Quaternion.Inverse(lUpperLook) * lUpper.rotation;
            lLowerOffset = Quaternion.Inverse(lLowerLook) * lLower.rotation;
            rUpperOffset = Quaternion.Inverse(rUpperLook) * rUpper.rotation;
            rLowerOffset = Quaternion.Inverse(rLowerLook) * rLower.rotation;

            offsetsReady = true;
        }

        // 최종 목표 회전 = LookRotation * offset
        Quaternion lUpperTarget = lUpperLook * lUpperOffset;
        Quaternion lLowerTarget = lLowerLook * lLowerOffset;
        Quaternion rUpperTarget = rUpperLook * rUpperOffset;
        Quaternion rLowerTarget = rLowerLook * rLowerOffset;

        // 적용(부드럽게)
        float s = 1f - rotSmooth; // rotSmooth가 높을수록 천천히
        lUpper.rotation = Quaternion.Slerp(lUpper.rotation, lUpperTarget, s);
        lLower.rotation = Quaternion.Slerp(lLower.rotation, lLowerTarget, s);
        rUpper.rotation = Quaternion.Slerp(rUpper.rotation, rUpperTarget, s);
        rLower.rotation = Quaternion.Slerp(rLower.rotation, rLowerTarget, s);
    }

    Vector3 GetPose3D(List<float> pose, int idx)
    {
        int b = idx * 4;
        float x = pose[b + 0];
        float y = pose[b + 1];
        float z = pose[b + 2];

        // 축 보정
        if (flipY) y = -y;
        if (flipZ) z = -z;

        return new Vector3(x, y, z) * scale;
    }
}
