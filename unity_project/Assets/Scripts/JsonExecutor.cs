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
public class NamedPosition
{
    public string name;
    public float x, y, z;
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
    // C 給的高階格式
    public string action;
    public string @object;
    public string target;
    public string direction;
    public float? distance_cm;
    public NamedPosition object_position;
    public NamedPosition target_position;

    // 展開後的低階格式
    public List<RobotAction> action_sequence;
}

public class JsonExecutor : MonoBehaviour
{
    [Header("設定")]
    public string jsonFileName = "robot_plan.json";
    public string urIP = "192.168.31.225";

    private URPackageListener urListener;
    private RobotPlan plan;

    void Start()
    {
        urListener = new URPackageListener();
        urListener.Connect(urIP);
        Debug.Log("嘗試連線到 " + urIP);
    }

    void StartLLMPlanner()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "run",
                WorkingDirectory = @"C:\Users\ASUS\LLM_RobotArm\csharp_server",
                UseShellExecute = true,
                CreateNoWindow = false
            };
            System.Diagnostics.Process.Start(psi);
            Debug.Log("LLM Planner 已啟動");
        }
        catch (System.Exception e)
        {
            Debug.LogError("啟動 LLM Planner 失敗: " + e.Message);
        }
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

        // 如果是 C 的高階格式，展開成 action_sequence
        if ((plan.action_sequence == null || plan.action_sequence.Count == 0) && !string.IsNullOrEmpty(plan.action))
        {
            plan.action_sequence = ExpandAction(plan);
            Debug.Log($"展開動作：{plan.action} → {plan.action_sequence.Count} 步");
        }

        Debug.Log($"載入任務");

        if (!urListener.Connected)
        {
            Debug.LogWarning("尚未連線到 UR，嘗試重新連線");
            urListener.Connect(urIP);
        }

        // 先回到 home 位置
        urListener.SendCommand("movej([0, -1.5708, 0, -1.5708, 0, 0], a=1.2, v=1.05)");
        Debug.Log("回到 home");

        StartCoroutine(ExecutePlan());
    }

    List<RobotAction> ExpandAction(RobotPlan p)
    {
        var seq = new List<RobotAction>();

        // 公分轉公尺
        float ox = p.object_position != null ? p.object_position.x / 1000f + 0.2f : 0;
        float oy = p.object_position != null ? p.object_position.y / 1000f : 0;
        float oz = p.object_position != null ? p.object_position.z / 100f + 0.2f : 0.2f;

        float tx = p.target_position != null ? p.target_position.x / 1000f + 0.2f : 0;
        float ty = p.target_position != null ? p.target_position.y / 1000f : 0;
        float tz = p.target_position != null ? p.target_position.z / 100f + 0.2f : 0.2f;

        if (p.action == "pick_and_place")
        {
            // 物件上方
            seq.Add(MakeMove(ox, oy, oz + 0.1f));
            // 物件位置
            seq.Add(MakeMove(ox, oy, oz));
            // 夾取
            seq.Add(new RobotAction { action = "grasp" });
            // 物件上方
            seq.Add(MakeMove(ox, oy, oz + 0.1f));
            // 中間 home（強制走長路徑）
            seq.Add(new RobotAction { action = "home" });
            // 目標上方
            seq.Add(MakeMove(tx, ty, tz + 0.1f));
            // 目標位置
            seq.Add(MakeMove(tx, ty, tz));
            // 放開
            seq.Add(new RobotAction { action = "release" });
            // 目標上方
            seq.Add(MakeMove(tx, ty, tz + 0.1f));
        }
        else if (p.action == "move_relative")
        {
            // 簡化版本，先不支援
            Debug.LogWarning("move_relative 尚未支援");
        }

        return seq;
    }

    RobotAction MakeMove(float x, float y, float z)
    {
        return new RobotAction
        {
            action = "move_to",
            position = new Position { x = x, y = y, z = z }
        };
    }

    IEnumerator ExecutePlan()
    {
        Debug.Log($"=== 開始執行，共 {plan.action_sequence.Count} 步 ===");

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
                    string cmd = $"movej(get_inverse_kin(p[{act.position.x.ToString("F4")}, {act.position.y.ToString("F4")}, {act.position.z.ToString("F4")}, 3.14, 0, 0]), a=1.2, v=1.05)";
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
            else if (act.action == "home")
            {
                urListener.SendCommand("movej([0, -1.5708, 0, -1.5708, 0, 0], a=1.2, v=1.05)");
                Debug.Log("SEND: home");
                yield return new WaitForSeconds(3f);
            }
        }

        Debug.Log("=== 任務完成 ===");
    }
}