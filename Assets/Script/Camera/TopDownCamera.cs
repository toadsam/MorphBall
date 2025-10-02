using UnityEngine;

public class TopDownCamera : MonoBehaviour
{
    public Transform target;        // 따라갈 대상 (플레이어)
    public Vector3 offset = new Vector3(0, 10, -10); // 카메라 위치 오프셋
    public float followSpeed = 5f;  // 따라가는 속도

    private void LateUpdate()
    {
        if (target == null) return;

        // 목표 위치 = 플레이어 위치 + 오프셋
        Vector3 targetPos = target.position + offset;

        // 부드럽게 이동
        transform.position = Vector3.Lerp(transform.position, targetPos, followSpeed * Time.deltaTime);

        // 플레이어 바라보기 (위에서 내려다보는 시점)
        transform.LookAt(target);
    }
}
