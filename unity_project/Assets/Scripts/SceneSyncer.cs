using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

// SceneSyncer:
// 快照式同步：只在「執行狀態切回 idle」時抓一次 /scene 更新場景。
//
// 流程：
//   1. Poll /scene/mode（每 pollIntervalSec 秒，很輕量）
//   2. 若 mode == "executing" → 完全不動 cube（虛擬夾爪負責視覺）
//   3. 若 mode == "idle" 且上次是 executing（或首次啟動）→ GET /scene 一次，refresh 所有 cube
//   4. 否則什麼都不做（perception 端持續在更新，但 Unity 不覆蓋顯示）
//
// 座標系轉換（跟 Unity UR3 base 目前朝向對齊）：
//   Robot/QR 是右手系 Z-up（X 前伸、Y 左、Z 上）
//   Unity 是左手系 Y-up
//   Mapping：Robot (X, Y, Z) → Unity (-Y, Z, X)
//     Robot +X（前伸）→ Unity +Z
//     Robot +Y（左）  → Unity -X
//     Robot +Z（上）  → Unity +Y
//   統一走 QRToUnity(qrX, qrY, qrZ) helper，不要在別的地方硬 code

public class SceneSyncer : MonoBehaviour
{
    // ★ Robot/QR → Unity local 座標轉換（single source of truth）
    // 對應 RobotArm.Robot2Unity：Robot X → Unity +Z、Robot Y → Unity -X、Robot Z → Unity +Y
    // 這是保持左右手性的轉換；換成別的對應（例如 X→X、Y→-Z）場景會變成現實的鏡像
    public static Vector3 QRToUnity(float qrX, float qrY, float qrZ)
    {
        return new Vector3(-qrY, qrZ, qrX);
    }
    public static Vector3 QRToUnity(Vector3 qr) => QRToUnity(qr.x, qr.y, qr.z);

    // Unity local → Robot/QR frame（反向）
    public static Vector3 UnityToQR(float ux, float uy, float uz)
    {
        return new Vector3(uz, -ux, uy);
    }
    public static Vector3 UnityToQR(Vector3 u) => UnityToQR(u.x, u.y, u.z);

    [Header("Perception Server")]
    public string sceneUrl = "http://localhost:5000/scene";
    public string sceneModeUrl = "http://localhost:5000/scene/mode";
    public float pollIntervalSec = 0.3f;         // mode 端點的輪詢頻率

    [Header("工作平面尺寸（公尺，對應真實工作台）")]
    // 真實值取自 perception_server /scene 的 workspace_info.width_m / depth_m（相機實測 QR 中心間距）
    public float workspaceWidthM = 0.622f;       // QR1 → QR2 距離
    public float workspaceDepthM = 0.281f;       // QR1 → QR3 距離

    [Header("手臂 base 在 QR frame 中的位置（把 workspace 對齊到手臂）")]
    // 預設值 = -JsonExecutor.QR1_X / -QR1_Y（Teach Pendant 實測值）
    // 手臂 base 在 robot (0,0,0)，QR1 在 robot (QR1_X, QR1_Y)，所以在 QR frame 裡
    // 手臂座標 = (-QR1_X, -QR1_Y) = (0.38824, 0.35473)
    // Inspector 也可以手動蓋掉這個值
    public float armBaseAtQrX = -JsonExecutor.QR1_X;   // 0.38824
    public float armBaseAtQrY = -JsonExecutor.QR1_Y;   // 0.35473
    // 桌面（QR 平面）比手臂安裝面高 QR1_Z，所以手臂 base 在 QR frame 的 Z 是 -QR1_Z
    public float armBaseAtQrZ = -JsonExecutor.QR1_Z;   // -0.030

    [Header("白色底板往外延伸（公尺）")]
    // 底板往 4 個 QR 邊界外多延伸這個距離（純視覺，QR marker 位置不變）
    // 例：0.15 = 每一邊多 15cm，讓底板比 QR 圍住的範圍大
    public float planeMarginM = 0.15f;

