using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class UR3Controller : MonoBehaviour
{
    [Header("夾取設定")]
    public string targetObjectName = "Cup";
    private Transform graspTarget;

    public void Grasp()
    {
        SetJointTarget(finger, 40f);

        GameObject obj = GameObject.Find(targetObjectName);
        if (obj != null)
        {
            graspTarget = obj.transform;
            graspTarget.SetParent(transform);
            Debug.Log("夾取：" + targetObjectName);
        }
    }

    public void Release()
    {
        SetJointTarget(finger, 0f);
        if (graspTarget != null)
        {
            graspTarget.SetParent(null);
            graspTarget = null;
        }
        Debug.Log("放開");
    }

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

    void Start()
    {
        SetAllJointsDrive(shoulder_pan);
        SetAllJointsDrive(shoulder_lift);
        SetAllJointsDrive(elbow);
        SetAllJointsDrive(wrist_1);
        SetAllJointsDrive(wrist_2);
        SetAllJointsDrive(wrist_3);
        SetAllJointsDrive(finger);
        foreach (var joint in GetComponentsInChildren<ArticulationBody>())
        {
            SetAllJointsDrive(joint);
        }
    }

    void SetAllJointsDrive(ArticulationBody joint)
    {
        if (joint == null) return;
        var drive = joint.xDrive;
        drive.stiffness = 100000f;
        drive.damping = 10000f;
        drive.forceLimit = 10000f;
        joint.xDrive = drive;
    }

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
}