using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// UR3e Kinematics 校準工具
//
// 目的：讓 Unity UR3 mesh 在給定 UR3e 官方 joint 角度時，視覺 TCP 落在 UR3eKinematics
//       算出來的位置。校準完就能開 useCanonicalKinematics=true。
//
// 用法：
//   1. Hierarchy 空 GameObject 掛此 script（例如 "Calibrator"）
//   2. Inspector 把 robotArm 拖進來
//   3. 按下方 Preset 按鈕測試
//   4. 觀察 IK 目標球（紅色球）跟手臂實際 wrist 是否重合
//      - 重合 → RotationAxis/Offsets 校準正確
//      - 不重合 → 該 joint 的 axis/offset 需要調整
public class UR3eKinematicsCalibrator : MonoBehaviour
{
    [Header("Target arm")]
    public RobotArm robotArm;

    [Header("IK 目標視覺化（紅色球顯示 UR3eKinematics 算出的 TCP 位置）")]
    public bool showIKTarget = true;
    public float targetSphereSize = 0.03f;

    [Header("Custom test joints（度）")]
    public float base_deg = 0f;
    public float shoulder_deg = -90f;
    public float elbow_deg = 0f;
    public float wrist1_deg = -90f;
    public float wrist2_deg = 0f;
    public float wrist3_deg = 0f;

    [Header("Debug output")]
    [TextArea(6, 20)] public string lastKinematicsInfo = "";

    private GameObject ikTargetSphere;

    void Reset()
    {
        if (robotArm == null) robotArm = FindObjectOfType<RobotArm>();
    }

    void Update()
    {
        UpdateIKTargetSphere();
    }

    // ============================================================
    // Preset：等距 45° 掃單一 joint（其他 joint 保持 home）
    // 每個 preset 應該符合真實 UR3e 該姿態
    // ============================================================
    [ContextMenu("Preset: Home [0,-90,0,-90,0,0]")]
    public void PresetHome() => Drive(0, -90, 0, -90, 0, 0);

    [ContextMenu("Preset: Startpoint [-90,-90,0,-90,0,0]")]
    public void PresetStartpoint() => Drive(-90, -90, 0, -90, 0, 0);

    [ContextMenu("Preset: Zero [0,0,0,0,0,0] (全直橫伸)")]
    public void PresetZero() => Drive(0, 0, 0, 0, 0, 0);

    [ContextMenu("Preset: 只轉 J1(base) +90")]
    public void P_J1() => Drive(90, -90, 0, -90, 0, 0);

    [ContextMenu("Preset: 只轉 J2(shoulder) -45 (從 home)")]
    public void P_J2() => Drive(0, -135, 0, -90, 0, 0);

    [ContextMenu("Preset: 只轉 J3(elbow) +45 (從 home)")]
    public void P_J3() => Drive(0, -90, 45, -90, 0, 0);

    [ContextMenu("Preset: 只轉 J4(wrist1) +45 (從 home)")]
    public void P_J4() => Drive(0, -90, 0, -45, 0, 0);

    [ContextMenu("Preset: 只轉 J5(wrist2) +90 (從 home)")]
    public void P_J5() => Drive(0, -90, 0, -90, 90, 0);

    [ContextMenu("Preset: 只轉 J6(wrist3) +90 (從 home)")]
    public void P_J6() => Drive(0, -90, 0, -90, 0, 90);

    [ContextMenu("Preset: Upright candle [0,-90,0,0,0,0]")]
    public void PresetUpright() => Drive(0, -90, 0, 0, 0, 0);

