using System.Text.Json;

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name)
{
    try { action(); } catch (InvalidOperationException) { Check(true, name); return; }
    throw new Exception("Expected rejection: " + name);
}
if (args.Contains("--incident-only"))
{
    double[] a={1.172200,-1.480958,1.477767,-1.567605,-1.570796,1.172200};
    double[] b={-1.691140,-2.986597,1.148887,0.266914,-1.570796,-1.691140};
    Check(!DemoMotionGeometry.Transition(a,b,out string why),"reported source_travel switch rejected");
    Check(why.Contains("90 degrees"),"rejection identifies large joint/IK branch switch");
    double[] degrees={-45.14,-143.91,71.74,-18.66,-89.88,314.83};
    var measured=degrees.Select(d=>d*Math.PI/180).ToArray();
    var aligned=measured.Select((r,i)=>r+Math.Round(((a[i]+b[i])/2-r)/(2*Math.PI))*2*Math.PI).ToArray();
    double denominator=a.Zip(b,(x,y)=>(y-x)*(y-x)).Sum();
    double progress=Enumerable.Range(0,6).Sum(i=>(aligned[i]-a[i])*(b[i]-a[i]))/denominator;
    double maxResidual=Enumerable.Range(0,6).Max(i=>Math.Abs(aligned[i]-(a[i]+progress*(b[i]-a[i]))))*180/Math.PI;
    Check(progress>0.67 && progress<0.70,"pendant pose matches the second movej at approximately 68 percent");
    Check(maxResidual<1.5,"all six pendant angles match the same segment within 1.5 degrees");
    Console.WriteLine($"Recorded pose: path progress {progress:P2}; maximum joint residual {maxResidual:F2} degrees.");
    var checker=typeof(DemoMotionGeometry).GetMethod("ValidateApproximateRobotCollision",
        System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
    var fk=UR3eKinematics.toolOffsetZ;
    UR3eKinematics.toolOffsetZ=0.170;
    object[] diagnosticArgs={measured,""};
    bool capsuleAllows=(bool)checker.Invoke(null,diagnosticArgs)!;
    Console.WriteLine($"Original capsule model with assumed 170 mm TCP allows supplied pose: {capsuleAllows}; reason: {diagnosticArgs[1]}");
    Check(capsuleAllows,"reproduces original capsule false-negative on supplied pose");
    Check(!DemoMotionGeometry.Transition(measured,measured,out string poseRejection),"new per-sample check rejects supplied pose itself");
    Check(poseRejection.Contains("base exclusion"),"pose rejection comes from base clearance, independent of large-angle guard");
    Console.WriteLine("Nominal base-frame FK with assumed 170 mm axial TCP: "+UR3eKinematics.FKPose(measured));
    UR3eKinematics.toolOffsetZ=fk;
    Console.WriteLine("Incident guard passed. This does not validate the physical arm/tool geometry or a replacement route.");
    return;
}
var workspace = new WorkspaceBounds();
var scene = Enumerable.Range(0, 6).Select(i => new SceneObject {
    Name = "yellow_cube", Shape = "cube", X = 0.12 + (i % 3) * 0.05,
    Y = 0.06 + (i / 3) * 0.06, Z = 0.025
}).ToList();
var route = new RoutedCommand { Action = "arrange_pattern" };
var batch = DemoPlanner.Build(route, "排黃色 L", scene, workspace, 100);
Check(batch.Bitmap.SequenceEqual(new[] { "100", "100", "111" }), "hard-coded 3x3 L");
Check(batch.Steps.Count == 5 && batch.Steps.Select(s => s.SourceIndex).Distinct().Count() == 5, "five distinct sources");
Check(batch.Steps.All(s => s.Commands.SequenceEqual(DemoPlanner.Commands) && s.Commands.Length == 12), "fixed 12 commands on every step");
Check(batch.Steps.Select(s => (s.Target.X,s.Target.Y)).Distinct().Count() == 5, "five distinct targets");
Check(scene.All(s => s.Z == 0.025), "snapshot not mutated");
Reject(() => DemoPlanner.Build(route, "L", scene.Take(4).ToList(), workspace, 100), "insufficient supply rejected before execution");
Reject(() => DemoPlanner.Build(route,"L",Enumerable.Range(0,5).Select(_ => scene[0]).ToList(),workspace,100), "duplicate detections cannot reuse a cube");
var near = new SceneObject { X=0.38824, Y=0.35473, Z=0.025 };
Check(!DemoPlanner.IsPickable(near), "0.20 m inner pick exclusion");
Check(!DemoPlanner.IsPickable(new SceneObject { X=1, Y=0, Z=0.025 }), "0.47 m outer pick exclusion");
Check(!DemoPlanner.IsPickable(new SceneObject { X=double.NaN, Y=0, Z=0.025 }), "NaN source rejected");
var blocked = scene.Concat(new[] { new SceneObject { Name="black_cube", X=workspace.TargetOriginX,
    Y=workspace.TargetOriginY+2*workspace.CellSize, Z=0.025 } }).ToList();
Reject(() => DemoPlanner.Build(route, "L", blocked, workspace, 100), "occupied target rejected");
Reject(() => DemoPlanner.Build(new RoutedCommand { Action="arrange_3d_pattern" }, "3D L", scene, workspace, 100), "3D demo rejected without LLM planning");
using var encoded = JsonDocument.Parse(JsonSerializer.Serialize(batch));
Check(encoded.RootElement.GetProperty("demo_fixed12").GetBoolean() && encoded.RootElement.GetProperty("steps").GetArrayLength()==5, "batch wire contract");

// Production tests assert the pre-demo controller contract, not the retired
// numerical planner's ability to produce joints.
string golden="movej(get_inverse_kin(pose_trans(p[-0.1546, -0.1924, 0.2500, 0, 3.14, 0], p[0, 0, 0, 0, 0, 1.5708]), qnear=get_actual_joint_positions()), a=1.2, v=0.8)";
Check(LegacyDemoMotion.Movej(-0.1546,-0.1924,0.25,null!)==golden,"controller movej exactly matches legacy URScript");
Check(LegacyDemoMotion.Movel(0.1,0.2,0.15,"horizontal")=="movel(p[0.1000, 0.2000, 0.1500, 0, 3.14, 0], a=0.3, v=0.10)",
    "Cartesian movel exactly matches legacy URScript");
Check(LegacyDemoMotion.HomeLine=="movej([-1.5708, -1.5708, 0, -1.5708, 0, 0], a=1.2, v=0.8)","original Home command preserved");
var ioCommands=LegacyDemoMotion.Build(new LegacyDemoMotion.Point(-0.15,-0.19,0.075),
    new LegacyDemoMotion.Point(0.10,-0.24,0.075),0.25,0.08,new LegacyDemoMotion.ContactSettings());
Check(ioCommands[4].Program=="set_standard_digital_out(4, True)","grasp sends the exact original single-line DO4 command");
Check(ioCommands[0].Program=="set_standard_digital_out(4, False)" && ioCommands[9].Program==ioCommands[0].Program,
    "open and release send original single-line DO4-off command");
Check(ioCommands[4].SettleSeconds==1.5,"original gripper settling time retained on host");
Check(!LegacyDemoMotion.OutputMatches(ioCommands[4],0),"grasp cannot complete when DO4 is off");
Check(!LegacyDemoMotion.OutputMatches(ioCommands[4],1<<3),"DO3 does not count as DO4 acknowledgement");
Check(LegacyDemoMotion.OutputMatches(ioCommands[4],1<<4),"DO4-on acknowledges grasp");
Check(!LegacyDemoMotion.OutputMatches(ioCommands[9],1<<4) && LegacyDemoMotion.OutputMatches(ioCommands[9],0),
    "release requires DO4-off feedback");
foreach(var step in batch.Steps)
{
    var s=new LegacyDemoMotion.Point(step.Source.X-0.38824,step.Source.Y-0.35473,step.Source.Z+0.05,step.Source.Orientation!);
    var t=new LegacyDemoMotion.Point(step.Target.X-0.38824,step.Target.Y-0.35473,step.Target.Z+0.05,step.Target.Orientation!);
    var commands=LegacyDemoMotion.Build(s,t,0.25,0.08,new LegacyDemoMotion.ContactSettings());
    Check(commands.Length==12 && commands.Select(c=>c.Name).SequenceEqual(DemoPlanner.Commands),
        "fixed 12 controller commands for source "+step.SourceIndex);
    Check(commands[1].Position![2]==0.25 && commands[6].Position![2]==0.25,
        "fixed legacy travel height, no 0.6939 m Home-height propagation");
    Check(commands[1].Program.Contains("qnear=get_actual_joint_positions()") &&
        commands[6].Program.Contains("qnear=get_actual_joint_positions()"),"UR controller owns live IK");
    Check(Math.Abs(commands[2].Position![2]-(s.Z+0.08))<1e-9,"legacy source hover remains 8 cm above pick");
    Check(commands[3].Program.Contains("tool_x = pose_trans(pose") &&
        commands[3].Program.Contains("side = (0.0 - ty / fz) * ax + (tx / fz) * ay"),
        "contact correction uses actual controller tool direction and torque sign");
}
var corrected=LegacyDemoMotion.SourceLift(new LegacyDemoMotion.Point(-0.150,-0.190,0.075),0.155);
Check(corrected.Program.Contains("pose = get_actual_tcp_pose()") && corrected.Program.Contains("pose[2] = 0.155000"),
    "post-correction lift preserves actual XY/orientation");
Check(corrected.Position!.SequenceEqual(new[]{-0.150,-0.190,0.155}),"post-correction lift feedback target is actual XY");
Console.WriteLine($"TOTAL {passed} production checks passed. No LLM/camera/robot calls were made.");

// Exercise durable ownership and terminal results with actual files. No Unity,
// robot or network process is started by these lifecycle regression checks.
var receiptDirectory=Path.Combine(Path.GetTempPath(),"demo-receipt-checks-"+Guid.NewGuid().ToString("N"));
var receiptJson=new JsonSerializerOptions {IncludeFields=true};
DemoExecutionJournal OpenJournal() => new(receiptDirectory,
    r=>JsonSerializer.Serialize(r,receiptJson),s=>JsonSerializer.Deserialize<DemoExecutionReceipt>(s,receiptJson)!);
var journal=OpenJournal();
Check(journal.TryBegin(200,"session-a",123,out var first),"new batch claims durable ownership before execution");
Check(!OpenJournal().TryBegin(200,"session-b",124,out var duplicate) && duplicate.status=="executing",
    "reload/second session cannot replay a running batch");
journal.Checkpoint(first,205,12,"go_home",120.5);
Check(OpenJournal().Read(200).last_action=="go_home" && OpenJournal().Read(200).action_index==12,
    "last Home progress survives a reload");
journal.Finish(first,true,false,null!,125);
Check(!OpenJournal().TryBegin(200,"session-c",125,out var finished) && finished.completed && finished.status=="completed",
    "completed batch returns saved result instead of replaying");
journal.Interrupt(finished,"late OnDisable");
Check(OpenJournal().Read(200).completed,"late disable cannot turn confirmed completion into interruption");
Check(journal.TryBegin(300,"session-a",123,out var interrupted),"another batch can be claimed");
journal.Checkpoint(interrupted,305,12,"go_home",90);
journal.Interrupt(interrupted,"Unity script reload");
Check(!OpenJournal().TryBegin(300,"new-session",124,out var restored) && restored.status=="interrupted" &&
    !restored.completed && restored.execution_unknown,"interrupted batch is durable and never replayed");
Check(restored.error.Contains("go_home"),"interruption identifies the command that lost confirmation");
Check(journal.TryBegin(400,"session-a",123,out var failed),"new task ID remains independently claimable");
journal.Finish(failed,false,false,"DO4 did not confirm",2.5);
Check(!OpenJournal().TryBegin(400,"session-b",124,out var failureReceipt) && failureReceipt.status=="failed" &&
    !failureReceipt.completed,"failed batch cannot be replayed or reported as success");
Check(OpenJournal().Read(200).status=="completed", "terminal receipt can recover completion when transient report file is absent");
Console.WriteLine($"TOTAL {passed} checks passed, including execution lifecycle persistence.");
