using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

[Serializable]
public sealed class DemoBatchEnvelope
{
    public int batch_id;
    public bool demo_fixed12;
    public List<string> bitmap;
    public List<DemoStepEnvelope> steps;
}
[Serializable]
public sealed class DemoStepEnvelope
{
    public int step_id, source_index;
    public NamedPosition source_position, target_position;
    public string[] commands;
}

public partial class JsonExecutor
{
    [Header("Demo: contact pick (main 4b422a8)")]
    public bool contactStopOnPick = true;
    public float pickContactApproachM = 0.025f;
    public float pickContactSpeedMps = 0.010f;
    public float pickContactForceN = 15f;
    public float pickRetreatM = 0.030f;
    public float pickCorrectionStepM = 0.002f;
    public int pickMaxCorrections = 7;
    public float pickMinContactOffsetM = 0.005f;
    bool demoExecutionUnknown;
    bool HardwareMotionBlocked => File.Exists(Path.Combine(Application.streamingAssetsPath,"demo_motion_blocked.txt"));
    static readonly string[] DemoCommands = {
        "open", "source_travel", "source_hover", "pick_contact", "grasp", "source_lift",
        "target_travel", "target_hover", "target_descend", "release", "target_lift", "go_home"
    };
    static string F(double v) => v.ToString("0.000000",CultureInfo.InvariantCulture);
    static LegacyDemoMotion.Point DemoPoint(NamedPosition p) => new LegacyDemoMotion.Point(
        QR1_X+p.x,QR1_Y+p.y,QR1_Z+p.z+Z_CORRECTION,p.orientation);

