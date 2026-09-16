using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Assets.Scripts;
using UnityEngine;

public class RobotArm : MonoBehaviour
{
    public static bool FreezeVisualFeedback { get; set; } = false;

    public Transform TCP;
    public Transform[] Transforms;
    public Axis[] RotationAxis;
    public int[] RotationOffsets;
    public Transform PrintBed;

    public float[] Angles = new float[] { 0, 0, 0, 0, 0, 0 };

    [Header("啟動姿態（度）")]
    // Start() 時把 Angles 覆蓋成這個值。預設 = UR3e Home ([0, -90, 0, -90, 0, 0]°)
    public bool overrideAnglesOnStart = true;
    public float[] startupAnglesDeg = new float[] { 0f, -90f, 0f, -90f, 0f, 0f };

    [Header("工具（夾爪）")]
    // 必須等於實機 Teach Pendant 安裝設定裡 TCP 的 Z 偏移（公尺）；程式沒送 set_tcp，實機 movel 用的就是 pendant 上的 TCP。
    // 不是夾爪 mesh 的指尖長度：夾方塊時手指要往下包住方塊，TCP 通常設在指尖上方的夾持點。
    public float toolOffsetZ = 0f;

    [Header("基準姿態")]
    // Angles=0 時每個 joint 的 localRotation。由 UR3eArmBuilder.WireToRobotArm 填入。
    // 留空的話會在第一次套用角度時就地擷取——但那要求當下手臂沒被轉過，不夠可靠。
    public Quaternion[] restRotations;

    private Quaternion[] startRotations;

    private URPackageListener urListener;
    private string ipInput = "192.168.56.101";

    // 自動連線 UR3e（跟 JsonExecutor 的 Ur IP 設一樣即可，例如 "192.168.50.204"）
    // 留空的話就等使用者在 OnGUI 面板手動輸入 IP + 按 Connect
    [Header("Auto-connect")]
    public string autoConnectIP = "";

    [Header("Visual feedback")]
    public bool followRealRobotFeedback = true;

    public Vector3 TCPPosition;
    public Quaternion TCPRotation;
    public bool[] Outputs;

    public TextAsset GCode;

    // Robot base frame（= UR 控制器 / Teach Pendant 的 Base 座標）→ Unity local
    //   Robot +X → Unity +Z
    //   Robot +Y → Unity -X
    //   Robot +Z → Unity +Y
    // 這是保持左右手性的標準 ROS→Unity 轉換（行列式 -1）。
    // 若改成行列式 +1 的對應（例如 X→X、Y→-Z），手臂與工作平面會一起變成現實的鏡像，
    // 運動學驗證仍會全過，只有跟現實比才看得出來。
    // 必須跟 UR3eArmBuilder 的 wrapper（scale(-1,1,1) + Euler(-90,90,0)）一致。
    public static readonly Matrix4x4 Robot2Unity = new Matrix4x4(
        new Vector4(0, 0, 1, 0),    // Robot X 單位向量 → Unity +Z
        new Vector4(-1, 0, 0, 0),   // Robot Y 單位向量 → Unity -X
        new Vector4(0, 1, 0, 0),    // Robot Z 單位向量 → Unity +Y
        new Vector4(0, 0, 0, 1));

    void Start()
    {
        urListener = new URPackageListener();
        EnsureBaseline();
        Outputs = new bool[18];

        // 啟動時把 Angles 覆蓋成 home 姿態（預設 UR3e home = [0,-90,0,-90,0,0]°）
        if (overrideAnglesOnStart && startupAnglesDeg != null && Angles != null)
        {
            int n = System.Math.Min(startupAnglesDeg.Length, Angles.Length);
            for (int i = 0; i < n; i++) Angles[i] = startupAnglesDeg[i];
            UnityEngine.Debug.Log($"[RobotArm] Startup Angles set to home: [{string.Join(", ", Angles)}]°");
        }

        // 自動連線（若 Inspector 有填 autoConnectIP）
        if (!string.IsNullOrWhiteSpace(autoConnectIP))
        {
            urListener.Connect(autoConnectIP);
            UnityEngine.Debug.Log($"[RobotArm] Auto-connecting to {autoConnectIP}");
        }
    }