    // 檢查工作平面的 QR 標記是否在現實該在的位置：量到的位置換回 robot base 座標，
    // 跟 JsonExecutor.QR1_X/Y/Z（Teach Pendant 實測）加上工作平面尺寸算出的期望值比對。
    // 能抓出場景物件的 Transform 被動過、Inspector 殘留值等問題，不用靠看截圖判斷。
    [ContextMenu("★ Verify workspace QR positions")]
    public void VerifyWorkspaceQR()
    {
        if (robotArm == null) robotArm = FindObjectOfType<RobotArm>();
        var syncer = FindObjectOfType<SceneSyncer>();
        if (robotArm == null || syncer == null) { Debug.LogError("[Calibrator] 找不到 RobotArm 或 SceneSyncer"); return; }

        float W = syncer.workspaceWidthM, D = syncer.workspaceDepthM;
        var expect = new (string name, float qx, float qy)[]
            { ("QR1", 0f, 0f), ("QR2", W, 0f), ("QR3", 0f, D), ("QR4", W, D) };
        Matrix4x4 unity2Robot = RobotArm.Robot2Unity.inverse;

        var sb = new System.Text.StringBuilder("[Calibrator] QR 標記位置（robot base 座標，m）\n");
        float worst = 0f;
        foreach (var (name, qx, qy) in expect)
        {
            var go = GameObject.Find(name);
            if (go == null) { sb.AppendLine($"  {name}: 場景找不到（要在 Play 中執行）"); worst = float.MaxValue; continue; }
            Vector3 got = unity2Robot.MultiplyPoint(robotArm.transform.InverseTransformPoint(go.transform.position));
            Vector2 want = new Vector2(JsonExecutor.QR1_X + qx, JsonExecutor.QR1_Y + qy);
            float errMm = Vector2.Distance(new Vector2(got.x, got.y), want) * 1000f;
            worst = Mathf.Max(worst, errMm);
            sb.AppendLine($"  {name}: 實際 ({got.x,7:F3}, {got.y,7:F3}, z {got.z:F3})   " +
                          $"應在 ({want.x,7:F3}, {want.y,7:F3})   水平誤差 {errMm,6:F1} mm");
        }
        sb.Append(worst < 1f
            ? $"  → 工作平面相對手臂的位置與現實一致（z 應約為 QR1_Z + 0.006 = {JsonExecutor.QR1_Z + 0.006f:F3}）"
            : "  → 有偏差：檢查 SceneSyncer 物件 Transform 是否為原點、無旋轉，以及 Arm Base At Qr X/Y 的值");
        Debug.Log(sb.ToString());
    }

    [ContextMenu("Drive with Inspector custom joints")]
    public void DriveCustom() => Drive(base_deg, shoulder_deg, elbow_deg, wrist1_deg, wrist2_deg, wrist3_deg);

