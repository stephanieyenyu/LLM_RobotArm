using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using System.IO;
using System;
using System.Linq;
using System.Text;
using Assets.Scripts;

// -----------------------------------------------------------------
// Layer 4：Executor（Unity 端）
// 分層架構下的角色簡化了：
//   - 持續 poll current_step.json
//   - 拿到新 step_id → 展開成 12 步 URScript 執行
//   - 執行完寫 step_done.json 回報結果
//   - 讀到 {"done": true} 就停 poll
// 不再一次讀 batch plan，也不再需要按 Space。
// -----------------------------------------------------------------

[System.Serializable]
public class Position
{
    public float x, y, z;
}

[System.Serializable]
public class NamedPosition
{
    public string name;
    public float x, y, z;
    public string shape;
    public string orientation;
    public float skew_deg;
}

[System.Serializable]
public class StepEnvelope
{
    public int step_id;
    public bool done;
    public NamedPosition source_position;
    public NamedPosition target_position;
    public string comment;
}

[System.Serializable]
public class StepDoneReport
{
    public int step_id;
    public bool completed;
    public string error;
    public float duration_sec;
}

public class JsonExecutor : MonoBehaviour
{
    [Header("設定")]
    public string currentStepFile = "current_step.json";
    public string stepDoneFile = "step_done.json";
    public string urIP = "192.168.50.204";
    public float pollIntervalSec = 0.3f;

    [Header("Perception Server")]
    public string perceptionModeUrl = "http://localhost:5000/scene/mode";

    [Header("UI（用於顯示訊息，可留空）")]
    public UIManager uiManager;

    // QR1 在 UR3 基座座標系的位置（Teach Pendant 量測值，單位公尺）
    private const float QR1_X = -0.36552f;
    private const float QR1_Y = -0.40836f;
    private const float QR1_Z = 0.035f;

    private const float SAFE_Z_OFFSET = 0.08f;
    private const float Z_CORRECTION = 0.02f;
    private const float TRAVEL_Z_ABOVE_WORKSPACE = 0.15f;
    private const float SKEW_SIGN = 1f;

    // Home 姿態的關節角度（rad）— [base, shoulder, elbow, wrist1, wrist2, wrist3]
    // 改這一行就同時影響「每步結束回 home」跟「UI 按 Home」
    private const string HOME_MOVEJ_CMD = "movej([-1.5708, -1.5708, 0, -1.5708, 0, 0], a=1.2, v=0.8)";

    private URPackageListener urListener;
    private int lastExecutedStepId = -1;

    // 追蹤目前跑的 ExecuteStep coroutine 跟 step_id，讓 Home 按鈕能中斷
    private Coroutine currentStepCoroutine;
    private int currentStepId = -1;

    void Start()
    {
        urListener = new URPackageListener();
        urListener.Connect(urIP);
        Debug.Log("嘗試連線到 " + urIP);

        StartCoroutine(PollLoop());
    }

    void OnDestroy()
    {
        urListener?.Close();
    }

    // 相容舊 UIManager 的空 stub：新架構下 Executor 是自動 poll、不需要手動觸發。
    // UIManager 收到「plan 檔出現」還是會呼叫這個，但實際執行由 PollLoop 驅動。
    public void LoadAndExecute()
    {
        Debug.Log("[Executor] LoadAndExecute() 已無作用（分層架構下 executor 自動 poll current_step.json）");
    }

    // -----------------------------------------------------------
    // UI 手動控制按鈕：即時鬆開夾爪、夾緊夾爪、回 home
    // -----------------------------------------------------------
    public void ReleaseGripper()
    {
        if (urListener == null || !urListener.Connected)
        {
            Debug.LogWarning("[Executor] UR 未連線，Release 忽略");
            return;
        }
        urListener.SendCommand("set_standard_digital_out(4, False)");
        Debug.Log("[Executor] 手動鬆開夾爪");
    }

    public void GripGripper()
    {
        if (urListener == null || !urListener.Connected)
        {
            Debug.LogWarning("[Executor] UR 未連線，Grip 忽略");
            return;
        }
        urListener.SendCommand("set_standard_digital_out(4, True)");
        Debug.Log("[Executor] 手動夾緊夾爪");
    }

