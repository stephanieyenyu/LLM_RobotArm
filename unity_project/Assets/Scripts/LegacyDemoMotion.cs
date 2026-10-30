using System;
using System.Globalization;
#nullable disable

// Controller-owned IK, matching the pre-demo JsonExecutor builders. No local
// IK/FK, alternative joint branches, clearance waypoints or Home-Z propagation.
public static class LegacyDemoMotion
{
    public const string EngineVersion="legacy-controller-qnear-v2-io-feedback";
    public struct Point
    {
        public double X, Y, Z;
        public string Orientation;
        public Point(double x, double y, double z, string orientation = null)
        { X=x; Y=y; Z=z; Orientation=orientation; }
    }
    public sealed class Command
    {
        public string Name, Program;
        public double[] Position, Joints;
        public bool ContactPick, ActualXYLift;
        public int DigitalOutputChannel=-1;
        public bool DigitalOutputHigh;
        public double SettleSeconds;
    }
    public sealed class ContactSettings
    {
        public double Approach=0.025, Speed=0.010, Force=15, Retreat=0.030, Correction=0.002, MinimumOffset=0.005;
        public int MaxCorrections=7;
    }
    public const string HomeLine="movej([-1.5708, -1.5708, 0, -1.5708, 0, 0], a=1.2, v=0.8)";
    public static readonly double[] HomeJoints={-1.5708,-1.5708,0,-1.5708,0,0};
    static string F(double x) => x.ToString("0.000000",CultureInfo.InvariantCulture);
    public static string PoseExpression(double x,double y,double z,string orientation)
    {
        string p=FormattableString.Invariant($"p[{x:F4}, {y:F4}, {z:F4}, 0, 3.14, 0]");
        return orientation=="horizontal" ? p
            : "pose_trans("+p+", p[0, 0, 0, 0, 0, 1.5708])";
    }
    public static string Movej(double x,double y,double z,string orientation) =>
        "movej(get_inverse_kin("+PoseExpression(x,y,z,orientation)+", qnear=get_actual_joint_positions()), a=1.2, v=0.8)";
    public static string Movel(double x,double y,double z,string orientation) =>
        "movel("+PoseExpression(x,y,z,orientation)+", a=0.3, v=0.10)";
    public static string Wrap(string line) => "def demo_motion():\n  "+line+"\n  sleep(0.10)\nend";
    static Command Move(string name,Point p,bool linear) => new Command {
        Name=name,Position=new[]{p.X,p.Y,p.Z},Program=Wrap(linear?Movel(p.X,p.Y,p.Z,p.Orientation):Movej(p.X,p.Y,p.Z,p.Orientation)) };
    static Point AtZ(Point p,double z) {p.Z=z;return p;}
    static Command Io(string name,bool close) => new Command {
        Name=name,DigitalOutputChannel=4,DigitalOutputHigh=close,SettleSeconds=1.50,
        Program="set_standard_digital_out(4, "+(close?"True":"False")+")" };
    public static bool OutputMatches(Command command,int outputBits) =>
        command.DigitalOutputChannel>=0 && command.DigitalOutputChannel<8 &&
        ((outputBits & (1<<command.DigitalOutputChannel))!=0)==command.DigitalOutputHigh;

    public static Command[] Build(Point source,Point target,double travelZ,double hover,ContactSettings contact)
    {
        var pick=Move("pick_contact",source,true);
        if(contact!=null) {pick.Program=ContactPick(source.Z,contact);pick.ContactPick=true;}
        return new[] {
            Io("open",false),Move("source_travel",AtZ(source,travelZ),false),
            Move("source_hover",AtZ(source,source.Z+hover),true),pick,Io("grasp",true),
            SourceLift(source,source.Z+hover),Move("target_travel",AtZ(target,travelZ),false),
            Move("target_hover",AtZ(target,target.Z+hover),true),Move("target_descend",target,true),
            Io("release",false),Move("target_lift",AtZ(target,target.Z+hover),true),
            new Command {Name="go_home",Program=Wrap(HomeLine),Joints=(double[])HomeJoints.Clone()}
        };
    }
    public static Command SourceLift(Point actual,double targetZ) => new Command {
        Name="source_lift",ActualXYLift=true,Position=new[]{actual.X,actual.Y,targetZ},
        Program="def demo_lift():\n  pose = get_actual_tcp_pose()\n  pose[2] = "+F(targetZ)+
            "\n  movel(pose, a=0.3, v=0.10)\n  sleep(0.10)\nend" };

    static string ContactPick(double pickZ,ContactSettings c)
    {
        // main's force/torque correction, using the CONTROLLER's actual tool X
        // direction instead of a C# FK solution. Fast approach remains Cartesian.
        return string.Join("\n",
            "def contact_pick():",
            "  pose = get_actual_tcp_pose()",
            "  pose[2] = "+F(pickZ+c.Approach),
            "  movel(pose, a=0.3, v=0.10)",
            "  pose = get_actual_tcp_pose()",
            "  tool_x = pose_trans(pose, p[1, 0, 0, 0, 0, 0])",
            "  ax = tool_x[0] - pose[0]", "  ay = tool_x[1] - pose[1]",
            "  axis_length = sqrt(ax * ax + ay * ay)",
            "  if axis_length > 0.000001:",
            "    ax = ax / axis_length", "    ay = ay / axis_length",
            "    corrections = 0", "    finished = False",
            "    while not finished:", "      zero_ftsensor()", "      sleep(0.2)",
            "      pose = get_actual_tcp_pose()", "      start_z = pose[2]", "      moved = 0.0",
            "      contact = False", "      fz = 0.0", "      tx = 0.0", "      ty = 0.0",
            "      while (not contact) and (moved < "+F(c.Approach)+"):",
            "        speedl([0, 0, -"+F(c.Speed)+", 0, 0, 0], 0.5, 0.008)",
            "        force = get_tcp_force()",
            "        if norm(force[2]) > "+F(c.Force)+":",
            "          contact = True", "          fz = force[2]", "          tx = force[3]", "          ty = force[4]", "        end",
            "        pose = get_actual_tcp_pose()", "        moved = start_z - pose[2]", "      end", "      stopl(0.5)",
            "      if not contact:", "        finished = True", "      else:",
            "        up = pose_add(get_actual_tcp_pose(), p[0, 0, "+F(c.Retreat)+", 0, 0, 0])",
            "        movel(up, a=0.5, v=0.05)",
            "        side = (0.0 - ty / fz) * ax + (tx / fz) * ay",
            "        if (corrections >= "+c.MaxCorrections+") or (norm(side) < "+F(c.MinimumOffset)+"):",
            "          finished = True", "        else:", "          corrections = corrections + 1",
            "          step = "+F(c.Correction),"          if side < 0:","            step = 0.0 - step","          end",
            "          up = pose_add(up, p[step * ax, step * ay, 0, 0, 0, 0])",
            "          movel(up, a=0.5, v=0.05)", "          dz = start_z - up[2]",
            "          up = pose_add(up, p[0, 0, dz, 0, 0, 0])", "          movel(up, a=0.5, v=0.05)",
            "        end", "      end", "    end", "  end", "  sleep(0.3)", "end");
    }
}
