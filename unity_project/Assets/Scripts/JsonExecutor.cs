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
    // csharp_server 收到指令時讀一次 Unity 的驗證開關，把結果蓋在整批上。
    // 刻意用 disabled 當欄位名：舊版 server 沒送這個欄位時 JsonUtility 讀成 false，
    // 等於驗證開啟，不會因為兩邊版本不同就默默變成對照組。
    public bool verification_disabled;
    // 模擬結束比對 bitmap 用；只有排 pattern 的指令才有，其他指令為空，Unity 就不比對
    public List<string> bitmap;
    public List<ExpectedCell> expected_cells;
    public float cell_size_m;
}

// bitmap 裡一個應該放積木的物件（cube 佔一格，domino 佔兩格），QR frame 座標
[System.Serializable]
public class ExpectedCell
{
    public int row;
    public int col;
    public int second_row = -1;   // domino 的第二格；cube 為 -1
    public int second_col = -1;
    public float x, y;
    public float z;               // 方塊頂面高度
    public string shape;
    public string orientation;
}

// 模擬結束比對 bitmap 的結果，寫到 StreamingAssets/sim_check.json 給 csharp_server 印在 terminal
[System.Serializable]
public class SimulationCheckReport
{
    public int batch_id;
    public bool performed;             // false = 這批有 bitmap 但沒做到比對（例如沒有播模擬）
    public string skipped_reason;
    public bool passed;
    public bool verification_enabled;  // 開 = 不通過就擋；關 = 只記錄
    public int expected_count;
    public int correct_count;
    public List<string> expected_rows;
    public List<string> result_rows;
    public List<string> errors;
    public List<string> notes;
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

    [Header("Unity 模擬預覽（實機執行前演一次）")]
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

    [Header("預演 / 實機共用 movej 軌跡")]
    public bool useSharedMovejTrajectory = true;
    public bool previewOnlySharedTrajectory = false;
    [Range(0.5f, 5f)] public float trajectoryCollisionSampleDeg = 2f;
    public float sharedMovejAcceleration = 0.40f;
    public float sharedMovejVelocity = 0.50f;
    public bool allowTopLeftCubeYaw180OnHardware = true;

    [Header("模擬結束比對 bitmap（屬於驗證，受一鍵驗證開關控制）")]
    // 落點中心離 bitmap 格子中心多遠以內算放對（公尺）。格距 4 cm、方塊 2.5 cm，間隙只有 1.5 cm
    public float bitmapXYToleranceM = 0.010f;
    // 落點頂面高度跟預期差多少以內算對；超過代表疊到別的方塊上，或在空中放開
    public float bitmapZToleranceM = 0.010f;
    // 模擬夾取時，夾爪 TCP 離方塊中心多遠以內才夾得到
    public float simGraspToleranceM = 0.015f;

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
    // wait 沒給秒數時的預設值，跟 csharp_server MotionPlanner.DefaultWaitSeconds 一致
    private const float DefaultWaitSeconds = 0.2f;
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
    private const float SOURCE_BASE_EXCLUSION_RADIUS_M = 0.20f;
    // High-plane lateral travel is routed around this radius when a direct
    // Cartesian chord would cut through the base exclusion cylinder.
    private const float BASE_DETOUR_RADIUS_M = 0.28f;
    private const float BASE_DETOUR_MAX_ANGLE_STEP_DEG = 25f;
    // Avoid poses that make the UR3e almost fully extend. Those IK solutions are
    // fragile and can trigger a protective stop before the TCP reaches the block.
    private const float MAX_SOURCE_REACH_RADIUS_M = 0.42f;
    private const float MAX_TARGET_REACH_RADIUS_M = 0.47f;
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

    class PlannedJointAction
    {
        public string function;
        public readonly List<double[]> targets = new List<double[]>();
        public float seconds;
    }

    private readonly Dictionary<int, List<PlannedJointAction>> sharedTrajectory =
        new Dictionary<int, List<PlannedJointAction>>();
    private readonly List<double[]> sharedFinalTrajectory = new List<double[]>();
    private double[] sharedTrajectoryStartQ;

    // ---- 一鍵開關（實驗組 / 對照組）----
    // 由每一批的 BatchEnvelope.verification_disabled 決定，整批用同一個值。
    // 只控制「模擬結束比對 bitmap」這一關。動作規劃檢查、軌跡奇點 / 碰撞檢查、
    // 硬體安全範圍一律開著，不受這個開關影響。
    private bool verificationEnabled = true;

    // ---- 模擬結束比對 bitmap ----
    // 模擬過程中每顆方塊的狀態。落點由夾爪 TCP（正向運動學）決定，不直接瞬移到指令目標，
    // 比對才驗得出「手臂實際把方塊放在哪」，而不是「指令寫了放哪」。
    class SimBlockState
    {
        public bool isDomino;
        public float angleDeg;       // domino 長軸：0 = 沿 robot X（horizontal），90 = 沿 robot Y（vertical）
        public float graspYawDeg;    // 夾起時夾爪的 yaw；放開時的差值就是方塊被轉的角度
        public float graspOffsetZ;   // 夾起時 TCP 在方塊頂面上方多少；搬運中方塊跟著夾爪，這個距離不變
        public bool held;
        public bool released;        // 這一批裡被放下過
        public Vector3 landedQR;     // x, y = 中心；z = 頂面
        public float dropM;          // 放開瞬間方塊底面離支撐面；>0 在空中放開，<0 壓進下面的方塊
        public string releaseLabel;
    }
    private readonly Dictionary<GameObject, SimBlockState> simBlocks = new Dictionary<GameObject, SimBlockState>();
    // 夾取落空、序列結束仍夾著等狀況。通常就是「少放」的原因，跟報告一起印，但本身不算錯
    private readonly List<string> simPlacementNotes = new List<string>();
    // 最近一次比對的錯誤清單；null = 這一批沒有 bitmap（非排 pattern 指令），沒比對
    private List<string> bitmapCheckErrors;
    // 這一批的比對結果有沒有寫給 csharp_server；沒寫的話要補一份「未進行」
    private bool simCheckReported;

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

