using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using System.IO;
using System;
using System.Linq;
using System.Text;
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
    public string shape;         // "cube" / "domino"（未提供時為空字串）
    public string orientation;   // "horizontal" / "vertical" / ""（cube 或未提供）
}

[System.Serializable]
public class RobotAction
{
    public string action;
    public Position position;
    public JointAngles joints;
    // move_to 專用：wrist 旋轉依據
    //   ""/"vertical"    → 預設 pose (0, 3.14, 0)，夾爪開口沿 Base X
    //   "horizontal"     → 加轉 90° 圍 tool Z，夾爪開口沿 Base Y
    public string orientation;
}

[System.Serializable]
public class PlacementStep
{
    public NamedPosition source_position;
    public NamedPosition target_position;
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
    public string error_message;

    // arrange_pattern 專用
    public string pattern;
    public string block_color;
    public List<PlacementStep> placement_steps;
}

public class JsonExecutor : MonoBehaviour
{
    [Header("設定")]
    public string jsonFileName = "robot_plan.json";
    public string urIP = "192.168.50.204";

    [Header("Perception Server")]
    // 執行手臂動作前後會 POST 這裡，通知 SceneSyncer 凍結 / 解凍畫面
    public string perceptionModeUrl = "http://localhost:5000/scene/mode";

    [Header("UI（用於顯示 error 訊息，可留空）")]
    public UIManager uiManager;

    // QR1 在 UR3 基座座標系的位置（Teach Pendant 量測值，單位公尺）
    // 這是手臂 TCP 移到 QR1 正上方 5cm 時的座標
    private const float QR1_X = -0.31404f;
    private const float QR1_Y = -0.40606f;
    private const float QR1_Z = 0.0345f;   //TCP_Z(0.05974) - 0.05

    // 工作時的安全高度（在物件上方多少公尺）
    private const float SAFE_Z_OFFSET = 0.08f;

    // 深度感測 z 值系統性偏低（USB 2.1 低解析度 + 光滑面 IR 亂反射），
    // 補一個固定 offset 讓爪尖不要撞下去
    private const float Z_CORRECTION = 0.02f;

    // 高空巡航高度（相對 QR1 平面）：所有物件之間的水平飛行都走這個高度
    // 手臂會先升到這裡再水平移動、再下降，強制走「直角軌跡」不是斜線
    // 15cm 遠高於任何常見物件（積木 5cm、杯子 10cm）
    private const float TRAVEL_Z_ABOVE_WORKSPACE = 0.15f;

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

        if (plan == null)
        {
            Debug.LogError("JSON 解析失敗，plan 是 null");
            return;
        }

        // error action：不執行任何動作、直接印警告
        if (plan.action == "error")
        {
            Debug.LogWarning($"[LLM] 指令無法執行：{plan.error_message}");
            return;
        }

