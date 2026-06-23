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
public class JointAngles
{
    public float pan, lift, elbow, wrist1, wrist2, wrist3;
}

[System.Serializable]
public class RobotAction
{
    public string action;
    public Position position;
    public JointAngles joints;
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
    public string jsonFileName = "robot_plan.json";
    public UR3Controller ur3Controller;

    private RobotPlan plan;

    void Start()
    {
        LoadAndExecute();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            StopAllCoroutines();
            LoadAndExecute();
        }
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
        Debug.Log($"=== 開始任務：{plan.task} | 目標：{plan.target_object} ===");

        for (int i = 0; i < plan.action_sequence.Count; i++)
        {
            var act = plan.action_sequence[i];
            Debug.Log($"[{i + 1}/{plan.action_sequence.Count}] 執行：{act.action}");

            if (act.action == "move_to" && act.joints != null)
            {
                yield return StartCoroutine(ur3Controller.MoveToJointAngles(
                    act.joints.pan,
                    act.joints.lift,
                    act.joints.elbow,
                    act.joints.wrist1,
                    act.joints.wrist2,
                    act.joints.wrist3
                ));
            }
            else if (act.action == "grasp")
            {
                ur3Controller.Grasp();
                yield return new WaitForSeconds(0.5f);
            }
            else if (act.action == "release")
            {
                ur3Controller.Release();
                yield return new WaitForSeconds(0.5f);
            }
        }

        Debug.Log("=== 任務完成！===");
    }
}