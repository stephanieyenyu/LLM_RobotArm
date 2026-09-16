using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// UR3eArmBuilder：從 UR3e 官方 URDF 數值手動建出正確的 joint 鏈
//
// 為什麼不用 Unity URDF Importer：它會把階層攤平，之前試過多次都失敗。
// 這裡直接把 ur_description/urdf 的 joint origin 和 link visual origin 硬寫進來，
// 保證 link 長度、joint 軸向、單位（公尺）全部構造上就正確，不需要事後校準。
//
// 資料來源：ur_description ur3e.urdf
//   joint origin（決定 link 長度）：
//     shoulder_pan  xyz(0, 0, 0.15185)        rpy(0, 0, 0)
//     shoulder_lift xyz(0, 0, 0)              rpy(90, 0, 0)
//     elbow         xyz(-0.24355, 0, 0)       rpy(0, 0, 0)
//     wrist_1       xyz(-0.2132, 0, 0.13105)  rpy(0, 0, 0)
//     wrist_2       xyz(0, -0.08535, 0)       rpy(90, 0, 0)
//     wrist_3       xyz(0, 0.0921, 0)         rpy(90, 180, 180)
//   六個 joint 的 axis 全部是 local Z。
//
// 用法：
//   1. Hierarchy 建空 GameObject（例如 "UR3e"），Position 設 (0,0,0)
//   2. Add Component → UR3e Arm Builder
//   3. 元件右上三個點 → Build UR3e Arm
//   4. 再按 Wire To RobotArm，自動把 6 個 joint 填進 RobotArm
//
// 座標轉換（含左右手系鏡射）寫死在 WRAPPER_EULER / WRAPPER_SCALE，不需要手調。
public class UR3eArmBuilder : MonoBehaviour
{
    [Header("Mesh 來源")]
    public string visualMeshFolder = "Assets/ur3e_meshes/visual";

    // 座標系轉換（URDF 右手系 Z-up → Unity 左手系 Y-up）
    // 左右手系之間的轉換是鏡射（行列式 -1），只靠旋轉做不到。之前只用 Euler(-90,0,0)：
    // 運動學自洽（批次驗證全過），但手臂與工作平面一起變成現實的鏡像。
    // 正確做法是「一軸負縮放 + 旋轉」：scale(-1,1,1) 之後 Euler(-90,90,0)
    //   → URDF (x,y,z) 對到 Unity (-y, z, x)，即 ROS→Unity 標準轉換。
    static readonly Vector3 WRAPPER_EULER = new Vector3(-90f, 90f, 0f);
    static readonly Vector3 WRAPPER_SCALE = new Vector3(-1f, 1f, 1f);

    [Header("Joint 旋轉軸（URDF 六個 joint 都是 local Z）")]
    // handedness 差異會讓旋轉方向相反；不對就改成 NegativeZ
    public Axis jointAxis = Axis.PositiveZ;

    [Header("Mesh 額外修正（Unity Collada 匯入若自動轉過 Z-up 就需要）")]
    // Unity 讀到 .dae 的 <up_axis>Z_UP</up_axis> 會自己套一次 Rx(-90) 轉成 Y-up。
    // URDF 的 visual rpy 假設 mesh 還在 Z-up，所以要右乘 Rx(+90) 抵銷，否則零件會散開。
    public Vector3 meshExtraRotationEuler = new Vector3(90f, 0f, 0f);

    [Header("建好後要自動接上的 RobotArm")]
    public RobotArm targetRobotArm;

    // 建完後填入，供 Wire To RobotArm 使用
    [SerializeField] private Transform[] builtJoints = new Transform[6];
    [SerializeField] private Transform builtFlange;

    // URDF 一個 link 的完整定義
    struct LinkSpec
    {
        public string linkName;
        public Vector3 jointXyz;      // joint origin（相對 parent link）
        public Vector3 jointRpyDeg;   // joint origin rpy
        public string meshName;
        public Vector3 visualXyz;     // visual origin（相對這個 link）
        public Vector3 visualRpyDeg;
    }

    // base_link_inertia 是鏈的起點（固定，不是 revolute joint），之後 6 個是 J1..J6
    static readonly LinkSpec BASE = new LinkSpec
    {
        linkName = "base_link_inertia",
        jointXyz = Vector3.zero, jointRpyDeg = Vector3.zero,
        meshName = "base", visualXyz = Vector3.zero, visualRpyDeg = new Vector3(0f, 0f, 180f),
    };