    IEnumerator ExecuteDemoBatch(DemoBatchEnvelope batch,long epoch)
    {
        float start=Time.realtimeSinceStartup;
        demoExecutionUnknown=false;
        Debug.Log("[Demo] Motion engine="+LegacyDemoMotion.EngineVersion+
            "; fixed travel Z="+F(QR1_Z+TRAVEL_Z_ABOVE_WORKSPACE)+" m; hover="+F(SAFE_Z_OFFSET)+" m");
        string failure=null;
        if(HardwareMotionBlocked)
        {
            FinishDemoBatch(batch.batch_id,false,"Hardware motion lock active; no motion sent",0f,false);
            yield break;
        }
        float waited=0;
        while((!urListener.Connected || !urListener.HasFreshState) && waited<3)
        {yield return new WaitForSeconds(0.1f);waited+=0.1f;}
        if(!urListener.Connected || !urListener.HasFreshState) failure="No fresh UR feedback";
        else if(IsEmergencyStop() || IsRecoverableSafetyStop()) failure="Robot is in a safety stop; batch not started";
        else if(urListener.RobotModeData.isProgramRunning) failure="Another UR program is running; batch not started";

        var plans=new List<LegacyDemoMotion.Command[]>();
        if(failure==null)
        {
            try
            {
                ValidateDemo(batch);
                var settings=new LegacyDemoMotion.ContactSettings {
                    Approach=pickContactApproachM,Speed=pickContactSpeedMps,Force=pickContactForceN,
                    Retreat=pickRetreatM,Correction=pickCorrectionStepM,MaxCorrections=pickMaxCorrections,
                    MinimumOffset=pickMinContactOffsetM
                };
                foreach(var step in batch.steps)
                    plans.Add(LegacyDemoMotion.Build(DemoPoint(step.source_position),DemoPoint(step.target_position),
                        QR1_Z+TRAVEL_Z_ABOVE_WORKSPACE,SAFE_Z_OFFSET,settings));
                // Geometry/contract validation only. IK is evaluated by the UR
                // controller against actual joints when EACH command is sent.
                // No claim of full link/tool collision certification is made here.
            }
            catch(Exception ex) {failure="Demo input validation: "+ex.Message;}
        }
        if(failure==null)
        {
            yield return SetPerceptionMode("executing");
            for(int stepIndex=0;stepIndex<batch.steps.Count && failure==null;stepIndex++)
            {
                var step=batch.steps[stepIndex];
                var commands=plans[stepIndex];
                Debug.Log("[Demo] source index "+step.source_index+" -> target ("+
                    F(step.target_position.x)+", "+F(step.target_position.y)+", "+F(step.target_position.z)+")");
                for(int i=0;i<commands.Length;i++)
                {
                    if(!IsExecutionCurrent(epoch,batch.batch_id))yield break;
                    try {CheckpointDemoBatch(step.step_id,i+1,commands[i].Name);}
                    catch(Exception ex)
                    {demoExecutionUnknown=true;failure="Cannot persist progress; batch stopped: "+ex.Message;break;}
                    if(i==5)
                    {
                        // Contact correction is the only source of XY changes.
                        // Preserve the ACTUAL XY and orientation during the lift.
                        var tcp=urListener.CartesianInfo;
                        commands[i]=LegacyDemoMotion.SourceLift(
                            new LegacyDemoMotion.Point((double)tcp.X,(double)tcp.Y,(double)tcp.Z),
                            DemoPoint(step.source_position).Z+SAFE_Z_OFFSET);
                    }
                    var command=commands[i];
                    string tag="demo "+step.step_id+" "+(i+1)+"/12 "+command.Name;
                    Debug.Log("["+tag+"] SEND\n"+command.Program);
                    try
                    {
                        var tcp=urListener.CartesianInfo;
                        string joints=string.Join(", ",urListener.JointData.AsArray.Select(j=>F((double)j.q_actual)));
                        File.AppendAllText(Path.Combine(Application.streamingAssetsPath,"demo_urscript.log"),
                            "\n# "+DateTime.UtcNow.ToString("O")+" "+tag+
                            "\n# actual_joints_rad=["+joints+"]"+
                            "\n# actual_tcp_base_m=["+F((double)tcp.X)+", "+F((double)tcp.Y)+", "+F((double)tcp.Z)+"]\n"+
                            "# digital_outputs_before=0x"+((int)urListener.MasterboardData.digitalOutputBits).ToString("X8")+"\n"+
                            command.Program+"\n");
                    }
                    catch(IOException ex) {Debug.LogWarning(ex.Message);}
                    yield return SendLegacyDemoProgram(command,tag,epoch,batch.batch_id);
                    if(!lastMotionSucceeded) {failure=lastMotionError ?? "Demo command failed";break;}
                }
            }
        }
        bool success=failure==null;
        if(!success && urListener.Connected && IsRecoverableSafetyStop())
        {
            File.WriteAllText(Path.Combine(Application.streamingAssetsPath,"demo_motion_blocked.txt"),
                "Safety stop: batch aborted; no automatic Home/resume. Inspect the robot before a new test.");
            demoExecutionUnknown=true;
            failure+="; safety stop latched; automatic Home/resume disabled";
        }
        if(!IsExecutionCurrent(epoch,batch.batch_id))yield break;
        // Completion is durable as soon as the final Home is confirmed. Do not
        // put a perception HTTP request ahead of the terminal report.
        try {FinishDemoBatch(batch.batch_id,success,failure,Time.realtimeSinceStartup-start,demoExecutionUnknown);}
        catch(Exception ex)
        {
            WriteStepDone(batch.batch_id,false,"Completion reporting failed: "+ex.Message,
                Time.realtimeSinceStartup-start,true,"reporting_error");
            Debug.LogError("[Demo] Completion reporting failed: "+ex.Message);
            yield break;
        }
        Debug.Log("[Demo] Batch "+batch.batch_id+" success="+success+" elapsed="+(Time.realtimeSinceStartup-start).ToString("F1")+"s "+failure);
        if(success)Debug.Log("[Demo] 最後 Home 已確認，整批完成回報已寫入；正在恢復場景更新。");
        yield return SetPerceptionMode("idle");
    }

