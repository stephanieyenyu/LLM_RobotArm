using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;

[System.Serializable]
public class Position
{
    public float x, y, z;
}

[System.Serializable]
public class RobotAction
{
    public string action;
    public Position position;
}

[System.Serializable]
public class RobotPlan
{
    public string task;
    public string target_object;
    public List<RobotAction> action_sequence;
}

public class JsonExecutor : MonoBehaviour
{
    [Header("設定")]
    public string jsonFileName = "robot_plan.json";  // 放在 StreamingAssets/
    public RobotMover robotMover;

    private RobotPlan plan;

    void Start()
    {
        LoadAndExecute();
    }

    public void LoadAndExecute()
    {
        string path = Path.Combine(Application.streamingAssetsPath, jsonFileName);

        if (!File.Exists(path))
        {
            Debug.LogError("找不到 JSON 檔案：" + path);
            return;
        }

        string json = File.ReadAllText(path);
        plan = JsonUtility.FromJson<RobotPlan>(json);

        Debug.Log($"載入任務：{plan.task}，目標：{plan.target_object}");
        StartCoroutine(ExecutePlan());
    }

    IEnumerator ExecutePlan()
    {
        foreach (var act in plan.action_sequence)
        {
            Debug.Log("執行動作：" + act.action);

            if (act.action == "move_to")
            {
                Vector3 target = new Vector3(act.position.x, act.position.y, act.position.z);
                yield return StartCoroutine(robotMover.MoveTo(target));
            }
            else if (act.action == "grasp")
            {
                robotMover.Grasp();
                yield return new WaitForSeconds(0.5f);
            }
            else if (act.action == "release")
            {
                robotMover.Release();
                yield return new WaitForSeconds(0.5f);
            }
        }

        Debug.Log("任務完成！");
    }
}