    public void GoHome()
    {
        if (urListener == null || !urListener.Connected)
        {
            Debug.LogWarning("[Executor] UR 未連線，Home 忽略");
            return;
        }

        // 1. 停掉正在跑的 ExecuteStep（如果有），並回報這步失敗給 csharp_server
        if (currentStepCoroutine != null)
        {
            int abortedStepId = currentStepId;
            StopCoroutine(currentStepCoroutine);
            currentStepCoroutine = null;
            currentStepId = -1;
            WriteStepDone(abortedStepId, false, "aborted by user (GoHome)", 0f);
            Debug.LogWarning($"[Executor] 中斷 step {abortedStepId}（使用者按 Home）");

            // perception 切回 idle，讓 SceneSyncer 可以刷新
            StartCoroutine(SetPerceptionMode("idle"));
        }

        // 2. 送 home 指令（會蓋掉當下任何 movej）
        string homeCmd = HOME_MOVEJ_CMD;
        urListener.SendCommand(homeCmd);
        Debug.Log("[Executor] 手動回 home: " + homeCmd);
    }

    // --- 主 poll loop：抓 current_step.json 有沒有新 step_id ---
    IEnumerator PollLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(pollIntervalSec);

            string path = Path.Combine(Application.streamingAssetsPath, currentStepFile);
            if (!File.Exists(path)) continue;