        DiscardStaleCommandFile();
        EnsureUrConnectionStarted();
        StartCoroutine(PollLoop());
    }

    // PollLoop 用 lastProcessedStepJson 判斷指令是不是新的，但這份紀錄每次按 Play 都從空的開始；
    // current_step.json 執行完又沒有人刪（只有 csharp_server 重開時才清），
    // 所以不清掉的話，重按 Play 會把上一批當新指令整批重跑，實機也會跟著動。
    // 啟動時一律刪掉；若那一批還沒結束，回報失敗，讓還在等它的 server 立刻回到待命，不用空等到逾時。
    void DiscardStaleCommandFile()
    {
        string stepPath = Path.Combine(Application.streamingAssetsPath, currentStepFile);
        if (!File.Exists(stepPath)) return;

        int staleId = -1;
        bool staleDone = true;
        try
        {
            string json = File.ReadAllText(stepPath);
            File.Delete(stepPath);
            try
            {
                if (json.Contains("\"steps\""))
                {
                    var batch = JsonUtility.FromJson<BatchEnvelope>(json);
                    if (batch != null) { staleId = batch.batch_id; staleDone = batch.done; }
                }
                else
                {
                    var step = JsonUtility.FromJson<StepEnvelope>(json);
                    if (step != null) { staleId = step.step_id; staleDone = step.done; }
                }
            }
            catch (Exception)
            {
                // 內容壞掉也沒關係，檔案已經刪了，只是不知道要回報哪個 id
            }
        }
        catch (IOException ex)
        {
            Debug.LogError($"[Executor] 無法刪除上一次留下的 {currentStepFile}：{ex.Message}。" +
                           "請手動刪除，否則上一批會被重新執行");
            return;
        }

        Debug.LogWarning($"[Executor] 啟動時發現上一次留下的 {currentStepFile}（id {staleId}），已刪除、不會執行");
        if (!staleDone && staleId > 0)
            WriteStepDone(staleId, false, "Unity restarted; stale command discarded without execution", 0f);
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
        bool usingTopLeftFallback = false;

        if (previewOnlySharedTrajectory && !useSharedMovejTrajectory)
        {
            WriteStepDone(batch.batch_id, false,
                "preview-only requires the shared joint trajectory", 0f);
            yield break;
        }

        verificationEnabled = !batch.verification_disabled;
        ResetSimPlacementTracking();
        Debug.Log(verificationEnabled
            ? $"[Verification] batch {batch.batch_id}：模擬結束比對開啟（實驗組）"
            : $"[Verification] batch {batch.batch_id}：模擬結束比對關閉（對照組）— 比對結果只記錄不擋；其他檢查照常生效");

        if (useSharedMovejTrajectory)
        {
            if (!useReadyPose || readyJointsRad == null || readyJointsRad.Length != 6)
            {
                WriteStepDone(batch.batch_id, false,
                    "shared movej trajectory requires a calibrated 6-axis Ready pose", 0f);
                yield break;
            }
            if (robotArm == null) robotArm = FindObjectOfType<RobotArm>();
            double[] startQ = useReadyPose && readyJointsRad != null && readyJointsRad.Length == 6
                ? System.Array.ConvertAll(readyJointsRad, value => (double)value)
                : DegArrayToRad(simIKReferenceDeg);
            bool trajectoryReady = BuildSharedTrajectory(batch, startQ, false, out string trajectoryError);
            if (!trajectoryReady)
            {
                StepEnvelope topLeftCube = batch.steps.Find(step =>
                    step != null && step.target_position != null &&
                    step.target_position.shape == "cube" &&
                    step.target_position.name == "target_cube_r0_c0");
                if (topLeftCube != null &&
                    trajectoryError.StartsWith($"step {topLeftCube.step_id} action ", StringComparison.Ordinal) &&
                    trajectoryError.Contains("self-collision"))
                {
                    // A 180-degree yaw preserves the parallel-jaw footprint at this cube.
                    string originalError = trajectoryError;
                    trajectoryReady = BuildSharedTrajectory(batch, startQ, true, out trajectoryError);
                    if (trajectoryReady)
                    {
                        usingTopLeftFallback = true;
                        Debug.Log($"[Executor-shared] step {topLeftCube.step_id} top-left cube uses 180-degree tool yaw; all other target orientations unchanged.");
                    }
                    else
                        trajectoryError = originalError + "; top-left cube 180-degree yaw also rejected: " + trajectoryError;
                }
            }
            if (!trajectoryReady)
            {
                string message = $"shared movej trajectory rejected before preview: {trajectoryError}";
                Debug.LogError("[Executor-shared] " + message);
                WriteStepDone(batch.batch_id, false, message, 0f);
                yield break;
            }
        }

        // ★ Pre-flight kinematics 檢查：任一步不可達/奇點就 abort，不動實機
        if (!useSharedMovejTrajectory && useCanonicalKinematics && verifyBatchReachability)
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

        EnsureUrConnectionStarted();
        yield return StartCoroutine(SetPerceptionMode("executing"));
        if (sceneSyncer == null)
            sceneSyncer = FindObjectOfType<SceneSyncer>();
        if (robotArm == null)
            robotArm = FindObjectOfType<RobotArm>();
        if (previewBatchInUnityBeforeRobot || previewOnlySharedTrajectory || usingTopLeftFallback)
        {
            // 動畫預覽：手臂 + 方塊完整演示（跑完自動復原）
            // 預覽期間關 followRealRobotFeedback，讓 Update() 用我們設的 Angles 而非實機 q_actual
            if (robotArm != null) robotArm.followRealRobotFeedback = false;
            RobotArm.FreezeVisualFeedback = false;
            yield return StartCoroutine(PreviewBatchAnimated(batch));
            ReportSimulationCheckSkippedIfNeeded(batch, "模擬預覽沒有執行（SceneSyncer 未設定）");

            // 模擬結束比對 bitmap：不一致且驗證開啟時，實機完全不動
            if (!BitmapCheckAllowsContinue(batch))
            {
                if (robotArm != null) robotArm.followRealRobotFeedback = true;
                RobotArm.FreezeVisualFeedback = false;
                yield return StartCoroutine(SetPerceptionMode("idle"));
                yield break;
            }
        }
        else
        {
            ReportSimulationCheckSkippedIfNeeded(batch,
                "previewBatchInUnityBeforeRobot 關閉，沒有播模擬，直接送實機");
        }
        if (previewOnlySharedTrajectory)
        {
            if (robotArm != null) robotArm.followRealRobotFeedback = true;
            RobotArm.FreezeVisualFeedback = false;
            yield return StartCoroutine(SetPerceptionMode("idle"));
            WriteStepDone(batch.batch_id, false,
                "preview-only: shared trajectory shown; no UR motion sent", 0f);
            Debug.Log("[Executor-preview] Preview-only finished; UR motion was not sent.");
            yield break;
        }
        if (usingTopLeftFallback && !allowTopLeftCubeYaw180OnHardware)
        {
            if (robotArm != null) robotArm.followRealRobotFeedback = true;
            RobotArm.FreezeVisualFeedback = false;
            yield return StartCoroutine(SetPerceptionMode("idle"));
            WriteStepDone(batch.batch_id, false,
                "top-left cube 180-degree yaw previewed; hardware motion requires explicit Inspector approval", 0f);
            Debug.LogWarning("[Executor-preview] Top-left cube yaw fallback previewed only; no UR motion sent.");
            yield break;
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

        for (int i = 0; i < batch.steps.Count; i++)
        {
            StepEnvelope env = batch.steps[i];
            if (env == null || env.done) continue;

            if (env.source_position == null || env.target_position == null)
            {
                Debug.LogWarning($"[Executor] batch step {env?.step_id} 缺少 source/target");
                WriteStepDone(env != null ? env.step_id : batch.batch_id, false, "batch step missing source/target", 0f);
                RobotArm.FreezeVisualFeedback = false;
                yield return StartCoroutine(SetPerceptionMode("idle"));
                yield break;
            }

            lastExecutedStepId = env.step_id;
            currentStepId = env.step_id;
            long stepEpoch = ++executionEpoch;
            // perception mode 由整批統一管理，單步不自己切換
            currentStepCoroutine = StartCoroutine(ExecuteStep(env, stepEpoch, managePerceptionMode: false));
            yield return currentStepCoroutine;
            currentStepCoroutine = null;
            currentStepId = -1;

            if (stepEpoch != executionEpoch || !lastStepReportedSuccess)
            {
                Debug.LogWarning($"[Executor] batch {batch.batch_id} stopped after step {env.step_id}");
                RobotArm.FreezeVisualFeedback = false;
                yield return StartCoroutine(SetPerceptionMode("idle"));
                yield break;
            }
        }

        if (useSharedMovejTrajectory)
        {
            currentStepId = batch.batch_id;
            long finalEpoch = ++executionEpoch;
            for (int i = 0; i < sharedFinalTrajectory.Count; i++)
            {
                yield return SendExplicitJointTarget(sharedFinalTrajectory[i],
                    $"batch final shared {i + 1}/{sharedFinalTrajectory.Count}",
                    finalEpoch, batch.batch_id);
                if (!lastMotionSucceeded)
                {
                    WriteStepDone(batch.batch_id, false,
                        lastMotionError ?? "UR final shared trajectory failed", 0f);
                    RobotArm.FreezeVisualFeedback = false;
                    yield return StartCoroutine(SetPerceptionMode("idle"));
                    currentStepId = -1;
                    yield break;
                }
            }
            currentStepId = -1;
            yield return StartCoroutine(SetPerceptionMode("idle"));
            yield return new WaitForSeconds(1.5f);
        }
        else
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

        long finalReadyEpoch = ++executionEpoch;
        yield return SendReady("batch final ready", finalReadyEpoch, batch.batch_id);
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
    // 模擬預覽的手臂預設姿態（從 Inspector 讀，方便動態調整）
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
        double urX = QR1_X + qrX;
        double urY = QR1_Y + qrY;
        double urZ = QR1_Z + qrZ + extraZAboveCube;

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

    bool BuildSharedTrajectory(BatchEnvelope batch, double[] startQ,
        bool reverseTopLeftCube, out string error)
    {
        sharedTrajectory.Clear();
        sharedFinalTrajectory.Clear();
        sharedTrajectoryStartQ = (double[])startQ.Clone();
        double[] reference = (double[])startQ.Clone();
        UR3eKinematics.toolOffsetZ = robotArm != null ? robotArm.toolOffsetZ : 0.0;

        foreach (var env in batch.steps)
        {
            if (env == null || env.done) continue;
            var planned = new List<PlannedJointAction>();
            if (env.action_sequence == null)
            {
                error = $"step {env.step_id} has no action_sequence";
                return false;
            }

            bool holding = false;
            for (int actionIndex = 0; actionIndex < env.action_sequence.Count; actionIndex++)
            {
                var action = env.action_sequence[actionIndex];
                var pa = new PlannedJointAction
                {
                    function = action.function,
                    seconds = Mathf.Clamp(action.seconds > 0f ? action.seconds : DefaultWaitSeconds, 0.1f, 3f)
                };

                bool source = action.location == "source";
                NamedPosition pos = source ? env.source_position : env.target_position;
                if (pos == null && action.function != "wait" && action.function != "go_home")
                {
                    error = $"step {env.step_id} action {actionIndex + 1} lacks position";
                    return false;
                }

                float x = pos == null ? 0f : QR1_X + pos.x + (source ? pickOffsetX : 0f);
                float y = pos == null ? 0f : QR1_Y + pos.y + (source ? pickOffsetY : 0f);
                float z = pos == null ? 0f : QR1_Z + pos.z + Z_CORRECTION;
                float height = Mathf.Clamp(action.height_m > 0f ? action.height_m : SAFE_Z_OFFSET, 0.05f, 0.15f);
                string orientation = pos == null ? "horizontal" : EffectiveOrientation(pos, source);
                if (!source && reverseTopLeftCube &&
                    env.target_position != null && env.target_position.shape == "cube" &&
                    env.target_position.name == "target_cube_r0_c0")
                    orientation = "cube_yaw_180";

                if (action.function == "move_above")
                {
                    var current = UR3eKinematics.FKPose(reference);
                    if (!PlanMoveAbove(pa, ref reference, current, x, y, z + height,
                            orientation, out error))
                    {
                        error = $"step {env.step_id} action {actionIndex + 1}: {error}";
                        return false;
                    }
                }
                else if (action.function == "descend")
                {
                    if (!source && holding) z += Mathf.Max(0f, placeDescendExtraZ);
                    if (!PlanJointPose(pa, ref reference, x, y, z, orientation, out error))
                    {
                        error = $"step {env.step_id} action {actionIndex + 1}: {error}";
                        return false;
                    }
                }
                else if (action.function == "lift")
                {
                    if (!PlanJointPose(pa, ref reference, x, y, z + height, orientation, out error))
                    {
                        error = $"step {env.step_id} action {actionIndex + 1}: {error}";
                        return false;
                    }
                }
                else if (action.function == "go_home")
                {
                    double[] home = { -1.5708, -1.5708, 0.0, -1.5708, 0.0, 0.0 };
                    if (!ValidateJointTransition(reference, home, out error, allowSingularEnd: true))
                    {
                        error = $"step {env.step_id} action {actionIndex + 1}: {error}";
                        return false;
                    }
                    pa.targets.Add(home);
                    reference = (double[])home.Clone();
                }

                if (action.function == "grasp") holding = true;
                if (action.function == "release") holding = false;
                planned.Add(pa);
            }
            sharedTrajectory[env.step_id] = planned;
        }

        // The old executor returned through Ready and Home after the batch. Plan
        // and validate that return too so preview and hardware end with the same
        // motions rather than appending unpreviewed commands.
        double[] calibratedReady = System.Array.ConvertAll(readyJointsRad, value => (double)value);
        // The fixed calibrated Ready joints may be on the opposite elbow IK
        // branch from the final pick/place pose. A direct movej interpolation
        // would then have to cross q3=0/PI and trip the elbow singularity gate.
        // Preserve the calibrated Ready TCP pose, but solve it from the current
        // branch so preview and hardware can return without changing branches.
        var readyPose = UR3eKinematics.FKPose(calibratedReady);
        var readySolution = UR3eKinematics.IKNearest(readyPose, reference);
        if (!readySolution.ok)
        {
            error = $"final Ready IK {readySolution.error}: {readySolution.message}";
            return false;
        }
        double[] ready = readySolution.q;
        if (!ValidateJointTransition(reference, ready, out error, allowSingularEnd: false))
        {
            error = "final Ready transition: " + error;
            return false;
        }
        sharedFinalTrajectory.Add((double[])ready.Clone());
        reference = ready;
        double[] homeTarget = { -1.5708, -1.5708, 0.0, -1.5708, 0.0, 0.0 };
        if (!ValidateJointTransition(reference, homeTarget, out error, allowSingularEnd: true))
        {
            error = "final Home transition: " + error;
            return false;
        }
        sharedFinalTrajectory.Add(homeTarget);

        error = null;
        Debug.Log($"[Executor-shared] Built and collision-checked shared movej trajectory for {sharedTrajectory.Count} steps.");
        return true;
    }

    // ============================================================
    // 模擬結束比對 bitmap
    // ============================================================
    void ResetSimPlacementTracking()
    {
        simCheckReported = false;
        simBlocks.Clear();
        simPlacementNotes.Clear();
        bitmapCheckErrors = null;
    }

    SimBlockState SimBlock(GameObject block)
    {
        if (simBlocks.TryGetValue(block, out var state)) return state;
        Vector3 scale = block.transform.localScale;
        state = new SimBlockState
        {
            isDomino = block.name.Contains("domino"),
            // SceneSyncer 的慣例：vertical 長軸沿 Unity X（= robot Y），horizontal 沿 Unity Z（= robot X）
            angleDeg = Mathf.Abs(scale.x) > Mathf.Abs(scale.z) + 1e-4f ? 90f : 0f,
        };
        simBlocks[block] = state;
        return state;
    }

    static bool DominoIsVertical(float angleDeg)
    {
        float a = Mathf.Repeat(angleDeg, 180f);
        return a >= 45f && a < 135f;
    }

    Transform SimBlockFrame() =>
        sceneSyncer.GetCubeContainer() != null ? sceneSyncer.GetCubeContainer() : sceneSyncer.transform;

    // 方塊在 QR frame 的位置：x, y = 中心，z = 頂面（跟 perception / csharp_server 同一個慣例）
    Vector3 BlockQR(GameObject block)
    {
        Vector3 local = SimBlockFrame().InverseTransformPoint(block.transform.position);
        Vector3 qr = SceneSyncer.UnityToQR(local);
        qr.z += sceneSyncer.cubeSizeM / 2f;
        return qr;
    }

    // 找 source 位置上相機實際看到的方塊。找不到只提醒、不補生：
    // 真實場景那裡沒有方塊，實機就會夾空，預覽也該演成夾空。
    GameObject WarnIfNoSourceBlock(StepEnvelope env, string tag)
    {
        var block = sceneSyncer.FindNearestCube(
            env.source_position.x, env.source_position.y, env.source_position.z, simGraspToleranceM);
        if (block == null)
            Debug.LogWarning($"[Executor-{tag}] step {env.step_id}: source QR({env.source_position.x:F3}, " +
                             $"{env.source_position.y:F3}) {simGraspToleranceM * 1000f:F0} mm 內沒有方塊，實機會夾空");
        return block;
    }

    bool CanReadSimTcp() =>
        robotArm != null && robotArm.Angles != null && robotArm.Angles.Length >= 6;

    // 模擬手臂 TCP 在 QR frame 的位置，以及夾爪繞垂直軸的 yaw（度）。
    // 跟產生軌跡用同一套運動學，所以就是實機 movej 到這組關節角時的落點。
    Vector3 SimTcpQR(out float toolYawDeg)
    {
        var q = new double[6];
        for (int i = 0; i < 6; i++) q[i] = robotArm.Angles[i] * Mathf.Deg2Rad;
        UR3eKinematics.toolOffsetZ = robotArm.toolOffsetZ;
        double[,] T = UR3eKinematics.FK(q);
        toolYawDeg = (float)(System.Math.Atan2(T[1, 0], T[0, 0]) * 180.0 / System.Math.PI);
        return new Vector3((float)T[0, 3] - QR1_X, (float)T[1, 3] - QR1_Y, (float)T[2, 3] - QR1_Z);
    }

    // 模擬夾取：只夾得到夾爪正下方的方塊。descend 之後 TCP 在方塊頂面上方 contactClearanceM。
    // 夾不到就回傳 null —— 動畫照演，但沒有方塊被搬走，比對時就會出現「少放」。
    GameObject SimGrasp(float contactClearanceM, string label)
    {
        if (!CanReadSimTcp())
        {
            simPlacementNotes.Add($"{label}: 讀不到模擬手臂姿態，無法判斷夾取");
            return null;
        }
        Vector3 tcp = SimTcpQR(out float yawDeg);
        float expectedTop = tcp.z - contactClearanceM;
        GameObject best = null;
        float bestDistance = float.MaxValue;
        float bestTop = 0f;
        foreach (var block in sceneSyncer.GetCurrentCubes())
        {
            if (block == null || SimBlock(block).held) continue;
            Vector3 qr = BlockQR(block);
            float d = Vector2.Distance(new Vector2(qr.x, qr.y), new Vector2(tcp.x, tcp.y));
            if (d > simGraspToleranceM || Mathf.Abs(qr.z - expectedTop) > bitmapZToleranceM) continue;
            if (d < bestDistance) { bestDistance = d; best = block; bestTop = qr.z; }
        }
        if (best == null)
        {
            simPlacementNotes.Add($"{label}: 夾取落空，TCP QR({tcp.x:F3},{tcp.y:F3}) 下方 " +
                                  $"{simGraspToleranceM * 1000f:F0} mm 內沒有頂面在 {expectedTop:F3} m 的方塊");
            return null;
        }
        var state = SimBlock(best);
        state.held = true;
        state.graspYawDeg = yawDeg;
        state.graspOffsetZ = tcp.z - bestTop;
        return best;
    }

    // 模擬放開：方塊中心落在 TCP 正下方，往下掉到最近的支撐面（桌面或其他方塊頂）。
    // domino 方向 = 夾起時的方向 + 搬運途中夾爪轉過的角度。畫面跟判定用同一個落點。
    void SimRelease(GameObject block, string label)
    {
        var state = SimBlock(block);
        state.held = false;
        if (!CanReadSimTcp())
        {
            simPlacementNotes.Add($"{label}: 讀不到模擬手臂姿態，無法判斷落點");
            return;
        }
        Vector3 tcp = SimTcpQR(out float yawDeg);
        float size = sceneSyncer.cubeSizeM;

        float supportTop = 0f;
        foreach (var other in sceneSyncer.GetCurrentCubes())
        {
            if (other == null || other == block || SimBlock(other).held) continue;
            Vector3 qr = BlockQR(other);
            if (Vector2.Distance(new Vector2(qr.x, qr.y), new Vector2(tcp.x, tcp.y)) < size * 0.9f)
                supportTop = Mathf.Max(supportTop, qr.z);
        }

        float heldBottom = tcp.z - state.graspOffsetZ - size;
        state.dropM = heldBottom - supportTop;
        state.landedQR = new Vector3(tcp.x, tcp.y, supportTop + size);
        if (state.isDomino)
            state.angleDeg = Mathf.Repeat(state.angleDeg + Mathf.DeltaAngle(state.graspYawDeg, yawDeg), 180f);
        state.released = true;
        state.releaseLabel = label;

        block.transform.SetParent(SimBlockFrame(), true);
        Vector3 local = SceneSyncer.QRToUnity(state.landedQR.x, state.landedQR.y, state.landedQR.z);
        local.y -= size / 2f;
        block.transform.localPosition = local;
        block.transform.localRotation = Quaternion.identity;
        block.transform.localScale = !state.isDomino
            ? Vector3.one * size
            : DominoIsVertical(state.angleDeg)
                ? new Vector3(size * 2f, size, size)
                : new Vector3(size, size, size * 2f);
    }

    static string ExpectedCellName(ExpectedCell cell)
    {
        string cells = cell.second_row >= 0
            ? $"r{cell.row}c{cell.col}+r{cell.second_row}c{cell.second_col}"
            : $"r{cell.row}c{cell.col}";
        return cell.shape == "domino" ? $"{cells} domino {cell.orientation}" : $"{cells} {cell.shape}";
    }

    // 模擬結束後比對：每個 bitmap 物件是否有一顆方塊落在對應格子（容許誤差內），
    // 形狀、domino 方向、高度也要對；多放、少放、放錯格、在空中放開都算錯。
    // 把報告印出來，錯誤清單存在 bitmapCheckErrors（空 = 通過，null = 這批沒有 bitmap）。
    void RunBitmapCheck(BatchEnvelope batch)
    {
        bitmapCheckErrors = null;
        if (batch.expected_cells == null || batch.expected_cells.Count == 0 || sceneSyncer == null) return;

        var expected = batch.expected_cells;
        var errors = new List<string>();
        float cell = batch.cell_size_m > 0f ? batch.cell_size_m : 0.04f;

        // 用任一個預期物件推回格子座標系。row 往下增加時 y 變小（LayoutRealizer 讓字母不上下顛倒）
        ExpectedCell anchor = expected[0];
        float anchorX = anchor.x - (anchor.second_col >= 0 ? (anchor.second_col - anchor.col) * 0.5f * cell : 0f);
        float anchorY = anchor.y + (anchor.second_row >= 0 ? (anchor.second_row - anchor.row) * 0.5f * cell : 0f);
        int rows = batch.bitmap != null && batch.bitmap.Count > 0
            ? batch.bitmap.Count
            : expected.Max(c => Mathf.Max(c.row, c.second_row)) + 1;
        int cols = batch.bitmap != null && batch.bitmap.Count > 0
            ? batch.bitmap[0].Length
            : expected.Max(c => Mathf.Max(c.col, c.second_col)) + 1;
        (int r, int c) NearestGridCell(Vector3 qr) => (
            anchor.row - Mathf.RoundToInt((qr.y - anchorY) / cell),
            anchor.col + Mathf.RoundToInt((qr.x - anchorX) / cell));
        bool InsideCanvas((int r, int c) g) => g.r >= 0 && g.r < rows && g.c >= 0 && g.c < cols;

        // 候選：這一批放下的方塊 + 原本就躺在圖案範圍內的方塊（殘留的方塊一樣會破壞字形）
        var candidates = new List<(GameObject block, Vector3 qr, SimBlockState state)>();
        foreach (var block in sceneSyncer.GetCurrentCubes())
        {
            if (block == null) continue;
            var state = SimBlock(block);
            if (state.held) continue;
            Vector3 qr = state.released ? state.landedQR : BlockQR(block);
            if (!state.released && !InsideCanvas(NearestGridCell(qr))) continue;
            candidates.Add((block, qr, state));
        }

        // 一對一配對：全部距離由小到大，各自還沒配到才配
        var pairs = new List<(int e, int c, float d)>();
        for (int e = 0; e < expected.Count; e++)
            for (int c = 0; c < candidates.Count; c++)
            {
                float d = Vector2.Distance(new Vector2(expected[e].x, expected[e].y),
                                           new Vector2(candidates[c].qr.x, candidates[c].qr.y));
                if (d <= bitmapXYToleranceM) pairs.Add((e, c, d));
            }
        pairs.Sort((a, b) => a.d.CompareTo(b.d));
        var matchOf = Enumerable.Repeat(-1, expected.Count).ToArray();
        var candidateUsed = new bool[candidates.Count];
        foreach (var p in pairs)
        {
            if (matchOf[p.e] >= 0 || candidateUsed[p.c]) continue;
            matchOf[p.e] = p.c;
            candidateUsed[p.c] = true;
        }

        var grid = new char[rows, cols];
        for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) grid[r, c] = '□';
        void Mark(int r, int c, char symbol) { if (r >= 0 && r < rows && c >= 0 && c < cols) grid[r, c] = symbol; }

        int correct = 0;
        for (int e = 0; e < expected.Count; e++)
        {
            var exp = expected[e];
            string name = ExpectedCellName(exp);
            bool ok = true;
            if (matchOf[e] < 0)
            {
                errors.Add($"少放 {name} @ QR({exp.x:F3},{exp.y:F3})");
                ok = false;
            }
            else
            {
                var cand = candidates[matchOf[e]];
                bool expectDomino = exp.shape == "domino";
                if (expectDomino != cand.state.isDomino)
                {
                    errors.Add($"形狀不符 {name}：放的是 {(cand.state.isDomino ? "domino" : "cube")}（{cand.block.name}）");
                    ok = false;
                }
                else if (expectDomino && (exp.orientation == "vertical") != DominoIsVertical(cand.state.angleDeg))
                {
                    errors.Add($"方向不符 {name}：放成 {(DominoIsVertical(cand.state.angleDeg) ? "vertical" : "horizontal")}");
                    ok = false;
                }
                if (Mathf.Abs(cand.qr.z - exp.z) > bitmapZToleranceM)
                {
                    errors.Add($"高度不符 {name}：頂面 {cand.qr.z:F3} m，應為 {exp.z:F3} m（疊到其他方塊上）");
                    ok = false;
                }
                if (cand.state.released && cand.state.dropM > bitmapZToleranceM)
                {
                    errors.Add($"在空中放開 {name}：方塊離支撐面 {cand.state.dropM * 100f:F1} cm（{cand.state.releaseLabel}）");
                    ok = false;
                }
                else if (cand.state.released && cand.state.dropM < -bitmapZToleranceM)
                {
                    errors.Add($"放開時壓到下方方塊 {name}：重疊 {-cand.state.dropM * 100f:F1} cm（{cand.state.releaseLabel}）");
                    ok = false;
                }
            }
            if (ok) correct++;
            char symbol = ok ? '■' : '✗';
            Mark(exp.row, exp.col, symbol);
            if (exp.second_row >= 0) Mark(exp.second_row, exp.second_col, symbol);
        }

        for (int c = 0; c < candidates.Count; c++)
        {
            if (candidateUsed[c]) continue;
            var cand = candidates[c];
            var g = NearestGridCell(cand.qr);
            string where = InsideCanvas(g) ? $"r{g.r}c{g.c}" : "圖案範圍外";
            var nearest = expected.OrderBy(x => Vector2.Distance(new Vector2(x.x, x.y),
                                                                 new Vector2(cand.qr.x, cand.qr.y))).First();
            float offMm = Vector2.Distance(new Vector2(nearest.x, nearest.y), new Vector2(cand.qr.x, cand.qr.y)) * 1000f;
            if (cand.state.released)
                errors.Add($"放錯位置 {cand.block.name} @ QR({cand.qr.x:F3},{cand.qr.y:F3}) 落在 {where}，" +
                           $"最近的預期格 {ExpectedCellName(nearest)} 偏 {offMm:F0} mm（{cand.state.releaseLabel}）");
            else
                errors.Add($"多放 {cand.block.name} @ QR({cand.qr.x:F3},{cand.qr.y:F3})：原本就在圖案範圍 {where}");
            if (InsideCanvas(g)) Mark(g.r, g.c, grid[g.r, g.c] == '□' ? '●' : '✗');
        }

        var resultRows = new List<string>();
        for (int r = 0; r < rows; r++)
        {
            var line = new System.Text.StringBuilder();
            for (int c = 0; c < cols; c++) line.Append(grid[r, c]);
            resultRows.Add(line.ToString());
        }

        var report = new System.Text.StringBuilder();
        report.AppendLine(errors.Count == 0
            ? $"[BitmapCheck] batch {batch.batch_id}：✓ 模擬結果與 bitmap 一致（{expected.Count} 個物件全部放對）"
            : $"[BitmapCheck] batch {batch.batch_id}：✗ 模擬結果與 bitmap 不一致，" +
              $"預期 {expected.Count} 個物件，放對 {correct}，錯誤 {errors.Count} 項");
        report.AppendLine("  預期 bitmap    模擬結果（■ 正確  ✗ 少放/錯誤  ● 多放/放錯  □ 空）");
        for (int r = 0; r < rows; r++)
        {
            string want = batch.bitmap != null && r < batch.bitmap.Count ? batch.bitmap[r] : "";
            report.AppendLine($"  {want.PadRight(cols)}          {resultRows[r]}");
        }
        foreach (var error in errors) report.AppendLine("  - " + error);
        foreach (var note in simPlacementNotes) report.AppendLine("  · " + note);

        if (errors.Count == 0) Debug.Log(report.ToString());
        else Debug.LogWarning(report.ToString());
        bitmapCheckErrors = errors;

        WriteSimulationCheckReport(new SimulationCheckReport
        {
            batch_id = batch.batch_id,
            performed = true,
            passed = errors.Count == 0,
            verification_enabled = verificationEnabled,
            expected_count = expected.Count,
            correct_count = correct,
            expected_rows = batch.bitmap ?? new List<string>(),
            result_rows = resultRows,
            errors = errors,
            notes = new List<string>(simPlacementNotes),
        });
    }

    // 排 pattern 的批次一定要給 server 一個交代：沒做到比對也要寫「未進行」和原因
    void ReportSimulationCheckSkippedIfNeeded(BatchEnvelope batch, string reason)
    {
        if (simCheckReported || batch.expected_cells == null || batch.expected_cells.Count == 0) return;
        Debug.LogWarning($"[BitmapCheck] batch {batch.batch_id}：沒有進行模擬結束比對 — {reason}");
        WriteSimulationCheckReport(new SimulationCheckReport
        {
            batch_id = batch.batch_id,
            performed = false,
            skipped_reason = reason,
            verification_enabled = verificationEnabled,
            expected_count = batch.expected_cells.Count,
        });
    }

    void WriteSimulationCheckReport(SimulationCheckReport report)
    {
        simCheckReported = true;
        try
        {
            string path = Path.Combine(Application.streamingAssetsPath, "sim_check.json");
            File.WriteAllText(path, JsonUtility.ToJson(report, prettyPrint: true));
        }
        catch (IOException e)
        {
            Debug.LogWarning($"[BitmapCheck] 寫入 sim_check.json 失敗，server 不會印出比對結果：{e.Message}");
        }
    }

    // bitmap 比對不一致時要不要擋。回傳 true = 可以繼續（送實機 / 回報完成）。
    // 屬於驗證：開啟就擋；關閉就記錄、放行。
    bool BitmapCheckAllowsContinue(BatchEnvelope batch)
    {
        if (bitmapCheckErrors == null || bitmapCheckErrors.Count == 0) return true;
        if (!verificationEnabled)
        {
            Debug.LogWarning($"[Verification OFF] batch {batch.batch_id}：bitmap 比對不一致 " +
                             $"{bitmapCheckErrors.Count} 項，對照組照樣繼續");
            return true;
        }
        string summary = $"simulation bitmap check failed ({bitmapCheckErrors.Count}): " +
                         string.Join(" | ", bitmapCheckErrors);
        Debug.LogError("[BitmapCheck] " + summary);
        WriteStepDone(batch.batch_id, false, summary, 0f);
        return false;
    }

    bool PlanMoveAbove(PlannedJointAction action, ref double[] reference,
        UR3eKinematics.Pose current, double targetX, double targetY,
        double endpointHoverZ, string orientation, out string error)
    {
        // A single global travel plane can be unreachable for a far-away block
        // when the configured TCP includes a long gripper. Try progressively
        // lower planes, but never go below the action's validated hover height.
        // The first segment may lower from the current TCP to that safe plane.
        // Every candidate still passes the normal
        // joint-transition and approximate collision checks.
        double configuredTravelZ = QR1_Z + TRAVEL_Z_ABOVE_WORKSPACE;
        double minimumTravelZ = endpointHoverZ;
        double firstTravelZ = System.Math.Max(configuredTravelZ, minimumTravelZ);
        string lastError = null;

        double travelZ = firstTravelZ;
        while (true)
        {
            double[] trialReference = (double[])reference.Clone();
            var trial = new PlannedJointAction
            {
                function = action.function,
                seconds = action.seconds
            };

            if (PlanJointPose(trial, ref trialReference, current.x, current.y,
                    travelZ, orientation, out lastError) &&
                PlanSharedTravelXY(trial, ref trialReference, current.x, current.y,
                    targetX, targetY, travelZ, orientation, out lastError) &&
                PlanJointPose(trial, ref trialReference, targetX, targetY,
                    endpointHoverZ, orientation, out lastError))
            {
                action.targets.AddRange(trial.targets);
                reference = trialReference;
                if (travelZ < configuredTravelZ - 1e-6)
                    Debug.Log($"[Executor-shared] Adapted travel Z from {configuredTravelZ:F3}m " +
                              $"to {travelZ:F3}m for reachability; safe hover floor={minimumTravelZ:F3}m.");
                error = null;
                return true;
            }

            if (travelZ <= minimumTravelZ + 1e-6)
                break;
            travelZ = System.Math.Max(minimumTravelZ, travelZ - 0.01);
        }

        error = $"no reachable collision-free travel height in " +
                $"{minimumTravelZ:F3}..{firstTravelZ:F3}m; last attempt: {lastError}";
        return false;
    }

    bool PlanSharedTravelXY(PlannedJointAction action, ref double[] reference,
        double startX, double startY, double targetX, double targetY,
        double travelZ, string orientation, out string error)
    {
        double dx = targetX - startX, dy = targetY - startY;
        double lengthSquared = dx * dx + dy * dy;
        double t = lengthSquared > 1e-9
            ? System.Math.Max(0.0, System.Math.Min(1.0,
                -(startX * dx + startY * dy) / lengthSquared))
            : 0.0;
        double closestX = startX + t * dx, closestY = startY + t * dy;
        double startRadius = System.Math.Sqrt(startX * startX + startY * startY);
        double targetRadius = System.Math.Sqrt(targetX * targetX + targetY * targetY);
        double detourFloor = BASE_EXCLUSION_RADIUS_M + 0.04;

        if (closestX * closestX + closestY * closestY < detourFloor * detourFloor)
        {
            if (startRadius < BASE_EXCLUSION_RADIUS_M ||
                targetRadius < BASE_EXCLUSION_RADIUS_M)
            {
                error = "shared travel starts or ends inside the base exclusion radius";
                return false;
            }
            double startAngle = System.Math.Atan2(startY, startX);
            double targetAngle = System.Math.Atan2(targetY, targetX);
            double delta = System.Math.Atan2(System.Math.Sin(targetAngle - startAngle),
                System.Math.Cos(targetAngle - startAngle));
            int steps = System.Math.Max(1, (int)System.Math.Ceiling(
                System.Math.Abs(delta) * Mathf.Rad2Deg / BASE_DETOUR_MAX_ANGLE_STEP_DEG));

            if (!PlanJointPose(action, ref reference,
                    BASE_DETOUR_RADIUS_M * System.Math.Cos(startAngle),
                    BASE_DETOUR_RADIUS_M * System.Math.Sin(startAngle),
                    travelZ, orientation, out error)) return false;

            for (int i = 1; i <= steps; i++)
            {
                double angle = startAngle + delta * i / steps;
                if (!PlanJointPose(action, ref reference,
                        BASE_DETOUR_RADIUS_M * System.Math.Cos(angle),
                        BASE_DETOUR_RADIUS_M * System.Math.Sin(angle),
                        travelZ, orientation, out error)) return false;
            }
            Debug.Log($"[Executor-shared] Routed move_above around base in {steps} arc segments.");
        }

        return PlanJointPose(action, ref reference, targetX, targetY,
            travelZ, orientation, out error);
    }

    bool PlanJointPose(PlannedJointAction action, ref double[] reference,
        double x, double y, double z, string orientation, out string error)
    {
        var pose = SharedTargetPose(x, y, z, orientation);
        var solution = UR3eKinematics.IKNearest(pose, reference);
        if (!solution.ok)
        {
            error = $"IK {solution.error}: {solution.message} at {pose}";
            return false;
        }
        if (!ValidateBaseFacing(solution.q, x, y, out error) ||
            !ValidateJointTransition(reference, solution.q, out error, allowSingularEnd: false))
        {
            string firstTransitionError = error;
            bool foundSafeAlternative = false;
            var alternatives = UR3eKinematics.IKAlternatives(pose, reference);
            foreach (var alternative in alternatives)
            {
                if (!ValidateBaseFacing(alternative.q, x, y, out error)) continue;
                if (!ValidateJointTransition(reference, alternative.q, out error,
                        allowSingularEnd: false)) continue;
                solution = alternative;
                foundSafeAlternative = true;
                Debug.Log($"[Executor-shared] Selected collision-free alternative IK at {pose}.");
                break;
            }
            if (!foundSafeAlternative)
            {
                error = $"no safe IK transition among primary and {alternatives.Count} alternative solutions; " +
                        $"primary rejection: {firstTransitionError}; last rejection: {error}";
                return false;
            }
        }
        action.targets.Add((double[])solution.q.Clone());
        reference = (double[])solution.q.Clone();
        return true;
    }

    bool ValidateBaseFacing(double[] joints, double x, double y, out string error)
    {
        double bearing = System.Math.Atan2(y, x);
        double delta = System.Math.Atan2(System.Math.Sin(joints[0] - bearing),
            System.Math.Cos(joints[0] - bearing));
        if (System.Math.Abs(delta) > System.Math.PI / 2.0)
        {
            error = $"base faces away from target by {System.Math.Abs(delta) * Mathf.Rad2Deg:F1} deg " +
                    $"(base={joints[0] * Mathf.Rad2Deg:F1} deg, bearing={bearing * Mathf.Rad2Deg:F1} deg)";
            return false;
        }
        error = null;
        return true;
    }

    UR3eKinematics.Pose SharedTargetPose(double x, double y, double z, string orientation)
    {
        float toolDeg = orientation == "cube_yaw_180" ? 180f
            : orientation == "horizontal" ? 0f : 90f;
        Quaternion rotation = Quaternion.AngleAxis(180f, Vector3.up) *
                              Quaternion.AngleAxis(toolDeg, Vector3.forward);
        rotation.ToAngleAxis(out float angleDeg, out Vector3 axis);
        if (angleDeg > 180f) { angleDeg = 360f - angleDeg; axis = -axis; }
        float angleRad = angleDeg * Mathf.Deg2Rad;
        return new UR3eKinematics.Pose
        {
            x = x, y = y, z = z,
            rx = axis.x * angleRad, ry = axis.y * angleRad, rz = axis.z * angleRad
        };
    }

    bool ValidateJointTransition(double[] from, double[] to, out string error,
        bool allowSingularEnd)
    {
        double maxDelta = 0.0;
        for (int i = 0; i < 6; i++) maxDelta = System.Math.Max(maxDelta, System.Math.Abs(to[i] - from[i]));
        int samples = System.Math.Max(2, (int)System.Math.Ceiling(
            maxDelta * Mathf.Rad2Deg / Mathf.Max(0.5f, trajectoryCollisionSampleDeg)));

        for (int sample = 0; sample <= samples; sample++)
        {
            double t = (double)sample / samples;
            var q = new double[6];
            for (int i = 0; i < 6; i++) q[i] = from[i] + (to[i] - from[i]) * t;
            var jointCheck = UR3eKinematics.CheckJoints(q);
            if (jointCheck != UR3eKinematics.IKError.None &&
                !(allowSingularEnd && sample == samples))
            {
                error = $"joint validation failed at {t:P0}: {jointCheck}";
                return false;
            }
            if (!ValidateApproximateRobotCollision(q, out string collision))
            {
                error = $"collision at {t:P0}: {collision}";
                return false;
            }
        }
        error = null;
        return true;
    }

    bool ValidateApproximateRobotCollision(double[] q, out string error)
    {
        double[][] p = UR3eKinematics.LinkPoints(q);
        float[] radii = { 0.085f, 0.070f, 0.055f, 0.050f, 0.045f, 0.035f };
        float tableZ = QR1_Z;

        // The base/shoulder are mounted through the table; check all moving links
        // after the upper arm against the tabletop with conservative radii.
        for (int segment = 2; segment < 6; segment++)
        {
            float minZ = (float)System.Math.Min(p[segment][2], p[segment + 1][2]) - radii[segment];
            if (minZ < tableZ - 0.005f)
            {
                error = $"link segment {segment} enters tabletop (z={minZ:F3}m)";
                return false;
            }
        }

        for (int a = 0; a < 6; a++)
        {
            for (int b = a + 2; b < 6; b++)
            {
                // Segments 3 and 5 are separated by the fixed d5 wrist spacer
                // (about 85 mm). Their conservative capsules naturally meet at
                // that assembly, so adding the general 8 mm clearance creates a
                // permanent false positive even in the calibrated Ready pose.
                if (a == 3 && b == 5) continue;
                float distance = SegmentDistance(ToVector3(p[a]), ToVector3(p[a + 1]),
                                                 ToVector3(p[b]), ToVector3(p[b + 1]));
                float required = radii[a] + radii[b] + 0.008f;
                if (distance < required)
                {
                    error = $"self-collision link {a}↔{b}: {distance:F3}m < {required:F3}m";
                    return false;
                }
            }
        }
        error = null;
        return true;
    }

    static Vector3 ToVector3(double[] p) => new Vector3((float)p[0], (float)p[1], (float)p[2]);

    static float SegmentDistance(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
    {
        Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
        float s, t;
        if (a <= 1e-8f && e <= 1e-8f) return Vector3.Distance(p1, p2);
        if (a <= 1e-8f) { s = 0f; t = Mathf.Clamp01(f / e); }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= 1e-8f) { t = 0f; s = Mathf.Clamp01(-c / a); }
            else
            {
                float b = Vector3.Dot(d1, d2), denom = a * e - b * b;
                s = denom != 0f ? Mathf.Clamp01((b * f - c * e) / denom) : 0f;
                t = (b * s + f) / e;
                if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                else if (t > 1f) { t = 1f; s = Mathf.Clamp01((b - c) / a); }
            }
        }
        return Vector3.Distance(p1 + d1 * s, p2 + d2 * t);
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
            if (InsideSourceBaseExclusion(ox, oy) || OutsideReachEnvelope(ox, oy, MAX_SOURCE_REACH_RADIUS_M))
                failures.Add($"step {env.step_id} source @ UR({ox:F3},{oy:F3}) 半徑 {Mathf.Sqrt(ox * ox + oy * oy):F3} m " +
                             $"超出實機安全範圍 {SOURCE_BASE_EXCLUSION_RADIUS_M:F2}..{MAX_SOURCE_REACH_RADIUS_M:F2} m");
            if (InsideBaseExclusion(tx, ty) || OutsideReachEnvelope(tx, ty, MAX_TARGET_REACH_RADIUS_M))
                failures.Add($"step {env.step_id} target @ UR({tx:F3},{ty:F3}) 半徑 {Mathf.Sqrt(tx * tx + ty * ty):F3} m " +
                             $"超出實機安全範圍 {BASE_EXCLUSION_RADIUS_M:F2}..{MAX_TARGET_REACH_RADIUS_M:F2} m");

            foreach (var (label, pos, hover) in new[]
            {
                ($"step {env.step_id} source hover",  env.source_position, true),
                ($"step {env.step_id} source contact",env.source_position, false),
                ($"step {env.step_id} target hover",  env.target_position, true),
                ($"step {env.step_id} target contact",env.target_position, false),
            })
            {
                double urX = QR1_X + pos.x; double urY = QR1_Y + pos.y;
                double urZ = QR1_Z + pos.z + (hover ? simGripperHoverM : ContactExtraZ);
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

    // 純動畫：pick-and-place 一步（手臂 + 方塊），不寫 step_done
    // 實機執行前的預覽在 useSharedMovejTrajectory 關閉時用這個
    IEnumerator AnimateOneStep(StepEnvelope env, string tag)
    {
        if (sceneSyncer == null) yield break;

        // 預覽只用相機實際看到的方塊，不補生、也不改它的形狀；找不到就照演手臂動作，
        // 夾取會落空、bitmap 比對抓成少放。cube 只在沒有手臂動畫時拿來直接搬。
        GameObject cube = WarnIfNoSourceBlock(env, tag);

        // QR frame → Unity local（統一走 SceneSyncer.QRToUnity）
        // 加上 Inspector 放置微調（simPlaceOffsetX/Y/Z 也視為 QR frame 的偏移）
        float halfHeight = sceneSyncer.cubeSizeM / 2f;
        Vector3 sourceLocal = SceneSyncer.QRToUnity(
            env.source_position.x, env.source_position.y, env.source_position.z);
        sourceLocal.y -= halfHeight;
        Vector3 targetLocal = SceneSyncer.QRToUnity(
            env.target_position.x + simPlaceOffsetX,
            env.target_position.y + simPlaceOffsetY,
            env.target_position.z + simPlaceOffsetZ);
        targetLocal.y -= halfHeight;
        float hoverY = Mathf.Max(sourceLocal.y, targetLocal.y) + 0.08f;
        Vector3 sourceHover = new Vector3(sourceLocal.x, hoverY, sourceLocal.z);
        Vector3 targetHover = new Vector3(targetLocal.x, hoverY, targetLocal.z);

        Debug.Log($"[Executor-{tag}] step {env.step_id}: source local={sourceLocal} → target local={targetLocal}");

        // 手臂夾爪 transform：優先用 RobotArm.TCP（場景裡的 RealTCP，標記夾爪
        // 指尖實際位置）；沒指定才退回用最後一個 joint（wrist_3 的旋轉樞紐，
        // 不是指尖，退回用這個只是保底，不是正確位置）。null 就無手臂動畫。
        Transform gripper = null;
        bool armEnabled = simAnimateArm && robotArm != null &&
                          robotArm.Transforms != null && robotArm.Transforms.Length > 0;
        if (armEnabled)
        {
            gripper = robotArm.TCP != null
                ? robotArm.TCP
                : robotArm.Transforms[robotArm.Transforms.Length - 1];
        }

        // 7-phase 動畫：hover 掃 → 下降到 cube 頂 → 抬 → hover 掃 → 下降 → 抬 → home
        // 每個 phase 的 Z 都用該 step 自己的 qrZ 算，所以 source / target 高度不同也自動處理。
        //   hover   用 hoverOverride = -1（canonical → qrZ + simGripperHoverM）
        //   contact 用 ContactExtraZ（canonical：跟實機相同的 Z_CORRECTION）
        // canonical 模式下 IK 目標是 TCP（RobotArm.toolOffsetZ = Teach Pendant 的 TCP），
        // 所以這裡不需要任何「夾爪多長」的補償。
        float[] poseSourceHover = BuildReachPoseForQR(
            env.source_position.x + simPickGripperOffsetX,
            env.source_position.y + simPickGripperOffsetY,
            env.source_position.z, hoverOverride: -1f);
        float[] poseSourceContact = BuildReachPoseForQR(
            env.source_position.x + simPickGripperOffsetX,
            env.source_position.y + simPickGripperOffsetY,
            env.source_position.z, hoverOverride: ContactExtraZ);
        float[] poseTargetHover = BuildReachPoseForQR(
            env.target_position.x + simPlaceGripperOffsetX,
            env.target_position.y + simPlaceGripperOffsetY,
            env.target_position.z, hoverOverride: -1f);
        float[] poseTargetContact = BuildReachPoseForQR(
            env.target_position.x + simPlaceGripperOffsetX,
            env.target_position.y + simPlaceGripperOffsetY,
            env.target_position.z, hoverOverride: ContactExtraZ);

        // 時間分配（總長 simMoveSecPerStep）：分給 7 個 phase
        float total = simMoveSecPerStep;
        float tA  = total * 0.20f;  // swing to source hover
        float tA2 = total * 0.15f;  // descend to source cube
        float tB  = total * 0.10f;  // lift from source
        float tC  = total * 0.20f;  // swing to target hover
        float tC2 = total * 0.15f;  // descend to target
        float tD  = total * 0.10f;  // lift from target
        float tF  = total * 0.10f;  // home

        // ---- A. Arm swing 到 source XY（hover Z） ----
        if (armEnabled) yield return AnimateJointsTo(poseSourceHover, tA);
        else            yield return new WaitForSeconds(tA * 0.1f);
        yield return new WaitForSeconds(simHoldSec);

        // ---- A2. Arm 垂直下降到 source cube 頂（Cartesian 直線，每 frame 重算 IK） ----
        float srcX = env.source_position.x + simPickGripperOffsetX;
        float srcY = env.source_position.y + simPickGripperOffsetY;
        float srcZ = env.source_position.z;
        float tgtX = env.target_position.x + simPlaceGripperOffsetX;
        float tgtY = env.target_position.y + simPlaceGripperOffsetY;
        float tgtZ = env.target_position.z;
        float srcContactZ = srcZ + ContactExtraZ;
        // 實機放置時（手上有方塊）descend 會再加 placeDescendExtraZ，這裡照做
        float tgtContactZ = tgtZ + ContactExtraZ + (useCanonicalKinematics ? Mathf.Max(0f, placeDescendExtraZ) : 0f);
        if (armEnabled) yield return AnimateArmVertical(srcX, srcY, srcZ, HoverArmZFor(srcZ), srcContactZ, tA2);
        else if (cube != null) yield return AnimateLocalTo(cube.transform, sourceLocal, tA2);
        // 夾爪碰到方塊後 attach：只夾得到 TCP 正下方的方塊，跟 bitmap 比對用同一套判定
        GameObject held = null;
        if (armEnabled)
        {
            held = SimGrasp(ContactExtraZ, $"{tag} step {env.step_id} grasp");
            if (held != null && gripper != null) held.transform.SetParent(gripper, worldPositionStays: true);
        }
        yield return new WaitForSeconds(simHoldSec);

        // ---- B. Arm 垂直抬回 hover（cube 隨 gripper 上來） ----
        if (armEnabled) yield return AnimateArmVertical(srcX, srcY, srcZ, srcContactZ, HoverArmZFor(srcZ), tB);
        else if (cube != null) yield return AnimateLocalTo(cube.transform, sourceHover, tB);
        yield return new WaitForSeconds(simHoldSec);

        // ---- C. Arm swing 到 target XY（hover Z，cube 隨 gripper 飛） ----
        if (armEnabled) yield return AnimateJointsTo(poseTargetHover, tC);
        else if (cube != null) yield return AnimateLocalTo(cube.transform, targetHover, tC);
        yield return new WaitForSeconds(simHoldSec);

        // ---- C2. Arm 垂直下降到 target 位置（Cartesian 直線） ----
        if (armEnabled) yield return AnimateArmVertical(tgtX, tgtY, tgtZ, HoverArmZFor(tgtZ), tgtContactZ, tC2);
        else if (cube != null) yield return AnimateLocalTo(cube.transform, targetLocal, tC2);
        yield return new WaitForSeconds(simHoldSec);

        // ---- release ----
        if (armEnabled)
        {
            // 落點由夾爪實際位置決定，不直接瞬移到 target，bitmap 比對才驗得出放錯
            if (held != null)
            {
                SimRelease(held, $"{tag} step {env.step_id} release");
                held.name = $"{tag}_step{env.step_id}_{(SimBlock(held).isDomino ? "domino" : "cube")}";
            }
        }
        else if (cube != null)
        {
            // 沒有手臂動畫就無從判斷落點，只能照指令擺放
            cube.transform.localScale = SimScaleFor(env.target_position);
            cube.transform.localPosition = targetLocal;
            cube.name = $"{tag}_step{env.step_id}_{(env.target_position.shape == "domino" ? "domino" : "cube")}";
        }

        // ---- D. Arm 垂直抬回 target hover（cube 已放，不跟） ----
        if (armEnabled) yield return AnimateArmVertical(tgtX, tgtY, tgtZ, tgtContactZ, HoverArmZFor(tgtZ), tD);
        yield return new WaitForSeconds(simHoldSec);

        // ---- F. Arm 回 home ----
        if (armEnabled) yield return AnimateJointsTo(BuildHomePose(), tF);
        yield return new WaitForSeconds(0.15f);
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

    // hover 時 TCP 的目標高度，語意跟 BuildReachPoseForQR 的 hoverOverride 一致。
    // 垂直升降的起訖點必須用這個，才會跟 swing 階段停的高度接得上（否則手臂會先跳一下）。
    float HoverArmZFor(float qrZ)
        => useCanonicalKinematics ? (qrZ + simGripperHoverM) : simFixedArmZ;

    // 垂直下降/上升：TCP 沿 Cartesian Z 走直線，每 frame 重算 IK
    //   qrX/qrY 保持固定，qrZ 也固定（是 cube 頂），只有 arm 目標 Z 從 startArmZ 線性到 endArmZ
    //   內部用 hoverOverride = armZ - qrZ 灌進 BuildReachPoseForQR 讓 IK 每次算對應姿態
    IEnumerator AnimateArmVertical(float qrX, float qrY, float qrZ,
                                    float startArmZ, float endArmZ, float seconds)
    {
        if (robotArm == null || robotArm.Angles == null || robotArm.Angles.Length == 0)
        {
            yield return new WaitForSeconds(seconds * 0.1f);
            yield break;
        }
        int n = robotArm.Angles.Length;
        seconds = Mathf.Max(0.01f, seconds);

        Debug.Log($"[Executor-sim] AnimateArmVertical: qr=({qrX:F3},{qrY:F3},{qrZ:F3}), armZ {startArmZ:F3}→{endArmZ:F3} in {seconds:F2}s");

        int frameCount = 0;
        float elapsed = 0f;
        while (elapsed < seconds)
        {
            elapsed += Time.deltaTime;
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / seconds));
            float armZ = Mathf.Lerp(startArmZ, endArmZ, k);
            float hOvr = armZ - qrZ;   // BuildReachPoseForQR 內部：zTarget = qrZ + hOvr = armZ
            var pose = BuildReachPoseForQR(qrX, qrY, qrZ, hoverOverride: hOvr);
            int m = Mathf.Min(n, pose.Length);
            for (int i = 0; i < m; i++) robotArm.Angles[i] = pose[i];
            frameCount++;
            yield return null;
        }
        // 收尾：確保停在 endArmZ
        var final = BuildReachPoseForQR(qrX, qrY, qrZ, hoverOverride: endArmZ - qrZ);
        int mf = Mathf.Min(n, final.Length);
        for (int i = 0; i < mf; i++) robotArm.Angles[i] = final[i];

        Debug.Log($"[Executor-sim] AnimateArmVertical done: {frameCount} frames, final Angles=[{string.Join(",", System.Array.ConvertAll(final, a => a.ToString("F1")))}]°");
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

        // The real batch moves to Ready before consuming the shared trajectory.
        // Show the same starting joint pose before previewing its waypoints.
        if (useSharedMovejTrajectory && sharedTrajectoryStartQ != null && robotArm != null)
            yield return AnimateJointsTo(RadToDeg(sharedTrajectoryStartQ), 1.0f);

        // 對每個 step 跑動畫
        foreach (StepEnvelope env in batch.steps)
        {
            if (env == null || env.done ||
                env.source_position == null || env.target_position == null)
                continue;
            if (useSharedMovejTrajectory)
                yield return AnimateSharedTrajectoryStep(env, "preview");
            else
                yield return AnimateOneStep(env, "preview");
        }

        if (useSharedMovejTrajectory)
        {
            foreach (double[] target in sharedFinalTrajectory)
                yield return AnimateJointsTo(RadToDeg(target), 1.5f);
        }

        // 模擬結束、復原場景之前比對 bitmap（復原之後落點就沒了）
        RunBitmapCheck(batch);

        // 復原：手臂角度
        if (initialArmAngles != null && robotArm != null && robotArm.Angles != null)
        {
            int n = Mathf.Min(initialArmAngles.Length, robotArm.Angles.Length);
            for (int i = 0; i < n; i++) robotArm.Angles[i] = initialArmAngles[i];
        }

        // 復原：原本 cube 的位置/名稱（預覽不會新增方塊，所以只需要還原）
        var currentCubes = sceneSyncer.GetCurrentCubes();
        for (int i = currentCubes.Count - 1; i >= 0; i--)
        {
            var cube = currentCubes[i];
            if (cube == null) { currentCubes.RemoveAt(i); continue; }
            if (!initialCubePositions.ContainsKey(cube)) continue;
            if (initialCubeParents[cube] != null && cube.transform.parent != initialCubeParents[cube])
                cube.transform.SetParent(initialCubeParents[cube], worldPositionStays: false);
            cube.transform.localPosition = initialCubePositions[cube];
            cube.transform.localScale = initialCubeScales[cube];
            cube.name = initialCubeNames[cube];
        }

        Debug.Log($"[Executor-preview] 動畫預覽結束，已復原場景。實機開始執行");
    }

    IEnumerator AnimateSharedTrajectoryStep(StepEnvelope env, string tag)
    {
        if (!sharedTrajectory.TryGetValue(env.step_id, out var actions)) yield break;
        // 預覽只用相機實際看到的方塊，不補生。source 附近沒有方塊就照演手臂動作，
        // 夾取會落空、bitmap 比對抓成少放 —— 跟實機遇到同樣情況的結果一致。
        WarnIfNoSourceBlock(env, tag);
        Transform gripper = robotArm != null ? robotArm.TCP : null;
        SyncGripper previewGripper = FindObjectOfType<SyncGripper>();
        GameObject held = null;
        string stepLabel = $"{tag} step {env.step_id}";

        for (int i = 0; i < actions.Count; i++)
        {
            PlannedJointAction action = actions[i];
            foreach (double[] target in action.targets)
            {
                float maxDeltaRad = 0f;
                if (robotArm != null && robotArm.Angles != null)
                    for (int j = 0; j < Mathf.Min(6, robotArm.Angles.Length); j++)
                        maxDeltaRad = Mathf.Max(maxDeltaRad,
                            Mathf.Abs(RadToDeg(target)[j] - robotArm.Angles[j]) * Mathf.Deg2Rad);
                float duration = Mathf.Max(0.15f, maxDeltaRad / Mathf.Max(0.05f, sharedMovejVelocity));
                yield return AnimateJointsTo(RadToDeg(target), duration);
            }

            if (action.function == "grasp")
            {
                if (previewGripper != null) previewGripper.SetPreviewGrip(true);
                // 只夾得到夾爪正下方的方塊；夾不到就什麼都不搬（比對時會變成少放）
                var grabbed = SimGrasp(Z_CORRECTION, $"{stepLabel} action {i + 1} grasp");
                if (grabbed != null)
                {
                    held = grabbed;
                    Transform gripParent = previewGripper != null
                        ? previewGripper.transform
                        : gripper;
                    if (gripParent != null) held.transform.SetParent(gripParent, true);
                }
                yield return new WaitForSeconds(1.5f);
            }
            else if (action.function == "release")
            {
                if (previewGripper != null) previewGripper.SetPreviewGrip(false);
                if (held != null)
                {
                    // 落點由夾爪實際位置決定，不直接瞬移到 target_position，
                    // bitmap 比對才驗得出手臂把方塊放在哪。旋轉歸零、方向由 scale 表示。
                    SimRelease(held, $"{stepLabel} action {i + 1} release");
                    string shape = SimBlock(held).isDomino ? "domino" : "cube";
                    held.name = $"{tag}_step{env.step_id}_{shape}";
                    held = null;
                }
                yield return new WaitForSeconds(1.5f);
                if (previewGripper != null) previewGripper.ClearPreviewGripOverride();
            }
            else if (action.function == "wait")
            {
                yield return new WaitForSeconds(action.seconds);
            }
        }
        if (held != null)
            simPlacementNotes.Add($"{stepLabel}: 動作序列結束時方塊還夾在夾爪上（沒有 release）");
    }

    static float[] RadToDeg(double[] radians)
    {
        var degrees = new float[6];
        for (int i = 0; i < 6; i++) degrees[i] = (float)(radians[i] * 180.0 / System.Math.PI);
        return degrees;
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
            OutsideReachEnvelope(ox, oy, MAX_SOURCE_REACH_RADIUS_M) ||
            OutsideReachEnvelope(tx, ty, MAX_TARGET_REACH_RADIUS_M))
        {
            string error = $"unsafe target reach: source radius={Mathf.Sqrt(ox * ox + oy * oy):F3}m, " +
                           $"target radius={Mathf.Sqrt(tx * tx + ty * ty):F3}m, " +
                           $"source allowed={SOURCE_BASE_EXCLUSION_RADIUS_M:F3}..{MAX_SOURCE_REACH_RADIUS_M:F3}m, " +
                           $"target allowed={BASE_EXCLUSION_RADIUS_M:F3}..{MAX_TARGET_REACH_RADIUS_M:F3}m";
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

            bool sharedMotion = useSharedMovejTrajectory &&
                (action.function == "move_above" || action.function == "descend" ||
                 action.function == "lift" || action.function == "go_home");
            if (sharedMotion)
            {
                yield return ExecuteSharedJointAction(env.step_id, i, tag, stepEpoch);
            }
            else switch (action.function)
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
                    yield return new WaitForSeconds(Mathf.Clamp(action.seconds > 0f ? action.seconds : DefaultWaitSeconds, 0.1f, 3f));
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

    IEnumerator ExecuteSharedJointAction(int stepId, int actionIndex, string tag, long stepEpoch)
    {
        lastMotionSucceeded = false;
        lastMotionError = null;
        if (!sharedTrajectory.TryGetValue(stepId, out var actions) ||
            actionIndex < 0 || actionIndex >= actions.Count)
        {
            lastMotionError = $"missing shared trajectory for step {stepId} action {actionIndex + 1}";
            yield break;
        }

        var action = actions[actionIndex];
        if (action.targets.Count == 0)
        {
            lastMotionError = $"shared trajectory action {tag} has no joint targets";
            yield break;
        }
        for (int i = 0; i < action.targets.Count; i++)
        {
            yield return SendExplicitJointTarget(action.targets[i],
                $"{tag} shared {i + 1}/{action.targets.Count}", stepEpoch, stepId);
            if (!lastMotionSucceeded) yield break;
        }
    }

    IEnumerator SendExplicitJointTarget(double[] target, string tag, long stepEpoch, int stepId)
    {
        if (!IsExecutionCurrent(stepEpoch, stepId)) yield break;
        lastMotionSucceeded = false;
        lastMotionError = null;
        string command = $"movej([{target[0]:F6},{target[1]:F6},{target[2]:F6}," +
                         $"{target[3]:F6},{target[4]:F6},{target[5]:F6}], " +
                         $"a={sharedMovejAcceleration:F3}, v={sharedMovejVelocity:F3})";
        Debug.Log($"  [{tag}] SEND EXACT: {command}");
        urListener.SendCommand(command);
        yield return new WaitForSeconds(MOTION_START_GRACE_SEC);

        float startedAt = Time.realtimeSinceStartup;
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
            if (IsEmergencyStop() || IsRecoverableSafetyStop())
            {
                lastMotionError = $"UR safety stop during {tag}; shared trajectory aborted";
                yield break;
            }

            var joints = urListener.JointData.AsArray;
            float maxError = 0f;
            for (int i = 0; i < 6; i++)
            {
                float actual = (float)joints[i].q_actual;
                maxError = Mathf.Max(maxError, Mathf.Abs(Mathf.DeltaAngle(
                    actual * Mathf.Rad2Deg, (float)target[i] * Mathf.Rad2Deg)) * Mathf.Deg2Rad);
            }
            if (maxError <= HOME_JOINT_TOLERANCE_RAD &&
                !urListener.RobotModeData.isProgramRunning)
            {
                lastMotionSucceeded = true;
                Debug.Log($"  [{tag}] REACHED EXACT: max joint error {maxError * Mathf.Rad2Deg:F2} deg");
                yield break;
            }
            yield return new WaitForSeconds(0.05f);
        }
        lastMotionError = $"UR motion timeout during shared trajectory {tag}";
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
        float startX = (float)tcp.X;
        float startY = (float)tcp.Y;
        float startAngle = Mathf.Atan2(startY, startX) * Mathf.Rad2Deg;
        float targetAngle = Mathf.Atan2(targetY, targetX) * Mathf.Rad2Deg;
        float angleDelta = Mathf.DeltaAngle(startAngle, targetAngle);
        int arcSteps = Mathf.Max(1,
            Mathf.CeilToInt(Mathf.Abs(angleDelta) / BASE_DETOUR_MAX_ANGLE_STEP_DEG));

        Debug.Log($"  [{tag}] Direct path crosses base exclusion; routing " +
                  $"around R={BASE_DETOUR_RADIUS_M:F3}m in {arcSteps} arc segment(s).");

        // Move radially to the routing circle, trace a short polygonal arc, then
        // move radially to the destination. Every segment remains outside the
        // exclusion cylinder and is independently checked by SendMove.
        float startRad = startAngle * Mathf.Deg2Rad;
        yield return SendMove(
            BASE_DETOUR_RADIUS_M * Mathf.Cos(startRad),
            BASE_DETOUR_RADIUS_M * Mathf.Sin(startRad),
            targetZ, orientation, skewDeg, tag + " detour-entry", true,
            stepEpoch, stepId);
        if (!lastMotionSucceeded) yield break;

        for (int i = 1; i <= arcSteps; i++)
        {
            float angle = (startAngle + angleDelta * i / arcSteps) * Mathf.Deg2Rad;
            yield return SendMove(
                BASE_DETOUR_RADIUS_M * Mathf.Cos(angle),
                BASE_DETOUR_RADIUS_M * Mathf.Sin(angle),
                targetZ, orientation, skewDeg, $"{tag} detour-arc {i}/{arcSteps}",
                true, stepEpoch, stepId);
            if (!lastMotionSucceeded) yield break;
        }

        yield return SendMove(targetX, targetY, targetZ, orientation, skewDeg,
            tag + " detour-exit", true, stepEpoch, stepId);
    }

    bool InsideSourceBaseExclusion(float x, float y)
    {
        return (x * x + y * y) <
               SOURCE_BASE_EXCLUSION_RADIUS_M * SOURCE_BASE_EXCLUSION_RADIUS_M;
    }

    bool IsLinearTcpPathClearOfBase(float targetX, float targetY, out float minimumRadius)
    {
        var tcp = urListener.CartesianInfo;
        float startX = (float)tcp.X;
        float startY = (float)tcp.Y;
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

    bool OutsideReachEnvelope(float x, float y, float maxRadius)
    {
        return (x * x + y * y) > maxRadius * maxRadius;
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