    static readonly LinkSpec[] JOINTS = new LinkSpec[]
    {
        new LinkSpec {
            linkName = "shoulder_link",
            jointXyz = new Vector3(0f, 0f, 0.15185f), jointRpyDeg = Vector3.zero,
            meshName = "shoulder", visualXyz = Vector3.zero, visualRpyDeg = new Vector3(0f, 0f, 180f),
        },
        new LinkSpec {
            linkName = "upper_arm_link",
            jointXyz = Vector3.zero, jointRpyDeg = new Vector3(90f, 0f, 0f),
            meshName = "upperarm", visualXyz = new Vector3(0f, 0f, 0.12f), visualRpyDeg = new Vector3(90f, 0f, -90f),
        },
        new LinkSpec {
            linkName = "forearm_link",
            jointXyz = new Vector3(-0.24355f, 0f, 0f), jointRpyDeg = Vector3.zero,
            meshName = "forearm", visualXyz = new Vector3(0f, 0f, 0.027f), visualRpyDeg = new Vector3(90f, 0f, -90f),
        },
        new LinkSpec {
            linkName = "wrist_1_link",
            jointXyz = new Vector3(-0.2132f, 0f, 0.13105f), jointRpyDeg = Vector3.zero,
            meshName = "wrist1", visualXyz = new Vector3(0f, 0f, -0.104f), visualRpyDeg = new Vector3(90f, 0f, 0f),
        },
        new LinkSpec {
            linkName = "wrist_2_link",
            jointXyz = new Vector3(0f, -0.08535f, 0f), jointRpyDeg = new Vector3(90f, 0f, 0f),
            meshName = "wrist2", visualXyz = new Vector3(0f, 0f, -0.08535f), visualRpyDeg = Vector3.zero,
        },
        new LinkSpec {
            linkName = "wrist_3_link",
            jointXyz = new Vector3(0f, 0.0921f, 0f), jointRpyDeg = new Vector3(90f, 180f, 180f),
            meshName = "wrist3", visualXyz = new Vector3(0f, 0f, -0.0921f), visualRpyDeg = new Vector3(90f, 0f, 0f),
        },
    };

    // 不重建、不動夾爪：只把既有 urdf_root 改成正確的座標轉換
    [ContextMenu("Fix handedness on existing arm (不重建)")]
    public void FixHandedness()
    {
        var wrapper = transform.Find("urdf_root");
        if (wrapper == null) { Debug.LogError("[UR3eArmBuilder] 找不到 urdf_root，還沒 Build 過"); return; }
#if UNITY_EDITOR
        Undo.RecordObject(wrapper, "Fix UR3e handedness");
#endif
        wrapper.localRotation = Quaternion.Euler(WRAPPER_EULER);
        wrapper.localScale = WRAPPER_SCALE;
#if UNITY_EDITOR
        EditorUtility.SetDirty(wrapper);
#endif
        Debug.Log("[UR3eArmBuilder] urdf_root 已改為 scale (-1,1,1) + Euler(-90,90,0)，URDF (x,y,z) → Unity (-y,z,x)。" +
                  "joint、夾爪、tcp_tip 都沒動，不用重新 Wire。");
    }

    [ContextMenu("Build UR3e Arm")]
    public void Build()
    {
        // 使用者掛在 flange 底下的東西（夾爪、tcp_tip）先搬出來，重建後放回，避免被一起刪掉
        var keep = new System.Collections.Generic.List<(Transform t, Vector3 p, Quaternion r, Vector3 s)>();
        foreach (var ft in GetComponentsInChildren<Transform>(true))
        {
            if (ft.name != "flange_tool0") continue;
            for (int i = ft.childCount - 1; i >= 0; i--)
            {
                var child = ft.GetChild(i);
                keep.Add((child, child.localPosition, child.localRotation, child.localScale));
                child.SetParent(null, false);
            }
        }

        // 清掉舊的
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var c = transform.GetChild(i).gameObject;
            if (Application.isPlaying) Destroy(c); else DestroyImmediate(c);
        }

        // Root wrapper：URDF 右手系 Z-up → Unity 左手系 Y-up
        var wrapper = new GameObject("urdf_root").transform;
        wrapper.SetParent(transform, false);
        wrapper.localRotation = Quaternion.Euler(WRAPPER_EULER);
        wrapper.localScale = WRAPPER_SCALE;
        wrapper.localPosition = Vector3.zero;

