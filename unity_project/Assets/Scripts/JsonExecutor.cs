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
    public string action;
    public string @object;
    public string target;
    public string direction;
    public float? distance_cm;
    public NamedPosition object_position;
    public NamedPosition target_position;
    public List<RobotAction> action_sequence;
}

public class JsonExecutor : MonoBehaviour
{
    [Header("設定")]
    public string jsonFileName = "robot_plan.json";
    public string urIP = "192.168.31.237";

    // QR1 在 UR3 基座座標系的位置（Teach Pendant 量測值，單位公尺）
    // 這是手臂 TCP 移到 QR1 正上方 5cm 時的座標
    private const float QR1_X = -0.12264f;
    private const float QR1_Y = -0.44734f;
    private const float QR1_Z = -0.32492f;

    // 工作時的安全高度（在物件上方多少公尺）
    private const float SAFE_Z_OFFSET = 0.08f;

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

        if ((plan.action_sequence == null || plan.action_sequence.Count == 0)
            && !string.IsNullOrEmpty(plan.action))
        {
            plan.action_sequence = ExpandAction(plan);
            Debug.Log($"展開動作：{plan.action} → {plan.action_sequence.Count} 步");
        }

        Debug.Log("載入任務");

        if (!urListener.Connected)
        {
            Debug.LogWarning("尚未連線到 UR，嘗試重新連線");
            urListener.Connect(urIP);
        }

        StartCoroutine(ExecutePlan());
    }

    List<RobotAction> ExpandAction(RobotPlan p)
    {
        var seq = new List<RobotAction>();

        // Part B 的 object_position / target_position 單位是公尺
        // QR 工作平面座標：
        //   x = QR1 → QR2 方向
        //   y = QR1 → QR3 方向
        //   z = 高度
        //
        // 目前假設：
        //   QR1 → QR2 對應 UR3 的 Y 方向
        //   QR1 → QR3 對應 UR3 的 X 方向
        //   高度對應 UR3 的 Z 方向

        float obj_qr_x = p.object_position != null ? p.object_position.x : 0f; // QR1 → QR2
        float obj_qr_y = p.object_position != null ? p.object_position.y : 0f; // QR1 → QR3
        float obj_qr_z = p.object_position != null ? p.object_position.z : 0f; // height

        float tgt_qr_x = p.target_position != null ? p.target_position.x : obj_qr_x;
        float tgt_qr_y = p.target_position != null ? p.target_position.y : obj_qr_y;
        float tgt_qr_z = p.target_position != null ? p.target_position.z : obj_qr_z;

        // QR coordinate → UR3 base coordinate
        float ox = QR1_X + obj_qr_x;
        float oy = QR1_Y + obj_qr_y;
        float oz = QR1_Z + obj_qr_z;

        float tx = QR1_X + tgt_qr_x;
        float ty = QR1_Y + tgt_qr_y;
        float tz = QR1_Z + tgt_qr_z;

        Debug.Log($"物件 UR3 座標：({ox:F4}, {oy:F4}, {oz:F4})");
        Debug.Log($"目標 UR3 座標：({tx:F4}, {ty:F4}, {tz:F4})");

        if (p.action == "pick_and_place" || p.action == "move_relative")
        {
            seq.Add(MakeMove(ox, oy, oz + SAFE_Z_OFFSET));  // 物件上方
            seq.Add(MakeMove(ox, oy, oz));                   // 物件位置
            seq.Add(new RobotAction { action = "grasp" });   // 夾取
            seq.Add(MakeMove(ox, oy, oz + SAFE_Z_OFFSET));  // 拿起
            seq.Add(MakeMove(tx, ty, tz + SAFE_Z_OFFSET));  // 目標上方
            seq.Add(MakeMove(tx, ty, tz));                   // 目標位置
            seq.Add(new RobotAction { action = "release" }); // 放開
            seq.Add(MakeMove(tx, ty, tz + SAFE_Z_OFFSET));  // 完成上方
        }
        else
        {
            Debug.LogWarning($"未知 action：{p.action}");
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
        // 等待連線建立（最多 3 秒）
        float waited = 0f;
        while (!urListener.Connected && waited < 3f)
        {
            yield return new WaitForSeconds(0.1f);
            waited += 0.1f;
        }

        if (!urListener.Connected)
        {
            Debug.LogError("無法連線到 UR，請確認 IP 和網路設定");
            yield break;
        }

        Debug.Log($"=== 開始執行，共 {plan.action_sequence.Count} 步 ===");

        for (int i = 0; i < plan.action_sequence.Count; i++)
        {
            var act = plan.action_sequence[i];
            Debug.Log($"[{i + 1}/{plan.action_sequence.Count}] {act.action}");

            if (act.action == "move_to" && act.position != null)
            {
                string cmd = $"movej(get_inverse_kin(p[{act.position.x:F4}, {act.position.y:F4}, {act.position.z:F4}, 0, 3.14, 0], qnear=[1.5708, -1.5708, 1.5708, -1.5708, -1.5708, 0]), a=0.5, v=0.3)";
                urListener.SendCommand(cmd);
                Debug.Log("SEND: " + cmd);
                yield return new WaitForSeconds(5f);
            }
            else if (act.action == "grasp")
            {
                urListener.SendCommand("set_standard_digital_out(4, True)");
                Debug.Log("SEND: grasp");
                yield return new WaitForSeconds(2f);
            }
            else if (act.action == "release")
            {
                urListener.SendCommand("set_standard_digital_out(4, False)");
                Debug.Log("SEND: release");
                yield return new WaitForSeconds(2f);
            }
        }

        Debug.Log("=== 任務完成 ===");
    }
}