    private void OnDestroy()
    {
        urListener?.Close();
    }

    // 決定 Angles=0 的基準姿態。
    // 優先用 builder 寫入的 restRotations（確定是建構當下的 rest pose）；
    // 沒有才就地擷取，那種情況要求呼叫時手臂尚未被轉過。
    void EnsureBaseline()
    {
        if (restRotations != null && restRotations.Length == Transforms.Length)
        {
            startRotations = restRotations;
            return;
        }
        if (startRotations != null && startRotations.Length == Transforms.Length) return;

        startRotations = new Quaternion[Transforms.Length];
        for (int i = 0; i < Transforms.Length; i++)
            if (Transforms[i] != null) startRotations[i] = Transforms[i].localRotation;
    }

    // 把 Angles[] 立即套到 Transform 上。
    // Update() 每幀會呼叫；外部（校準工具）改完 Angles 想馬上讀 TCP 位置也要呼叫，
    // 否則讀到的是上一幀的姿態。
    public void ApplyAnglesToTransforms()
    {
        if (Transforms == null || Angles == null) return;
        // 讓運動學模組跟 Inspector 的工具長度保持同步（Edit mode 也要，校準工具會用到）
        UR3eKinematics.toolOffsetZ = toolOffsetZ;
        EnsureBaseline();

        int n = Mathf.Min(Transforms.Length, Angles.Length);
        for (int i = 0; i < n; i++)
        {
            if (Transforms[i] == null) continue;
            // 不用 Transform.Rotate(axis, angle, Self)：它轉世界軸時忽略 scale，父層負縮放（鏡射 wrapper）會讓方向反轉
            Transforms[i].localRotation = startRotations[i]
                * Quaternion.AngleAxis(Angles[i] + RotationOffsets[i], axisTovector3(RotationAxis[i]));
        }
    }

    void Update()
    {
        if (FreezeVisualFeedback)
            return;

        // 只有 followRealRobotFeedback=true 才從實機讀 Angles；
        // sim 模式關掉這個，讓外部（JsonExecutor）自己寫 Angles
        if (followRealRobotFeedback && urListener != null && urListener.Connected)
            for (int i = 0; i < Transforms.Length; i++)
                Angles[i] = (float)urListener.JointData.AsArray[i].q_actual * 180f / MathF.PI;

        ApplyAnglesToTransforms();


        if(urListener != null && urListener.Connected)
        {
            // TCP Position und Rotation auslesen
            Vector4 cartPosition = Robot2Unity * new Vector4((float)urListener.CartesianInfo.X, 
                (float)urListener.CartesianInfo.Y,
                (float)urListener.CartesianInfo.Z, 1);
            Quaternion cartRotation = Quaternion.Euler(new Vector3(
                (float)urListener.CartesianInfo.Rx * 180f / Mathf.PI,
                (float)urListener.CartesianInfo.Ry * 180f / Mathf.PI,
                (float)urListener.CartesianInfo.Rz * 180f / Mathf.PI));
            var rotMat = transform.localToWorldMatrix;
            rotMat.SetColumn(3, new(0, 0, 0, 1));
            var cartForward = rotMat * (cartRotation * Vector3.forward);
            var cartUp = rotMat * (cartRotation * Vector3.up);

            // TCP 是可選的視覺化 Transform（在 UR3 末端放一個球或方塊當標記）
            // 沒指定就跳過，避免 UnassignedReferenceException
            // TCP 若是手臂自己的 tcp_tip（子物件），會隨 joint 自然跟上，不能被實機回報覆寫位置
            if (TCP != null && !TCP.IsChildOf(transform))
            {
                TCP.position = transform.localToWorldMatrix * cartPosition;
                TCP.rotation = cartRotation;
            }
            
            TCPPosition = new Vector3((float)urListener.CartesianInfo.Y, -(float)urListener.CartesianInfo.Z,
                (float)urListener.CartesianInfo.X);
            TCPRotation = Quaternion.Euler((float)urListener.CartesianInfo.Rx, (float)urListener.CartesianInfo.Ry,
                (float)urListener.CartesianInfo.Rz);

            // Digitale Ausgänge in Bool-Array übertragen
            for (int i = 0; i < Outputs.Length; i++)
            {
                int bits = urListener.MasterboardData.digitalOutputBits;
                bits >>= i;
                bits &= 1;
                Outputs[i] = bits != 0;
            }
        }
    }

