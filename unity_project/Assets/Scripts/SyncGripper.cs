using System.Collections.Generic;
using UnityEngine;

// SyncGripper：
// 1. 讀 RobotArm.Outputs[digitalOutputIndex]（UR3e digital output pin），開合兩根手指
// 2. 在 grip 邊緣觸發（False→True）時：找 SceneSyncer 管理的 cube 清單，
//    把離 TCP 最近、距離 < pickThresholdM 的那顆 parent 到夾爪，讓它跟著手臂走
// 3. 在 release 邊緣（True→False）時：把 cube 放回 SceneSyncer 的 cubeContainer，
//    位置保留在放下當下的世界座標。SceneSyncer 下一次 idle refresh 會用真實 perception 覆蓋
//
// Inspector 設定：
//   Robot Arm       ← 場景中的 UR3 GameObject
//   Left Finger     ← GripperX- Transform
//   Right Finger    ← GripperX+ Transform
//   Scene Syncer    ← 場景中的 PerceptionSync GameObject（拿它管理的 cube 清單）
//   Tcp Transform   ← RobotArm 的 TCP transform（測距離用；不填就用夾爪自己）

public class SyncGripper : MonoBehaviour
{
    [Header("資料來源")]
    public RobotArm robotArm;                      // 由此讀 Outputs 陣列
    public int digitalOutputIndex = 4;             // UR3e digital output pin 編號

    [Header("夾爪手指 Transform")]
    public Transform leftFinger;                   // GripperX-
    public Transform rightFinger;                  // GripperX+

    [Header("開合位移（單位公尺，沿手指 local X 軸）")]
    public float openOffset = 0.03f;               // 張開時每根手指相對中心 3 cm
    public float closedOffset = 0.005f;            // 夾住時每根手指相對中心 0.5 cm
    public float animateSpeed = 8f;                // 開合動畫速度（越大越快）

    [Header("虛擬夾取")]
    public SceneSyncer sceneSyncer;                // 抓 cube 清單找最近的
    public Transform tcpTransform;                 // 用來算距離；不填就用自己 transform
    public float pickThresholdM = 0.05f;           // TCP 與 cube 距離 < 5cm 才會夾到

    private float currentOffset;                   // 目前的手指位移（Lerp 用）
    private Vector3 leftInitialPos;
    private Vector3 rightInitialPos;
    private bool initialized = false;

    private bool lastGripClosed = false;           // 用來偵測 grip 邊緣
    private GameObject heldCube;                   // 目前被夾住的 cube（未夾時為 null）
    private Transform heldCubeOriginalParent;      // 放開時要還回去的 parent

    // grasp 期間持續嘗試靠近 → 抓：JsonExecutor 的 URScript 每一步是獨立 program，
    // 會中斷前一個 movej，所以 grasp 信號觸發時 TCP 常常還沒真的到 cube 位置。
    // 這裡改成邊緣觸發後進入 wantingToGrab 狀態，每 frame 檢查距離、真的靠近才 attach。
    private bool wantingToGrab = false;
    private float wantingToGrabStart = 0f;
    private float wantingToGrabMinDist = float.MaxValue;   // wantingToGrab 期間看過的最小距離
    private string wantingToGrabMinCube = "";              // 對應的 cube 名稱
    private const float GRAB_TIMEOUT_SEC = 10f;    // 10 秒內都沒靠近 → 放棄

    void Start()
    {
        currentOffset = openOffset;
        if (leftFinger != null) leftInitialPos = leftFinger.localPosition;
        if (rightFinger != null) rightInitialPos = rightFinger.localPosition;
        initialized = true;
    }

