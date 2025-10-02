using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerController : MonoBehaviour
{
    public float moveSpeed = 5f;
    public float jumpForce = 5f;
    private Rigidbody rb;

    private Vector2 moveInput;
    private bool jumpPressed;
    //private bool attackPressed;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    // Input System �ڵ����� ȣ��Ǵ� �޼���
    public void OnMove(InputAction.CallbackContext context)
    {
        moveInput = context.ReadValue<Vector2>();
    }

    public void OnJump(InputAction.CallbackContext context)
    {
        if (context.performed) jumpPressed = true;
    }

    public void OnAttack(InputAction.CallbackContext context)
    {
     //   if (context.performed) attackPressed = true;
    }

    private void FixedUpdate()
    {
        // �̵�
        Vector3 move = new Vector3(moveInput.x, 0, moveInput.y);
        rb.MovePosition(rb.position + move * moveSpeed * Time.fixedDeltaTime);
    }

    private void Update()
    {
        // ���� ó��
        if (jumpPressed)
        {
            rb.AddForce(Vector3.up * jumpForce, ForceMode.Impulse);
            jumpPressed = false;
        }

        // ���� ó��
      //  if (attackPressed)
       // {
           // Debug.Log("���� ����!");
         //   attackPressed = false;
        //}
    }
}