        if ((plan.action_sequence == null || plan.action_sequence.Count == 0)
            && !string.IsNullOrEmpty(plan.action))
        {
            if (plan.action == "arrange_pattern")
            {
                plan.action_sequence = ExpandArrangePattern(plan);
                int stepCount = plan.placement_steps != null ? plan.placement_steps.Count : 0;
                Debug.Log($"展開 arrange_pattern（pattern={plan.pattern}, color={plan.block_color}, {stepCount} 顆積木）→ {plan.action_sequence.Count} 步");
            }
            else
            {
                plan.action_sequence = ExpandAction(plan);
                Debug.Log($"展開動作：{plan.action} → {plan.action_sequence.Count} 步");
            }
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
        //   x = QR1 → QR2 方向，也就是 QR +X
        //   y = QR1 → QR3 方向，也就是 QR +Y
        //   z = 高度，也就是 QR +Z
        //
        // 由 URSim 測得的座標軸對應：
        //   QR +X 對應 UR3 +X
        //   QR +Y 對應 UR3 +Y
        //   QR +Z 對應 UR3 +Z

        float obj_qr_x = p.object_position != null ? p.object_position.x : 0f; // QR1 → QR2
        float obj_qr_y = p.object_position != null ? p.object_position.y : 0f; // QR1 → QR3
        float obj_qr_z = p.object_position != null ? p.object_position.z : 0f; // height

        float tgt_qr_x = p.target_position != null ? p.target_position.x : obj_qr_x;
        float tgt_qr_y = p.target_position != null ? p.target_position.y : obj_qr_y;
        float tgt_qr_z = p.target_position != null ? p.target_position.z : obj_qr_z;

        // QR coordinate → UR3 base coordinate
        // UR base 的 X, Y 軸與 QR 平面同向：
        //   QR +X (QR1→QR2) = UR +X
        //   QR +Y (QR1→QR3) = UR +Y
        //   QR +Z            = UR +Z

        float ox = QR1_X + obj_qr_x;
        float oy = QR1_Y + obj_qr_y;
        float oz = QR1_Z + obj_qr_z + Z_CORRECTION;

        float tx = QR1_X + tgt_qr_x;
        float ty = QR1_Y + tgt_qr_y;
        float tz = QR1_Z + tgt_qr_z + Z_CORRECTION;

        Debug.Log($"物件 UR3 座標：({ox:F4}, {oy:F4}, {oz:F4})");
        Debug.Log($"目標 UR3 座標：({tx:F4}, {ty:F4}, {tz:F4})");

        if (p.action == "pick_and_place" || p.action == "move_relative" || p.action == "place_relative")
        {
            // 巡航高度 = QR1_Z + TRAVEL_Z_ABOVE_WORKSPACE，所有水平移動走這個高度
            float travelZ = QR1_Z + TRAVEL_Z_ABOVE_WORKSPACE;

            // Wrist orientation：pick 時用物件本身的 orientation（source），place 時用目標的
            //（pick_and_place / move_relative / place_relative 沒改變形狀，通常 src == tgt）
            string srcOri = p.object_position != null ? p.object_position.orientation : "";
            string tgtOri = p.target_position != null ? p.target_position.orientation : srcOri;

            seq.Add(MakeMove(ox, oy, travelZ, srcOri));              // ① 到物件上方高空巡航點（純水平）
            seq.Add(MakeMove(ox, oy, oz + SAFE_Z_OFFSET, srcOri));   // ② 垂直下降到 SAFE_Z（同 x,y）
            seq.Add(MakeMove(ox, oy, oz, srcOri));                    // ③ 垂直下降到物件（同 x,y）
            seq.Add(new RobotAction { action = "grasp" });            // ④ 夾取
            seq.Add(MakeMove(ox, oy, oz + SAFE_Z_OFFSET, srcOri));   // ⑤ 垂直上升
            seq.Add(MakeMove(ox, oy, travelZ, srcOri));               // ⑥ 垂直上升到巡航高度
            seq.Add(MakeMove(tx, ty, travelZ, tgtOri));               // ⑦ 純水平飛到目標上空（同時把 wrist 轉到目標 orientation）
            seq.Add(MakeMove(tx, ty, tz + SAFE_Z_OFFSET, tgtOri));   // ⑧ 垂直下降到 SAFE_Z
            seq.Add(MakeMove(tx, ty, tz, tgtOri));                    // ⑨ 垂直下降到目標
            seq.Add(new RobotAction { action = "release" });          // ⑩ 放開
            seq.Add(MakeMove(tx, ty, tz + SAFE_Z_OFFSET, tgtOri));   // ⑪ 垂直上升
            seq.Add(MakeMove(tx, ty, travelZ, tgtOri));               // ⑫ 垂直上升到巡航高度
        }
        else
        {
            Debug.LogWarning($"未知 action：{p.action}");
        }

        return seq;
    }

    RobotAction MakeMove(float x, float y, float z, string orientation = null)
    {
        return new RobotAction
        {
            action = "move_to",
            position = new Position { x = x, y = y, z = z },
            orientation = orientation ?? "",
        };
    }