    // 一鍵掃過多組姿態，比對 UR3eKinematics.FK 的預測 TCP 跟手臂實際 flange 位置。
    // 全部通過 = mesh 的 joint 慣例跟 UR3e 官方一致，可以開 useCanonicalKinematics。
    [ContextMenu("★ Verify ALL poses (batch)")]
    public void VerifyAllPoses()
    {
        if (robotArm == null) robotArm = FindObjectOfType<RobotArm>();
        if (robotArm == null || robotArm.Angles == null || robotArm.Angles.Length < 6)
        { Debug.LogError("[Calibrator] RobotArm 沒配好"); return; }
        if (robotArm.TCP == null)
        { Debug.LogError("[Calibrator] RobotArm.TCP 沒設，無法比對。先跑 UR3eArmBuilder 的 Wire To RobotArm"); return; }

        var configs = new (string name, float[] q)[]
        {
            ("zero",       new float[]{   0,    0,   0,    0,   0,   0 }),
            ("home",       new float[]{   0,  -90,   0,  -90,   0,   0 }),
            ("startpoint", new float[]{ -90,  -90,   0,  -90,   0,   0 }),
            ("J1 +90",     new float[]{  90,  -90,   0,  -90,   0,   0 }),
            ("J1 -90",     new float[]{ -90,  -90,   0,  -90,   0,   0 }),
            ("J2 -45",     new float[]{   0, -135,   0,  -90,   0,   0 }),
            ("J3 +45",     new float[]{   0,  -90,  45,  -90,   0,   0 }),
            ("J4 +45",     new float[]{   0,  -90,   0,  -45,   0,   0 }),
            ("J5 +90",     new float[]{   0,  -90,   0,  -90,  90,   0 }),
            ("J6 +90",     new float[]{   0,  -90,   0,  -90,   0,  90 }),
            ("mixed A",    new float[]{  30,  -70,  50, -100,  45,  20 }),
            ("mixed B",    new float[]{ -55, -120,  80,  -40, -60, -35 }),
        };

        var saved = (float[])robotArm.Angles.Clone();
        var sb = new System.Text.StringBuilder("[Calibrator] FK vs 實際 flange 批次驗證\n");
        float worst = 0f; string worstName = "";
        int passed = 0;

        foreach (var (name, q) in configs)
        {
            for (int i = 0; i < 6; i++) robotArm.Angles[i] = q[i];
            robotArm.ApplyAnglesToTransforms();

            double[] qRad = new double[6];
            for (int i = 0; i < 6; i++) qRad[i] = q[i] * System.Math.PI / 180.0;
            var pose = UR3eKinematics.FKPose(qRad);

            Vector3 predicted = RobotArm.Robot2Unity.MultiplyPoint(
                new Vector3((float)pose.x, (float)pose.y, (float)pose.z));
            Vector3 actual = robotArm.transform.InverseTransformPoint(robotArm.TCP.position);

            float errMm = Vector3.Distance(predicted, actual) * 1000f;
            bool ok = errMm < 1f;
            if (ok) passed++;
            if (errMm > worst) { worst = errMm; worstName = name; }

            sb.AppendLine($"  {name,-11} err={errMm,7:F3} mm  {(ok ? "OK" : "FAIL")}" +
                          (ok ? "" : $"\n      預測 {predicted}  實際 {actual}"));
        }

        // 還原
        for (int i = 0; i < 6; i++) robotArm.Angles[i] = saved[i];
        robotArm.ApplyAnglesToTransforms();

        sb.AppendLine($"  ───────────────────────────────");
        sb.Append(passed == configs.Length
            ? $"  全部 {configs.Length} 組通過（最大誤差 {worst:F3} mm @ {worstName}）\n" +
              $"  → mesh joint 慣例與 UR3e 官方一致，可以開 useCanonicalKinematics"
            : $"  {passed}/{configs.Length} 通過，最大誤差 {worst:F3} mm @ {worstName}\n" +
              $"  → 失敗的那幾組看預測 vs 實際的差異方向，通常是某個 joint 的軸符號反了");
        Debug.Log(sb.ToString());
    }

    // ============================================================
    // 灌進 Angles 並更新 IK 目標球位置
    // ============================================================
    public void Drive(float b, float s, float e, float w1, float w2, float w3)
    {
        if (robotArm == null) robotArm = FindObjectOfType<RobotArm>();
        if (robotArm == null || robotArm.Angles == null || robotArm.Angles.Length < 6)
        {
            Debug.LogError("[Calibrator] RobotArm 沒配好");
            return;
        }
        robotArm.followRealRobotFeedback = false;
        RobotArm.FreezeVisualFeedback = false;
        robotArm.Angles[0] = b;
        robotArm.Angles[1] = s;
        robotArm.Angles[2] = e;
        robotArm.Angles[3] = w1;
        robotArm.Angles[4] = w2;
        robotArm.Angles[5] = w3;

        // 立即套用，否則下面讀到的 TCP 位置會是上一幀的姿態
        robotArm.ApplyAnglesToTransforms();

        double[] q = new double[6];
        for (int i = 0; i < 6; i++) q[i] = robotArm.Angles[i] * System.Math.PI / 180.0;
        var pose = UR3eKinematics.FKPose(q);

        Vector3 unityTCP = RobotArm.Robot2Unity.MultiplyPoint(new Vector3((float)pose.x, (float)pose.y, (float)pose.z));

        // 實際 flange/TCP 在 robotArm 本地座標的位置——拿來跟 FK 預測對照，
        // 兩者的差異直接告訴我們 Robot2Unity 該用哪個映射
        string actualLine = "  (RobotArm.TCP 未設定，無法比對)";
        if (robotArm.TCP != null)
        {
            Vector3 actualLocal = robotArm.transform.InverseTransformPoint(robotArm.TCP.position);
            actualLine = $"  實際 TCP local:  ({actualLocal.x:F4}, {actualLocal.y:F4}, {actualLocal.z:F4})";
        }

        lastKinematicsInfo =
            $"Angles = [{b:F1}, {s:F1}, {e:F1}, {w1:F1}, {w2:F1}, {w3:F1}]°\n" +
            $"  Robot frame TCP: ({pose.x:F4}, {pose.y:F4}, {pose.z:F4}) m\n" +
            $"  FK→Unity local:  ({unityTCP.x:F4}, {unityTCP.y:F4}, {unityTCP.z:F4})\n" +
            actualLine + "\n" +
            $"→ 後兩行要一致。不一致就是 Robot2Unity 的映射跟實際手臂擺法不合。";
        Debug.Log($"[Calibrator] {lastKinematicsInfo}");
    }

