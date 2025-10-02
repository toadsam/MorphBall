using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerController : MonoBehaviour
{
    public float moveSpeed = 3f;
    public float jumpForce = 5f;
    private Rigidbody rb;

    private Vector2 moveInput;
    private bool jumpPressed;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
    }

    public void OnMove(InputAction.CallbackContext context)
    {
        moveInput = context.ReadValue<Vector2>();
    }

    public void OnJump(InputAction.CallbackContext context)
    {
        if (context.performed) jumpPressed = true;
    }

    private void FixedUpdate()
    {
        // 입력 방향 → 월드 좌표 변환
        Vector3 move = new Vector3(moveInput.x, 0, moveInput.y);

        // 위치를 직접 이동시키는 대신 힘을 가해 굴러가도록
        rb.AddForce(move * moveSpeed, ForceMode.Impulse);

        // 점프 처리
        if (jumpPressed)
        {
            rb.AddForce(Vector3.up * jumpForce, ForceMode.Impulse);
            jumpPressed = false;
        }
    }
}