    // arrange_pattern 展開：把 N 個 PlacementStep 各自展成 8 步 pick_and_place
    // 共 N × 8 個動作，依序執行
    List<RobotAction> ExpandArrangePattern(RobotPlan p)
    {
        var seq = new List<RobotAction>();

        if (p.placement_steps == null || p.placement_steps.Count == 0)
        {
            Debug.LogWarning("arrange_pattern 沒有 placement_steps");
            return seq;
        }

        int index = 0;
        foreach (var step in p.placement_steps)
        {
            index++;

            if (step.source_position == null || step.target_position == null)
            {
                Debug.LogWarning($"第 {index} 步 placement_step 缺 source 或 target，跳過");
                continue;
            }

            // QR 平面座標 → UR3 base 座標，比照現有 ExpandAction 的公式
            float ox = QR1_X + step.source_position.x;
            float oy = QR1_Y + step.source_position.y;
            float oz = QR1_Z + step.source_position.z + Z_CORRECTION;

            float tx = QR1_X + step.target_position.x;
            float ty = QR1_Y + step.target_position.y;
            float tz = QR1_Z + step.target_position.z + Z_CORRECTION;

            Debug.Log($"[step {index}] 積木 UR3 座標 ({ox:F4}, {oy:F4}, {oz:F4}) → 擺放 ({tx:F4}, {ty:F4}, {tz:F4})  " +
                      $"[src:{step.source_position.orientation ?? "-"} → tgt:{step.target_position.orientation ?? "-"}]");

            // 巡航高度，強制走直角軌跡（升 → 平飛 → 降）
            float travelZ = QR1_Z + TRAVEL_Z_ABOVE_WORKSPACE;

            // pick 時 wrist 對源 orientation（perception 偵測到 domino 的方向）
            // place 時 wrist 對目標 orientation（PlacementPlanner packing 決定的方向）
            string srcOri = step.source_position.orientation ?? "";
            string tgtOri = step.target_position.orientation ?? "";

            seq.Add(MakeMove(ox, oy, travelZ, srcOri));              // ① 積木上方巡航點（純水平飛過來）
            seq.Add(MakeMove(ox, oy, oz + SAFE_Z_OFFSET, srcOri));   // ② 垂直下降
            seq.Add(MakeMove(ox, oy, oz, srcOri));                    // ③ 垂直下降到積木
            seq.Add(new RobotAction { action = "grasp" });            // ④ 夾取
            seq.Add(MakeMove(ox, oy, oz + SAFE_Z_OFFSET, srcOri));   // ⑤ 垂直上升
            seq.Add(MakeMove(ox, oy, travelZ, srcOri));               // ⑥ 上升到巡航高度
            seq.Add(MakeMove(tx, ty, travelZ, tgtOri));               // ⑦ 純水平飛到目標上空（同時 wrist 轉到目標方向）
            seq.Add(MakeMove(tx, ty, tz + SAFE_Z_OFFSET, tgtOri));   // ⑧ 垂直下降
            seq.Add(MakeMove(tx, ty, tz, tgtOri));                    // ⑨ 垂直下降到目標
            seq.Add(new RobotAction { action = "release" });          // ⑩ 放開
            seq.Add(MakeMove(tx, ty, tz + SAFE_Z_OFFSET, tgtOri));   // ⑪ 垂直上升
            seq.Add(MakeMove(tx, ty, travelZ, tgtOri));               // ⑫ 上升到巡航高度（供下一個 step 銜接）
        }

        return seq;
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

        // 通知 perception 進入執行狀態 → SceneSyncer 會凍結畫面
        yield return StartCoroutine(SetPerceptionMode("executing"));

        // ------------------------------------------------------------------
        // 把整個 action_sequence 打包成一支 URScript program，一次送出。
        // 因為 UR 的 primary interface（port 30002）每次 SendCommand 都當成
        // 「新 program」執行，會中斷前一個 movej。以前一步一步送，很容易在
        // movej 還沒完成時就被下一步（例如 set_digital_out）打斷，導致夾爪
        // 在錯的位置關閉、抓不到 cube。
        //
        // 打包成一支 program 後，URScript 內部會嚴格依序執行：
        //   movej(...)  ← 阻塞直到手臂到達目標
        //   set_standard_digital_out(...)  ← 到達後才觸發
        //   movej(...)  ← 依序繼續
        // ------------------------------------------------------------------

        Debug.Log($"=== 開始執行，共 {plan.action_sequence.Count} 步 ===");

        int total = plan.action_sequence.Count;

        for (int i = 0; i < total; i++)
        {
            var act = plan.action_sequence[i];
            int stepNo = i + 1;

            if (act.action == "move_to" && act.position != null)
            {
                string cmd = BuildMovejLine(act.position.x, act.position.y, act.position.z, act.orientation);
                string oriTag = string.IsNullOrEmpty(act.orientation) ? "-" : act.orientation;
                Debug.Log($"[{stepNo}/{total}] move_to ({act.position.x:F4}, {act.position.y:F4}, {act.position.z:F4}) ori={oriTag}");
                Debug.Log($"    SEND: {cmd}");
                urListener.SendCommand(cmd);
                yield return new WaitForSeconds(3f);
            }
            else if (act.action == "grasp")
            {
                Debug.Log($"[{stepNo}/{total}] grasp");
                Debug.Log("    SEND: set_standard_digital_out(4, True)");
                urListener.SendCommand("set_standard_digital_out(4, True)");
                yield return new WaitForSeconds(1.5f);
            }
            else if (act.action == "release")
            {
                Debug.Log($"[{stepNo}/{total}] release");
                Debug.Log("    SEND: set_standard_digital_out(4, False)");
                urListener.SendCommand("set_standard_digital_out(4, False)");
                yield return new WaitForSeconds(1.5f);
            }
        }

        // 任務結束後回 home
        string homeCmd = "movej([0, -1.5708, 0, -1.5708, 0, 0], a=1.2, v=0.8)";
        Debug.Log($"[home] SEND: {homeCmd}");
        urListener.SendCommand(homeCmd);
        yield return new WaitForSeconds(3f);

        // 通知 perception 恢復 idle → SceneSyncer 會再抓一次 /scene 更新最終位置
        yield return StartCoroutine(SetPerceptionMode("idle"));

        Debug.Log("=== 任務完成 ===");
    }

