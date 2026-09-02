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
// Layer 4（Executor）：Unity 端
// 完整 Batch Executor：poll batch_plan.json，收到後按陣列順序逐步執行，
// 任一步失敗即停止整批，最後寫 batch_done.json。
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

[System.Serializable]
public class BatchPlanEnvelope
{
    public int batch_id;
    public string created_at;
    public string scene_captured_at;
    public string comment;
    public List<StepEnvelope> steps;
}

[System.Serializable]
public class BatchDoneReport
{
    public int batch_id;
    public bool completed;
    public int completed_steps;
    public int total_steps;
    public int failed_step_id;
    public string error;
    public float duration_sec;
}

public class JsonExecutor : MonoBehaviour
{
    private static JsonExecutor activeInstance;
    [Header("設定")]
    public string batchPlanFile = "batch_plan.json";
    public string batchDoneFile = "batch_done.json";
    public string currentStepFile = "current_step.json"; // legacy report compatibility
    public string stepDoneFile = "step_done.json";
    public string urIP = "192.168.50.204";
    public float pollIntervalSec = 0.3f;

    [Header("Perception Server")]
    public string perceptionModeUrl = "http://localhost:5000/scene/mode";

    [Header("UI（保留既有按鈕相容性）")]
    public UIManager uiManager;

    // QR1 到 UR3 base 的座標偏移（以 Teach Pendant 實際校正值為準）
    private const float QR1_X = -0.38824f;
    private const float QR1_Y = -0.35973f+0.005f;
    private const float QR1_Z = 0.030f;

    private const float SAFE_Z_OFFSET = 0.08f;
    private const float Z_CORRECTION = 0.02f;
    private const float TRAVEL_Z_ABOVE_WORKSPACE = 0.22f;
    // Fine-angle correction is intentionally disabled. We retain only the two
    // discrete gripper directions: horizontal = 0 degrees, vertical = 90 degrees.
    // private const float SKEW_SIGN = 1f;
    // Reject TCP targets too close to the base axis. Reaching into this cylinder
    // requires a tightly folded arm and can make adjacent UR3e links collide.
    private const float BASE_EXCLUSION_RADIUS_M = 0.16f;
    // Do not advance merely because a fixed delay elapsed.  Every motion is
    // confirmed against the UR secondary-interface feedback first.
    private const float MOTION_START_GRACE_SEC = 0.35f;
    private const float MOTION_TIMEOUT_SEC = 180f;
    private const float TCP_POSITION_TOLERANCE_M = 0.012f;
    private const float HOME_JOINT_TOLERANCE_RAD = 0.04f;
    // Some UR/URSim secondary-interface versions keep isProgramRunning=true
    // briefly (or indefinitely) after the commanded pose has settled. Accept a
    // pose that remains inside tolerance for this period instead of deadlocking.
    private const float TARGET_STABLE_CONFIRM_SEC = 0.30f;
    private const float COMMAND_SETTLE_SEC = 0.25f;
    private const float MOTION_PROGRESS_LOG_SEC = 2.0f;
    private const float SAFETY_RECOVERY_TIMEOUT_SEC = 300f;
    private const float SAFETY_STABLE_SEC = 1f;
    // Only the emergency return-to-Home command may be sent again after a
    // second manual unlock. The interrupted pick/place motion is never resent.
    private const int MAX_MANUAL_HOME_RETRIES = 1;

    // Home 關節角度，單位為 rad：[base, shoulder, elbow, wrist1, wrist2, wrist3]
    // 若實機姿勢不符，請由 Teach Pendant 讀取 home 姿勢後更新此值。
    private const string HOME_MOVEJ_CMD = "movej([-1.5708, -1.5708, 0, -1.5708, 0, 0], a=1.2, v=0.8)";

    private URPackageListener urListener;
    private int lastExecutedStepId = -1;
    // C# step IDs restart when dotnet run is restarted while Unity may remain in
    // the same Play Mode. Deduplicate by the complete JSON payload instead of
    // step_id alone, otherwise a new task reusing Step 1/2/... is silently skipped.
    private string lastProcessedStepJson = "";
    private DateTime lastProcessedStepWriteTimeUtc = DateTime.MinValue;
    private string lastProcessedBatchJson = "";
    private DateTime lastProcessedBatchWriteTimeUtc = DateTime.MinValue;
    private int currentBatchId = -1;
    private int currentBatchTotalSteps;
    private int currentBatchCompletedSteps;
    private bool lastStepCompleted;
    private string lastStepError;