    void Update()
    {
        if (!initialized || leftFinger == null || rightFinger == null) return;
        if (RobotArm.FreezeVisualFeedback) return;

        // 目標開合狀態：讀 RobotArm 的 Outputs（True = 夾住；False = 張開）
        bool targetClosed = robotArm != null
            && robotArm.Outputs != null
            && digitalOutputIndex < robotArm.Outputs.Length
            && robotArm.Outputs[digitalOutputIndex];

        // ---- 手指開合動畫 ----
        float targetOffset = targetClosed ? closedOffset : openOffset;
        currentOffset = Mathf.Lerp(currentOffset, targetOffset, Time.deltaTime * animateSpeed);

        leftFinger.localPosition = leftInitialPos + new Vector3(-currentOffset + openOffset, 0f, 0f);
        rightFinger.localPosition = rightInitialPos + new Vector3(currentOffset - openOffset, 0f, 0f);

        // ---- 虛擬夾取狀態機 ----
        // 邊緣：False→True 進入 wantingToGrab；True→False 放開
        if (targetClosed != lastGripClosed)
        {
            if (targetClosed)
            {
                wantingToGrab = true;
                wantingToGrabStart = Time.time;
                wantingToGrabMinDist = float.MaxValue;
                wantingToGrabMinCube = "";
                Debug.Log("[SyncGripper] grasp 邊緣 → 進入 wantingToGrab，等待 TCP 靠近 cube");
            }
            else
            {
                // 放開前若還在 wantingToGrab（代表整個循環都沒真的抓到），印出最小距離
                if (wantingToGrab && heldCube == null)
                {
                    Debug.LogWarning($"[SyncGripper] release 但沒抓到 cube — grasp 期間最靠近 {wantingToGrabMinCube}，距離 {wantingToGrabMinDist * 100f:F1} cm（threshold {pickThresholdM * 100f:F1} cm）");
                }
                wantingToGrab = false;
                ReleaseHeldCube();
            }
            lastGripClosed = targetClosed;
        }

        // grasp 期間持續嘗試：每 frame 檢查 TCP 是否靠近某顆 cube，
        // 靠近就 attach、離開狀態。時序不對也能自癒。
        if (wantingToGrab && heldCube == null)
        {
            TryGrabNearestCube();
            if (heldCube != null)
                wantingToGrab = false;
            else if (Time.time - wantingToGrabStart > GRAB_TIMEOUT_SEC)
            {
                LogNearestCubeOnTimeout();
                wantingToGrab = false;
            }
        }
    }

    // Timeout 時印出最近 cube 距離，方便判斷 threshold 要不要調
    void LogNearestCubeOnTimeout()
    {
        Transform tcp = tcpTransform != null ? tcpTransform : transform;
        Vector3 tcpPos = tcp.position;
        var cubes = sceneSyncer != null ? sceneSyncer.GetCurrentCubes() : null;

        if (cubes == null || cubes.Count == 0)
        {
            Debug.LogWarning($"[SyncGripper] timeout：場景沒有 cube");
            return;
        }

        GameObject nearest = null;
        float nearestSqr = float.MaxValue;
        foreach (var cube in cubes)
        {
            if (cube == null) continue;
            float d = (cube.transform.position - tcpPos).sqrMagnitude;
            if (d < nearestSqr) { nearestSqr = d; nearest = cube; }
        }

        float cm = Mathf.Sqrt(nearestSqr) * 100f;
        Debug.LogWarning($"[SyncGripper] {GRAB_TIMEOUT_SEC}s timeout — TCP {tcpPos} → 最近 {nearest?.name} @ {nearest?.transform.position}，距離 {cm:F1} cm（threshold {pickThresholdM * 100f:F1} cm）→ 調 pickThresholdM 或對齊 UR3/PerceptionSync 座標");
    }

    void TryGrabNearestCube()
    {
        if (sceneSyncer == null)
        {
            Debug.LogWarning("[SyncGripper] sceneSyncer 未指定 → 無法夾取");
            wantingToGrab = false;    // 避免每 frame 洗版
            return;
        }

        Transform tcp = tcpTransform != null ? tcpTransform : transform;
        Vector3 tcpPos = tcp.position;

        List<GameObject> cubes = sceneSyncer.GetCurrentCubes();
        if (cubes == null || cubes.Count == 0) return;

        GameObject nearest = null;
        float nearestSqr = float.MaxValue;

        foreach (var cube in cubes)
        {
            if (cube == null) continue;
            float d = (cube.transform.position - tcpPos).sqrMagnitude;
            if (d < nearestSqr)
            {
                nearestSqr = d;
                nearest = cube;
            }
        }

        // 追蹤 wantingToGrab 期間看過的最小距離，release 時 log 給人看
        float nearestDist = Mathf.Sqrt(nearestSqr);
        if (nearest != null && nearestDist < wantingToGrabMinDist)
        {
            wantingToGrabMinDist = nearestDist;
            wantingToGrabMinCube = nearest.name;
        }

        // 沒靠近就靜靜等下 frame（不 log 避免洗版）
        if (nearest == null || nearestSqr > pickThresholdM * pickThresholdM)
            return;

        heldCubeOriginalParent = nearest.transform.parent;
        nearest.transform.SetParent(transform, worldPositionStays: true);
        heldCube = nearest;
        Debug.Log($"[SyncGripper] 夾取 {nearest.name}（距離 {Mathf.Sqrt(nearestSqr) * 100f:F1} cm）");
    }

    void ReleaseHeldCube()
    {
        if (heldCube == null) return;

        Transform parent = heldCubeOriginalParent != null
            ? heldCubeOriginalParent
            : (sceneSyncer != null ? sceneSyncer.GetCubeContainer() : null);

        heldCube.transform.SetParent(parent, worldPositionStays: true);
        Debug.Log($"[SyncGripper] 放開 {heldCube.name}");
        heldCube = null;
        heldCubeOriginalParent = null;
    }
}
