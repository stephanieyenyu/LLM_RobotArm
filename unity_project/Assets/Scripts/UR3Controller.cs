using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class UR3Controller : MonoBehaviour
{
    [Header("關節設定")]
    public ArticulationBody shoulder_pan;
    public ArticulationBody shoulder_lift;
    public ArticulationBody elbow;
    public ArticulationBody wrist_1;
    public ArticulationBody wrist_2;
    public ArticulationBody wrist_3;
    public ArticulationBody finger;

    [Header("移動速度")]
    public float jointSpeed = 1.0f;

    public void SetJointTarget(ArticulationBody joint, float targetDegree)
    {
        var drive = joint.xDrive;
        drive.target = targetDegree;
        joint.xDrive = drive;
    }

    public IEnumerator MoveToJointAngles(float pan, float lift, float elb, float w1, float w2, float w3)
    {
        SetJointTarget(shoulder_pan, pan);
        SetJointTarget(shoulder_lift, lift);
        SetJointTarget(elbow, elb);
        SetJointTarget(wrist_1, w1);
        SetJointTarget(wrist_2, w2);
        SetJointTarget(wrist_3, w3);

        yield return new WaitForSeconds(2.0f);
        Debug.Log("關節移動完成");
    }

    public void Grasp()
    {
        SetJointTarget(finger, 40f);
        Debug.Log("夾取");
    }

    public void Release()
    {
        SetJointTarget(finger, 0f);
        Debug.Log("放開");
    }
}