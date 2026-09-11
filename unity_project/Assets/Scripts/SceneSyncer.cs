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
    // 呼叫端拿到 Vector3 後可再加 halfHeight 之類的 offset
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
    public float workspaceWidthM = 0.622f;       // QR1 → QR2 距離
    public float workspaceDepthM = 0.281f;       // QR1 → QR3 距離

    [Header("手臂 base 在 QR frame 中的位置（把 workspace 對齊到手臂）")]
    // 預設：手臂在 QR3-QR4 邊的中點（近手臂側邊的中央）
    // 若實際擺法不同，改這個 X/Y（QR frame 座標）
    public float armBaseAtQrX = 0.311f;   // = workspaceWidthM / 2
    public float armBaseAtQrY = 0.281f;   // = workspaceDepthM（QR3-QR4 邊）

    [Header("Workspace 視覺旋轉")]
    // 繞 Unity Y 軸轉整個 workspace（含 cubes / QR 標記 / zones）
    // 90 = 順時針 90°；只影響視覺不影響 QR→Unity 的 IK 計算
    public float workspaceYawDeg = 90f;

    [Header("補貨區 / 擺放區邊界（跟 PlacementPlanner 常數對齊）")]
    public float supplyZoneXMax = 0.35f;
    public float targetZoneOriginX = 0.49f;
    public float targetZoneOriginY = 0.04f;
    public float cellSize = 0.04f;               // 2.5cm 立方體 + 1.5cm 間隙
    public int gridRows = 5;
    public int gridCols = 5;

    [Header("積木顯示")]
    public float cubeSizeM = 0.025f;             // 2.5 cm 立方體
    public bool autoCreateWorkspace = true;      // 啟動時自動建工作平面 + QR 標記

    [Header("桌面顯示放大")]
    // 只放大視覺，不改任何座標邏輯（cube localPosition 仍是真實公尺）
    // 1.0 = 原本大小；> 1 放大；< 1 縮小
    public float visualScale = 1.0f;

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

        // 把整組 workspace 位移，讓 QR(armBaseAtQrX, armBaseAtQrY, 0) 對到 Unity (0,0,0)
        // 這樣手臂（在 world 原點）視覺上就落在 QR frame 指定的位置
        Vector3 armInWorkspace = QRToUnity(armBaseAtQrX, armBaseAtQrY, 0f);
        workspaceRoot.localPosition = -armInWorkspace;

        // workspaceYawDeg 之前只宣告沒套用，桌面方向永遠沒轉到，紅色 QR1
        // 標記才會對不上手臂方位。繞 workspaceRoot 自己的原點（就是上面設定的
        // 手臂位置）轉，只影響視覺（plane/QR 標記/zones/cubes 都是它的子物件，
        // 一起轉），不影響 QRToUnity 本身、也不影響 JsonExecutor 那邊的 IK 計算。
        workspaceRoot.localRotation = Quaternion.Euler(0f, workspaceYawDeg, 0f);

        // 工作平面（薄薄的白色 Cube 當桌板）
        // Robot X 沿 workspaceWidthM，Y 沿 workspaceDepthM；在 Unity 是 Z 沿 width、-X 沿 depth
        GameObject plane = GameObject.CreatePrimitive(PrimitiveType.Cube);
        plane.name = "WorkspacePlane";
        plane.transform.SetParent(workspaceRoot, false);
        Vector3 planeCenter = QRToUnity(workspaceWidthM / 2f, workspaceDepthM / 2f, 0f);
        planeCenter.y = -0.005f;
        plane.transform.localPosition = planeCenter;
        // Unity local scale: X 是 depth（robot Y 方向）、Z 是 width（robot X 方向）
        plane.transform.localScale = new Vector3(workspaceDepthM, 0.01f, workspaceWidthM);
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
        float targetW = gridCols * cellSize;  // robot X 方向
        float targetD = gridRows * cellSize;  // robot Y 方向
        GameObject target = GameObject.CreatePrimitive(PrimitiveType.Cube);
        target.name = "TargetZone";
        target.transform.SetParent(workspaceRoot, false);
        Vector3 targetCenter = QRToUnity(
            targetZoneOriginX + (gridCols - 1) * cellSize / 2f,
            targetZoneOriginY + (gridRows - 1) * cellSize / 2f,
            0f);
        targetCenter.y = 0.002f;
        target.transform.localPosition = targetCenter;
        target.transform.localScale = new Vector3(targetD, 0.001f, targetW);
        SetColor(target, new Color(1f, 0.85f, 0.4f, 0.5f));

        // 積木容器
        cubeContainer = new GameObject("CubeContainer").transform;
        cubeContainer.SetParent(workspaceRoot, false);

        // 應用視覺放大（只影響顯示大小，不影響 cube 內部座標）
        if (visualScale > 0f && Mathf.Abs(visualScale - 1f) > 0.001f)
        {
            workspaceRoot.localScale = Vector3.one * visualScale;
        }
    }

    // 讓外部（JsonExecutor 模擬模式）依 QR frame 座標找最近的 cube
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

    // 讓外部（JsonExecutor 模擬模式）以 QR frame 座標建立新 cube
    public GameObject SpawnCube(string name, float qrX, float qrY, float qrZ, Color color)
    {
        if (cubeContainer == null) return null;
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = name;
        cube.transform.SetParent(cubeContainer, false);
        cube.transform.localScale = Vector3.one * cubeSizeM;
        float halfHeight = cubeSizeM / 2f;
        Vector3 pos = QRToUnity(qrX, qrY, qrZ);
        pos.y -= halfHeight;
        cube.transform.localPosition = pos;
        SetColor(cube, color);
        currentCubes.Add(cube);
        return cube;
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
            //   vertical   → 長軸沿 robot Y（= Unity X 反向；scale 只需要絕對值）
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
