using System;
using System.IO;
using UnityEngine;

public partial class JsonExecutor
{
    DemoExecutionJournal demoJournal;
    DemoExecutionReceipt activeDemoReceipt;
    string demoSessionId;
    float demoReceiptStarted;

    void InitializeDemoReporting()
    {
        demoSessionId=Guid.NewGuid().ToString("N");
        demoJournal=new DemoExecutionJournal(DemoExecutionJournal.DirectoryForAssets(Application.streamingAssetsPath),
            r=>JsonUtility.ToJson(r,true),s=>JsonUtility.FromJson<DemoExecutionReceipt>(s));
        int pid=System.Diagnostics.Process.GetCurrentProcess().Id;
        Debug.Log("[Demo 報告] 已啟用持久化批次紀錄；初始化不算完成，重載不重播舊動作。");
        foreach(var r in demoJournal.ReadAll())
        {
            if(r.status!="executing")continue;
            // Do not rewrite another live editor's ownership record.
            if(r.process_id!=pid && ProcessIsAlive(r.process_id))continue;
            demoJournal.Interrupt(r,"Unity restarted/reloaded before a terminal report; automatic replay disabled");
            ReportDemoReceipt(r);
            Debug.LogError("[Demo] Batch "+r.batch_id+" interrupted by reload; old commands will not be replayed.");
        }
    }
    static bool ProcessIsAlive(int id)
    {
        if(id<=0)return false;
        try {using(var p=System.Diagnostics.Process.GetProcessById(id))return !p.HasExited;}
        catch(ArgumentException) {return false;}
        catch(Exception) {return true;} // Unknown ownership must never be stolen.
    }
    bool ClaimDemoBatch(int id)
    {
        try
        {
            if(demoJournal==null)throw new InvalidOperationException("Execution journal unavailable");
            if(!demoJournal.TryBegin(id,demoSessionId,System.Diagnostics.Process.GetCurrentProcess().Id,out var receipt))
            {
                if(receipt.status=="executing")
                    WriteStepDone(id,false,"Batch is already owned by another execution session; not replayed",0f,true,"execution_unknown");
                else ReportDemoReceipt(receipt);
                Debug.LogWarning("[Demo] Batch "+id+" already recorded as "+receipt.status+"; no commands resent.");
                return false;
            }
            activeDemoReceipt=receipt;demoReceiptStarted=Time.realtimeSinceStartup;
            return true;
        }
        catch(Exception ex)
        {
            WriteStepDone(id,false,"Cannot persist batch ownership; no motion sent: "+ex.Message,0f,true,"execution_unknown");
            Debug.LogError("[Demo] Execution receipt error: "+ex.Message);
            return false;
        }
    }
    void CheckpointDemoBatch(int step,int action,string name)
    {
        demoJournal.Checkpoint(activeDemoReceipt,step,action,name,Time.realtimeSinceStartup-demoReceiptStarted);
    }
    void FinishDemoBatch(int id,bool completed,string error,double duration,bool unknown)
    {
        demoJournal.Finish(activeDemoReceipt,completed,unknown,error,duration);
        ReportDemoReceipt(activeDemoReceipt);
    }
    void ReportDemoReceipt(DemoExecutionReceipt r)
    {
        WriteStepDone(r.batch_id,r.completed,r.error,(float)r.duration_sec,r.execution_unknown,r.status);
    }
    void OnDisable()
    {
        if(activeInstance!=this)return;
        // Unity invokes OnDisable before a script reload/Play Mode exit. Write
        // synchronously: a coroutine cannot finish after its domain is gone.
        executionEpoch++;
        StopAllCoroutines();
        try
        {
            if(activeDemoReceipt!=null && activeDemoReceipt.status=="executing")
            {
                activeDemoReceipt.duration_sec=Time.realtimeSinceStartup-demoReceiptStarted;
                demoJournal.Interrupt(activeDemoReceipt,"Unity stopped/reloaded; execution state not confirmed");
                ReportDemoReceipt(activeDemoReceipt);
                Debug.LogWarning("[Demo] Batch "+activeDemoReceipt.batch_id+" interrupted; report saved, no automatic Home/replay.");
            }
        }
        catch(Exception ex) {Debug.LogError("[Demo] Could not save interruption report: "+ex.Message);}
        currentStepCoroutine=null;currentStepId=-1;
    }
}
