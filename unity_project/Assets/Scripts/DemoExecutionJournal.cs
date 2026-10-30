using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
#nullable disable

// Durable receipts, outside Assets: survive script reloads without triggering
// Unity asset imports. This file contains no robot commands or Unity API calls.
[Serializable]
public sealed class DemoExecutionReceipt
{
    public int batch_id, step_id, action_index, process_id;
    public string session_id, status, last_action, error, updated_utc;
    public bool completed, execution_unknown;
    public double duration_sec;
}

public sealed class DemoExecutionJournal
{
    readonly string directory;
    readonly Func<DemoExecutionReceipt,string> serialize;
    readonly Func<string,DemoExecutionReceipt> deserialize;
    public static string DirectoryForAssets(string assets) => Path.GetFullPath(
        Path.Combine(assets,"../../../csharp_server/outputs/execution_receipts"));
    public DemoExecutionJournal(string directory,Func<DemoExecutionReceipt,string> serialize,
        Func<string,DemoExecutionReceipt> deserialize)
    { this.directory=directory;this.serialize=serialize;this.deserialize=deserialize;Directory.CreateDirectory(directory); }
    string ReceiptPath(int id)
    { if(id<=0)throw new ArgumentOutOfRangeException(nameof(id));return Path.Combine(directory,"batch_"+id+".json"); }
    public DemoExecutionReceipt Read(int id)
    {
        string path=ReceiptPath(id);
        if(!File.Exists(path))return null;
        var receipt=deserialize(File.ReadAllText(path));
        if(receipt==null || receipt.batch_id!=id || string.IsNullOrEmpty(receipt.status))
            throw new InvalidDataException("Invalid execution receipt: "+path);
        return receipt;
    }
    public bool TryBegin(int id,string session,int processId,out DemoExecutionReceipt receipt)
    {
        receipt=Read(id);
        if(receipt!=null)return false; // Includes failed/interrupted: never replay.
        receipt=new DemoExecutionReceipt {batch_id=id,session_id=session,process_id=processId,
            status="executing",last_action="accepted",updated_utc=DateTime.UtcNow.ToString("O")};
        try
        {
            using(var stream=new FileStream(ReceiptPath(id),FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                byte[] bytes=Encoding.UTF8.GetBytes(serialize(receipt));
                stream.Write(bytes,0,bytes.Length);stream.Flush();
            }
            return true;
        }
        catch(IOException) when(File.Exists(ReceiptPath(id)))
        {receipt=Read(id);return false;}
    }
    public void Checkpoint(DemoExecutionReceipt r,int stepId,int actionIndex,string action,double elapsed)
    {
        if(r.status!="executing")throw new InvalidOperationException("Cannot advance a terminal receipt");
        r.step_id=stepId;r.action_index=actionIndex;r.last_action=action;r.duration_sec=elapsed;Save(r);
    }
    public void Finish(DemoExecutionReceipt r,bool completed,bool unknown,string error,double elapsed)
    {
        r.completed=completed;r.execution_unknown=unknown;r.error=error;r.duration_sec=elapsed;
        r.status=completed?"completed":unknown?"execution_unknown":"failed";Save(r);
    }
    public void Interrupt(DemoExecutionReceipt r,string reason)
    {
        if(r.status!="executing")return; // Never overwrite confirmed completion.
        r.status="interrupted";r.completed=false;r.execution_unknown=true;
        r.error=reason+"; last action="+r.step_id+"/"+r.action_index+" "+r.last_action;Save(r);
    }
    public void Save(DemoExecutionReceipt r)
    {r.updated_utc=DateTime.UtcNow.ToString("O");AtomicWrite(ReceiptPath(r.batch_id),serialize(r));}
    public IEnumerable<DemoExecutionReceipt> ReadAll()
    {
        foreach(var path in Directory.GetFiles(directory,"batch_*.json"))
        {
            var r=deserialize(File.ReadAllText(path));
            if(r==null || r.batch_id<=0 || string.IsNullOrEmpty(r.status))
                throw new InvalidDataException("Invalid execution receipt: "+path);
            yield return r;
        }
    }
    public static void AtomicWrite(string path,string text)
    {
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        File.WriteAllText(temporary,text,new UTF8Encoding(false));
        try
        {
            for(int attempt=0;;attempt++)
            {
                try
                {
                    if(File.Exists(path))File.Replace(temporary,path,null);
                    else File.Move(temporary,path);
                    break;
                }
                catch(IOException) when(attempt<4) {Thread.Sleep(10);}
            }
        }
        finally {if(File.Exists(temporary))File.Delete(temporary);}
    }
}