    // In der OnGUI Methode sind alle UI Elemente uund deren Funktionalitäten vorhanden.
    private void OnGUI()
    {
        // OnGUI can run before Start or during a script reload, when the
        // connection listener has not been initialized yet. Return before
        // beginning any GUILayout scope so a null reference cannot unbalance it.
        if (urListener == null) return;
        GUILayout.BeginArea(new Rect(10, 10, 200, 400));
        try
        {
            if (!urListener.Connected)
            {
                GUILayout.BeginHorizontal();
                try
                {
                    ipInput = GUILayout.TextField(ipInput);
                    if (GUILayout.Button("Connect"))
                        urListener.Connect(ipInput, false);
                }
                finally
                {
                    GUILayout.EndHorizontal();
                }
            }
        }
        finally
        {
            GUILayout.EndArea();
        }
        // 連線後不再顯示 Disconnect / Home 按鈕
        // 這些功能移到 UIManager 的三個按鈕（Open / Grip / Home）
    }

    static Vector3 axisTovector3(Axis axis)
    {
        switch (axis)
        {
            case Axis.PositiveX: return new Vector3(1, 0, 0);
            case Axis.PositiveY: return new Vector3(0, 1, 0);
            case Axis.PositiveZ: return new Vector3(0, 0, 1);
            case Axis.NegativeX: return new Vector3(-1, 0, 0);
            case Axis.NegativeY: return new Vector3(0, -1, 0);
            case Axis.NegativeZ: return new Vector3(0, 0, -1);
            default: throw new Exception($"Undefined Axis: {axis}");
        }
    }
    
    public void SendProgram(IEnumerable<string> program, string programName = "program")
    {
        var list = Enumerable.Concat(Enumerable.Concat(Enumerable.Repeat($"def {programName}():", 1), program), Enumerable.Repeat("end", 1));
        urListener.SendCommand(string.Join('\n', list));
    }

    // Funktion zum Erzeugen eines Roboterskripts aus einer Liste von Punkten
    public static string[] CreatePath(Transform bed, Transform robotBase, IEnumerable<Vector3> points, float v = 0.3f, float r = 0.02f)
    {
        // Die Punkte werden vom Bed-Koordinatensystem in das Roboterkoordinatensystem konvertiert
        var robotToBed = robotBase.worldToLocalMatrix * bed.localToWorldMatrix;
        var transform = Robot2Unity.inverse * robotToBed * Robot2Unity;
        var transformedPoints = points.Select(p => float.IsNaN(p.x) ? p : transform.MultiplyPoint(p));
        
        // Die beiden Bewegungsbefehle werden als Formatvorlage definiert
        string commandl = $"movel(p[{{0}}, {{1}}, {{2}}, {{3}}, {{4}}, {{5}}], a=1.4, v={v.ToString(CultureInfo.InvariantCulture)}, t=0, r={r.ToString(CultureInfo.InvariantCulture)})";
        string command2 = $"movej(p[{{0}}, {{1}}, {{2}}, {{3}}, {{4}}, {{5}}])";
        
        // Die Punkte werden in eine Liste von Textbefehlen umgewandelt
        List<string> commands = new List<string>();
        bool moved = false;
        foreach (var p in transformedPoints)
        {
            if (float.IsNaN(p.x) && p.y == 1) // Extrution
            {
                if(p.z == 0)
                    commands.Add("set_digital_out(0, False)");
                else if(p.z == 1)
                    commands.Add("set_digital_out(0, True)");
            }
            else // Move
            {
                if (moved)
                {
                    commands.Add(string.Format(CultureInfo.InvariantCulture, commandl, p.x, p.y, p.z, Math.PI, 0, 0));
                }
                else
                {
                    moved = true;
                    commands.Add(string.Format(CultureInfo.InvariantCulture, command2, p.x, p.y, p.z, Math.PI, 0, 0));
                }
            }
        }

        return commands.ToArray();
    }
}

public enum Axis
{
    PositiveX,
    PositiveY,
    PositiveZ,
    NegativeX,
    NegativeY,
    NegativeZ
}
