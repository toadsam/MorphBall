using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// ===============================
// NIA JSON DTO (충돌 방지용 namespace)
// ===============================
namespace NiaKeypoints
{
    [Serializable]
    public class NiaFrameRoot
    {
        public NiaPerson people;
    }

    [Serializable]
    public class NiaPerson
    {
        public int person_id;

        // (x, y, z, conf) 반복 배열
        public List<float> pose_keypoints_3d;
    }
}

// ===============================
// IK Driver (attach to Y Bot root)
// ===============================
public class KeypointIKDriver : MonoBehaviour
{
    [Header("Source")]
    public Animator animator;
    public string folderInStreamingAssets = "SignJson";
    public float fps = 30f;

    [Header("Pose Indices (must match your dataset)")]
    public int LShoulder = 5, LElbow = 6, LWrist = 7;
    public int RShoulder = 2, RElbow = 3, RWrist = 4;

    [Header("Coordinate Conversion")]
    public float sourceToMeters = 0.01f; // 데이터 스케일 조절
    public bool flipY = true;            // 카메라 y-down이면 true가 많음
    public bool flipZ = true;            // 깊이축이 반대면 true

    [Header("IK Targets (under Rig)")]
    public Transform L_Target;
    public Transform L_Hint;
    public Transform R_Target;
    public Transform R_Hint;

    [Header("Smoothing")]
    [Range(0f, 1f)] public float posSmooth = 0.15f; // 0~0.2 추천

    // files
    private string[] files = Array.Empty<string>();
    private int frame = 0;
    private float acc = 0f;

    // calibration (first frame as "neutral")
    private bool calibrated = false;

    // source reference points (first frame)
    private Vector3 srcLS0, srcLE0, srcLW0;
    private Vector3 srcRS0, srcRE0, srcRW0;

    // avatar reference target positions (first frame)
    private Vector3 LTarget0, LHint0, RTarget0, RHint0;

    // reference bases (source & avatar)
    private Matrix4x4 srcBasis0;
    private Matrix4x4 avatarBasis0;

    void Awake()
    {
        if (!animator) animator = GetComponentInChildren<Animator>();
    }

    void Start()
    {
        // 1) json 파일 로드
        string folder = Path.Combine(Application.streamingAssetsPath, folderInStreamingAssets);
        if (!Directory.Exists(folder))
        {
            Debug.LogError($"[KeypointIKDriver] StreamingAssets folder not found: {folder}");
            return;
        }

        files = Directory.GetFiles(folder, "*_keypoints.json");
        Array.Sort(files);

        if (files.Length == 0)
        {
            Debug.LogError($"[KeypointIKDriver] No *_keypoints.json in: {folder}");
            return;
        }

        frame = 0;
        acc = 0f;
        calibrated = false;

        // (권장) Animator에 다른 애니메이션이 돌고 있으면 IK가 섞여 헷갈릴 수 있음
        // - Animator Controller를 비워두거나
        // - RigBuilder Weight로 제어
        // - ApplyRootMotion OFF 권장
    }