            StepEnvelope env;
            try
            {
                env = JsonUtility.FromJson<StepEnvelope>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Executor] step json parse failed: {ex.Message}");
                continue;
            }

            if (env == null || env.step_id == lastExecutedStepId) continue;

            if (env.done)
            {
                Debug.Log($"[Executor] 收到 done 信號 (step {env.step_id})，任務結束");
                lastExecutedStepId = env.step_id;
                continue;
            }

            if (env.source_position == null || env.target_position == null)
            {
                Debug.LogWarning($"[Executor] step {env.step_id} 缺 source/target");
                continue;
            }

            lastExecutedStepId = env.step_id;
            currentStepId = env.step_id;
            currentStepCoroutine = StartCoroutine(ExecuteStep(env));
            yield return currentStepCoroutine;
            currentStepCoroutine = null;
            currentStepId = -1;
        }
    }

    // --- 執行單一 step（1 顆 pick_and_place = 12 URScript movej + grasp/release）---
    IEnumerator ExecuteStep(StepEnvelope env)
    {
        Debug.Log($"═══ Step {env.step_id} ═══  {env.comment}");

        // 等連線
        float waited = 0f;
        while (!urListener.Connected && waited < 3f)
        {
            yield return new WaitForSeconds(0.1f);
            waited += 0.1f;
        }
        if (!urListener.Connected)
        {
            Debug.LogError("無法連線到 UR");
            WriteStepDone(env.step_id, false, "UR 未連線", 0f);
            yield break;
        }

        // 通知 perception 進入 executing → SceneSyncer 凍結畫面
        yield return StartCoroutine(SetPerceptionMode("executing"));

        // 座標換算：QR 平面 → UR3 base
        float ox = QR1_X + env.source_position.x;
        float oy = QR1_Y + env.source_position.y;
        float oz = QR1_Z + env.source_position.z + Z_CORRECTION;
        float tx = QR1_X + env.target_position.x;
        float ty = QR1_Y + env.target_position.y;
        float tz = QR1_Z + env.target_position.z + Z_CORRECTION;
        float travelZ = QR1_Z + TRAVEL_Z_ABOVE_WORKSPACE;

        string srcOri = env.source_position.orientation ?? "";
        string tgtOri = env.target_position.orientation ?? "";
        float srcSkew = env.source_position.skew_deg;
        float tgtSkew = env.target_position.skew_deg;

        var t0 = Time.realtimeSinceStartup;

        // 12 步 pick_and_place
        yield return SendMove(ox, oy, travelZ, srcOri, srcSkew, "1/12 approach travel");
        yield return SendMove(ox, oy, oz + SAFE_Z_OFFSET, srcOri, srcSkew, "2/12 safe above source");
        yield return SendMove(ox, oy, oz, srcOri, srcSkew, "3/12 descend to source");
        yield return SendGrasp();
        yield return SendMove(ox, oy, oz + SAFE_Z_OFFSET, srcOri, srcSkew, "5/12 lift");
        yield return SendMove(ox, oy, travelZ, srcOri, srcSkew, "6/12 travel above");
        yield return SendMove(tx, ty, travelZ, tgtOri, tgtSkew, "7/12 traverse to target above");
        yield return SendMove(tx, ty, tz + SAFE_Z_OFFSET, tgtOri, tgtSkew, "8/12 safe above target");
        yield return SendMove(tx, ty, tz, tgtOri, tgtSkew, "9/12 descend to target");
        yield return SendRelease();
        yield return SendMove(tx, ty, tz + SAFE_Z_OFFSET, tgtOri, tgtSkew, "11/12 lift");
        yield return SendMove(tx, ty, travelZ, tgtOri, tgtSkew, "12/12 travel above");

        // 回 home
        string homeCmd = HOME_MOVEJ_CMD;
        urListener.SendCommand(homeCmd);
        yield return new WaitForSeconds(3f);

        // 通知 perception idle → SceneSyncer 抓一次最新場景
        yield return StartCoroutine(SetPerceptionMode("idle"));

        // 等 perception 有時間掃到最新狀態（stabilize 需要幾幀）
        yield return new WaitForSeconds(1.5f);

        float duration = Time.realtimeSinceStartup - t0;
        WriteStepDone(env.step_id, true, null, duration);
        Debug.Log($"═══ Step {env.step_id} 完成 ({duration:F1}s) ═══");
    }

    IEnumerator SendMove(float x, float y, float z, string orientation, float skewDeg, string tag)
    {
        string cmd = BuildMovejLine(x, y, z, orientation, skewDeg);
        Debug.Log($"  [{tag}] SEND: {cmd}");
        urListener.SendCommand(cmd);
        yield return new WaitForSeconds(3f);
    }

    IEnumerator SendGrasp()
    {
        Debug.Log("  [grasp] SEND: set_standard_digital_out(4, True)");
        urListener.SendCommand("set_standard_digital_out(4, True)");
        yield return new WaitForSeconds(1.5f);
    }

    IEnumerator SendRelease()
    {
        Debug.Log("  [release] SEND: set_standard_digital_out(4, False)");
        urListener.SendCommand("set_standard_digital_out(4, False)");
        yield return new WaitForSeconds(1.5f);
    }

    string BuildMovejLine(float x, float y, float z, string orientation, float skewDeg)
    {
        bool rotate = orientation != "horizontal";
        float totalDeg = rotate ? 90f + (SKEW_SIGN * skewDeg) : (SKEW_SIGN * skewDeg);
        float totalRad = totalDeg * Mathf.Deg2Rad;

        string qnear = rotate
            ? "[0, -1.5708, 1.5708, -1.5708, -1.5708, 1.5708]"
            : "[0, -1.5708, 1.5708, -1.5708, -1.5708, 0]";

        if (Mathf.Abs(totalDeg) > 0.01f)
        {
            return $"movej(get_inverse_kin(pose_trans(p[{x:F4}, {y:F4}, {z:F4}, 0, 3.14, 0], p[0, 0, 0, 0, 0, {totalRad:F4}]), qnear={qnear}), a=1.2, v=0.8)";
        }
        return $"movej(get_inverse_kin(p[{x:F4}, {y:F4}, {z:F4}, 0, 3.14, 0], qnear={qnear}), a=1.2, v=0.8)";
    }

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
                Debug.LogWarning($"[perception mode] {mode} 失敗：{req.error}");
        }
    }

    void WriteStepDone(int stepId, bool completed, string error, float duration)
    {
        var report = new StepDoneReport
        {
            step_id = stepId,
            completed = completed,
            error = error ?? "",
            duration_sec = duration,
        };
        string json = JsonUtility.ToJson(report, prettyPrint: true);
        string path = Path.Combine(Application.streamingAssetsPath, stepDoneFile);
        File.WriteAllText(path, json);
    }
}
