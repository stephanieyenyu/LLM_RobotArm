using System.Collections;
using UnityEngine;

public class RobotMover : MonoBehaviour
{
    [Header("移動設定")]
    public float moveSpeed = 1.0f;
    public float arrivalThreshold = 0.01f;

    private Transform graspTarget;
    private bool isGrasping = false;

    public void SetTarget(string objectName)
    {
        GameObject found = GameObject.Find(objectName);
        if (found != null)
        {
            graspTarget = found.transform;
            Debug.Log($"找到目標物件：{objectName}");
        }
        else
        {
            Debug.LogWarning($"找不到物件：{objectName}");
        }
    }

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
            graspTarget.SetParent(transform);
        }
        Debug.Log("夾取物件");
    }

    public void Release()
    {
        isGrasping = false;
        if (graspTarget != null)
        {
            graspTarget.SetParent(null);
        }
        Debug.Log("放開物件");
    }
}