    void Update()
    {
        if (files == null || files.Length == 0) return;
        if (!L_Target || !L_Hint || !R_Target || !R_Hint) return;

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
        // 2) JSON 파싱 (충돌 없는 타입 사용)
        string json = File.ReadAllText(path);
        var root = JsonUtility.FromJson<NiaKeypoints.NiaFrameRoot>(json);
        if (root == null) return;
        if (root.people == null) return;
        if (root.people.pose_keypoints_3d == null) return;

        var pose = root.people.pose_keypoints_3d;
        // ===== DEBUG: joint count & wrist candidate search =====
        if (!calibrated)
        {
            int jointCount = pose.Count / 4;
            Debug.Log($"[DEBUG] jointCount = {jointCount}");

            Vector3 LE = GetPose3D(pose, LElbow);
            Vector3 RE = GetPose3D(pose, RElbow);

            int bestLW = -1, bestRW = -1;
            float bestLd = -1f, bestRd = -1f;

            for (int i = 0; i < jointCount; i++)
            {
                Vector3 p = GetPose3D(pose, i);

                float dl = (p - LE).magnitude;
                if (dl > bestLd) { bestLd = dl; bestLW = i; }

                float dr = (p - RE).magnitude;
                if (dr > bestRd) { bestRd = dr; bestRW = i; }
            }

            Debug.Log($"[DEBUG] farthestFromLElbow idx={bestLW}, dist={bestLd}");
            Debug.Log($"[DEBUG] farthestFromRElbow idx={bestRW}, dist={bestRd}");
        }

        int maxIdx = Mathf.Max(LWrist, RWrist);
        if (pose.Count < (maxIdx + 1) * 4) return;

        // 3) 필요한 키포인트
        Vector3 srcLS = GetPose3D(pose, LShoulder);
        Vector3 srcLE = GetPose3D(pose, LElbow);
        Vector3 srcLW = GetPose3D(pose, LWrist);

        Vector3 srcRS = GetPose3D(pose, RShoulder);
        Vector3 srcRE = GetPose3D(pose, RElbow);
        Vector3 srcRW = GetPose3D(pose, RWrist);

        // 4) 첫 프레임 캘리브레이션: "차렷 = 0"
        if (!calibrated)
        {
            srcLS0 = srcLS; srcLE0 = srcLE; srcLW0 = srcLW;
            srcRS0 = srcRS; srcRE0 = srcRE; srcRW0 = srcRW;

            srcBasis0 = BuildBasisFromShoulders(srcLS0, srcRS0);
            avatarBasis0 = BuildAvatarBasis(animator);

            LTarget0 = L_Target.position;
            LHint0 = L_Hint.position;
            RTarget0 = R_Target.position;
            RHint0 = R_Hint.position;

            calibrated = true;
            return; // 기준만 잡고 첫 프레임은 적용 안 함
        }

        // 5) delta 계산(기준 대비 변화량)
        Vector3 dLW = srcLW - srcLW0;
        Vector3 dRW = srcRW - srcRW0;
        Vector3 dLE = srcLE - srcLE0;
        Vector3 dRE = srcRE - srcRE0;

        // 소스 basis local로 변환
        Vector3 dLW_local = WorldToBasisLocal(srcBasis0, dLW);
        Vector3 dRW_local = WorldToBasisLocal(srcBasis0, dRW);
        Vector3 dLE_local = WorldToBasisLocal(srcBasis0, dLE);
        Vector3 dRE_local = WorldToBasisLocal(srcBasis0, dRE);

        // 아바타 basis로 월드 델타 변환
        Vector3 dLW_world = BasisLocalToWorld(avatarBasis0, dLW_local) * sourceToMeters;
        Vector3 dRW_world = BasisLocalToWorld(avatarBasis0, dRW_local) * sourceToMeters;
        Vector3 dLE_world = BasisLocalToWorld(avatarBasis0, dLE_local) * sourceToMeters;
        Vector3 dRE_world = BasisLocalToWorld(avatarBasis0, dRE_local) * sourceToMeters;

        // 6) 타겟/힌트 적용 (부드럽게)
        SetPosSmooth(L_Target, LTarget0 + dLW_world);
        SetPosSmooth(R_Target, RTarget0 + dRW_world);

        SetPosSmooth(L_Hint, LHint0 + dLE_world);
        SetPosSmooth(R_Hint, RHint0 + dRE_world);
    }

    void SetPosSmooth(Transform t, Vector3 target)
    {
        if (posSmooth <= 0f)
        {
            t.position = target;
            return;
        }

        float lerp = 1f - posSmooth; // 0.15면 꽤 안정+반응
        t.position = Vector3.Lerp(t.position, target, lerp);
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

    // ===============================
    // Basis helpers
    // ===============================

    // 어깨선으로 right, up은 월드업(초기 버전), forward = cross(right, up)
    Matrix4x4 BuildBasisFromShoulders(Vector3 LS, Vector3 RS)
    {
        Vector3 right = (RS - LS).normalized;
        Vector3 up = Vector3.up;
        Vector3 forward = Vector3.Cross(right, up);

        if (forward.sqrMagnitude < 1e-6f)
            forward = Vector3.forward;

        forward.Normalize();

        Matrix4x4 m = Matrix4x4.identity;
        m.SetColumn(0, new Vector4(right.x, right.y, right.z, 0));
        m.SetColumn(1, new Vector4(up.x, up.y, up.z, 0));
        m.SetColumn(2, new Vector4(forward.x, forward.y, forward.z, 0));
        return m;
    }

    // Hips 기준으로 아바타의 right/up/forward
    Matrix4x4 BuildAvatarBasis(Animator a)
    {
        Transform hips = a ? a.GetBoneTransform(HumanBodyBones.Hips) : null;
        if (!hips) return Matrix4x4.identity;

        Vector3 right = hips.right.normalized;
        Vector3 up = hips.up.normalized;
        Vector3 forward = hips.forward.normalized;

        Matrix4x4 m = Matrix4x4.identity;
        m.SetColumn(0, new Vector4(right.x, right.y, right.z, 0));
        m.SetColumn(1, new Vector4(up.x, up.y, up.z, 0));
        m.SetColumn(2, new Vector4(forward.x, forward.y, forward.z, 0));
        return m;
    }

    Vector3 WorldToBasisLocal(Matrix4x4 basis, Vector3 v)
    {
        Vector3 r = basis.GetColumn(0);
        Vector3 u = basis.GetColumn(1);
        Vector3 f = basis.GetColumn(2);

        return new Vector3(Vector3.Dot(v, r), Vector3.Dot(v, u), Vector3.Dot(v, f));
    }

    Vector3 BasisLocalToWorld(Matrix4x4 basis, Vector3 vLocal)
    {
        Vector3 r = basis.GetColumn(0);
        Vector3 u = basis.GetColumn(1);
        Vector3 f = basis.GetColumn(2);

        return r * vLocal.x + u * vLocal.y + f * vLocal.z;
    }
}