    // 產生單一 movej URScript 行（用 IK 從 TCP pose 求 joint angles）
    // 實測夾爪安裝方向後，orientation 對 wrist 旋轉的關係：
    //   "horizontal"             → 預設 pose [0, 3.14, 0]，開口沿 Base X 軸
    //                              → 適合抓「長軸沿 X 的 horizontal domino」
    //   "" / null / "vertical"  → 用 pose_trans 加轉 90° 圍 tool Z，開口沿 Base Y 軸
    //                              → 適合抓 cube（任意方向皆可）或「長軸沿 Y 的 vertical domino」
    // qnear 的 wrist_3 也跟著調，讓 IK 選到對的解
    string BuildMovejLine(float x, float y, float z, string orientation)
    {
        bool rotate = orientation != "horizontal";
        string qnear = rotate
            ? "[0, -1.5708, 1.5708, -1.5708, -1.5708, 1.5708]"
            : "[0, -1.5708, 1.5708, -1.5708, -1.5708, 0]";

        if (rotate)
        {
            // pose_trans(A, B) = A 作用 B（B 在 A 的 local frame）→ 在下降 pose 上再繞 tool Z 轉 90°
            return $"movej(get_inverse_kin(pose_trans(p[{x:F4}, {y:F4}, {z:F4}, 0, 3.14, 0], p[0, 0, 0, 0, 0, 1.5708]), qnear={qnear}), a=1.2, v=0.8)";
        }
        return $"movej(get_inverse_kin(p[{x:F4}, {y:F4}, {z:F4}, 0, 3.14, 0], qnear={qnear}), a=1.2, v=0.8)";
    }

    // 通知 perception_server 現在的執行狀態，讓 SceneSyncer 決定要不要更新 Unity 場景
    IEnumerator SetPerceptionMode(string mode)
    {
        string json = "{\"mode\":\"" + mode + "\"}";
        byte[] body = Encoding.UTF8.GetBytes(json);
        using (UnityWebRequest req = new UnityWebRequest(perceptionModeUrl, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 3;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
                Debug.LogWarning($"[perception mode] 設 {mode} 失敗：{req.error}");
            else
                Debug.Log($"[perception mode] → {mode}");
        }
    }
}