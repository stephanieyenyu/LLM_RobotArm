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
// Layer 4嚗xecutor嚗nity 蝡荔?
// ?惜?嗆?銝?閫蝪∪?鈭?
//   - ?? poll current_step.json
//   - ?踹??step_id ??撅???12 甇?URScript ?瑁?
//   - ?瑁?摰神 step_done.json ?蝯?
//   - 霈??{"done": true} 撠勗? poll
// 銝?銝甈∟? batch plan嚗?銝??閬? Space??
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
    public List<RobotFunctionCall> action_sequence;
}

[System.Serializable]
public class RobotFunctionCall
{
    public string function;
    public string location;
    // JsonUtility does not support Nullable<T>; JSON null is read as 0.
    public float height_m;
    public float seconds;
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
    [Header("閮剖?")]
    public string currentStepFile = "current_step.json";
    public string stepDoneFile = "step_done.json";
    public string urIP = "192.168.50.204";
    public float pollIntervalSec = 0.3f;

    [Header("Perception Server")]
    public string perceptionModeUrl = "http://localhost:5000/scene/mode";

    [Header("UI嚗?潮＊蝷箄??荔??舐?蝛綽?")]
    public UIManager uiManager;

    // QR1 ??UR3 ?箏漣摨扳?蝟餌?雿蔭嚗each Pendant ?葫?潘??桐??砍偕嚗?
    private const float QR1_X = -0.36552f;
    private const float QR1_Y = -0.40836f;
    private const float QR1_Z = 0.035f;

    private const float SAFE_Z_OFFSET = 0.08f;
    private const float Z_CORRECTION = 0.02f;
    private const float TRAVEL_Z_ABOVE_WORKSPACE = 0.15f;
    private const float SKEW_SIGN = 1f;

    // Home 憪踵???蝭閫漲嚗ad嚗?[base, shoulder, elbow, wrist1, wrist2, wrist3]
    // ?寥?銵停??敶梢??甇亦??? home???I ??Home??
    private const string HOME_MOVEJ_CMD = "movej([-1.5708, -1.5708, 0, -1.5708, 0, 0], a=1.2, v=0.8)";

    private URPackageListener urListener;
    private int lastExecutedStepId = -1;

    // 餈質馱?桀?頝? ExecuteStep coroutine 頝?step_id嚗? Home ???賭葉??
    private Coroutine currentStepCoroutine;
    private int currentStepId = -1;

    void Start()
    {
        urListener = new URPackageListener();
        urListener.Connect(urIP);
        Debug.Log("?岫?????" + urIP);

        StartCoroutine(PollLoop());
    }

    void OnDestroy()
    {
        urListener?.Close();
    }

