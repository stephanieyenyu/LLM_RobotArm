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
// 座標系轉換：
//   perception 端 QR frame 是右手系（Y 是水平深度、Z 是高度）
//   Unity 是左手系（Y 是高度）
//   映射：QR (x, y, z) → Unity local (x, z, y)
//         QR X (QR1→QR2)   → Unity X
//         QR Y (QR1→QR3)   → Unity Z（水平深度）
//         QR Z (高度)      → Unity Y（往上）

public class SceneSyncer : MonoBehaviour
{
    [Header("Perception Server")]
    public string sceneUrl = "http://localhost:5000/scene";
    public string sceneModeUrl = "http://localhost:5000/scene/mode";
    public float pollIntervalSec = 0.3f;         // mode 端點的輪詢頻率

    [Header("工作平面尺寸（公尺，對應真實工作台）")]
    public float workspaceWidthM = 0.622f;       // QR1 → QR2 距離
    public float workspaceDepthM = 0.281f;       // QR1 → QR3 距離

    [Header("補貨區 / 擺放區邊界（跟 PlacementPlanner 常數對齊）")]
    public float supplyZoneXMax = 0.30f;
    public float targetZoneOriginX = 0.35f;
    public float targetZoneOriginY = 0.02f;       // 往 QR1-QR2／畫面下方移 3 cm
    public float cellSize = 0.035f;              // 2.5cm 立方體 + 1cm 間隙
    public int gridRows = 6;
    public int gridCols = 6;

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

        // 工作平面（薄薄的白色 Cube 當桌板）
        GameObject plane = GameObject.CreatePrimitive(PrimitiveType.Cube);
        plane.name = "WorkspacePlane";
        plane.transform.SetParent(workspaceRoot, false);
        plane.transform.localPosition = new Vector3(workspaceWidthM / 2f, -0.005f, workspaceDepthM / 2f);
        plane.transform.localScale = new Vector3(workspaceWidthM, 0.01f, workspaceDepthM);
        SetColor(plane, new Color(0.92f, 0.92f, 0.92f));

        // 4 個 QR 角落標記（用小色塊）
        MakeQrMarker("QR1", new Vector3(0f, 0f, 0f), Color.red);
        MakeQrMarker("QR2", new Vector3(workspaceWidthM, 0f, 0f), Color.green);
        MakeQrMarker("QR3", new Vector3(0f, 0f, workspaceDepthM), Color.blue);
        MakeQrMarker("QR4", new Vector3(workspaceWidthM, 0f, workspaceDepthM), Color.magenta);

        // 補貨區（藍色半透明）
        GameObject supply = GameObject.CreatePrimitive(PrimitiveType.Cube);
        supply.name = "SupplyZone";
        supply.transform.SetParent(workspaceRoot, false);
        supply.transform.localPosition = new Vector3(supplyZoneXMax / 2f, 0.002f, workspaceDepthM / 2f);
        supply.transform.localScale = new Vector3(supplyZoneXMax, 0.001f, workspaceDepthM);
        SetColor(supply, new Color(0.4f, 0.7f, 1f, 0.5f));

        // 擺放區（黃色半透明，6×6 grid 實體邊界）
        float targetW = gridCols * cellSize;
        float targetD = gridRows * cellSize;
        GameObject target = GameObject.CreatePrimitive(PrimitiveType.Cube);
        target.name = "TargetZone";
        target.transform.SetParent(workspaceRoot, false);
        target.transform.localPosition = new Vector3(
            targetZoneOriginX + targetW / 2f,
            0.002f,
            targetZoneOriginY + targetD / 2f
        );
        target.transform.localScale = new Vector3(targetW, 0.001f, targetD);
        SetColor(target, new Color(1f, 0.85f, 0.4f, 0.5f));

        // 積木容器
        cubeContainer = new GameObject("CubeContainer").transform;
        cubeContainer.SetParent(workspaceRoot, false);
    }

    void MakeQrMarker(string name, Vector3 pos, Color color)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(workspaceRoot, false);
        go.transform.localPosition = new Vector3(pos.x, 0.006f, pos.z);
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
            //   horizontal → 長軸沿 Unity X（= QR X）
            //   vertical   → 長軸沿 Unity Z（= QR Y）
            Vector3 scale;
            if (obj.shape == "domino")
            {
                scale = obj.orientation == "vertical"
                    ? new Vector3(cubeSizeM, cubeSizeM, cubeSizeM * 2f)
                    : new Vector3(cubeSizeM * 2f, cubeSizeM, cubeSizeM);
            }
            else
            {
                scale = Vector3.one * cubeSizeM;
            }
            go.transform.localScale = scale;

            // QR frame → Unity local
            //   QR X = Unity X（水平寬度）
            //   QR Y = Unity Z（水平深度）
            //   QR Z = Unity Y（高度）
            //   物件中心 Y = position.z - halfHeight（position.z 是頂面）
            float halfHeight = cubeSizeM / 2f;    // domino 高度也是 2.5 cm
            float cy = obj.position.z - halfHeight;
            go.transform.localPosition = new Vector3(obj.position.x, cy, obj.position.y);

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