        // base_link_inertia
        Transform parent = MakeLink(wrapper, BASE);

        // J1..J6
        for (int i = 0; i < JOINTS.Length; i++)
        {
            parent = MakeLink(parent, JOINTS[i]);
            builtJoints[i] = parent;
        }

        // flange / tool0：TCP 參考點，掛 gripper 用
        var flange = new GameObject("flange_tool0").transform;
        flange.SetParent(parent, false);
        flange.localPosition = Vector3.zero;
        flange.localRotation = Quaternion.identity;
        builtFlange = flange;

        foreach (var (kt, kp, kr, ks) in keep)
        {
            kt.SetParent(flange, false);
            kt.localPosition = kp;
            kt.localRotation = kr;
            kt.localScale = ks;
        }

        Debug.Log($"[UR3eArmBuilder] 建好 7 個 link + flange（保留 {keep.Count} 個 flange 子物件）。" +
                  "joint 參照已換新，記得再按 Wire To RobotArm。");
        Debug.Log($"[UR3eArmBuilder] 驗證：shoulder 高度應為 0.15185 m，實測 " +
                  $"{(builtJoints[0].position - transform.position).magnitude:F5} m");
    }

    Transform MakeLink(Transform parent, LinkSpec spec)
    {
        var go = new GameObject(spec.linkName).transform;
        go.SetParent(parent, false);
        go.localPosition = spec.jointXyz;
        go.localRotation = UrdfRpyToQuat(spec.jointRpyDeg);

        AttachMesh(go, spec);
        return go;
    }

    void AttachMesh(Transform link, LinkSpec spec)
    {
#if UNITY_EDITOR
        string path = $"{visualMeshFolder}/{spec.meshName}.dae";
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null)
        {
            Debug.LogWarning($"[UR3eArmBuilder] {spec.linkName}: 找不到 {path}");
            return;
        }
        var visual = (GameObject)PrefabUtility.InstantiatePrefab(prefab, link);
        if (visual == null) visual = Instantiate(prefab, link);
        visual.name = "visual";
        visual.transform.localPosition = spec.visualXyz;
        visual.transform.localRotation = UrdfRpyToQuat(spec.visualRpyDeg)
                                       * Quaternion.Euler(meshExtraRotationEuler);
