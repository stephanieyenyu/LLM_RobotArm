using System.Collections;
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

    [Header("末端執行器")]
    public Transform endEffector;

    private Transform graspTarget;

    void Start()
    {
        foreach (var joint in GetComponentsInChildren<ArticulationBody>())
        {
            var drive = joint.xDrive;
            drive.stiffness = 100000f;
            drive.damping = 10000f;
            drive.forceLimit = 10000f;
            joint.xDrive = drive;
        }
    }

    public void SetJointTarget(ArticulationBody joint, float targetDegree)
    {
        if (joint == null) return;
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

        yield return new WaitForSeconds(2.5f);
        Debug.Log("關節移動完成");
    }

    public void Grasp()
    {
        SetJointTarget(finger, 40f);

        GameObject obj = GameObject.Find("Cup");
        if (obj != null && endEffector != null)
        {
            graspTarget = obj.transform;
            graspTarget.SetParent(endEffector);
            graspTarget.localPosition = Vector3.zero;
            Debug.Log("夾取成功");
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
}