    [Header("補貨區 / 擺放區邊界（跟 PlacementPlanner 常數對齊）")]
    // 必須與 csharp_server/LayeredTypes.cs 的 WorkspaceBounds 相同（LLM 規劃目標格用的值），不開放 Inspector 覆寫
    [System.NonSerialized] public float supplyZoneXMax = 0.35f;
    [System.NonSerialized] public float targetZoneRightX = 0.708f;
    [System.NonSerialized] public float targetZoneBottomY = 0.02f;
    [System.NonSerialized] public float cellSize = 0.053f;
    [System.NonSerialized] public int gridRows = 5;
    [System.NonSerialized] public int gridCols = 5;

    [Header("積木顯示")]
    public float cubeSizeM = 0.025f;             // 2.5 cm 立方體
    public bool autoCreateWorkspace = true;      // 啟動時自動建工作平面 + QR 標記


    // ---- 內部狀態 ----
    private Transform workspaceRoot;
    private Transform cubeContainer;
    private List<GameObject> currentCubes = new List<GameObject>();
    private string previousMode = null;                          // 上次 poll 到的 mode（首次為 null）

    // 給 SyncGripper 讀，讓虛擬夾爪找最近的 cube
    public List<GameObject> GetCurrentCubes() { return currentCubes; }
    public Transform GetCubeContainer() { return cubeContainer; }

    void Start()
    {
        if (autoCreateWorkspace)
            BuildWorkspaceVisuals();

        StartCoroutine(PollLoop());
    }

    // ==========================================================
    // 建立虛擬工作平面、QR 標記、補貨/擺放區半透明色塊、cube container
    // ==========================================================
    void BuildWorkspaceVisuals()
    {
        // 空 parent，方便之後整組拖動 / 隱藏
        workspaceRoot = new GameObject("Workspace").transform;
        workspaceRoot.SetParent(transform, false);

        // 整組 workspace 平移，讓「手臂 base 在 QR frame 的位置」對到 Unity 原點（手臂所在處）。
        // 含 Z：桌面落在 robot Z = QR1_Z，跟 IK 目標高度一致（否則方塊畫面上會比 IK 目標低 QR1_Z）。
        workspaceRoot.localRotation = Quaternion.identity;
        workspaceRoot.localPosition = -QRToUnity(armBaseAtQrX, armBaseAtQrY, armBaseAtQrZ);

        // 工作平面（薄薄的白色 Cube 當桌板）
        // Robot X 沿 workspaceWidthM → Unity +Z；Robot Y 沿 workspaceDepthM → Unity -X
        // planeMarginM 讓底板往 4 邊外延伸；QR marker 位置不變（仍在原 QR 座標）
        GameObject plane = GameObject.CreatePrimitive(PrimitiveType.Cube);
        plane.name = "WorkspacePlane";
        plane.transform.SetParent(workspaceRoot, false);
        Vector3 planeCenter = QRToUnity(workspaceWidthM / 2f, workspaceDepthM / 2f, 0f);
        planeCenter.y = -0.005f;
        plane.transform.localPosition = planeCenter;
        float extendedWidth = workspaceWidthM + 2f * planeMarginM;   // robot X 方向 → Unity Z 長度
        float extendedDepth = workspaceDepthM + 2f * planeMarginM;   // robot Y 方向 → Unity X 長度
        plane.transform.localScale = new Vector3(extendedDepth, 0.01f, extendedWidth);
        SetColor(plane, new Color(0.92f, 0.92f, 0.92f));

        // 4 個 QR 角落標記（QR frame 座標）
        MakeQrMarker("QR1", 0f, 0f, Color.red);
        MakeQrMarker("QR2", workspaceWidthM, 0f, Color.green);
        MakeQrMarker("QR3", 0f, workspaceDepthM, Color.blue);
        MakeQrMarker("QR4", workspaceWidthM, workspaceDepthM, Color.magenta);

        // 補貨區（藍色半透明）
        GameObject supply = GameObject.CreatePrimitive(PrimitiveType.Cube);
        supply.name = "SupplyZone";
        supply.transform.SetParent(workspaceRoot, false);
        Vector3 supplyCenter = QRToUnity(supplyZoneXMax / 2f, workspaceDepthM / 2f, 0f);
        supplyCenter.y = 0.002f;
        supply.transform.localPosition = supplyCenter;
        supply.transform.localScale = new Vector3(workspaceDepthM, 0.001f, supplyZoneXMax);
        SetColor(supply, new Color(0.4f, 0.7f, 1f, 0.5f));

        // 擺放區（黃色半透明，grid 實體邊界）
        float targetW = gridCols * cellSize;  // robot X 方向 → Unity Z
        float targetD = gridRows * cellSize;  // robot Y 方向 → Unity X
        GameObject target = GameObject.CreatePrimitive(PrimitiveType.Cube);
        target.name = "TargetZone";
        target.transform.SetParent(workspaceRoot, false);
        Vector3 targetCenter = QRToUnity(
            targetZoneRightX - (gridCols - 1) * cellSize / 2f,
            targetZoneBottomY + (gridRows - 1) * cellSize / 2f,
            0f);
        targetCenter.y = 0.002f;
        target.transform.localPosition = targetCenter;
        target.transform.localScale = new Vector3(targetD, 0.001f, targetW);
        SetColor(target, new Color(1f, 0.85f, 0.4f, 0.5f));

        // 積木容器
        cubeContainer = new GameObject("CubeContainer").transform;
        cubeContainer.SetParent(workspaceRoot, false);

    }

