using UnityEngine;
using UnityEngine.InputSystem;

public class MaterialChanger : MonoBehaviour
{
    public Material[] materials; // 머티리얼 목록
    private int currentIndex = 0;

    private Renderer rend;
    private PlayerController player;
    private Collider col;

    public PhysicsMaterial slippery; // 미끄러운 마찰
    public PhysicsMaterial sticky;   // 끈적한 마찰
    public PhysicsMaterial normal;   // 일반 마찰

    private void Awake()
    {
        rend = GetComponent<Renderer>();
        player = GetComponent<PlayerController>();
        col = GetComponent<Collider>();

        if (materials.Length > 0)
            ApplyMaterialStats(materials[0]);
    }

    private void Update()
    {
        if (Keyboard.current.zKey.wasPressedThisFrame)
        {
            currentIndex--;
            if (currentIndex < 0) currentIndex = materials.Length - 1;
            ApplyMaterialStats(materials[currentIndex]);
        }

        if (Keyboard.current.xKey.wasPressedThisFrame)
        {
            currentIndex++;
            if (currentIndex >= materials.Length) currentIndex = 0;
            ApplyMaterialStats(materials[currentIndex]);
        }
    }

    private void ApplyMaterialStats(Material mat)
    {
        rend.material = mat;

        switch (mat.name) // 머티리얼 이름으로 분기
        {
            case "불": // 빨강 머티리얼
                player.moveSpeed = 0.2f;
                player.jumpForce = 5f;
                col.material = sticky;
                break;

            case "얼음": // 파랑 머티리얼
                player.moveSpeed = 0.4f;
                player.jumpForce = 10f;
                col.material = sticky;
                break;

            case "풀": // 초록 머티리얼
                player.moveSpeed = 0.8f;
                player.jumpForce = 3f;
                col.material = sticky;
                break;

            default: // 기본값
                player.moveSpeed = 1f;
                player.jumpForce = 5f;
                col.material = sticky;
                break;
        }

        Debug.Log($"Changed to {mat.name} | Speed={player.moveSpeed}, Jump={player.jumpForce}");
    }
}