    void ValidateDemo(DemoBatchEnvelope b)
    {
        if(b.steps==null || b.steps.Count<1 || b.steps.Count>5) throw new InvalidOperationException("Demo needs 1–5 steps.");
        if(!contactStopOnPick || pickContactApproachM<0.01f || pickContactApproachM>0.04f ||
           pickContactSpeedMps<=0 || pickContactSpeedMps>0.02f || pickContactForceN<1 || pickContactForceN>20 ||
           pickRetreatM<0.01f || pickRetreatM>0.05f || pickCorrectionStepM<=0 || pickCorrectionStepM>0.003f ||
           pickMaxCorrections<0 || pickMaxCorrections>7 || pickMinContactOffsetM<0.001f)
            throw new InvalidOperationException("Invalid demo contact-pick settings.");
        var indexes=new HashSet<int>();
        foreach(var s in b.steps)
        {
            if(s.source_position==null || s.target_position==null || s.commands==null ||
                !s.commands.SequenceEqual(DemoCommands) || s.source_index<0 || !indexes.Add(s.source_index))
                throw new InvalidOperationException("Invalid fixed-12 plan or duplicate source.");
            foreach(var p in new[] {s.source_position,s.target_position})
            {
                double r=Math.Sqrt(Math.Pow(QR1_X+p.x,2)+Math.Pow(QR1_Y+p.y,2));
                double min=ReferenceEquals(p,s.source_position)?0.20:0.16;
                if(double.IsNaN(r) || double.IsInfinity(r) || r<min || r>0.47 ||
                   float.IsNaN(p.z) || float.IsInfinity(p.z) || p.z<0.005f || p.z>0.15f ||
                   p.x< -0.015f || p.x>0.805f || p.y< -0.015f || p.y>0.465f)
                    throw new InvalidOperationException("Source/target outside demo limits.");
            }
        }
    }

    IEnumerator SendLegacyDemoProgram(LegacyDemoMotion.Command command,string tag,long epoch,int batchId)
    {
        lastMotionSucceeded=false;lastMotionError=null;
        if(!IsExecutionCurrent(epoch,batchId))yield break;
        if(HardwareMotionBlocked) {demoExecutionUnknown=true;lastMotionError="Hardware motion lock active";yield break;}
        if(!urListener.Connected || !urListener.HasFreshState || IsEmergencyStop() || IsRecoverableSafetyStop())
        {lastMotionError=tag+": robot not ready";yield break;}
        if(urListener.RobotModeData.isProgramRunning)
        {demoExecutionUnknown=true;lastMotionError=tag+": controller already running another program";yield break;}
        if(DemoTcpInsideBase())
        {lastMotionError=tag+": actual TCP is inside 0.16 m base exclusion; motion not sent";yield break;}
        if(command.DigitalOutputChannel>=0)
        {
            yield return SendLegacyGripperOutput(command,tag,epoch,batchId);
            yield break;
        }
        demoExecutionUnknown=true;
        try {urListener.SendCommand(command.Program);}
        catch(Exception ex) {lastMotionError=tag+": send failed: "+ex.Message;}
        if(lastMotionError!=null)yield break;
        float start=Time.realtimeSinceStartup;
        bool seenRunning=false;
        float limit=command.ContactPick
            ? (pickContactApproachM/pickContactSpeedMps+0.5f+3*pickRetreatM/0.05f+2)*(pickMaxCorrections+1)+15
            : MOTION_TIMEOUT_SEC;
        while(Time.realtimeSinceStartup-start<limit)
        {
            if(!IsExecutionCurrent(epoch,batchId))yield break;
            if(!urListener.Connected || !urListener.HasFreshState)
            {lastMotionError=tag+": disconnect/stale feedback";yield break;}
            if(IsEmergencyStop() || IsRecoverableSafetyStop())
            {lastMotionError=tag+": safety stop";yield break;}
            if(DemoTcpInsideBase())
            {
                try {urListener.SendCommand("stopj(1.0)");}
                catch(Exception ex) {Debug.LogWarning(ex.Message);}
                lastMotionError=tag+": actual TCP entered 0.16 m base exclusion; stop requested";
                yield break;
            }
            bool running=urListener.RobotModeData.isProgramRunning;
            if(running)seenRunning=true;
            else if(seenRunning)
            {
                demoExecutionUnknown=false;
                var tcp=urListener.CartesianInfo;
                if(command.Position!=null)
                {
                    double dx=(double)tcp.X-command.Position[0],dy=(double)tcp.Y-command.Position[1],dz=(double)tcp.Z-command.Position[2];
                    double distance=Math.Sqrt(dx*dx+dy*dy+dz*dz);
                    bool reached=command.ContactPick
                        ? Math.Abs(dz)<=0.004 && Math.Sqrt(dx*dx+dy*dy)<=pickMaxCorrections*pickCorrectionStepM+TCP_POSITION_TOLERANCE_M
                        : distance<=TCP_POSITION_TOLERANCE_M;
                    if(!reached)
                    {lastMotionError=tag+(command.ContactPick?": contact correction did not reach pick height; grasp cancelled":": TCP did not reach target");yield break;}
                }
                if(command.Joints!=null)
                {
                    // Preserve the old Home tolerance's modulo-2pi comparison.
                    double maximum=urListener.JointData.AsArray.Select((j,i)=>
                        Math.Abs(Math.Atan2(Math.Sin((double)j.q_actual-command.Joints[i]),
                            Math.Cos((double)j.q_actual-command.Joints[i])))).Max();
                    if(maximum>HOME_JOINT_TOLERANCE_RAD)
                    {lastMotionError=tag+": Home not reached";yield break;}
                }
                lastMotionSucceeded=true;yield break;
            }
            else if(Time.realtimeSinceStartup-start>3)
            {lastMotionError=tag+": controller did not start URScript";yield break;}
            yield return new WaitForSeconds(0.02f);
        }
        try {urListener.SendCommand("stopj(1.0)");}
        catch(Exception ex) {Debug.LogWarning(ex.Message);}
        lastMotionError=tag+": timeout; stop requested; execution state must be inspected";
    }
    bool DemoTcpInsideBase()
    {
        var tcp=urListener.CartesianInfo;
        double radius=Math.Sqrt((double)tcp.X*(double)tcp.X+(double)tcp.Y*(double)tcp.Y);
        return double.IsNaN(radius) || double.IsInfinity(radius) || radius<BASE_EXCLUSION_RADIUS_M;
    }

