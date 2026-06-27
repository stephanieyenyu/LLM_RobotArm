using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System;
using System.Linq;
using Assets.Scripts;

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
    public string urIP = "192.168.56.101";

    private URPackageListener urListener;
    private RobotPlan plan;

    void Start()
    {
        urListener = new URPackageListener();
        urListener.Connect(urIP);
        Debug.Log("嘗試連線到 " + urIP);
    }

    void OnDestroy()
    {
        urListener?.Close();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            LoadAndExecute();
        }
    }

    public void LoadAndExecute()
    {
        string path = Path.Combine(Application.streamingAssetsPath, jsonFileName);

        if (!File.Exists(path))
        {
            Debug.LogError("找不到 JSON：" + path);
            return;
        }

        string json = File.ReadAllText(path);
        plan = JsonUtility.FromJson<RobotPlan>(json);

        Debug.Log($"載入任務：{plan.task}，目標：{plan.target_object}");

        if (!urListener.Connected)
        {
            Debug.LogWarning("尚未連線到 UR，嘗試重新連線");
            urListener.Connect(urIP);
        }

        StartCoroutine(ExecutePlan());
    }

    IEnumerator ExecutePlan()
    {
        Debug.Log($"=== 開始任務：{plan.task} ===");

        for (int i = 0; i < plan.action_sequence.Count; i++)
        {
            var act = plan.action_sequence[i];
            Debug.Log($"[{i + 1}/{plan.action_sequence.Count}] {act.action}");

            if (act.action == "move_to")
            {
                bool hasJoints = act.joints != null &&
                                 (act.joints.pan != 0 || act.joints.lift != 0 || act.joints.elbow != 0 ||
                                  act.joints.wrist1 != 0 || act.joints.wrist2 != 0 || act.joints.wrist3 != 0);

                if (hasJoints)
                {
                    var angles = new[] {
            act.joints.pan, act.joints.lift, act.joints.elbow,
            act.joints.wrist1, act.joints.wrist2, act.joints.wrist3
        };
                    var rad = angles.Select(a => a * Mathf.Deg2Rad);
                    string cmd = $"movej([{string.Join(", ", rad)}], a=1.2, v=1.05)";
                    urListener.SendCommand(cmd);
                    Debug.Log("SEND: " + cmd);
                }
                else if (act.position != null)
                {
                    string cmd = $"movel(p[{act.position.x.ToString("F4")}, {act.position.y.ToString("F4")}, {act.position.z.ToString("F4")}, 3.14, 0, 0], a=1.2, v=0.5)";
                    urListener.SendCommand(cmd);
                    Debug.Log("SEND: " + cmd);
                }
                yield return new WaitForSeconds(3f);
            }
            else if (act.action == "grasp")
            {
                urListener.SendCommand("set_standard_digital_out(4, True)");
                Debug.Log("SEND: grasp");
                yield return new WaitForSeconds(1f);
            }
            else if (act.action == "release")
            {
                urListener.SendCommand("set_standard_digital_out(4, False)");
                Debug.Log("SEND: release");
                yield return new WaitForSeconds(1f);
            }
        }

        Debug.Log("=== 任務完成 ===");
    }
}