    // 保存目前執行中的 ExecuteStep coroutine 與 step_id，供 Home 按鈕中止。
    private Coroutine currentStepCoroutine;
    private int currentStepId = -1;
    private bool lastMotionSucceeded;
    private string lastMotionError;
    private bool safetyRecoverySucceeded;
    private long executionEpoch;

    void Awake()
    {
        // A second executor would open another UR connection and could execute the
        // same JSON concurrently. Keep exactly one command owner in the scene.
        if (activeInstance != null && activeInstance != this)
        {
            Debug.LogError("[Executor] Duplicate JsonExecutor disabled; only one UR command owner is allowed.");
            enabled = false;
            return;
        }
        activeInstance = this;
    }

    void Start()
    {
        if (!enabled) return;
        urListener = new URPackageListener();
        urListener.Connect(urIP);
        Debug.Log("嘗試連線至 UR：" + urIP);

        StartCoroutine(PollLoop());
    }

    void OnDestroy()
    {
        urListener?.Close();
        if (activeInstance == this) activeInstance = null;
    }

    // 保留給 UIManager 呼叫的相容 stub；分層 Executor 啟動後會自行 polling。
    // UIManager 寫入 plan 後不需要主動觸發，PollLoop 會自動偵測新步驟。
    public void LoadAndExecute()
    {
        Debug.Log("[Executor] Batch executor 會自動 poll batch_plan.json");
    }

    // -----------------------------------------------------------
    // UI 按鈕相容介面：release、grip、home
    // -----------------------------------------------------------
    public void ReleaseGripper()
    {
        if (urListener == null || !urListener.Connected)
        {
            Debug.LogWarning("[Executor] UR 未連線，release 失敗");
            return;
        }
        urListener.SendCommand("set_standard_digital_out(4, False)");
        Debug.Log("[Executor] 已送出夾爪釋放指令");
    }

    public void GripGripper()
    {
        if (urListener == null || !urListener.Connected)
        {
            Debug.LogWarning("[Executor] UR 未連線，grip 失敗");
            return;
        }
        urListener.SendCommand("set_standard_digital_out(4, True)");
        Debug.Log("[Executor] 已送出夾爪閉合指令");
    }

    public void GoHome()
    {
        if (urListener == null || !urListener.Connected)
        {
            Debug.LogWarning("[Executor] UR 未連線，home 失敗");
            return;
        }

        // 1. 若正在執行 ExecuteStep，先中止並寫入失敗回報，避免 csharp_server 一直等待。
        if (currentStepCoroutine != null)
        {
            executionEpoch++;
            int abortedStepId = currentStepId;
            StopCoroutine(currentStepCoroutine);
            currentStepCoroutine = null;
            currentStepId = -1;
            WriteStepDone(abortedStepId, false, "aborted by user (GoHome)", 0f);
            if (currentBatchId >= 0)
                WriteBatchDone(currentBatchId, false, currentBatchCompletedSteps,
                    currentBatchTotalSteps, abortedStepId, "aborted by user (GoHome)", 0f);
            Debug.LogWarning($"[Executor] 已中止 step {abortedStepId}，改為返回 Home");

            // 將 perception 切回 idle，讓 SceneSyncer 恢復更新。
            StartCoroutine(SetPerceptionMode("idle"));
        }

        // 2. 送出 home 指令（使用關節角 movej）。
        string homeCmd = HOME_MOVEJ_CMD;
        urListener.SendCommand(homeCmd);
        Debug.Log("[Executor] 已送出 home：" + homeCmd);
    }

    // --- 主 poll loop：監看完整 batch_plan.json ---
    IEnumerator PollLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(pollIntervalSec);