    IEnumerator SendLegacyGripperOutput(LegacyDemoMotion.Command command,string tag,long epoch,int batchId)
    {
        // Same raw single-line command as the original GripGripper/SendGrasp.
        // I/O completion is output-bit feedback, not a primary-program flag.
        long before=urListener.MasterboardRevision;
        float started=Time.realtimeSinceStartup,confirmedAt=-1f;
        demoExecutionUnknown=true;
        try {urListener.SendCommand(command.Program);}
        catch(Exception ex) {lastMotionError=tag+": gripper send failed: "+ex.Message;}
        if(lastMotionError!=null)yield break;
        Debug.Log("[Demo I/O] "+tag+" SENT DO"+command.DigitalOutputChannel+"="+command.DigitalOutputHigh);
        while(Time.realtimeSinceStartup-started<3f+command.SettleSeconds)
        {
            if(!IsExecutionCurrent(epoch,batchId))yield break;
            if(!urListener.Connected || !urListener.HasFreshState)
            {lastMotionError=tag+": disconnected/stale I/O feedback";yield break;}
            if(IsEmergencyStop() || IsRecoverableSafetyStop())
            {lastMotionError=tag+": safety stop while waiting for gripper output";yield break;}
            int bits=urListener.MasterboardData.digitalOutputBits;
            if(urListener.MasterboardRevision>before && LegacyDemoMotion.OutputMatches(command,bits))
            {
                if(confirmedAt<0)
                {
                    confirmedAt=Time.realtimeSinceStartup;
                    Debug.Log("[Demo I/O] "+tag+" controller confirmed DO"+command.DigitalOutputChannel+"="+
                        command.DigitalOutputHigh+"; outputs=0x"+bits.ToString("X8"));
                    try {File.AppendAllText(Path.Combine(Application.streamingAssetsPath,"demo_urscript.log"),
                        "# I/O CONFIRMED "+tag+" DO"+command.DigitalOutputChannel+"="+command.DigitalOutputHigh+
                        " outputs=0x"+bits.ToString("X8")+"\n");}
                    catch(IOException ex) {Debug.LogWarning(ex.Message);}
                }
                if(Time.realtimeSinceStartup-confirmedAt>=command.SettleSeconds)
                {demoExecutionUnknown=false;lastMotionSucceeded=true;yield break;}
            }
            else confirmedAt=-1f;
            yield return new WaitForSeconds(0.02f);
        }
        int finalBits=urListener.MasterboardData.digitalOutputBits;
        demoExecutionUnknown=urListener.MasterboardRevision<=before;
        lastMotionError=tag+": DO"+command.DigitalOutputChannel+" did not hold requested value "+
            command.DigitalOutputHigh+"; actual outputs=0x"+finalBits.ToString("X8")+"; batch stopped before lift";
    }
}