    // ============================================================
    // 每 frame 更新目標球位置（Angles 隨時可能被外部改）
    // ============================================================
    void UpdateIKTargetSphere()
    {
        if (!showIKTarget)
        {
            if (ikTargetSphere != null) ikTargetSphere.SetActive(false);
            return;
        }
        if (robotArm == null || robotArm.Angles == null || robotArm.Angles.Length < 6) return;

        if (ikTargetSphere == null)
        {
            ikTargetSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ikTargetSphere.name = "_IKTargetSphere_";
            var col = ikTargetSphere.GetComponent<Collider>();
            if (col != null) Destroy(col);
            var rend = ikTargetSphere.GetComponent<Renderer>();
            var mat = new Material(Shader.Find("Standard"));
            mat.color = new Color(1f, 0.15f, 0.15f, 0.7f);
            mat.SetFloat("_Mode", 3f);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.renderQueue = 3000;
            rend.material = mat;
        }
        ikTargetSphere.SetActive(true);
        ikTargetSphere.transform.localScale = Vector3.one * targetSphereSize;

        double[] q = new double[6];
        for (int i = 0; i < 6; i++) q[i] = robotArm.Angles[i] * System.Math.PI / 180.0;
        var pose = UR3eKinematics.FKPose(q);
        // Robot frame → Unity local，再套 robotArm 本身的 transform 得到 world
        Vector3 unityLocal = RobotArm.Robot2Unity.MultiplyPoint(new Vector3((float)pose.x, (float)pose.y, (float)pose.z));
        ikTargetSphere.transform.position = robotArm.transform.TransformPoint(unityLocal);
    }

    [ContextMenu("Hide IK target sphere")]
    public void HideSphere()
    {
        showIKTarget = false;
        if (ikTargetSphere != null) ikTargetSphere.SetActive(false);
    }

    // ============================================================
    // 直接對六 joint 逐一測試哪個 axis + offset 對
    // 需要當前手臂視覺至少在 home，然後執行此方法會把 J1 分別轉到六個方向
    // 觀察哪個方向讓 J1 mesh 繞正確軸旋轉
    // ============================================================
    [ContextMenu("Diagnose: print current joint transforms")]
    public void DiagnoseTransforms()
    {
        if (robotArm == null || robotArm.Transforms == null) { Debug.LogError("RobotArm not set"); return; }
        for (int i = 0; i < robotArm.Transforms.Length; i++)
        {
            var t = robotArm.Transforms[i];
            if (t == null) { Debug.Log($"[Calibrator] Transforms[{i}] = null"); continue; }
            Debug.Log($"[Calibrator] Transforms[{i}] = '{t.name}', axis={robotArm.RotationAxis[i]}, offset={robotArm.RotationOffsets[i]}, localRot={t.localRotation.eulerAngles}");
        }
    }
}