            string path = Path.Combine(Application.streamingAssetsPath, batchPlanFile);
            BatchPlanEnvelope batchToExecute = null;
            if (File.Exists(path))
            {
                try
                {
                    DateTime writeTime = File.GetLastWriteTimeUtc(path);
                    string json = File.ReadAllText(path);
                    if (json != lastProcessedBatchJson || writeTime > lastProcessedBatchWriteTimeUtc)
                    {
                        BatchPlanEnvelope batch = JsonUtility.FromJson<BatchPlanEnvelope>(json);
                        lastProcessedBatchJson = json;
                        lastProcessedBatchWriteTimeUtc = writeTime;
                        if (batch != null && batch.steps != null && batch.steps.Count > 0)
                            batchToExecute = batch;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Executor] batch json parse failed: {ex.Message}");
                }
            }

            // Iterator methods cannot yield inside a try block that has catch.
            // Execute only after file I/O and JSON parsing have left try/catch.
            if (batchToExecute != null)
            {
                currentStepCoroutine = StartCoroutine(ExecuteBatch(batchToExecute));
                yield return currentStepCoroutine;
                currentStepCoroutine = null;
                currentStepId = -1;
                continue;
            }

            // Compatibility for move_relative/stack/3D commands that still use
            // the closed-loop single-step channel. arrange_pattern never enters it.
            string legacyPath = Path.Combine(Application.streamingAssetsPath, currentStepFile);
            if (!File.Exists(legacyPath)) continue;
            StepEnvelope legacyStepToExecute = null;
            try
            {
                DateTime writeTime = File.GetLastWriteTimeUtc(legacyPath);
                string json = File.ReadAllText(legacyPath);
                if (json == lastProcessedStepJson && writeTime <= lastProcessedStepWriteTimeUtc)
                    continue;
                StepEnvelope env = JsonUtility.FromJson<StepEnvelope>(json);
                lastProcessedStepJson = json;
                lastProcessedStepWriteTimeUtc = writeTime;
                if (env == null || env.done) continue;
                legacyStepToExecute = env;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Executor] legacy step json parse failed: {ex.Message}");
            }

            if (legacyStepToExecute != null)
            {
                currentStepId = legacyStepToExecute.step_id;
                long stepEpoch = ++executionEpoch;
                currentStepCoroutine = StartCoroutine(ExecuteStep(legacyStepToExecute, stepEpoch));
                yield return currentStepCoroutine;
                currentStepCoroutine = null;
                currentStepId = -1;
            }
        }
    }

    IEnumerator ExecuteBatch(BatchPlanEnvelope batch)
    {
        currentBatchId = batch.batch_id;
        currentBatchTotalSteps = batch.steps.Count;
        currentBatchCompletedSteps = 0;
        float startedAt = Time.realtimeSinceStartup;
        Debug.Log($"═══ Batch {batch.batch_id}: {batch.steps.Count} steps ═══ {batch.comment}");

        foreach (StepEnvelope env in batch.steps)
        {
            if (env == null || env.source_position == null || env.target_position == null)
            {
                string error = $"step {env?.step_id ?? -1} missing source/target";
                WriteBatchDone(batch.batch_id, false, currentBatchCompletedSteps,
                    batch.steps.Count, env?.step_id ?? -1, error,
                    Time.realtimeSinceStartup - startedAt);
                yield break;
            }
            currentStepId = env.step_id;
            lastExecutedStepId = env.step_id;
            long stepEpoch = ++executionEpoch;
            lastStepCompleted = false;
            lastStepError = "step ended without a completion report";
            yield return ExecuteStep(env, stepEpoch);
            if (!lastStepCompleted)
            {
                WriteBatchDone(batch.batch_id, false, currentBatchCompletedSteps,
                    batch.steps.Count, env.step_id, lastStepError,
                    Time.realtimeSinceStartup - startedAt);
                currentBatchId = -1;
                yield break;
            }
            currentBatchCompletedSteps++;
        }

        WriteBatchDone(batch.batch_id, true, currentBatchCompletedSteps,
            batch.steps.Count, -1, null, Time.realtimeSinceStartup - startedAt);
        Debug.Log($"═══ Batch {batch.batch_id} 完成：{currentBatchCompletedSteps}/{batch.steps.Count} ═══");
        currentBatchId = -1;
    }

    // --- 執行單一步驟：依序解讀 LLM Motion Planner 的 robot functions ---
    IEnumerator ExecuteStep(StepEnvelope env, long stepEpoch)
    {
        Debug.Log($"═══ Step {env.step_id} ═══ {env.comment}");

        // 等待連線
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

        if (IsRecoverableSafetyStop())
        {
            yield return WaitForManualSafetyRecovery(
                "before executing the step", stepEpoch, env.step_id);
            if (!safetyRecoverySucceeded)
            {
                string error = lastMotionError ?? "UR safety recovery failed";
                WriteStepDone(env.step_id, false, error, 0f);
                yield return StartCoroutine(SetPerceptionMode("idle"));
                yield break;
            }
        }

        // 通知 perception 進入 executing，讓 SceneSyncer 凍結畫面。
        yield return StartCoroutine(SetPerceptionMode("executing"));

        // 座標換算：QR 平面 → UR3 base
        float ox = QR1_X + env.source_position.x;
        float oy = QR1_Y + env.source_position.y;
        float oz = QR1_Z + env.source_position.z + Z_CORRECTION;
        float tx = QR1_X + env.target_position.x;
        float ty = QR1_Y + env.target_position.y;
        float tz = QR1_Z + env.target_position.z + Z_CORRECTION;
        float travelZ = QR1_Z + TRAVEL_Z_ABOVE_WORKSPACE;

        if (InsideBaseExclusion(ox, oy) || InsideBaseExclusion(tx, ty))
        {
            string error = $"unsafe target near UR base: source radius={Mathf.Sqrt(ox * ox + oy * oy):F3}m, " +
                           $"target radius={Mathf.Sqrt(tx * tx + ty * ty):F3}m, " +
                           $"minimum={BASE_EXCLUSION_RADIUS_M:F3}m";
            Debug.LogError("[Executor] " + error);
            WriteStepDone(env.step_id, false, error, 0f);
            yield return StartCoroutine(SetPerceptionMode("idle"));
            yield break;
        }

        string srcOri = env.source_position.orientation ?? "";
        string tgtOri = env.target_position.orientation ?? "";
        // Disable camera-estimated fine skew. It was causing noisy wrist rotation.
        // float srcSkew = env.source_position.skew_deg;
        // float tgtSkew = env.target_position.skew_deg;
        float srcSkew = 0f;
        float tgtSkew = 0f;

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
            if (!IsExecutionCurrent(stepEpoch, env.step_id))
            {
                Debug.LogWarning($"[Executor] Stale step {env.step_id} cancelled before action {i + 1}.");
                yield break;
            }

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
                    yield return SendMove(x, y, travelZ, orientation, skew,
                        tag + " travel", false, stepEpoch, env.step_id);
                    if (!lastMotionSucceeded) break;
                    yield return SendMove(x, y, z + height, orientation, skew,
                        tag + " above", true, stepEpoch, env.step_id);
                    break;
                case "descend":
                    yield return SendMove(x, y, z, orientation, skew,
                        tag, true, stepEpoch, env.step_id);
                    break;
                case "grasp":
                    yield return SendGrasp(stepEpoch, env.step_id);
                    break;
                case "release":
                    yield return SendRelease(stepEpoch, env.step_id);
                    break;
                case "lift":
                    // A lift must be Cartesian-linear. movej can change IK branch and
                    // swing/fold the links even when only TCP Z changes.
                    yield return SendMove(x, y, z + height, orientation, skew,
                        tag, true, stepEpoch, env.step_id);
                    break;
                case "wait":
                    yield return new WaitForSeconds(Mathf.Clamp(action.seconds > 0f ? action.seconds : 0.5f, 0.1f, 3f));
                    break;
                case "go_home":
                    yield return SendHome(tag, stepEpoch, env.step_id);
                    break;
                default:
                    WriteStepDone(env.step_id, false, "unknown robot function: " + action.function, 0f);
                    yield return StartCoroutine(SetPerceptionMode("idle"));
                    yield break;
            }


            if (!lastMotionSucceeded &&
                (action.function == "move_above" || action.function == "descend" ||
                 action.function == "lift" || action.function == "go_home"))
            {
                float failedDuration = Time.realtimeSinceStartup - t0;
                string error = string.IsNullOrEmpty(lastMotionError)
                    ? $"UR motion failed at {tag}"
                    : lastMotionError;
                Debug.LogError($"[Executor] Step {env.step_id} stopped: {error}");
                WriteStepDone(env.step_id, false, error, failedDuration);
                yield return StartCoroutine(SetPerceptionMode("idle"));
                yield break;
            }

        }

        // 通知 perception 回到 idle，讓 SceneSyncer 擷取最新場景。
        if (!IsExecutionCurrent(stepEpoch, env.step_id)) yield break;
        yield return StartCoroutine(SetPerceptionMode("idle"));

        // 等待 perception 取得足夠影格以穩定偵測結果。
        yield return new WaitForSeconds(1.5f);

        if (!IsExecutionCurrent(stepEpoch, env.step_id))
        {
            Debug.LogWarning($"[Executor] Suppressed stale completion for step {env.step_id}.");
            yield break;
        }

        float duration = Time.realtimeSinceStartup - t0;
        WriteStepDone(env.step_id, true, null, duration);
        Debug.Log($"═══ Step {env.step_id} 完成 ({duration:F1}s) ═══");
    }

    IEnumerator SendMove(
        float x, float y, float z, string orientation, float skewDeg, string tag,
        bool linear, long stepEpoch, int stepId)
    {
        if (!IsExecutionCurrent(stepEpoch, stepId)) yield break;
        string cmd = linear
            ? BuildMovelLine(x, y, z, orientation, skewDeg)
            : BuildMovejLine(x, y, z, orientation, skewDeg);
        lastMotionSucceeded = false;
        lastMotionError = null;

        if (!IsExecutionCurrent(stepEpoch, stepId))
        {
            lastMotionError = $"stale step {stepId} cancelled during {tag}";
            yield break;
        }
        Debug.Log($"  [{tag}] SEND: {cmd}");
        urListener.SendCommand(cmd);

        // Give the controller a brief chance to start the program, then require
        // actual Cartesian feedback to reach the commanded translation.
        yield return new WaitForSeconds(MOTION_START_GRACE_SEC);
        float startedAt = Time.realtimeSinceStartup;
        float nextProgressLogAt = startedAt + MOTION_PROGRESS_LOG_SEC;
        float reachedToleranceAt = -1f;
        bool protectiveStopDetected = false;
        while (Time.realtimeSinceStartup - startedAt < MOTION_TIMEOUT_SEC)
        {
            if (!IsExecutionCurrent(stepEpoch, stepId))
            {
                lastMotionError = $"stale step {stepId} cancelled during {tag}";
                yield break;
            }
            if (!urListener.Connected)
            {
                lastMotionError = $"UR disconnected during {tag}";
                yield break;
            }
            if (IsEmergencyStop())
            {
                lastMotionError = $"UR emergency stop during {tag}; automatic resume is disabled";
                yield break;
            }
            if (IsRecoverableSafetyStop())
            {
                protectiveStopDetected = true;
                break;
            }

            var tcp = urListener.CartesianInfo;
            float dx = (float)tcp.X - x;
            float dy = (float)tcp.Y - y;
            float dz = (float)tcp.Z - z;
            float distance = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distance <= TCP_POSITION_TOLERANCE_M)
            {
                if (reachedToleranceAt < 0f)
                    reachedToleranceAt = Time.realtimeSinceStartup;
                bool controllerEnded = !urListener.RobotModeData.isProgramRunning;
                bool poseStable = Time.realtimeSinceStartup - reachedToleranceAt >=
                                  TARGET_STABLE_CONFIRM_SEC;
                if (controllerEnded || poseStable)
                {
                    lastMotionSucceeded = true;
                    string confirmation = controllerEnded ? "program ended" : "pose stable";
                    Debug.Log($"  [{tag}] REACHED ({confirmation}): TCP error {distance * 1000f:F1} mm");
                    // Give the secondary interface time to finish the previous
                    // script before the next standalone program is submitted.
                    yield return new WaitForSeconds(COMMAND_SETTLE_SEC);
                    yield break;
                }
            }
            else
                reachedToleranceAt = -1f;

            if (Time.realtimeSinceStartup >= nextProgressLogAt)
            {
                Debug.Log($"  [{tag}] WAITING: actual=({(float)tcp.X:F4}," +
                          $"{(float)tcp.Y:F4},{(float)tcp.Z:F4}), " +
                          $"target=({x:F4},{y:F4},{z:F4}), " +
                          $"error={distance * 1000f:F1} mm, " +
                          $"programRunning={urListener.RobotModeData.isProgramRunning}");
                nextProgressLogAt = Time.realtimeSinceStartup + MOTION_PROGRESS_LOG_SEC;
            }
            yield return new WaitForSeconds(0.05f);
        }

        if (protectiveStopDetected)
        {
            yield return WaitForManualSafetyRecovery(tag, stepEpoch, stepId);
            if (!safetyRecoverySucceeded)
                yield break;

            Debug.LogWarning(
                $"[Executor] Protective Stop recovery at {tag}: interrupted motion will NOT be retried; returning Home.");
            yield return SendHome(tag + " safety return_home", stepEpoch, stepId);
            bool homeSucceeded = lastMotionSucceeded;
            string homeFailure = lastMotionError;
            if (homeSucceeded)
            {
                // The stop may have happened after grasping. Reset the gripper at
                // Home so the server can safely assign a different source cube.
                Debug.LogWarning(
                    "[Executor] Safety Home reached; releasing the gripper before selecting another cube.");
                yield return SendRelease(stepEpoch, stepId);
            }
            lastMotionSucceeded = false; // The pick/place step itself still failed.
            lastMotionError = homeSucceeded
                ? $"UR protective stop during {tag}; returned Home without retrying the interrupted motion"
                : $"UR protective stop during {tag}; Home recovery failed: {homeFailure}";
            yield break;
        }

        var finalTcp = urListener.CartesianInfo;
        float finalDx = (float)finalTcp.X - x;
        float finalDy = (float)finalTcp.Y - y;
        float finalDz = (float)finalTcp.Z - z;
        float finalDistance = Mathf.Sqrt(finalDx * finalDx + finalDy * finalDy + finalDz * finalDz);
        lastMotionError = $"UR motion timeout during {tag}: TCP remained {finalDistance * 1000f:F1} mm from target";
    }

    IEnumerator SendHome(string tag, long stepEpoch, int stepId)
    {
        if (!IsExecutionCurrent(stepEpoch, stepId)) yield break;
        lastMotionSucceeded = false;
        lastMotionError = null;
        float[] target = { -1.5708f, -1.5708f, 0f, -1.5708f, 0f, 0f };

        for (int safetyAttempt = 0;
             safetyAttempt <= MAX_MANUAL_HOME_RETRIES;
             safetyAttempt++)
        {
            if (!IsExecutionCurrent(stepEpoch, stepId))
            {
                lastMotionError = $"stale step {stepId} cancelled during {tag}";
                yield break;
            }
            string retryLabel = safetyAttempt == 0 ? "" : " (manual safety retry)";
            Debug.Log($"  [{tag}] SEND{retryLabel}: {HOME_MOVEJ_CMD}");
            urListener.SendCommand(HOME_MOVEJ_CMD);
            yield return new WaitForSeconds(MOTION_START_GRACE_SEC);

            float startedAt = Time.realtimeSinceStartup;
            float nextProgressLogAt = startedAt + MOTION_PROGRESS_LOG_SEC;
            float reachedToleranceAt = -1f;
            bool protectiveStopDetected = false;
            while (Time.realtimeSinceStartup - startedAt < MOTION_TIMEOUT_SEC)
            {
                if (!IsExecutionCurrent(stepEpoch, stepId))
                {
                    lastMotionError = $"stale step {stepId} cancelled during {tag}";
                    yield break;
                }
                if (!urListener.Connected)
                {
                    lastMotionError = $"UR disconnected during {tag}";
                    yield break;
                }
                if (IsEmergencyStop())
                {
                    lastMotionError = $"UR emergency stop during {tag}; automatic resume is disabled";
                    yield break;
                }
                if (IsRecoverableSafetyStop())
                {
                    protectiveStopDetected = true;
                    break;
                }

                var joints = urListener.JointData.AsArray;
                float maxError = 0f;
                for (int i = 0; i < target.Length; i++)
                {
                    float actual = (float)joints[i].q_actual;
                    float error = Mathf.Abs(Mathf.DeltaAngle(actual * Mathf.Rad2Deg,
                        target[i] * Mathf.Rad2Deg)) * Mathf.Deg2Rad;
                    maxError = Mathf.Max(maxError, error);
                }
                if (maxError <= HOME_JOINT_TOLERANCE_RAD)
                {
                    if (reachedToleranceAt < 0f)
                        reachedToleranceAt = Time.realtimeSinceStartup;
                    bool controllerEnded = !urListener.RobotModeData.isProgramRunning;
                    bool poseStable = Time.realtimeSinceStartup - reachedToleranceAt >=
                                      TARGET_STABLE_CONFIRM_SEC;
                    if (controllerEnded || poseStable)
                    {
                        lastMotionSucceeded = true;
                        string confirmation = controllerEnded ? "program ended" : "pose stable";
                        Debug.Log($"  [{tag}] REACHED ({confirmation}): max joint error " +
                                  $"{maxError * Mathf.Rad2Deg:F2} deg");
                        yield return new WaitForSeconds(COMMAND_SETTLE_SEC);
                        yield break;
                    }
                }
                else
                    reachedToleranceAt = -1f;
                if (Time.realtimeSinceStartup >= nextProgressLogAt)
                {
                    Debug.Log($"  [{tag}] WAITING HOME: max joint error " +
                              $"{maxError * Mathf.Rad2Deg:F2} deg, " +
                              $"programRunning={urListener.RobotModeData.isProgramRunning}");
                    nextProgressLogAt = Time.realtimeSinceStartup + MOTION_PROGRESS_LOG_SEC;
                }
                yield return new WaitForSeconds(0.05f);
            }

            if (!protectiveStopDetected)
                break;
            if (safetyAttempt >= MAX_MANUAL_HOME_RETRIES)
            {
                lastMotionError = $"UR protective stop repeated during {tag}; retry limit reached";
                yield break;
            }

            yield return WaitForManualSafetyRecovery(tag, stepEpoch, stepId);
            if (!safetyRecoverySucceeded)
                yield break;
        }

        lastMotionError = $"UR motion timeout during {tag}: home was not reached";
    }

    IEnumerator WaitForManualSafetyRecovery(
        string context, long stepEpoch, int stepId)
    {
        if (!IsExecutionCurrent(stepEpoch, stepId)) yield break;
        safetyRecoverySucceeded = false;
        Debug.LogWarning(
            $"[Executor] Protective Stop at {context}. No more commands will be sent. " +
            "Clear the obstruction, unlock the protective stop, and enable the robot on the teach pendant.");

        float startedAt = Time.realtimeSinceStartup;
        float stableSince = -1f;
        while (Time.realtimeSinceStartup - startedAt < SAFETY_RECOVERY_TIMEOUT_SEC)
        {
            if (!IsExecutionCurrent(stepEpoch, stepId))
            {
                lastMotionError = $"stale step {stepId} cancelled while waiting at {context}";
                yield break;
            }
            if (!urListener.Connected)
            {
                lastMotionError = $"UR disconnected while waiting for manual safety recovery at {context}";
                yield break;
            }
            if (IsEmergencyStop())
            {
                lastMotionError = $"UR emergency stop at {context}; automatic resume is disabled";
                yield break;
            }

            bool ready = !IsRecoverableSafetyStop() &&
                         urListener.RobotModeData.isRobotPowerOn &&
                         urListener.RobotModeData.isRealRobotEnabled &&
                         !urListener.RobotModeData.isProgramRunning;
            if (ready)
            {
                if (stableSince < 0f) stableSince = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - stableSince >= SAFETY_STABLE_SEC)
                {
                    safetyRecoverySucceeded = true;
                    Debug.LogWarning(
                        $"[Executor] Manual safety recovery confirmed at {context}; " +
                        "the controller is ready for the caller's recovery action.");
                    yield break;
                }
            }
            else
            {
                stableSince = -1f;
            }
            yield return new WaitForSeconds(0.1f);
        }

        lastMotionError = $"Timed out waiting for manual safety recovery at {context}";
    }

    bool IsExecutionCurrent(long stepEpoch, int stepId)
    {
        return enabled && activeInstance == this &&
               executionEpoch == stepEpoch && currentStepId == stepId;
    }

    bool IsEmergencyStop()
    {
        SafetyMode mode = urListener.MasterboardData.safetyMode;
        return urListener.RobotModeData.isEmergencyStopped ||
               mode == SafetyMode.RobotEmergencyStop ||
               mode == SafetyMode.SystemEmergencyStop;
    }

    bool IsRecoverableSafetyStop()
    {
        SafetyMode mode = urListener.MasterboardData.safetyMode;
        return urListener.RobotModeData.isProtectiveStopped ||
               mode == SafetyMode.ProtectiveStop ||
               mode == SafetyMode.SafeguardStop ||
               mode == SafetyMode.Recovery ||
               mode == SafetyMode.Violation ||
               mode == SafetyMode.Fault;
    }

    bool InsideBaseExclusion(float x, float y)
    {
        return (x * x + y * y) < BASE_EXCLUSION_RADIUS_M * BASE_EXCLUSION_RADIUS_M;
    }

    string BuildMovelLine(float x, float y, float z, string orientation, float skewDeg)
    {
        // Keep horizontal/vertical grasp support, but ignore the detected skewDeg.
        // Cubes have no orientation (null/empty) and are square, so use the same
        // known-safe 90-degree wrist direction as vertical dominos. Only an
        // explicitly horizontal domino uses 0 degrees.
        // bool rotate = orientation != "horizontal";
        // float totalDeg = rotate ? 90f + (SKEW_SIGN * skewDeg) : (SKEW_SIGN * skewDeg);
        bool rotate = orientation != "horizontal";
        float totalDeg = rotate ? 90f : 0f;
        float totalRad = totalDeg * Mathf.Deg2Rad;
        string pose = Mathf.Abs(totalDeg) > 0.01f
            ? $"pose_trans(p[{x:F4}, {y:F4}, {z:F4}, 0, 3.14, 0], p[0, 0, 0, 0, 0, {totalRad:F4}])"
            : $"p[{x:F4}, {y:F4}, {z:F4}, 0, 3.14, 0]";
        return $"movel({pose}, a=0.3, v=0.10)";
    }

    IEnumerator SendGrasp(long stepEpoch, int stepId)
    {
        if (!IsExecutionCurrent(stepEpoch, stepId)) yield break;
        Debug.Log("  [grasp] SEND: set_standard_digital_out(4, True)");
        urListener.SendCommand("set_standard_digital_out(4, True)");
        yield return new WaitForSeconds(1.5f);
    }

    IEnumerator SendRelease(long stepEpoch, int stepId)
    {
        if (!IsExecutionCurrent(stepEpoch, stepId)) yield break;
        Debug.Log("  [release] SEND: set_standard_digital_out(4, False)");
        urListener.SendCommand("set_standard_digital_out(4, False)");
        yield return new WaitForSeconds(1.5f);
    }

    string BuildMovejLine(float x, float y, float z, string orientation, float skewDeg)
    {
        // Keep horizontal/vertical grasp support, but ignore the detected skewDeg.
        // Cubes have no orientation (null/empty) and are square, so use the same
        // known-safe 90-degree wrist direction as vertical dominos. Only an
        // explicitly horizontal domino uses 0 degrees.
        // bool rotate = orientation != "horizontal";
        // float totalDeg = rotate ? 90f + (SKEW_SIGN * skewDeg) : (SKEW_SIGN * skewDeg);
        bool rotate = orientation != "horizontal";
        float totalDeg = rotate ? 90f : 0f;
        float totalRad = totalDeg * Mathf.Deg2Rad;

        // Keep the current elbow/wrist configuration as the IK seed. A fixed qnear
        // can select the folded-back branch and make the forearm collide with the
        // upper arm even though the requested TCP pose itself is reachable.
        const string qnear = "get_actual_joint_positions()";

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
        lastStepCompleted = completed;
        lastStepError = error ?? "";
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

    void WriteBatchDone(int batchId, bool completed, int completedSteps,
        int totalSteps, int failedStepId, string error, float duration)
    {
        var report = new BatchDoneReport
        {
            batch_id = batchId,
            completed = completed,
            completed_steps = completedSteps,
            total_steps = totalSteps,
            failed_step_id = failedStepId,
            error = error ?? "",
            duration_sec = duration,
        };
        string json = JsonUtility.ToJson(report, prettyPrint: true);
        string path = Path.Combine(Application.streamingAssetsPath, batchDoneFile);
        File.WriteAllText(path, json);
    }
}

