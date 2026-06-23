using System.Collections;
using UnityEngine;

public class RobotMover : MonoBehaviour
{
    [Header("移動設定")]
    public float moveSpeed = 1.0f;
    public float arrivalThreshold = 0.01f;

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
}