    // 讓外部（JsonExecutor 模擬預覽）依 QR frame 座標找最近的 cube
    // qrPos: 感知/csharp_server 用的 QR frame (x=水平寬, y=水平深, z=高)
    public GameObject FindNearestCube(float qrX, float qrY, float qrZ, float maxDistM = 0.10f)
    {
        float halfHeight = cubeSizeM / 2f;
        Vector3 target = QRToUnity(qrX, qrY, qrZ);
        target.y -= halfHeight;
        GameObject best = null;
        float bestD = float.MaxValue;
        foreach (var c in currentCubes)
        {
            if (c == null) continue;
            float d = Vector3.Distance(c.transform.localPosition, target);
            if (d < bestD)
            {
                bestD = d;
                best = c;
            }
        }
        return bestD <= maxDistM ? best : null;
    }

    void MakeQrMarker(string name, float qrX, float qrY, Color color)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(workspaceRoot, false);
        Vector3 pos = QRToUnity(qrX, qrY, 0f);
        pos.y = 0.006f;
        go.transform.localPosition = pos;
        go.transform.localScale = new Vector3(0.02f, 0.012f, 0.02f);
        SetColor(go, color);
    }

    void SetColor(GameObject go, Color color)
    {
        var renderer = go.GetComponent<Renderer>();
        if (renderer == null) return;
        var mat = new Material(Shader.Find("Standard"));
        if (color.a < 1f)
        {
            mat.SetFloat("_Mode", 3f);                                              // Transparent mode
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = 3000;
        }
        mat.color = color;
        renderer.material = mat;
    }

    // ==========================================================
    // 主輪詢迴圈：只 poll mode 端點，決定要不要抓 /scene
    // ==========================================================
    IEnumerator PollLoop()
    {
        while (true)
        {
            yield return StartCoroutine(FetchModeAndMaybeRefresh());
            yield return new WaitForSeconds(pollIntervalSec);
        }
    }

    IEnumerator FetchModeAndMaybeRefresh()
    {
        using (UnityWebRequest req = UnityWebRequest.Get(sceneModeUrl))
        {
            req.timeout = 3;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                if (previousMode == null)   // 完全連不上，第一次就 log
                    Debug.LogWarning($"[SceneSyncer] 連不上 {sceneModeUrl}：{req.error}");
                yield break;
            }

            string currentMode = ParseMode(req.downloadHandler.text);
            if (string.IsNullOrEmpty(currentMode))
                yield break;

            // 何時 refresh 場景（抓 /scene）：
            //   1. 首次 poll（不管 mode 是 idle 還 executing，都先抓一次讓畫面有東西）
            //   2. 之後只在 executing→idle 邊緣抓
            bool firstPoll = (previousMode == null);
            bool executingToIdle = (currentMode == "idle" && previousMode == "executing");
            bool shouldRefresh = firstPoll || executingToIdle;

            if (firstPoll)
                Debug.Log($"[SceneSyncer] 首次連上 perception，mode={currentMode}，抓一次 /scene");

            previousMode = currentMode;

            if (shouldRefresh)
                yield return StartCoroutine(FetchAndApplyScene());
        }
    }

    string ParseMode(string json)
    {
        try
        {
            var resp = JsonUtility.FromJson<ModeResponse>(json);
            return resp != null ? resp.mode : null;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[SceneSyncer] mode parse failed: {e.Message}");
            return null;
        }
    }

    IEnumerator FetchAndApplyScene()
    {
        using (UnityWebRequest req = UnityWebRequest.Get(sceneUrl))
        {
            req.timeout = 3;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                // 靜默失敗（perception_server 未起），避免 console 洗版
                yield break;
            }

            SceneResponse scene = null;
            try
            {
                scene = JsonUtility.FromJson<SceneResponse>(req.downloadHandler.text);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[SceneSyncer] JSON parse failed: {e.Message}");
                yield break;
            }

            if (scene == null || scene.objects == null)
                yield break;

            ApplyObjects(scene.objects);
        }
    }

    void ApplyObjects(SceneObjectInfo[] objects)
    {
        if (cubeContainer == null)
        {
            cubeContainer = new GameObject("CubeContainer").transform;
            cubeContainer.SetParent(transform, false);
        }

        // 只保留有 position 的（perception 端算得出 3D 座標的）
        List<SceneObjectInfo> valid = new List<SceneObjectInfo>();
        foreach (var o in objects)
        {
            if (o == null || o.position == null) continue;
            if (string.IsNullOrEmpty(o.position.source)) continue;   // position 欄位存在但無有效值
            valid.Add(o);
        }

        // 確保 GameObject 數量對齊（都建成 primitive cube；scale 在下面 per-object 決定）
        while (currentCubes.Count < valid.Count)
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(cubeContainer, false);
            currentCubes.Add(cube);
        }
        while (currentCubes.Count > valid.Count)
        {
            int last = currentCubes.Count - 1;
            Destroy(currentCubes[last]);
            currentCubes.RemoveAt(last);
        }

        // 依序更新位置、scale（依 shape/orientation）、顏色、名稱
        for (int i = 0; i < valid.Count; i++)
        {
            var obj = valid[i];
            var go = currentCubes[i];

            // Scale：cube 是正方；domino 沿長軸 5cm、短軸 2.5cm
            //   robot X 方向 → Unity +Z；robot Y 方向 → Unity -X
            //   horizontal → 長軸沿 robot X（= Unity Z）
            //   vertical   → 長軸沿 robot Y（= Unity X，scale 取絕對值）
            Vector3 scale;
            if (obj.shape == "domino")
            {
                scale = obj.orientation == "vertical"
                    ? new Vector3(cubeSizeM * 2f, cubeSizeM, cubeSizeM)
                    : new Vector3(cubeSizeM, cubeSizeM, cubeSizeM * 2f);
            }
            else
            {
                scale = Vector3.one * cubeSizeM;
            }
            go.transform.localScale = scale;

            // QR frame → Unity local（統一走 helper）
            //   物件中心 Y = position.z - halfHeight（position.z 是頂面）
            float halfHeight = cubeSizeM / 2f;    // domino 高度也是 2.5 cm
            Vector3 unityPos = QRToUnity(obj.position.x, obj.position.y, obj.position.z);
            unityPos.y -= halfHeight;
            go.transform.localPosition = unityPos;

            // 顏色依 name 判斷
            Color color = Color.gray;
            if (obj.name.Contains("yellow")) color = new Color(1f, 0.85f, 0.1f);
            else if (obj.name.Contains("black")) color = new Color(0.1f, 0.1f, 0.1f);
            SetColor(go, color);

            go.name = $"{obj.name}_{i}";
        }
    }

    // ==========================================================
    // JSON 對應資料類別（JsonUtility 需要 [Serializable]）
    // ==========================================================
    [System.Serializable]
    public class ScenePosition
    {
        public string source;
        public float x, y, z;
    }

    [System.Serializable]
    public class SceneObjectInfo
    {
        public string name;
        public float confidence;
        public string source;
        public string shape;         // "cube" / "domino"
        public string orientation;   // "horizontal" / "vertical" / ""
        public ScenePosition position;
    }

    [System.Serializable]
    public class SceneResponse
    {
        public int image_width;
        public int image_height;
        public SceneObjectInfo[] objects;
    }

    [System.Serializable]
    public class ModeResponse
    {
        public string mode;
    }
}
