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
// 分層架構中的單步執行器：
//   - 持續 poll current_step.json
//   - 收到 steps batch 後連續執行整批；仍保留單 step 相容
//   - 執行完寫入 step_done.json 回報結果
//   - 收到 {"done": true} 後停止該批任務
// 不需要按 Space 執行。
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
public class BatchEnvelope
{
    public int batch_id;
    public bool done;
    public string comment;
    public List<StepEnvelope> steps;
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
    private static JsonExecutor activeInstance;
    [Header("設定")]
    public string currentStepFile = "current_step.json";
    public string stepDoneFile = "step_done.json";
    public string urIP = "192.168.50.204";
    public float pollIntervalSec = 0.3f;

    [Header("Perception Server")]
    public string perceptionModeUrl = "http://localhost:5000/scene/mode";

    [Header("UI（保留既有按鈕相容性）")]
    public UIManager uiManager;

    [Header("模擬模式（不接實機、Unity 內部演示 pick-and-place）")]
    public bool simulationOnly = true;
    public SceneSyncer sceneSyncer;         // 拖 PerceptionSync GameObject 進來
    public RobotArm robotArm;                // 拖 UR3 GameObject 進來，null 會自動找
    public float simMoveSecPerStep = 10.0f;  // 每步動畫秒數（含手臂 + 方塊）
    public float simHoldSec = 0.7f;          // 每段動作之間停頓秒數（讓觀察更清楚）
    public bool simAnimateArm = true;        // 是否讓虛擬手臂跟著動

    [Header("模擬手臂 - Canonical Kinematics（UR3e 官方 DH）")]
    // ★ 建議開啟：改用 UR3eKinematics 算 IK，才能當 pre-flight verifier
    //   若手臂視覺不對，需要先做 RobotArm 的 RotationAxis/Offsets 校準（Task 12）
    public bool useCanonicalKinematics = false;
    // Preview 前先掃全 batch，任一步不可達就 abort，不動實機
    public bool verifyBatchReachability = true;
    // TCP hover 高度（cube 頂端上方 m）
    public float simGripperHoverM = 0.08f;
    // TCP 接觸高度（cube 頂端加這個）— pick/place 下降時用
    // 舊 2D IK 專用；canonical 模式改用跟實機相同的 Z_CORRECTION（見 ContactExtraZ）
    public float simContactClearanceM = 0.005f;
    // 下降接觸時 TCP 高於方塊頂的距離；canonical 模式跟實機 ExecuteStep 用同一個常數，模擬才等於實機
    float ContactExtraZ => useCanonicalKinematics ? Z_CORRECTION : simContactClearanceM;
    // Gripper 朝下的 TCP rotation vector（URScript axis-angle）
    // 預設 (π,0,0) = TCP z 軸朝下（工具指向地面）
    public Vector3 simGripperDownRotVec = new Vector3(Mathf.PI, 0f, 0f);
    // IK 起始參考 q（度）；連續動作會以上次解為參考維持分支連續性
    public float[] simIKReferenceDeg = new float[] { 0f, -90f, 0f, -90f, 0f, 0f };

    [Header("模擬手臂 - IK 參數（舊版 2D 平面 IK，useCanonicalKinematics=false 才用）")]
    public bool simUseAutoReach = true;
    public float simArmL1 = 0.244f;
    public float simArmL2 = 0.213f;
    public float simShoulderHeightM = 0.152f;
    public float simFixedArmZ = 0.30f;       // 手臂固定的絕對 Z 高度
    public float simShoulderTilt = 0f;
    public float simElbowSign = 1f;
    public float simShoulderSign = 1f;
    public bool simAutoWrist1 = true;
    public float simWrist1Extra = 0f;
    public float simWrist2Down = 0f;

    [Header("模擬 - 放置位置微調（歸零，不做偏移校正）")]
    public float simPlaceOffsetX = 0f;
    public float simPlaceOffsetY = 0f;
    public float simPlaceOffsetZ = 0f;
    public float simSnapTransitionSec = 0.5f;

    [Header("模擬 - Gripper 對位偏移（歸零，不做校正）")]
    public float simPickGripperOffsetX = 0f;
    public float simPickGripperOffsetY = 0f;
    public float simPlaceGripperOffsetX = 0f;
    public float simPlaceGripperOffsetY = 0f;

    [Header("模擬手臂 - 軸向對映（歸零；假設 Unity model 座標系已對齊實機）")]
    public bool simYawInvert = false;
    public float simYawOffsetDeg = 0f;
    public bool simFlipX = false;
    public bool simFlipY = false;

    [Header("模擬手臂 - 前伸姿態（度；順序 base/shoulder/elbow/wrist_1/wrist_2/wrist_3）")]
    // 到達 source/target 上方時的姿態，base 會被動態覆蓋成計算的 yaw
    public float simReachShoulder = -60f;
    public float simReachElbow    =  90f;
    public float simReachWrist1   = -30f;
    public float simReachWrist2   =  90f;
    public float simReachWrist3   =   0f;

    [Header("模擬手臂 - Home 姿態")]
    public float simHomeBase     = -90f;
    public float simHomeShoulder = -90f;
    public float simHomeElbow    =   0f;
    public float simHomeWrist1   = -90f;
    public float simHomeWrist2   =   0f;
    public float simHomeWrist3   =   0f;
    public bool previewBatchInUnityBeforeRobot = true;
    public bool freezeUnityRobotDuringRealBatch = true;
    public float placeDescendExtraZ = 0f;

    [Header("實機夾取校正（只影響 source，不影響放置矩陣）")]
    public float pickOffsetX = -0.002f;
    public float pickOffsetY = 0.002f;

    [Header("安全預備姿勢（Teach Pendant 校正後再啟用）")]
    public bool useReadyPose = false;
    public float[] readyJointsRad = new float[6] { -1.5708f, -1.5708f, 1.5708f, -1.5708f, 0f, 0f };

    // QR1 到 UR3 base 的座標偏移（以 Teach Pendant 實際校正值為準）
    // public 讓 SceneSyncer 直接引用，workspace 視覺對齊 = 實測值單一來源
    public const float QR1_X = -0.38824f;
    public const float QR1_Y = -0.35973f+0.005f;
    public const float QR1_Z = 0.030f;

    private const float SAFE_Z_OFFSET = 0.08f;
    private const float Z_CORRECTION = 0.02f;
    private const float TRAVEL_Z_ABOVE_WORKSPACE = 0.24f;
    // Fine-angle correction is intentionally disabled. We retain only the two
    // discrete gripper directions: horizontal = 0 degrees, vertical = 90 degrees.
    // private const float SKEW_SIGN = 1f;
    // Reject TCP targets too close to the base axis. Reaching into this cylinder
    // requires a tightly folded arm and can make adjacent UR3e links collide.
    private const float BASE_EXCLUSION_RADIUS_M = 0.16f;
    // Picking needs more clearance than placing because the source-side tool
    // orientation and attached gripper can fold the wrist/forearm toward the base.
    private const float SOURCE_BASE_EXCLUSION_RADIUS_M = 0.23f;
    // High-plane lateral travel is routed around this radius when a direct
    // Cartesian chord would cut through the base exclusion cylinder.
    private const float BASE_DETOUR_RADIUS_M = 0.28f;
    private const float BASE_DETOUR_MAX_ANGLE_STEP_DEG = 25f;
    // Avoid poses that make the UR3e almost fully extend. Those IK solutions are
    // fragile and can trigger a protective stop before the TCP reaches the block.
    private const float MAX_REACH_RADIUS_M = 0.42f;
    // Do not advance merely because a fixed delay elapsed.  Every motion is
    // confirmed against the UR secondary-interface feedback first.
    private const float MOTION_START_GRACE_SEC = 0.35f;
    private const float MOTION_TIMEOUT_SEC = 180f;
    private const float TCP_POSITION_TOLERANCE_M = 0.012f;
    private const float HOME_JOINT_TOLERANCE_RAD = 0.04f;
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

    // 保存目前執行中的 ExecuteStep coroutine 與 step_id，供 Home 按鈕中止。
    private Coroutine currentStepCoroutine;
    private int currentStepId = -1;
    private bool lastMotionSucceeded;
    private string lastMotionError;
    private bool lastStepReportedSuccess;
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

