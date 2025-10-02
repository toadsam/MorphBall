using UnityEngine;

public class TopDownCamera : MonoBehaviour
{
    public Transform target;        // ���� ��� (�÷��̾�)
    public Vector3 offset = new Vector3(0, 10, -10); // ī�޶� ��ġ ������
    public float followSpeed = 5f;  // ���󰡴� �ӵ�

    private void LateUpdate()
    {
        if (target == null) return;

        // ��ǥ ��ġ = �÷��̾� ��ġ + ������
        Vector3 targetPos = target.position + offset;

        // �ε巴�� �̵�
        transform.position = Vector3.Lerp(transform.position, targetPos, followSpeed * Time.deltaTime);

        // �÷��̾� �ٶ󺸱� (������ �����ٺ��� ����)
        transform.LookAt(target);
    }
}