    // ?詨捆??UIManager ?征 stub嚗?嗆?銝?Executor ?航??poll???閬??孛?潦?
    // UIManager ?嗅?lan 瑼?整??舀??澆??雿祕?銵 PollLoop 撽???
    public void LoadAndExecute()
    {
        Debug.Log("[Executor] LoadAndExecute() 撌脩雿嚗?撅斗瑽? executor ?芸? poll current_step.json嚗?);
    }

    // -----------------------------------------------------------
    // UI ???批??嚗???冗?芥冗蝺冗?芥? home
    // -----------------------------------------------------------
    public void ReleaseGripper()
    {
        if (urListener == null || !urListener.Connected)
        {
            Debug.LogWarning("[Executor] UR ?芷??嚗elease 敹賜");
            return;
        }
        urListener.SendCommand("set_standard_digital_out(4, False)");
        Debug.Log("[Executor] ??擛?憭曄");
    }

    public void GripGripper()
    {
        if (urListener == null || !urListener.Connected)
        {
            Debug.LogWarning("[Executor] UR ?芷??嚗rip 敹賜");
            return;
        }
        urListener.SendCommand("set_standard_digital_out(4, True)");
        Debug.Log("[Executor] ??憭曄?憭曄");
    }

    public void GoHome()
    {
        if (urListener == null || !urListener.Connected)
        {
            Debug.LogWarning("[Executor] UR ?芷??嚗ome 敹賜");
            return;
        }

        // 1. ??甇?頝? ExecuteStep嚗???嚗?銝血??梢郊憭望?蝯?csharp_server
        if (currentStepCoroutine != null)
        {
            int abortedStepId = currentStepId;
            StopCoroutine(currentStepCoroutine);
            currentStepCoroutine = null;
            currentStepId = -1;
            WriteStepDone(abortedStepId, false, "aborted by user (GoHome)", 0f);
            Debug.LogWarning($"[Executor] 銝剜 step {abortedStepId}嚗蝙?刻? Home嚗?);

            // perception ?? idle嚗? SceneSyncer ?臭誑?瑟
            StartCoroutine(SetPerceptionMode("idle"));
        }

        // 2. ??home ?誘嚗????嗡?隞颱? movej嚗?
        string homeCmd = HOME_MOVEJ_CMD;
        urListener.SendCommand(homeCmd);
        Debug.Log("[Executor] ????home: " + homeCmd);
    }

    // --- 銝?poll loop嚗? current_step.json ??? step_id ---
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
                Debug.Log($"[Executor] ?嗅 done 靽∟? (step {env.step_id})嚗遙????);
                lastExecutedStepId = env.step_id;
                continue;
            }

            if (env.source_position == null || env.target_position == null)
            {
                Debug.LogWarning($"[Executor] step {env.step_id} 蝻?source/target");
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

    // --- ?瑁??桐? step嚗?雿?摨 LLM Motion Planner ??robot functions 瘙箏? ---
    IEnumerator ExecuteStep(StepEnvelope env)
    {
        Debug.Log($"????Step {env.step_id} ???? {env.comment}");

        // 蝑??
        float waited = 0f;
        while (!urListener.Connected && waited < 3f)
        {
            yield return new WaitForSeconds(0.1f);
            waited += 0.1f;
        }
        if (!urListener.Connected)
        {
            Debug.LogError("?⊥??????UR");
            WriteStepDone(env.step_id, false, "UR ?芷??", 0f);
            yield break;
        }

        // ? perception ?脣 executing ??SceneSyncer ???恍
        yield return StartCoroutine(SetPerceptionMode("executing"));

        // 摨扳???嚗R 撟喲 ??UR3 base
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

        if (env.action_sequence == null || env.action_sequence.Count == 0)
        {
            WriteStepDone(env.step_id, false, "missing action_sequence", 0f);
            yield return StartCoroutine(SetPerceptionMode("idle"));
            yield break;
        }

        // Unity only interprets a closed whitelist. Raw URScript and arbitrary coordinates
        // are deliberately not part of the JSON contract.
        for (int i = 0; i < env.action_sequence.Count; i++)
        {
            RobotFunctionCall action = env.action_sequence[i];
            string tag = $"{i + 1}/{env.action_sequence.Count} {action.function}";
            bool source = action.location == "source";
            float x = source ? ox : tx;
            float y = source ? oy : ty;
            float z = source ? oz : tz;
            string orientation = source ? srcOri : tgtOri;
            float skew = source ? srcSkew : tgtSkew;
            float height = Mathf.Clamp(action.height_m > 0f ? action.height_m : SAFE_Z_OFFSET, 0.05f, 0.15f);

            switch (action.function)
            {
                case "move_above":
                    // Use the configured travel plane for lateral motion, then the LLM-selected
                    // safe height. This preserves collision clearance while allowing variable plans.
                    yield return SendMove(x, y, travelZ, orientation, skew, tag + " travel");
                    yield return SendMove(x, y, z + height, orientation, skew, tag + " above");
                    break;
                case "descend":
                    yield return SendMove(x, y, z, orientation, skew, tag);
                    break;
                case "grasp":
                    yield return SendGrasp();
                    break;
                case "release":
                    yield return SendRelease();
                    break;
                case "lift":
                    yield return SendMove(x, y, z + height, orientation, skew, tag);
                    break;
                case "wait":
                    yield return new WaitForSeconds(Mathf.Clamp(action.seconds > 0f ? action.seconds : 0.5f, 0.1f, 3f));
                    break;
                case "go_home":
                    urListener.SendCommand(HOME_MOVEJ_CMD);
                    Debug.Log($"  [{tag}] SEND: {HOME_MOVEJ_CMD}");
                    yield return new WaitForSeconds(3f);
                    break;
                default:
                    WriteStepDone(env.step_id, false, "unknown robot function: " + action.function, 0f);
                    yield return StartCoroutine(SetPerceptionMode("idle"));
                    yield break;
            }
        }

        // ? perception idle ??SceneSyncer ??甈⊥??啣??
        yield return StartCoroutine(SetPerceptionMode("idle"));

        // 蝑?perception ?????唳??啁???stabilize ?閬嗾撟嚗?
        yield return new WaitForSeconds(1.5f);

        float duration = Time.realtimeSinceStartup - t0;
        WriteStepDone(env.step_id, true, null, duration);
        Debug.Log($"????Step {env.step_id} 摰? ({duration:F1}s) ????);
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
                Debug.LogWarning($"[perception mode] {mode} 憭望?嚗req.error}");
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