        if (simulationOnly)
        {
            RobotArm.FreezeVisualFeedback = false;
            Debug.Log("[Executor] 模擬模式啟用：不連線實機、Unity 內部動畫演示");
            // 自動找 SceneSyncer / RobotArm（如果 Inspector 沒拖）
            if (sceneSyncer == null)
                sceneSyncer = FindObjectOfType<SceneSyncer>();
            if (robotArm == null)
                robotArm = FindObjectOfType<RobotArm>();
            if (robotArm == null)
            {
                Debug.LogWarning("[Executor-sim] 找不到 RobotArm，手臂不會動");
            }
            else
            {
                // 模擬模式：關掉 followRealRobotFeedback，避免 URPackageListener 每 frame
                // 用 q_actual=0 覆蓋我們的 Angles。RobotArm.Update() 仍會套 Angles → Transforms。
                robotArm.followRealRobotFeedback = false;
                int tCount = robotArm.Transforms != null ? robotArm.Transforms.Length : 0;
                int aCount = robotArm.Angles != null ? robotArm.Angles.Length : 0;
                Debug.Log($"[Executor-sim] RobotArm='{robotArm.name}', Transforms={tCount}, Angles={aCount}, followFeedback={robotArm.followRealRobotFeedback} (sim 模式已關實機 feedback)");
                if (tCount == 0 || aCount == 0)
                    Debug.LogWarning("[Executor-sim] RobotArm 的 Transforms 或 Angles 陣列為空！請到 Inspector 檢查 UR3 的 Transforms/RotationAxis/RotationOffsets 有沒有配好");
            }
        }
        else
        {
            EnsureUrConnectionStarted();
        }

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
        Debug.Log("[Executor] LoadAndExecute() 已停用；分層 executor 會自動 poll current_step.json");
    }

    // -----------------------------------------------------------
    // UI 按鈕相容介面：release、grip、home
    // -----------------------------------------------------------
    public void ReleaseGripper()
    {
        if (simulationOnly) { Debug.Log("[Executor-sim] release (無實機)"); return; }
        EnsureUrConnectionStarted();
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
        if (simulationOnly) { Debug.Log("[Executor-sim] grip (無實機)"); return; }
        EnsureUrConnectionStarted();
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
        if (simulationOnly)
        {
            Debug.Log("[Executor-sim] home (無實機)，中止當前 step");
            if (currentStepCoroutine != null)
            {
                executionEpoch++;
                int abortedStepId = currentStepId;
                StopCoroutine(currentStepCoroutine);
                currentStepCoroutine = null;
                currentStepId = -1;
                WriteStepDone(abortedStepId, false, "aborted by user (GoHome sim)", 0f);
            }
            return;
        }
        EnsureUrConnectionStarted();
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
            Debug.LogWarning($"[Executor] 已中止 step {abortedStepId}，改為返回 Home");

            // 將 perception 切回 idle，讓 SceneSyncer 恢復更新。
            StartCoroutine(SetPerceptionMode("idle"));
        }

        // 2. 送出 home 指令（使用關節角 movej）。
        string homeCmd = HOME_MOVEJ_CMD;
        urListener.SendCommand(homeCmd);
        Debug.Log("[Executor] 已送出 home：" + homeCmd);
    }

    // --- 主 poll loop：監看 current_step.json 的新 step_id ---
    IEnumerator PollLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(pollIntervalSec);

            string path = Path.Combine(Application.streamingAssetsPath, currentStepFile);
            if (!File.Exists(path)) continue;

            string stepJson;
            DateTime stepWriteTimeUtc;
            try
            {
                stepWriteTimeUtc = File.GetLastWriteTimeUtc(path);
                stepJson = File.ReadAllText(path);
                if (stepJson == lastProcessedStepJson &&
                    stepWriteTimeUtc <= lastProcessedStepWriteTimeUtc) continue;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Executor] step json read failed: {ex.Message}");
                continue;
            }

            lastProcessedStepJson = stepJson;
            lastProcessedStepWriteTimeUtc = stepWriteTimeUtc;

            if (stepJson.Contains("\"steps\""))
            {
                BatchEnvelope batch;
                try
                {
                    batch = JsonUtility.FromJson<BatchEnvelope>(stepJson);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Executor] batch json parse failed: {ex.Message}");
                    continue;
                }

                if (batch == null || batch.steps == null || batch.steps.Count == 0)
                {
                    Debug.LogWarning("[Executor] batch 缺少 steps");
                    continue;
                }

                executionEpoch++;
                currentStepCoroutine = StartCoroutine(ExecuteBatch(batch));
                yield return currentStepCoroutine;
                currentStepCoroutine = null;
                currentStepId = -1;
                continue;
            }

            StepEnvelope env;
            try
            {
                env = JsonUtility.FromJson<StepEnvelope>(stepJson);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Executor] step json parse failed: {ex.Message}");
                continue;
            }

            if (env == null) continue;

            if (env.done)
            {
                // Invalidate any delayed child coroutine before accepting the end
                // of a batch. A stale safety-recovery coroutine must never resume.
                executionEpoch++;
                if (currentStepCoroutine != null)
                {
                    StopCoroutine(currentStepCoroutine);
                    currentStepCoroutine = null;
                    currentStepId = -1;
                }
                Debug.Log($"[Executor] 收到 done 訊號 (step {env.step_id})，等待下一批任務");
                lastExecutedStepId = env.step_id;
                continue;
            }

            if (env.source_position == null || env.target_position == null)
            {
                Debug.LogWarning($"[Executor] step {env.step_id} 缺少 source/target");
                continue;
            }

            lastExecutedStepId = env.step_id;
            currentStepId = env.step_id;
            long stepEpoch = ++executionEpoch;
            RobotArm.FreezeVisualFeedback = false;
            currentStepCoroutine = StartCoroutine(ExecuteStep(env, stepEpoch));
            yield return currentStepCoroutine;
            currentStepCoroutine = null;
            currentStepId = -1;
        }
    }

    IEnumerator ExecuteBatch(BatchEnvelope batch)
    {
        Debug.Log($"[Executor] 收到 batch {batch.batch_id}: {batch.steps.Count} steps — {batch.comment}");

        // ★ Pre-flight kinematics 檢查：任一步不可達/奇點就 abort，不動實機
        if (useCanonicalKinematics && verifyBatchReachability)
        {
            var failures = VerifyBatchReachability(batch);
            if (failures.Count > 0)
            {
                string summary = $"batch {batch.batch_id} 有 {failures.Count} 個不可達或奇點位置，abort：\n  - "
                                 + string.Join("\n  - ", failures);
                Debug.LogError($"[Executor-precheck] {summary}");
                WriteStepDone(batch.batch_id, false, summary, 0f);
                yield break;
            }
            Debug.Log($"[Executor-precheck] ✓ batch {batch.batch_id} 全部 {batch.steps.Count} steps 通過 UR3e kinematics 檢查");
        }

        if (!simulationOnly)
        {
            EnsureUrConnectionStarted();
            yield return StartCoroutine(SetPerceptionMode("executing"));
            if (sceneSyncer == null)
                sceneSyncer = FindObjectOfType<SceneSyncer>();
            if (robotArm == null)
                robotArm = FindObjectOfType<RobotArm>();
            if (previewBatchInUnityBeforeRobot)
            {
                // 動畫預覽：手臂 + 方塊完整演示（跑完自動復原）
                // 預覽期間關 followRealRobotFeedback，讓 Update() 用我們設的 Angles 而非實機 q_actual
                if (robotArm != null) robotArm.followRealRobotFeedback = false;
                RobotArm.FreezeVisualFeedback = false;
                yield return StartCoroutine(PreviewBatchAnimated(batch));
            }
            // 預覽結束、實機開始：Unity 手臂改由實機 feedback 驅動
            if (robotArm != null) robotArm.followRealRobotFeedback = true;
            RobotArm.FreezeVisualFeedback = freezeUnityRobotDuringRealBatch;

            float waited = 0f;
            while (!urListener.Connected && waited < 3f)
            {
                yield return new WaitForSeconds(0.1f);
                waited += 0.1f;
            }
            if (!urListener.Connected)
            {
                Debug.LogError("無法連線到 UR");
                WriteStepDone(batch.batch_id, false, "UR 未連線", 0f);
                RobotArm.FreezeVisualFeedback = false;
                yield return StartCoroutine(SetPerceptionMode("idle"));
                yield break;
            }

            if (IsRecoverableSafetyStop())
            {
                currentStepId = batch.batch_id;
                long recoveryEpoch = ++executionEpoch;
                yield return WaitForManualSafetyRecovery(
                    "before executing the batch", recoveryEpoch, batch.batch_id);
                currentStepId = -1;
                if (!safetyRecoverySucceeded)
                {
                    string error = lastMotionError ?? "UR safety recovery failed";
                    WriteStepDone(batch.batch_id, false, error, 0f);
                    RobotArm.FreezeVisualFeedback = false;
                    yield return StartCoroutine(SetPerceptionMode("idle"));
                    yield break;
                }
            }

            currentStepId = batch.batch_id;
            long readyEpoch = ++executionEpoch;
            yield return SendReady("batch initial ready", readyEpoch, batch.batch_id);
            if (!lastMotionSucceeded)
            {
                string error = string.IsNullOrEmpty(lastMotionError)
                    ? "UR initial Ready failed"
                    : lastMotionError;
                WriteStepDone(batch.batch_id, false, error, 0f);
                RobotArm.FreezeVisualFeedback = false;
                yield return StartCoroutine(SetPerceptionMode("idle"));
                currentStepId = -1;
                yield break;
            }
            currentStepId = -1;
        }
        else
        {
            // 純模擬模式也要對齊：實機 batch 開頭一定先 movej 到 ready pose
            yield return SimGoToReadyPose("sim initial ready");
        }

        for (int i = 0; i < batch.steps.Count; i++)
        {
            StepEnvelope env = batch.steps[i];
            if (env == null || env.done) continue;

            if (env.source_position == null || env.target_position == null)
            {
                Debug.LogWarning($"[Executor] batch step {env?.step_id} 缺少 source/target");
                WriteStepDone(env != null ? env.step_id : batch.batch_id, false, "batch step missing source/target", 0f);
                RobotArm.FreezeVisualFeedback = false;
                if (!simulationOnly) yield return StartCoroutine(SetPerceptionMode("idle"));
                yield break;
            }

            lastExecutedStepId = env.step_id;
            currentStepId = env.step_id;
            long stepEpoch = ++executionEpoch;
            currentStepCoroutine = StartCoroutine(ExecuteStep(env, stepEpoch, simulationOnly));
            yield return currentStepCoroutine;
            currentStepCoroutine = null;
            currentStepId = -1;

            if (stepEpoch != executionEpoch || !lastStepReportedSuccess)
            {
                Debug.LogWarning($"[Executor] batch {batch.batch_id} stopped after step {env.step_id}");
                RobotArm.FreezeVisualFeedback = false;
                if (!simulationOnly) yield return StartCoroutine(SetPerceptionMode("idle"));
                yield break;
            }
        }

        if (!simulationOnly)
        {
            currentStepId = batch.batch_id;
            long finalLiftEpoch = ++executionEpoch;
            yield return SendCurrentTcpLiftToTravelHeight(
                QR1_Z + TRAVEL_Z_ABOVE_WORKSPACE,
                "batch final safe lift", finalLiftEpoch, batch.batch_id);
            if (!lastMotionSucceeded)
            {
                string error = string.IsNullOrEmpty(lastMotionError)
                    ? "UR final safe lift failed"
                    : lastMotionError;
                WriteStepDone(batch.batch_id, false, error, 0f);
                RobotArm.FreezeVisualFeedback = false;
                yield return StartCoroutine(SetPerceptionMode("idle"));
                currentStepId = -1;
                yield break;
            }

            long readyEpoch = ++executionEpoch;
            yield return SendReady("batch final ready", readyEpoch, batch.batch_id);
            if (!lastMotionSucceeded)
            {
                string error = string.IsNullOrEmpty(lastMotionError)
                    ? "UR final Ready failed"
                    : lastMotionError;
                WriteStepDone(batch.batch_id, false, error, 0f);
                RobotArm.FreezeVisualFeedback = false;
                yield return StartCoroutine(SetPerceptionMode("idle"));
                currentStepId = -1;
                yield break;
            }

            long homeEpoch = ++executionEpoch;
            yield return SendHome("batch final go_home", homeEpoch, batch.batch_id);
            if (!lastMotionSucceeded)
            {
                string error = string.IsNullOrEmpty(lastMotionError)
                    ? "UR final Home failed"
                    : lastMotionError;
                WriteStepDone(batch.batch_id, false, error, 0f);
                RobotArm.FreezeVisualFeedback = false;
                yield return StartCoroutine(SetPerceptionMode("idle"));
                currentStepId = -1;
                yield break;
            }
            currentStepId = -1;
            yield return StartCoroutine(SetPerceptionMode("idle"));
            yield return new WaitForSeconds(1.5f);
        }

        currentStepId = batch.batch_id;
        WriteStepDone(batch.batch_id, true, null, 0f);
        currentStepId = -1;
        Debug.Log($"[Executor] batch {batch.batch_id} 全部完成");
    }

    // --- 執行單一步驟：依序解讀 LLM Motion Planner 的 robot functions ---
    // ----------------------------------------------------------
    // 模擬模式：不接實機，直接在 Unity 桌面上動畫演示 pick-and-place
    // 模擬手臂預設姿態（從 Inspector 讀，方便動態調整）
    float[] BuildHomePose() => new float[] {
        simHomeBase, simHomeShoulder, simHomeElbow, simHomeWrist1, simHomeWrist2, simHomeWrist3
    };
    float[] BuildReachPose(float baseYawDeg) => new float[] {
        baseYawDeg, simReachShoulder, simReachElbow, simReachWrist1, simReachWrist2, simReachWrist3
    };

    // 依 QR 位置算 6 個 joint 讓 gripper 到目標 (x, y, z) 附近
    // qrZ 通常是 cube 頂面高度（例如 0.025 for 2.5cm cube）
    // hoverOverride >= 0 時取代 simHoverAboveCubeM（讓 arm 下降到 cube 用）
    float[] BuildReachPoseForQR(float qrX, float qrY, float qrZ = 0.025f, float hoverOverride = -1f)
    {
        float baseYawDeg = ComputeBaseYawDegForQR(qrX, qrY);

        // Canonical UR3e kinematics 分支（★ 建議走這條）
        if (useCanonicalKinematics)
        {
            // hoverOverride < 0 → 用 simGripperHoverM；>= 0 → 直接當「TCP 高於 cube 頂多少 m」
            float extraZ = hoverOverride < 0f ? simGripperHoverM : hoverOverride;
            if (TryBuildKinematicsPose(qrX, qrY, qrZ, extraZ, out float[] kinJoints, out string kinErr))
                return kinJoints;
            Debug.LogError($"[Executor-sim] Canonical IK failed for QR ({qrX:F3},{qrY:F3},{qrZ:F3}): {kinErr}");
            // fallback: 退回舊 heuristic，讓動畫至少能動；但 batch 檢查會在上游擋住
            return BuildReachPose(baseYawDeg);
        }

        if (!simUseAutoReach)
            return BuildReachPose(baseYawDeg);

        // 水平距離
        float urX = QR1_X + qrX;
        float urY = QR1_Y + qrY;
        if (simFlipX) urX = -urX;
        if (simFlipY) urY = -urY;
        float r = Mathf.Sqrt(urX * urX + urY * urY);

        // 垂直距離：目標 z 固定用 simFixedArmZ（不依 cube 高度變化）
        float zTargetFromBase = hoverOverride >= 0f ? (qrZ + hoverOverride) : simFixedArmZ;
        float zFromShoulder = zTargetFromBase - simShoulderHeightM;

        // 2D 垂直平面 IK：從 shoulder pivot 到 target 的向量 (r, zFromShoulder)
        float d = Mathf.Sqrt(r * r + zFromShoulder * zFromShoulder);
        float dMax = simArmL1 + simArmL2 - 0.01f;
        if (d > dMax) d = dMax;
        if (d < 0.02f) d = 0.02f;

        // 三角形內角
        float cosAlpha = (simArmL1 * simArmL1 + d * d - simArmL2 * simArmL2)
                         / (2f * simArmL1 * d);
        cosAlpha = Mathf.Clamp(cosAlpha, -1f, 1f);
        float alpha = Mathf.Acos(cosAlpha);          // shoulder 到目標線與上臂夾角

        float cosBeta = (simArmL1 * simArmL1 + simArmL2 * simArmL2 - d * d)
                        / (2f * simArmL1 * simArmL2);
        cosBeta = Mathf.Clamp(cosBeta, -1f, 1f);
        float beta = Mathf.Acos(cosBeta);            // elbow 內角

        // 從水平算起的目標仰角
        float phi = Mathf.Atan2(zFromShoulder, r);

        // shoulder 關節角度（從水平起）：elbow-up 姿態
        float shoulderRad = phi + alpha;
        float elbowRad = Mathf.PI - beta;            // elbow 彎折角度

        float shoulderDeg = simShoulderSign * shoulderRad * Mathf.Rad2Deg + simShoulderTilt;
        float elbowDeg = simElbowSign * elbowRad * Mathf.Rad2Deg;

        // wrist_1 讓 gripper 朝下：sum = -180 度
        float wrist1Deg = simAutoWrist1
            ? -(shoulderDeg + elbowDeg) - 90f + simWrist1Extra
            : simReachWrist1;
        float wrist2Deg = simAutoWrist1 ? simWrist2Down : simReachWrist2;

        return new float[] {
            baseYawDeg, shoulderDeg, elbowDeg,
            wrist1Deg, wrist2Deg, simReachWrist3
        };
    }

    // ============================================================
    // Canonical UR3e Kinematics 分支
    // ============================================================
    // 上次 IK 解，作為下一步的參考 q（維持分支連續性）
    private double[] _lastIKJointsRad = null;

    // 目標：TCP 到 (QR frame qrX, qrY, cube 頂端 qrZ + hover 高度)，gripper 朝下
    // 回傳 6 個 joint 角度（度，UR 順序 base/shoulder/elbow/wrist1/wrist2/wrist3）
    bool TryBuildKinematicsPose(float qrX, float qrY, float qrZ, bool hoverAbove,
                                 out float[] jointsDeg, out string error)
    {
        float extraZ = hoverAbove ? simGripperHoverM : ContactExtraZ;
        return TryBuildKinematicsPose(qrX, qrY, qrZ, extraZ, out jointsDeg, out error);
    }

    // 連續版：extraZAboveCube 是 TCP 相對 cube 頂的高度差（公尺，任意值）
    bool TryBuildKinematicsPose(float qrX, float qrY, float qrZ, float extraZAboveCube,
                                 out float[] jointsDeg, out string error)
    {
        // canonical 模式的座標映射是精確的；simFlipX/Y 只屬於舊 2D IK，套在這裡會把目標鏡射
        return TryBuildPoseUR(QR1_X + qrX, QR1_Y + qrY, QR1_Z + qrZ + extraZAboveCube,
                              out jointsDeg, out error);
    }

    // UR base frame 版：直接給 TCP 在 base 座標的 (x, y, z)，gripper 朝下。
    // 模擬的 action_sequence 直譯器走這個，因為 ExecuteStep 也是全程在 UR base frame
    // 算座標；兩邊共用同一個入口，模擬與實機才不會各算各的。
    bool TryBuildPoseUR(double urX, double urY, double urZ,
                        out float[] jointsDeg, out string error)
    {
        var target = new UR3eKinematics.Pose
        {
            x = urX, y = urY, z = urZ,
            rx = simGripperDownRotVec.x,
            ry = simGripperDownRotVec.y,
            rz = simGripperDownRotVec.z
        };

        double[] refQ = _lastIKJointsRad ?? DegArrayToRad(simIKReferenceDeg);
        var sol = UR3eKinematics.IKNearest(target, refQ);
        if (!sol.ok)
        {
            jointsDeg = null;
            error = $"{sol.error} — {sol.message}";
            return false;
        }
        _lastIKJointsRad = sol.q;
        jointsDeg = new float[6];
        for (int i = 0; i < 6; i++) jointsDeg[i] = (float)(sol.q[i] * 180.0 / System.Math.PI);
        error = null;
        return true;
    }

    static double[] DegArrayToRad(float[] deg)
    {
        var r = new double[6];
        for (int i = 0; i < System.Math.Min(6, deg.Length); i++) r[i] = deg[i] * System.Math.PI / 180.0;
        return r;
    }

    // Pre-flight：掃全 batch，任一步 source/target 不可達或奇點就回報
    // 回傳 unreachable step 清單（空 = 全部可達）
    List<string> VerifyBatchReachability(BatchEnvelope batch)
    {
        var failures = new List<string>();
        if (batch == null || batch.steps == null) return failures;

        // 用暫時的 reference q，不影響實際執行時的 continuity
        double[] refQ = _lastIKJointsRad != null
            ? (double[])_lastIKJointsRad.Clone()
            : DegArrayToRad(simIKReferenceDeg);

        foreach (var env in batch.steps)
        {
            if (env == null || env.done) continue;
            if (env.source_position == null || env.target_position == null) continue;

            // 跟實機 ExecuteStep 用同一組安全範圍，否則會「模擬通過、實機拒絕」
            float ox = QR1_X + env.source_position.x + pickOffsetX;
            float oy = QR1_Y + env.source_position.y + pickOffsetY;
            float tx = QR1_X + env.target_position.x;
            float ty = QR1_Y + env.target_position.y;
            if (InsideSourceBaseExclusion(ox, oy) || OutsideReachEnvelope(ox, oy))
                failures.Add($"step {env.step_id} source @ UR({ox:F3},{oy:F3}) 半徑 {Mathf.Sqrt(ox * ox + oy * oy):F3} m " +
                             $"超出實機安全範圍 {SOURCE_BASE_EXCLUSION_RADIUS_M:F2}..{MAX_REACH_RADIUS_M:F2} m");
            if (InsideBaseExclusion(tx, ty) || OutsideReachEnvelope(tx, ty))
                failures.Add($"step {env.step_id} target @ UR({tx:F3},{ty:F3}) 半徑 {Mathf.Sqrt(tx * tx + ty * ty):F3} m " +
                             $"超出實機安全範圍 {BASE_EXCLUSION_RADIUS_M:F2}..{MAX_REACH_RADIUS_M:F2} m");

            // 驗手臂實際會經過的三個高度，順序跟 action_sequence 一致：
            //   contact = descend 的終點（QR1_Z + z + Z_CORRECTION，跟實機相同）
            //   above   = move_above / lift 的終點，用 height_m 的上限 0.15 驗最壞情況
            //   travel  = move_above 平移時所在的平面
            // 舊版驗的是 simGripperHoverM(0.08)，那個高度現在沒有任何動作會停在上面。
            float travelZ = QR1_Z + TRAVEL_Z_ABOVE_WORKSPACE;
            foreach (var (label, pos, zAbs) in new[]
            {
                ($"step {env.step_id} source contact", env.source_position, 0f),
                ($"step {env.step_id} source above",   env.source_position, 0.15f),
                ($"step {env.step_id} source travel",  env.source_position, travelZ),
                ($"step {env.step_id} target contact", env.target_position, 0f),
                ($"step {env.step_id} target above",   env.target_position, 0.15f),
                ($"step {env.step_id} target travel",  env.target_position, travelZ),
            })
            {
                double urX = QR1_X + pos.x; double urY = QR1_Y + pos.y;
                // zAbs >= travelZ → 絕對高度；否則是「相對 contact 再往上」
                double urZ = zAbs >= travelZ
                    ? travelZ
                    : QR1_Z + pos.z + Z_CORRECTION + zAbs;
                var tgt = new UR3eKinematics.Pose
                {
                    x = urX, y = urY, z = urZ,
                    rx = simGripperDownRotVec.x, ry = simGripperDownRotVec.y, rz = simGripperDownRotVec.z
                };
                var sol = UR3eKinematics.IKNearest(tgt, refQ);
                if (!sol.ok)
                {
                    failures.Add($"{label} @ UR({urX:F3},{urY:F3},{urZ:F3}) → {sol.error}: {sol.message}");
                }
                else
                {
                    refQ = sol.q; // 沿路更新，讓下一步的參考 q 有連續性
                }
            }
        }
        return failures;
    }

    IEnumerator ExecuteStepSimulated(StepEnvelope env)
    {
        DateTime t0 = DateTime.UtcNow;

        if (sceneSyncer == null)
        {
            Debug.LogWarning("[Executor-sim] SceneSyncer 未設定，跳過此 step");
            WriteStepDone(env.step_id, false, "sceneSyncer missing", 0f);
            yield break;
        }

        yield return AnimateOneStep(env, "sim");

        float dur = (float)(DateTime.UtcNow - t0).TotalSeconds;
        WriteStepDone(env.step_id, true, null, dur);
        Debug.Log($"[Executor-sim] step {env.step_id} 完成，{dur:F2}s");
    }

    // 純動畫：pick-and-place 一步（手臂 + 方塊），不寫 step_done
    // 給 simulation mode 和實機執行前的預覽共用
    IEnumerator AnimateOneStep(StepEnvelope env, string tag)
    {
        if (sceneSyncer == null) yield break;

        // 找 source 位置最接近的 cube；找不到就自動生一顆代替（用 target 期望顏色）
        GameObject cube = sceneSyncer.FindNearestCube(
            env.source_position.x, env.source_position.y, env.source_position.z);

        if (cube == null)
        {
            Color guess = env.source_position.name != null && env.source_position.name.Contains("yellow")
                ? new Color(1f, 0.85f, 0.1f) : new Color(0.4f, 0.4f, 0.4f);
            cube = sceneSyncer.SpawnCube(
                $"{tag}_cube_{env.step_id}",
                env.source_position.x, env.source_position.y, env.source_position.z, guess);
            Debug.Log($"[Executor-{tag}] source cube 不存在，生成一顆代替 @ ({env.source_position.x:F3}, {env.source_position.y:F3})");
        }
        cube.transform.localScale = SimScaleFor(env.source_position);

        // QR frame → Unity local（統一走 SceneSyncer.QRToUnity）
        // simPlaceOffsetX/Y/Z 只是方塊落點的視覺微調，不套用在手臂目標上。
        // 要讓預覽真的代表實機，這三個值必須維持 0。
        float halfHeight = sceneSyncer.cubeSizeM / 2f;
        Vector3 targetLocal = SceneSyncer.QRToUnity(
            env.target_position.x + simPlaceOffsetX,
            env.target_position.y + simPlaceOffsetY,
            env.target_position.z + simPlaceOffsetZ);
        targetLocal.y -= halfHeight;

        // 手臂夾爪 transform：優先用 RobotArm.TCP（tcp_tip，夾爪實際夾持點）；
        // 沒指定才退回最後一個 joint（wrist_3 樞紐，不是指尖，只是保底）。
        Transform gripper = null;
        bool armEnabled = simAnimateArm && robotArm != null &&
                          robotArm.Transforms != null && robotArm.Transforms.Length > 0;
        if (armEnabled)
        {
            gripper = robotArm.TCP != null
                ? robotArm.TCP
                : robotArm.Transforms[robotArm.Transforms.Length - 1];
        }
        Transform cubeOriginalParent = cube.transform.parent;

        // ---- 座標換算：跟 ExecuteStep 逐行對齊 ----
        // 這幾行只要跟 ExecuteStep 差一個字，模擬就不再是實機的模擬，
        // 所以這裡刻意寫死 Z_CORRECTION，不走 ContactExtraZ 那個 sim-only 開關。
        float ox = QR1_X + env.source_position.x + pickOffsetX;
        float oy = QR1_Y + env.source_position.y + pickOffsetY;
        float oz = QR1_Z + env.source_position.z + Z_CORRECTION;
        float tx = QR1_X + env.target_position.x;
        float ty = QR1_Y + env.target_position.y;
        float tz = QR1_Z + env.target_position.z + Z_CORRECTION;
        float travelZ = QR1_Z + TRAVEL_Z_ABOVE_WORKSPACE;

        if (!useCanonicalKinematics)
        {
            Debug.LogWarning("[Executor-sim] useCanonicalKinematics 是關的。動作直譯器一律用 " +
                             "canonical UR3e IK，舊的 2D heuristic 走不出 Cartesian 直線；" +
                             "要讓預覽代表實機，請把它打開。");
        }

        // ---- 動作序列：直接執行 LLM 要給實機的那一份 ----
        // 改版前這裡跑的是寫死的 7 個 phase，完全沒讀 action_sequence，
        // 所以預覽演的跟實機做的是兩回事：hover 高度、travel 平面、繞底座、
        // 每步結尾回不回 startpoint 全都不同。
        var actions = env.action_sequence;
        if (actions == null || actions.Count == 0)
        {
            actions = BuildDefaultActionSequence();
            Debug.LogWarning($"[Executor-{tag}] step {env.step_id} 沒有 action_sequence，" +
                             "改用預設 pick-place 序列模擬（實機遇到這種 step 會直接判 missing action_sequence）");
        }

        Debug.Log($"[Executor-{tag}] step {env.step_id}: source UR=({ox:F4},{oy:F4},{oz:F4}) " +
                  $"target UR=({tx:F4},{ty:F4},{tz:F4}) travelZ={travelZ:F4}，共 {actions.Count} 個動作");

        if (!armEnabled)
        {
            // 沒有手臂可動畫時只把方塊搬到定位，至少讓場景狀態正確
            cube.transform.localScale = SimScaleFor(env.target_position);
            cube.transform.localPosition = targetLocal;
            cube.name = $"{tag}_step{env.step_id}";
            yield break;
        }

        // 模擬的「目前 TCP」，等同實機讀 urListener.CartesianInfo
        Vector3 tcp = CurrentSimTcpUR();
        bool holdingObject = false;

        for (int i = 0; i < actions.Count; i++)
        {
            RobotFunctionCall action = actions[i];
            string label = $"{tag} {i + 1}/{actions.Count} {action.function}";
            bool source = action.location == "source";
            float x = source ? ox : tx;
            float y = source ? oy : ty;
            float z = source ? oz : tz;
            // 高度夾限跟 ExecuteStep 相同：LLM 沒給就用 SAFE_Z_OFFSET，再夾到 0.05~0.15
            float height = Mathf.Clamp(
                action.height_m > 0f ? action.height_m : SAFE_Z_OFFSET, 0.05f, 0.15f);

            switch (action.function)
            {
                case "move_above":
                {
                    // 跟實機一樣三段：升到 travel 平面 → 平面移動（必要時繞開底座）→ 降到 z+height
                    if (tcp.z < travelZ - TCP_POSITION_TOLERANCE_M)
                    {
                        Vector3 lifted = new Vector3(tcp.x, tcp.y, travelZ);
                        yield return AnimateTcpLinearUR(tcp, lifted,
                            simMoveSecPerStep * SIM_T_PRELIFT, label + " prelift");
                        tcp = lifted;
                    }

                    var waypoints = BuildBaseDetourWaypoints(tcp.x, tcp.y, x, y);
                    if (waypoints.Count > 1)
                    {
                        Debug.Log($"  [{label}] 直線會掃過底座，改繞 R={BASE_DETOUR_RADIUS_M:F3}m " +
                                  $"（{waypoints.Count} 段）");
                    }
                    float perLeg = simMoveSecPerStep * SIM_T_TRAVEL / waypoints.Count;
                    foreach (var wp in waypoints)
                    {
                        Vector3 next = new Vector3(wp.x, wp.y, travelZ);
                        yield return AnimateTcpLinearUR(tcp, next, perLeg, label + " travel");
                        tcp = next;
                    }

                    Vector3 above = new Vector3(x, y, z + height);
                    yield return AnimateTcpLinearUR(tcp, above,
                        simMoveSecPerStep * SIM_T_DESCEND, label + " above");
                    tcp = above;
                    break;
                }
                case "descend":
                {
                    // 放置時手上有方塊要再多留一點，跟 ExecuteStep 相同
                    float zd = z;
                    if (!source && holdingObject) zd += Mathf.Max(0f, placeDescendExtraZ);
                    Vector3 down = new Vector3(x, y, zd);
                    yield return AnimateTcpLinearUR(tcp, down,
                        simMoveSecPerStep * SIM_T_DESCEND, label);
                    tcp = down;
                    break;
                }
                case "lift":
                {
                    // lift 必須是 Cartesian 直線；movej 會換 IK 分支，把手臂整個甩開
                    Vector3 up = new Vector3(x, y, z + height);
                    yield return AnimateTcpLinearUR(tcp, up,
                        simMoveSecPerStep * SIM_T_LIFT, label);
                    tcp = up;
                    break;
                }
                case "grasp":
                    cube.transform.SetParent(gripper, worldPositionStays: true);
                    holdingObject = true;
                    Debug.Log($"  [{label}] 夾住 {cube.name}");
                    yield return new WaitForSeconds(simHoldSec);
                    break;
                case "release":
                    cube.transform.SetParent(cubeOriginalParent, worldPositionStays: true);
                    cube.transform.localScale = SimScaleFor(env.target_position);
                    cube.transform.localPosition = targetLocal;
                    holdingObject = false;
                    Debug.Log($"  [{label}] 放開 {cube.name}");
                    yield return new WaitForSeconds(simHoldSec);
                    break;
                case "wait":
                    yield return new WaitForSeconds(
                        Mathf.Clamp(action.seconds > 0f ? action.seconds : 0.5f, 0.1f, 3f));
                    break;
                case "go_home":
                    yield return AnimateJointsTo(BuildHomePose(),
                        simMoveSecPerStep * SIM_T_HOME);
                    tcp = CurrentSimTcpUR();
                    break;
                default:
                    Debug.LogError($"[Executor-{tag}] 不認得的 robot function：{action.function}" +
                                   "（實機遇到會直接 abort 這一步）");
                    break;
            }
        }

        // 序列跑完方塊還在夾爪上 = LLM 少給了 release，實機會把方塊一路帶著走
        if (holdingObject)
        {
            Debug.LogError($"[Executor-{tag}] step {env.step_id} 結束時方塊仍被夾著" +
                           "（action_sequence 裡沒有 release）");
            cube.transform.SetParent(cubeOriginalParent, worldPositionStays: true);
            cube.transform.localScale = SimScaleFor(env.target_position);
            cube.transform.localPosition = targetLocal;
        }

        cube.name = $"{tag}_step{env.step_id}";
    }

    // 依 QR frame (x, y) 計算 UR base 平面上的基座 yaw（度）
    float ComputeBaseYawDegForQR(float qrX, float qrY)
    {
        float urX = QR1_X + qrX;
        float urY = QR1_Y + qrY;
        if (simFlipX) urX = -urX;
        if (simFlipY) urY = -urY;
        float yawRad = Mathf.Atan2(urY, urX);
        float yawDeg = yawRad * Mathf.Rad2Deg;
        if (simYawInvert) yawDeg = -yawDeg;
        return yawDeg + simYawOffsetDeg;
    }

    // 各類動作各佔 simMoveSecPerStep 的比例。沿用改版前 7-phase 的權重，
    // 動畫節奏跟以前一致；但動作數量現在由 action_sequence 決定，單步總長不再固定。
    private const float SIM_T_PRELIFT = 0.10f;
    private const float SIM_T_TRAVEL  = 0.20f;
    private const float SIM_T_DESCEND = 0.15f;
    private const float SIM_T_LIFT    = 0.10f;
    private const float SIM_T_HOME    = 0.10f;

    // action_sequence 缺漏時的後備序列，內容跟 LLM Motion Planner 正常產出的一致。
    // 實機遇到空序列會直接判 missing action_sequence，這裡只是讓預覽仍演得出東西。
    List<RobotFunctionCall> BuildDefaultActionSequence()
    {
        return new List<RobotFunctionCall>
        {
            new RobotFunctionCall { function = "move_above", location = "source", height_m = SAFE_Z_OFFSET },
            new RobotFunctionCall { function = "descend",    location = "source" },
            new RobotFunctionCall { function = "grasp" },
            new RobotFunctionCall { function = "wait",       seconds = 0.2f },
            new RobotFunctionCall { function = "lift",       location = "source", height_m = SAFE_Z_OFFSET },
            new RobotFunctionCall { function = "move_above", location = "target", height_m = SAFE_Z_OFFSET },
            new RobotFunctionCall { function = "descend",    location = "target" },
            new RobotFunctionCall { function = "release" },
            new RobotFunctionCall { function = "wait",       seconds = 0.2f },
            new RobotFunctionCall { function = "lift",       location = "target", height_m = SAFE_Z_OFFSET },
        };
    }

    // 模擬手臂目前的 TCP 位置（UR base frame），等同實機讀 urListener.CartesianInfo。
    // 直譯器每個動作都要知道「現在在哪」，才能決定要不要 prelift、以及走哪條 travel 路徑。
    Vector3 CurrentSimTcpUR()
    {
        double[] q = new double[6];
        bool haveAngles = robotArm != null && robotArm.Angles != null && robotArm.Angles.Length >= 6;
        for (int i = 0; i < 6; i++)
        {
            if (haveAngles) q[i] = robotArm.Angles[i] * Mathf.Deg2Rad;
            else if (_lastIKJointsRad != null) q[i] = _lastIKJointsRad[i];
        }
        var p = UR3eKinematics.FKPose(q);
        return new Vector3((float)p.x, (float)p.y, (float)p.z);
    }

    // Cartesian 直線：TCP 在 UR base frame 由 fromUR 走到 toUR，每 frame 重算 IK。
    // 對應實機的 movel。不能改用關節內插（那是 movej，TCP 會走成弧線，
    // 直上直下的夾取放置就會歪掉，而且可能中途換 IK 分支把手臂甩開）。
    IEnumerator AnimateTcpLinearUR(Vector3 fromUR, Vector3 toUR, float seconds, string tag)
    {
        if (robotArm == null || robotArm.Angles == null || robotArm.Angles.Length == 0)
        {
            yield return new WaitForSeconds(seconds * 0.1f);
            yield break;
        }
        int n = robotArm.Angles.Length;
        seconds = Mathf.Max(0.01f, seconds);

        Debug.Log($"[Executor-sim] {tag}: UR ({fromUR.x:F4},{fromUR.y:F4},{fromUR.z:F4}) → " +
                  $"({toUR.x:F4},{toUR.y:F4},{toUR.z:F4}) in {seconds:F2}s");

        int ikFailures = 0;
        float elapsed = 0f;
        while (elapsed < seconds)
        {
            elapsed += Time.deltaTime;
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / seconds));
            Vector3 p = Vector3.Lerp(fromUR, toUR, k);
            if (TryBuildPoseUR(p.x, p.y, p.z, out float[] pose, out _))
            {
                int m = Mathf.Min(n, pose.Length);
                for (int i = 0; i < m; i++) robotArm.Angles[i] = pose[i];
            }
            else ikFailures++;
            yield return null;
        }

        // 收尾：確保精確停在終點
        if (TryBuildPoseUR(toUR.x, toUR.y, toUR.z, out float[] final, out string finalErr))
        {
            int mf = Mathf.Min(n, final.Length);
            for (int i = 0; i < mf; i++) robotArm.Angles[i] = final[i];
        }
        else
        {
            Debug.LogError($"[Executor-sim] {tag}: 終點 IK 無解 — {finalErr}（實機在這裡會停下來）");
        }

        if (ikFailures > 0)
        {
            Debug.LogWarning($"[Executor-sim] {tag}: 路徑上有 {ikFailures} 個取樣點 IK 無解，" +
                             "這段直線實機走不完整");
        }
    }

    // 將 robotArm.Angles 從目前值平滑插值到 targetDeg
    IEnumerator AnimateJointsTo(float[] targetDeg, float seconds)
    {
        if (robotArm == null || robotArm.Angles == null || robotArm.Angles.Length == 0)
        {
            Debug.LogWarning("[Executor-sim] AnimateJointsTo bail：robotArm 或 Angles 陣列為 null/空");
            yield break;
        }
        int n = Mathf.Min(robotArm.Angles.Length, targetDeg.Length);
        float[] start = new float[n];
        for (int i = 0; i < n; i++) start[i] = robotArm.Angles[i];
        Debug.Log($"[Executor-sim] AnimateJointsTo: {string.Join(",", start.Select(a => a.ToString("F1")))} → {string.Join(",", targetDeg.Take(n).Select(a => a.ToString("F1")))} in {seconds:F2}s");
        float elapsed = 0f;
        while (elapsed < seconds)
        {
            elapsed += Time.deltaTime;
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / seconds));
            for (int i = 0; i < n; i++)
                robotArm.Angles[i] = Mathf.Lerp(start[i], targetDeg[i], k);
            yield return null;
        }
        for (int i = 0; i < n; i++) robotArm.Angles[i] = targetDeg[i];
    }

    // 舊版：直接把方塊瞬移到最終位置（沒動畫）。保留給需要 fast preview 的情境。
    void PreviewBatchFinalLayout(BatchEnvelope batch)
    {
        if (sceneSyncer == null)
        {
            Debug.LogWarning("[Executor] SceneSyncer 未設定，略過 batch 最終畫面預覽");
            return;
        }

        int previewed = 0;
        foreach (StepEnvelope env in batch.steps)
        {
            if (env == null || env.done ||
                env.source_position == null || env.target_position == null)
                continue;

            GameObject cube = sceneSyncer.FindNearestCube(
                env.source_position.x, env.source_position.y, env.source_position.z);
            if (cube == null)
            {
                Color guess = env.source_position.name != null && env.source_position.name.Contains("yellow")
                    ? new Color(1f, 0.85f, 0.1f)
                    : new Color(0.4f, 0.4f, 0.4f);
                cube = sceneSyncer.SpawnCube(
                    $"preview_cube_{env.step_id}",
                    env.source_position.x, env.source_position.y, env.source_position.z, guess);
            }

            float halfHeight = sceneSyncer.cubeSizeM / 2f;
            cube.transform.localScale = SimScaleFor(env.target_position);
            Vector3 previewPos = SceneSyncer.QRToUnity(
                env.target_position.x, env.target_position.y, env.target_position.z);
            previewPos.y -= halfHeight;
            cube.transform.localPosition = previewPos;
            cube.name = $"preview_step{env.step_id}";
            previewed++;
        }

        Debug.Log($"[Executor] 已先在 Unity 預覽 batch {batch.batch_id} 最終位置：{previewed} 個物件");
    }

    // 新版：完整動畫 preview（手臂 + 方塊逐 step 演示），跑完後復原初始狀態，實機才開始動
    IEnumerator PreviewBatchAnimated(BatchEnvelope batch)
    {
        if (sceneSyncer == null)
        {
            Debug.LogWarning("[Executor] SceneSyncer 未設定，略過動畫預覽");
            yield break;
        }

        Debug.Log($"[Executor-preview] 開始動畫預覽 batch {batch.batch_id}: {batch.steps.Count} steps");

        // 儲存目前所有 cube 的 local position（動畫結束要復原）
        var cubes = sceneSyncer.GetCurrentCubes();
        var initialCubePositions = new Dictionary<GameObject, Vector3>();
        var initialCubeParents = new Dictionary<GameObject, Transform>();
        var initialCubeScales = new Dictionary<GameObject, Vector3>();
        var initialCubeNames = new Dictionary<GameObject, string>();
        foreach (var cube in cubes)
        {
            if (cube == null) continue;
            initialCubePositions[cube] = cube.transform.localPosition;
            initialCubeParents[cube] = cube.transform.parent;
            initialCubeScales[cube] = cube.transform.localScale;
            initialCubeNames[cube] = cube.name;
        }

        // 儲存手臂當前 Angles
        float[] initialArmAngles = null;
        if (robotArm != null && robotArm.Angles != null)
            initialArmAngles = (float[])robotArm.Angles.Clone();

        // 記錄 preview 過程中新生成的 cube（結束時要刪掉，不留垃圾）
        int cubesBeforePreview = cubes.Count;

        // 實機的 ExecuteBatch 在第一步之前會先 SendReady，預覽要從同一個姿態起步
        yield return SimGoToReadyPose("preview initial ready");

        // 對每個 step 跑 AnimateOneStep
        foreach (StepEnvelope env in batch.steps)
        {
            if (env == null || env.done ||
                env.source_position == null || env.target_position == null)
                continue;
            yield return AnimateOneStep(env, "preview");
        }

        // 復原：手臂角度
        if (initialArmAngles != null && robotArm != null && robotArm.Angles != null)
        {
            int n = Mathf.Min(initialArmAngles.Length, robotArm.Angles.Length);
            for (int i = 0; i < n; i++) robotArm.Angles[i] = initialArmAngles[i];
        }

        // 復原：原本 cube 的位置/名稱；preview 過程中新增的 cube 直接刪除
        var currentCubes = sceneSyncer.GetCurrentCubes();
        for (int i = currentCubes.Count - 1; i >= 0; i--)
        {
            var cube = currentCubes[i];
            if (cube == null) { currentCubes.RemoveAt(i); continue; }
            if (initialCubePositions.ContainsKey(cube))
            {
                if (initialCubeParents[cube] != null && cube.transform.parent != initialCubeParents[cube])
                    cube.transform.SetParent(initialCubeParents[cube], worldPositionStays: false);
                cube.transform.localPosition = initialCubePositions[cube];
                cube.transform.localScale = initialCubeScales[cube];
                cube.name = initialCubeNames[cube];
            }
            else
            {
                // preview 過程新生成的臨時 cube
                Destroy(cube);
                currentCubes.RemoveAt(i);
            }
        }

        Debug.Log($"[Executor-preview] 動畫預覽結束，已復原場景。實機開始執行");
    }

    Vector3 SimScaleFor(NamedPosition pos)
    {
        if (sceneSyncer == null) return Vector3.one * 0.025f;

        float size = sceneSyncer.cubeSizeM;
        if (pos != null && pos.shape == "domino")
        {
            // domino 長軸：horizontal 沿 robot X（= Unity +Z）；vertical 沿 robot Y（= Unity -X）
            return pos.orientation == "vertical"
                ? new Vector3(size * 2f, size, size)
                : new Vector3(size, size, size * 2f);
        }
        return Vector3.one * size;
    }

    IEnumerator AnimateLocalTo(Transform t, Vector3 target, float seconds)
    {
        Vector3 start = t.localPosition;
        float elapsed = 0f;
        while (elapsed < seconds)
        {
            elapsed += Time.deltaTime;
            float k = Mathf.Clamp01(elapsed / seconds);
            k = Mathf.SmoothStep(0f, 1f, k);
            t.localPosition = Vector3.Lerp(start, target, k);
            yield return null;
        }
        t.localPosition = target;
    }

    void EnsureUrConnectionStarted()
    {
        if (urListener != null) return;
        urListener = new URPackageListener();
        urListener.Connect(urIP);
        Debug.Log("嘗試連線至 UR：" + urIP);
    }

    IEnumerator ExecuteStep(StepEnvelope env, long stepEpoch, bool managePerceptionMode = true)
    {
        Debug.Log($"═══ Step {env.step_id} ═══ {env.comment}");

        // 模擬模式：直接動畫演示 cube，不走 URScript 那條路
        if (simulationOnly)
        {
            yield return StartCoroutine(ExecuteStepSimulated(env));
            yield break;
        }

        // 等待連線
        EnsureUrConnectionStarted();
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
                if (managePerceptionMode) yield return StartCoroutine(SetPerceptionMode("idle"));
                yield break;
            }
        }

        // 通知 perception 進入 executing，讓 SceneSyncer 凍結畫面。
        if (managePerceptionMode)
            yield return StartCoroutine(SetPerceptionMode("executing"));

        // 座標換算：QR 平面 → UR3 base
        float ox = QR1_X + env.source_position.x + pickOffsetX;
        float oy = QR1_Y + env.source_position.y + pickOffsetY;
        float oz = QR1_Z + env.source_position.z + Z_CORRECTION;
        float tx = QR1_X + env.target_position.x;
        float ty = QR1_Y + env.target_position.y;
        float tz = QR1_Z + env.target_position.z + Z_CORRECTION;
        float travelZ = QR1_Z + TRAVEL_Z_ABOVE_WORKSPACE;
        Debug.Log(
            $"[Executor] source UR=({ox:F4},{oy:F4},{oz:F4}) " +
            $"pickOffset=({pickOffsetX:F4},{pickOffsetY:F4}); " +
            $"target UR=({tx:F4},{ty:F4},{tz:F4})");

        if (InsideSourceBaseExclusion(ox, oy) || InsideBaseExclusion(tx, ty) ||
            OutsideReachEnvelope(ox, oy) || OutsideReachEnvelope(tx, ty))
        {
            string error = $"unsafe target reach: source radius={Mathf.Sqrt(ox * ox + oy * oy):F3}m, " +
                           $"target radius={Mathf.Sqrt(tx * tx + ty * ty):F3}m, " +
                           $"source allowed={SOURCE_BASE_EXCLUSION_RADIUS_M:F3}..{MAX_REACH_RADIUS_M:F3}m, " +
                           $"target allowed={BASE_EXCLUSION_RADIUS_M:F3}..{MAX_REACH_RADIUS_M:F3}m";
            Debug.LogError("[Executor] " + error);
            WriteStepDone(env.step_id, false, error, 0f);
            if (managePerceptionMode) yield return StartCoroutine(SetPerceptionMode("idle"));
            yield break;
        }

        string srcOri = EffectiveOrientation(env.source_position, true);
        string tgtOri = EffectiveOrientation(env.target_position, false);
        // Disable camera-estimated fine skew. It was causing noisy wrist rotation.
        // float srcSkew = env.source_position.skew_deg;
        // float tgtSkew = env.target_position.skew_deg;
        float srcSkew = 0f;
        float tgtSkew = 0f;

        var t0 = Time.realtimeSinceStartup;

        if (env.action_sequence == null || env.action_sequence.Count == 0)
        {
            WriteStepDone(env.step_id, false, "missing action_sequence", 0f);
            if (managePerceptionMode) yield return StartCoroutine(SetPerceptionMode("idle"));
            yield break;
        }

        // Unity only interprets a closed whitelist. Raw URScript and arbitrary coordinates
        // are deliberately not part of the JSON contract.
        bool holdingObject = false;
        for (int i = 0; i < env.action_sequence.Count; i++)
        {
            if (!IsExecutionCurrent(stepEpoch, env.step_id))
            {
                Debug.LogWarning($"[Executor] Stale step {env.step_id} cancelled before action {i + 1}.");
                yield break;
            }
            if (IsEmergencyStop() || IsRecoverableSafetyStop())
            {
                string error = IsEmergencyStop()
                    ? $"UR emergency stop before action {i + 1}; batch stopped"
                    : $"UR safety stop before action {i + 1}; batch stopped";
                WriteStepDone(env.step_id, false, error, Time.realtimeSinceStartup - t0);
                if (managePerceptionMode) yield return StartCoroutine(SetPerceptionMode("idle"));
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
                    yield return SendCurrentTcpLiftToTravelHeight(travelZ,
                        tag + " prelift", stepEpoch, env.step_id);
                    if (!lastMotionSucceeded) break;

                    // Travel only after the current TCP is already on the high
                    // plane, so the arm does not sweep across the blocks.
                    yield return SendTravelMoveWithBaseDetour(x, y, travelZ,
                        orientation, skew, tag + " travel", stepEpoch, env.step_id);
                    if (!lastMotionSucceeded) break;
                    yield return SendMove(x, y, z + height, orientation, skew,
                        tag + " above", true, stepEpoch, env.step_id);
                    break;
                case "descend":
                    if (!source && holdingObject)
                        z += Mathf.Max(0f, placeDescendExtraZ);
                    yield return SendMove(x, y, z, orientation, skew,
                        tag, true, stepEpoch, env.step_id);
                    break;
                case "grasp":
                    yield return SendGrasp(stepEpoch, env.step_id);
                    holdingObject = true;
                    break;
                case "release":
                    yield return SendRelease(stepEpoch, env.step_id);
                    holdingObject = false;
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
                    if (managePerceptionMode) yield return StartCoroutine(SetPerceptionMode("idle"));
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
                if (managePerceptionMode) yield return StartCoroutine(SetPerceptionMode("idle"));
                yield break;
            }

        }

        // 通知 perception 回到 idle，讓 SceneSyncer 擷取最新場景。
        if (!IsExecutionCurrent(stepEpoch, env.step_id)) yield break;
        if (managePerceptionMode)
        {
            yield return StartCoroutine(SetPerceptionMode("idle"));
            // 等待 perception 取得足夠影格以穩定偵測結果。
            yield return new WaitForSeconds(1.5f);
        }

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
        if (linear &&
            !IsLinearTcpPathClearOfBase(x, y, out float minimumPathRadius))
        {
            lastMotionSucceeded = false;
            lastMotionError = $"unsafe linear TCP path during {tag}: minimum base radius " +
                              $"{minimumPathRadius:F3}m is below {BASE_EXCLUSION_RADIUS_M:F3}m";
            Debug.LogError("[Executor] " + lastMotionError);
            yield break;
        }
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
            if (distance <= TCP_POSITION_TOLERANCE_M &&
                !urListener.RobotModeData.isProgramRunning)
            {
                lastMotionSucceeded = true;
                Debug.Log($"  [{tag}] REACHED: TCP error {distance * 1000f:F1} mm");
                yield break;
            }
            yield return new WaitForSeconds(0.05f);
        }

        if (protectiveStopDetected)
        {
            yield return WaitForManualSafetyRecovery(tag, stepEpoch, stepId);
            if (!safetyRecoverySucceeded)
                yield break;

            Debug.LogWarning(
                $"[Executor] Protective Stop recovery at {tag}: interrupted motion will NOT be retried; batch is stopped in place.");
            lastMotionSucceeded = false;
            lastMotionError = $"UR protective stop during {tag}; stopped in place without retrying the interrupted motion";
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
                if (maxError <= HOME_JOINT_TOLERANCE_RAD &&
                    !urListener.RobotModeData.isProgramRunning)
                {
                    lastMotionSucceeded = true;
                    Debug.Log($"  [{tag}] REACHED: max joint error {maxError * Mathf.Rad2Deg:F2} deg");
                    yield break;
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

    // 模擬版的 SendReady：實機每個 batch 開始前都會先 movej 到 readyJointsRad，
    // 預覽也必須從同一個姿態起步。少了這一步，第一個 move_above 會從 startpoint
    // （手臂打直、夾爪朝側面）直接拉一條斜線到 travel 平面，中途要求夾爪朝下的
    // 姿態根本不可達 —— 實機不會這樣走，預覽卻會演出來。
    IEnumerator SimGoToReadyPose(string tag)
    {
        if (!useReadyPose)
        {
            Debug.Log($"  [{tag}] SKIP: Use Ready Pose is off");
            yield break;
        }
        if (readyJointsRad == null || readyJointsRad.Length != 6)
        {
            Debug.LogError($"  [{tag}] Ready pose requires exactly 6 joint values");
            yield break;
        }

        float[] deg = new float[6];
        for (int i = 0; i < 6; i++) deg[i] = readyJointsRad[i] * Mathf.Rad2Deg;
        Debug.Log($"  [{tag}] ready pose = [{string.Join(", ", System.Array.ConvertAll(deg, a => a.ToString("F1")))}]°");
        yield return AnimateJointsTo(deg, simMoveSecPerStep * SIM_T_HOME);

        // IK 連續性的參考 q 也要跟著換，否則下一段 Cartesian 直線會從舊分支起算
        _lastIKJointsRad = new double[6];
        for (int i = 0; i < 6; i++) _lastIKJointsRad[i] = readyJointsRad[i];
    }

    IEnumerator SendReady(string tag, long stepEpoch, int stepId)
    {
        if (!IsExecutionCurrent(stepEpoch, stepId)) yield break;
        lastMotionSucceeded = false;
        lastMotionError = null;

        if (!useReadyPose)
        {
            lastMotionSucceeded = true;
            Debug.Log($"  [{tag}] SKIP: Use Ready Pose is off");
            yield break;
        }

        if (readyJointsRad == null || readyJointsRad.Length != 6)
        {
            lastMotionError = "Ready pose requires exactly 6 joint values";
            Debug.LogError("[Executor] " + lastMotionError);
            yield break;
        }

        float[] target = readyJointsRad;
        string readyCmd = BuildJointMovejLine(target);
        Debug.Log($"  [{tag}] SEND: {readyCmd}");
        urListener.SendCommand(readyCmd);
        yield return new WaitForSeconds(MOTION_START_GRACE_SEC);

        float startedAt = Time.realtimeSinceStartup;
        bool sawProgramRunning = urListener.RobotModeData.isProgramRunning;
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
                lastMotionError = $"UR safety stop during {tag}";
                yield break;
            }
            if (urListener.RobotModeData.isProgramRunning)
                sawProgramRunning = true;

            var joints = urListener.JointData.AsArray;
            float maxError = 0f;
            for (int i = 0; i < target.Length; i++)
            {
                float actual = (float)joints[i].q_actual;
                float error = Mathf.Abs(Mathf.DeltaAngle(actual * Mathf.Rad2Deg,
                    target[i] * Mathf.Rad2Deg)) * Mathf.Deg2Rad;
                maxError = Mathf.Max(maxError, error);
            }
            if (maxError <= HOME_JOINT_TOLERANCE_RAD &&
                !urListener.RobotModeData.isProgramRunning)
            {
                lastMotionSucceeded = true;
                Debug.Log($"  [{tag}] REACHED: max joint error {maxError * Mathf.Rad2Deg:F2} deg");
                yield break;
            }
            if (!sawProgramRunning && Time.realtimeSinceStartup - startedAt > 1.0f)
            {
                lastMotionError = $"UR did not start {tag}: {RobotStatusText()}";
                Debug.LogError("[Executor] " + lastMotionError);
                yield break;
            }
            yield return new WaitForSeconds(0.05f);
        }

        lastMotionError = $"UR motion timeout during {tag}: ready pose was not reached";
    }

    string BuildJointMovejLine(float[] joints)
    {
        return $"movej([{joints[0]:F4}, {joints[1]:F4}, {joints[2]:F4}, {joints[3]:F4}, {joints[4]:F4}, {joints[5]:F4}], a=1.2, v=0.8)";
    }

    IEnumerator SendCurrentTcpLiftToTravelHeight(
        float targetZ, string tag, long stepEpoch, int stepId)
    {
        if (!IsExecutionCurrent(stepEpoch, stepId)) yield break;
        lastMotionSucceeded = false;
        lastMotionError = null;

        var tcp = urListener.CartesianInfo;
        float x = (float)tcp.X;
        float y = (float)tcp.Y;
        float z = (float)tcp.Z;
        if (z >= targetZ - TCP_POSITION_TOLERANCE_M)
        {
            lastMotionSucceeded = true;
            Debug.Log($"  [{tag}] SKIP: TCP already lifted at Z={z:F4}");
            yield break;
        }

        float rx = (float)tcp.Rx;
        float ry = (float)tcp.Ry;
        float rz = (float)tcp.Rz;
        string cmd = $"movel(p[{x:F4}, {y:F4}, {targetZ:F4}, {rx:F4}, {ry:F4}, {rz:F4}], a=0.3, v=0.10)";
        Debug.Log($"  [{tag}] SEND: {cmd}");
        urListener.SendCommand(cmd);

        yield return new WaitForSeconds(MOTION_START_GRACE_SEC);
        float startedAt = Time.realtimeSinceStartup;
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

            var current = urListener.CartesianInfo;
            float dx = (float)current.X - x;
            float dy = (float)current.Y - y;
            float dz = (float)current.Z - targetZ;
            float distance = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distance <= TCP_POSITION_TOLERANCE_M &&
                !urListener.RobotModeData.isProgramRunning)
            {
                lastMotionSucceeded = true;
                Debug.Log($"  [{tag}] REACHED: TCP error {distance * 1000f:F1} mm");
                yield break;
            }
            yield return new WaitForSeconds(0.05f);
        }

        if (protectiveStopDetected)
        {
            yield return WaitForManualSafetyRecovery(tag, stepEpoch, stepId);
            if (!safetyRecoverySucceeded)
                yield break;
            lastMotionError = $"UR protective stop during {tag}; batch was not started";
            yield break;
        }

        var finalTcp = urListener.CartesianInfo;
        float finalDx = (float)finalTcp.X - x;
        float finalDy = (float)finalTcp.Y - y;
        float finalDz = (float)finalTcp.Z - targetZ;
        float finalDistance = Mathf.Sqrt(finalDx * finalDx + finalDy * finalDy + finalDz * finalDz);
        lastMotionError = $"UR motion timeout during {tag}: TCP remained {finalDistance * 1000f:F1} mm from lifted target";
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

    string RobotStatusText()
    {
        if (urListener == null)
            return "UR listener is not started";

        return $"connected={urListener.Connected}, " +
               $"robotMode={urListener.RobotModeData.robotMode}, " +
               $"safetyMode={urListener.MasterboardData.safetyMode}, " +
               $"programRunning={urListener.RobotModeData.isProgramRunning}, " +
               $"protectiveStopped={urListener.RobotModeData.isProtectiveStopped}, " +
               $"emergencyStopped={urListener.RobotModeData.isEmergencyStopped}";
    }

    bool InsideBaseExclusion(float x, float y)
    {
        return (x * x + y * y) < BASE_EXCLUSION_RADIUS_M * BASE_EXCLUSION_RADIUS_M;
    }

    IEnumerator SendTravelMoveWithBaseDetour(
        float targetX, float targetY, float targetZ, string orientation,
        float skewDeg, string tag, long stepEpoch, int stepId)
    {
        if (!IsExecutionCurrent(stepEpoch, stepId)) yield break;

        if (IsLinearTcpPathClearOfBase(targetX, targetY, out _))
        {
            yield return SendMove(targetX, targetY, targetZ, orientation, skewDeg,
                tag, true, stepEpoch, stepId);
            yield break;
        }

        var tcp = urListener.CartesianInfo;
        var waypoints = BuildBaseDetourWaypoints(
            (float)tcp.X, (float)tcp.Y, targetX, targetY);
        int arcSteps = waypoints.Count - 2;   // 扣掉 entry 與最後的目的地

        Debug.Log($"  [{tag}] Direct path crosses base exclusion; routing " +
                  $"around R={BASE_DETOUR_RADIUS_M:F3}m in {arcSteps} arc segment(s).");

        // Move radially to the routing circle, trace a short polygonal arc, then
        // move radially to the destination. Every segment remains outside the
        // exclusion cylinder and is independently checked by SendMove.
        for (int i = 0; i < waypoints.Count; i++)
        {
            string label = i == 0 ? " detour-entry"
                         : i == waypoints.Count - 1 ? " detour-exit"
                         : $" detour-arc {i}/{arcSteps}";
            yield return SendMove(waypoints[i].x, waypoints[i].y, targetZ,
                orientation, skewDeg, tag + label, true, stepEpoch, stepId);
            if (!lastMotionSucceeded) yield break;
        }
    }

    bool InsideSourceBaseExclusion(float x, float y)
    {
        return (x * x + y * y) <
               SOURCE_BASE_EXCLUSION_RADIUS_M * SOURCE_BASE_EXCLUSION_RADIUS_M;
    }

    // 水平移動要經過的 (x, y) 路徑點（UR base frame），最後一點一定是目的地。
    // 直線不會掃到底座就只回目的地；會掃到就回「徑向進入 → 沿 R=BASE_DETOUR_RADIUS_M
    // 的折線弧 → 徑向離開」。實機的 SendTravelMoveWithBaseDetour 跟模擬的直譯器
    // 都呼叫這個，兩邊才會走完全相同的路徑。
    List<Vector2> BuildBaseDetourWaypoints(float startX, float startY,
                                           float targetX, float targetY)
    {
        var points = new List<Vector2>();
        if (IsLinearPathClearOfBase(startX, startY, targetX, targetY, out _))
        {
            points.Add(new Vector2(targetX, targetY));
            return points;
        }

        float startAngle = Mathf.Atan2(startY, startX) * Mathf.Rad2Deg;
        float targetAngle = Mathf.Atan2(targetY, targetX) * Mathf.Rad2Deg;
        float angleDelta = Mathf.DeltaAngle(startAngle, targetAngle);
        int arcSteps = Mathf.Max(1,
            Mathf.CeilToInt(Mathf.Abs(angleDelta) / BASE_DETOUR_MAX_ANGLE_STEP_DEG));

        float startRad = startAngle * Mathf.Deg2Rad;
        points.Add(new Vector2(BASE_DETOUR_RADIUS_M * Mathf.Cos(startRad),
                               BASE_DETOUR_RADIUS_M * Mathf.Sin(startRad)));
        for (int i = 1; i <= arcSteps; i++)
        {
            float angle = (startAngle + angleDelta * i / arcSteps) * Mathf.Deg2Rad;
            points.Add(new Vector2(BASE_DETOUR_RADIUS_M * Mathf.Cos(angle),
                                   BASE_DETOUR_RADIUS_M * Mathf.Sin(angle)));
        }
        points.Add(new Vector2(targetX, targetY));
        return points;
    }

    bool IsLinearTcpPathClearOfBase(float targetX, float targetY, out float minimumRadius)
    {
        var tcp = urListener.CartesianInfo;
        return IsLinearPathClearOfBase((float)tcp.X, (float)tcp.Y,
                                       targetX, targetY, out minimumRadius);
    }

    // 純幾何版：起點也當參數傳進來，模擬沒有實機 TCP 可讀，必須用這個。
    bool IsLinearPathClearOfBase(float startX, float startY,
                                 float targetX, float targetY, out float minimumRadius)
    {
        float dx = targetX - startX;
        float dy = targetY - startY;
        float lengthSquared = dx * dx + dy * dy;
        float t = lengthSquared <= 1e-8f
            ? 0f
            : Mathf.Clamp01(-(startX * dx + startY * dy) / lengthSquared);
        float nearestX = startX + t * dx;
        float nearestY = startY + t * dy;
        minimumRadius = Mathf.Sqrt(nearestX * nearestX + nearestY * nearestY);
        return minimumRadius >= BASE_EXCLUSION_RADIUS_M;
    }

    bool OutsideReachEnvelope(float x, float y)
    {
        return (x * x + y * y) > MAX_REACH_RADIUS_M * MAX_REACH_RADIUS_M;
    }

    string EffectiveOrientation(NamedPosition pos, bool isSource)
    {
        if (pos == null)
            return "horizontal";
        // A cube is rotationally symmetric around the tool axis. Do not add a
        // needless 90-degree wrist rotation merely because it is a source pick.
        if (pos.shape != "domino")
            return "horizontal";
        return pos.orientation ?? "horizontal";
    }

    bool IsNearHomeJointPose()
    {
        if (urListener == null || !urListener.Connected)
            return false;

        float[] target = { -1.5708f, -1.5708f, 0f, -1.5708f, 0f, 0f };
        var joints = urListener.JointData.AsArray;
        float maxError = 0f;
        for (int i = 0; i < target.Length; i++)
        {
            float actual = (float)joints[i].q_actual;
            float error = Mathf.Abs(Mathf.DeltaAngle(actual * Mathf.Rad2Deg,
                target[i] * Mathf.Rad2Deg)) * Mathf.Deg2Rad;
            maxError = Mathf.Max(maxError, error);
        }
        return maxError <= 0.12f;
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

        // Bias IK toward the robot's current joint configuration.
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
        lastStepReportedSuccess = completed;
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

