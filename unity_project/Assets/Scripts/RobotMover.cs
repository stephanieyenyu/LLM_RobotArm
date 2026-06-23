using System.Collections;
using UnityEngine;

public class RobotMover : MonoBehaviour
{
    [Header("移動設定")]
    public float moveSpeed = 1.0f;
    public float arrivalThreshold = 0.01f;

    [Header("夾取設定")]
    public Transform graspTarget;   // 拖入 Cup
    private bool isGrasping = false;

    public IEnumerator MoveTo(Vector3 targetPosition)
    {
        Debug.Log($"移動到：{targetPosition}");

        while (Vector3.Distance(transform.position, targetPosition) > arrivalThreshold)
        {
            transform.position = Vector3.MoveTowards(
                transform.position,
                targetPosition,
                moveSpeed * Time.deltaTime
            );

            yield return null;
        }

        transform.position = targetPosition;
        Debug.Log("抵達目標位置");
    }

    public void Grasp()
    {
        isGrasping = true;
        if (graspTarget != null)
        {
            graspTarget.SetParent(transform);  // Cup 變成手臂子物件
        }
        Debug.Log("夾取物件");
    }

    public void Release()
    {
        isGrasping = false;
        if (graspTarget != null)
        {
            graspTarget.SetParent(null);  // Cup 脫離手臂
        }
        Debug.Log("放開物件");
    }
}