#endif
    }

    // URDF rpy 是固定軸 XYZ 外旋：R = Rz(yaw) · Ry(pitch) · Rx(roll)
    static Quaternion UrdfRpyToQuat(Vector3 rpyDeg)
    {
        return Quaternion.AngleAxis(rpyDeg.z, Vector3.forward)
             * Quaternion.AngleAxis(rpyDeg.y, Vector3.up)
             * Quaternion.AngleAxis(rpyDeg.x, Vector3.right);
    }

    // 把建好的 6 個 joint 自動填進 RobotArm，省得手動拖
    [ContextMenu("Wire To RobotArm")]
    public void WireToRobotArm()
    {
        if (targetRobotArm == null) targetRobotArm = GetComponent<RobotArm>() ?? FindObjectOfType<RobotArm>();
        if (targetRobotArm == null) { Debug.LogError("[UR3eArmBuilder] 找不到 RobotArm"); return; }
        for (int i = 0; i < 6; i++)
        {
            if (builtJoints[i] == null) { Debug.LogError("[UR3eArmBuilder] 還沒 Build，先按 Build UR3e Arm"); return; }
        }

#if UNITY_EDITOR
        Undo.RecordObject(targetRobotArm, "Wire UR3e joints");
#endif
        targetRobotArm.Transforms = new Transform[6];
        targetRobotArm.RotationAxis = new Axis[6];
        targetRobotArm.RotationOffsets = new int[6];
        targetRobotArm.restRotations = new Quaternion[6];
        for (int i = 0; i < 6; i++)
        {
            targetRobotArm.Transforms[i] = builtJoints[i];
            targetRobotArm.RotationAxis[i] = jointAxis;   // URDF 六個 joint 都是 local Z
            targetRobotArm.RotationOffsets[i] = 0;        // 構造正確，不需要 offset
            // 剛 Build 完，localRotation 就是 URDF 的 joint rpy = Angles 0 的基準
            targetRobotArm.restRotations[i] = builtJoints[i].localRotation;
        }
        if (targetRobotArm.TCP == null && builtFlange != null)
            targetRobotArm.TCP = builtFlange;

#if UNITY_EDITOR
        EditorUtility.SetDirty(targetRobotArm);
#endif
        Debug.Log($"[UR3eArmBuilder] 已接上 RobotArm '{targetRobotArm.name}'：" +
                  $"6 joints、axis 全部 {jointAxis}、offset 全部 0、TCP = {builtFlange?.name}");
    }

    // 量夾爪從 flange 往外伸多長，結果填進 RobotArm.toolOffsetZ。
    // 量的是「flange 本地座標的 Z 軸範圍」——Z 就是 UR 的 tool 軸方向。
    [ContextMenu("Measure tool offset (gripper tip)")]
    public void MeasureToolOffset()
    {
        if (builtFlange == null) { Debug.LogError("[UR3eArmBuilder] 還沒 Build"); return; }
        var renderers = builtFlange.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            Debug.LogWarning("[UR3eArmBuilder] flange_tool0 底下沒有 Renderer —— " +
                             "夾爪還沒掛上去。先把 Gripper prefab 拖進 flange_tool0 當子物件。");
            return;
        }

        Vector3 min = Vector3.one * float.MaxValue;
        Vector3 max = Vector3.one * float.MinValue;
        var names = new System.Text.StringBuilder();
        foreach (var r in renderers)
        {
            Bounds b = r.bounds;   // world space
            for (int c = 0; c < 8; c++)
            {
                Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3(
                    (c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                Vector3 p = builtFlange.InverseTransformPoint(corner);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            names.Append($"    {r.transform.name} (scale {r.transform.lossyScale.x:F4})\n");
        }

        Vector3 size = max - min;
        // 長軸是哪一個
        string longAxis = size.x >= size.y && size.x >= size.z ? "X"
                        : size.y >= size.z ? "Y" : "Z";

        Debug.Log($"[UR3eArmBuilder] 夾爪在 flange 本地座標的範圍（{renderers.Length} 個 Renderer）\n" +
                  $"  X: {min.x,8:F4} ~ {max.x,8:F4}   長 {size.x:F4} m\n" +
                  $"  Y: {min.y,8:F4} ~ {max.y,8:F4}   長 {size.y:F4} m\n" +
                  $"  Z: {min.z,8:F4} ~ {max.z,8:F4}   長 {size.z:F4} m   ← tool 軸\n" +
                  $"  最長的是 {longAxis} 軸。夾爪的長軸應該落在 Z（從 flange 往外伸），\n" +
                  $"  若不是 Z 就要轉 Gripper 的 Rotation 讓長軸對到 Z。\n" +
                  $"  Z 方向理想狀態：min 接近 0、max 等於夾爪長度（單向往外，不跨過原點）。\n" +
                  $"  計入的物件：\n{names}");
    }

    // 依 RobotArm.toolOffsetZ 在 flange 前方建一個 tcp_tip，並把 RobotArm.TCP 指過去。
    // 設了工具長度之後 FK 預測的是指尖，所以比對用的 TCP 也必須是指尖，否則會整體差一個工具長度。
    [ContextMenu("Apply tool offset → create TCP tip")]
    public void ApplyToolOffset()
    {
        if (builtFlange == null) { Debug.LogError("[UR3eArmBuilder] 還沒 Build"); return; }
        if (targetRobotArm == null) targetRobotArm = GetComponent<RobotArm>() ?? FindObjectOfType<RobotArm>();
        if (targetRobotArm == null) { Debug.LogError("[UR3eArmBuilder] 找不到 RobotArm"); return; }

        float off = targetRobotArm.toolOffsetZ;
        var tip = builtFlange.Find("tcp_tip");
        if (tip == null)
        {
            tip = new GameObject("tcp_tip").transform;
            tip.SetParent(builtFlange, false);
        }
        tip.localPosition = new Vector3(0f, 0f, off);
        tip.localRotation = Quaternion.identity;

#if UNITY_EDITOR
        Undo.RecordObject(targetRobotArm, "Apply tool offset");
#endif
        targetRobotArm.TCP = tip;
#if UNITY_EDITOR
        EditorUtility.SetDirty(targetRobotArm);
#endif
        Debug.Log($"[UR3eArmBuilder] tcp_tip 建在 flange 前方 {off:F4} m，RobotArm.TCP 已指向它。\n" +
                  $"  接著重跑 Calibrator 的「Verify ALL poses」應該仍然全過。");
    }

    // 建好後比對 6 個 DH 參數。
    // 注意：要比的是 localPosition 的「各分量」，不是 joint 之間的三維距離。
    // 例如 wrist_1 origin = (-0.2132, 0, 0.13105)，a3 是 X 分量、d4 是 Z 分量，
    // 三維距離 0.25026 是兩者的合成，拿來跟 a3 比會誤判。
    [ContextMenu("Verify DH parameters")]
    public void VerifyLinkLengths()
    {
        if (builtJoints[5] == null) { Debug.LogError("還沒 Build"); return; }

        var p1 = builtJoints[0].localPosition;   // shoulder_link
        var p3 = builtJoints[2].localPosition;   // forearm_link
        var p4 = builtJoints[3].localPosition;   // wrist_1_link
        var p5 = builtJoints[4].localPosition;   // wrist_2_link
        var p6 = builtJoints[5].localPosition;   // wrist_3_link

        (string, float, float)[] checks =
        {
            ("d1", p1.z,               0.15185f),
            ("a2", Mathf.Abs(p3.x),    0.24355f),
            ("a3", Mathf.Abs(p4.x),    0.21320f),
            ("d4", p4.z,               0.13105f),
            ("d5", Mathf.Abs(p5.y),    0.08535f),
            ("d6", Mathf.Abs(p6.y),    0.09210f),
        };

        var sb = new System.Text.StringBuilder("[UR3eArmBuilder] DH 參數比對\n");
        bool allOk = true;
        foreach (var (name, got, spec) in checks)
        {
            bool ok = Mathf.Abs(got - spec) < 1e-5f;
            allOk &= ok;
            sb.AppendLine($"  {name} = {got:F5} m   (spec {spec:F5})  {(ok ? "OK" : "MISMATCH")}");
        }
        sb.Append(allOk
            ? "  → 六個全部吻合：link 長度與 scale 構造上正確（1 Unity unit = 1 m）"
            : "  → 有不吻合的，檢查 JOINTS 表");
        Debug.Log(sb.ToString());
    }

    // 在每個 joint 原點放小球，讓你看得到「骨架」跟 mesh 分開判斷
    [ContextMenu("Toggle joint markers")]
    public void ToggleJointMarkers()
    {
        var existing = transform.Find("_JointMarkers_");
        if (existing != null)
        {
            if (Application.isPlaying) Destroy(existing.gameObject); else DestroyImmediate(existing.gameObject);
            Debug.Log("[UR3eArmBuilder] 已移除 joint markers");
            return;
        }
        if (builtJoints[5] == null) { Debug.LogError("還沒 Build"); return; }

        var holder = new GameObject("_JointMarkers_").transform;
        holder.SetParent(transform, false);
        for (int i = 0; i < 6; i++)
        {
            var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            s.name = $"J{i + 1}_{builtJoints[i].name}";
            s.transform.SetParent(holder, false);
            s.transform.position = builtJoints[i].position;
            s.transform.localScale = Vector3.one * 0.03f;
            var col = s.GetComponent<Collider>();
            if (col != null) { if (Application.isPlaying) Destroy(col); else DestroyImmediate(col); }
            var mat = new Material(Shader.Find("Standard"));
            mat.color = Color.HSVToRGB(i / 6f, 0.9f, 1f);
            s.GetComponent<Renderer>().sharedMaterial = mat;
        }
        Debug.Log("[UR3eArmBuilder] 已放 6 顆 joint marker（J1 紅 → J6 紫）。" +
                  "這是骨架位置，跟 mesh 對不對無關。");
    }

    // 印出每個 mesh 被 Unity 匯入後的實際 transform，用來診斷「散裝」問題
    [ContextMenu("Diagnose mesh transforms")]
    public void DiagnoseMeshes()
    {
        if (builtJoints[5] == null) { Debug.LogError("還沒 Build"); return; }
        var sb = new System.Text.StringBuilder("[UR3eArmBuilder] mesh 診斷\n");
        foreach (var link in GetComponentsInChildren<Transform>())
        {
            if (link.name != "visual") continue;
            var mf = link.GetComponentInChildren<MeshFilter>();
            string meshInfo = mf != null && mf.sharedMesh != null
                ? $"mesh='{mf.sharedMesh.name}' bounds={mf.sharedMesh.bounds.size}"
                : "（找不到 MeshFilter）";
            sb.AppendLine($"  {link.parent.name}/visual: localPos={link.localPosition} " +
                          $"localEuler={link.localEulerAngles} childCount={link.childCount}\n" +
                          $"      {meshInfo}");
        }
        Debug.Log(sb.ToString());
    }
}
