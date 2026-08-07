using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Assets.Scripts;
using UnityEngine;

public class RobotArm : MonoBehaviour
{
    public Transform TCP;
    public Transform[] Transforms;
    public Axis[] RotationAxis;
    public int[] RotationOffsets;
    public Transform PrintBed;

    public float[] Angles = new float[] { 0, 0, 0, 0, 0, 0 };
    private Quaternion[] startRotations;

    private URPackageListener urListener;
    private string ipInput = "192.168.56.101";

    // 自動連線 UR3e（跟 JsonExecutor 的 Ur IP 設一樣即可，例如 "192.168.50.204"）
    // 留空的話就等使用者在 OnGUI 面板手動輸入 IP + 按 Connect
    [Header("Auto-connect")]
    public string autoConnectIP = "";

    public Vector3 TCPPosition;
    public Quaternion TCPRotation;
    public bool[] Outputs;

    public TextAsset GCode;

    // Transformationsmatrix, die das Roboterkoordinatensystem in das Unity-Koordinatensystem umwandelt
    public static readonly Matrix4x4 Robot2Unity = new Matrix4x4(
        new Vector4(1, 0, 0, 0),
        new Vector4(0, 0, 1, 0),
        new Vector4(0, 1, 0, 0),
        new Vector4(0, 0, 0, 1));

    void Start()
    {
        urListener = new URPackageListener();
        startRotations = new Quaternion[Transforms.Length];
        for (int i = 0; i < Transforms.Length; i++)
            startRotations[i] = Transforms[i].localRotation;
        Outputs = new bool[18];

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

    void Update()
    {
        // Alle Gelenkwinkel auf die Gelenkobjekte übertragen
        for(int i = 0; i < Transforms.Length; i++)
        {
            if(urListener != null && urListener.Connected)
                Angles[i] = (float)urListener.JointData.AsArray[i].q_actual * 180f / MathF.PI;
            
            Transforms[i].localRotation = startRotations[i];
            Transforms[i].Rotate(axisTovector3(RotationAxis[i]), Angles[i] + RotationOffsets[i], Space.Self);
        }
        
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
            if (TCP != null)
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
        GUILayout.BeginArea(new Rect(10, 10, 200, 400));
        if (!urListener.Connected)
        {
            // Solange kein Roboter verbunden ist, wird eine Eingabefeld für die IP-Adresse angezeigt
            GUILayout.BeginHorizontal();
            ipInput = GUILayout.TextField(ipInput);
            if (GUILayout.Button("Connect"))
            {
                urListener.Connect(ipInput, false);
            }

            GUILayout.EndHorizontal();
        }
        // 連線後不再顯示 Disconnect / Home 按鈕
        // 這些功能移到 UIManager 的三個按鈕（Open / Grip / Home）
        GUILayout.EndArea();
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