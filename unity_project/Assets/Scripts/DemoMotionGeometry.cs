using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

// IK and conservative link-capsule gates ported from main 4b422a8.
// Hover look-ahead tests the entire following Cartesian descent BEFORE moving.
public static class DemoMotionGeometry
{
    public sealed class Motion
    {
        public string Name = "";
        public bool Linear;
        public List<double[]> Targets = new List<double[]>();
        public double[] End => Targets[Targets.Count - 1];
    }
    public static UR3eKinematics.Pose Pose(double x, double y, double z, string orientation)
    {
        // Ry(pi) * Rz(theta): axis-angle = pi * (sin(theta/2), cos(theta/2), 0).
        double half = orientation == "horizontal" ? 0 : Math.PI / 4;
        return new UR3eKinematics.Pose { x=x, y=y, z=z,
            rx=Math.PI*Math.Sin(half), ry=Math.PI*Math.Cos(half), rz=0 };
    }

    static IEnumerable<UR3eKinematics.IKSolution> Candidates(UR3eKinematics.Pose p, double[] q)
    {
        yield return UR3eKinematics.IKNearest(p,q);
        foreach(var alternative in UR3eKinematics.IKAlternatives(p,q)) yield return alternative;
    }
    public static Motion Joint(string name, double[] from, UR3eKinematics.Pose pose, bool facing = true)
    {
        var candidates = Candidates(pose, from);
        foreach (var s in candidates)
            if (s.ok && (!facing || Facing(s.q, pose.x, pose.y)) && Transition(from, s.q, out _))
                return new Motion { Name=name, Targets=new List<double[]> { s.q } };
        throw new InvalidOperationException(name + ": no collision-free IK transition to " + pose);
    }

    public static Motion Linear(string name, double[] from, UR3eKinematics.Pose target)
    {
        var start = UR3eKinematics.FKPose(from);
        double distance = Math.Sqrt(Math.Pow(start.x-target.x,2)+Math.Pow(start.y-target.y,2)+Math.Pow(start.z-target.z,2));
        if (distance < 1e-6 && Math.Abs(start.rx-target.rx)+Math.Abs(start.ry-target.ry)+Math.Abs(start.rz-target.rz)<1e-6)
            return new Motion { Name=name, Linear=true, Targets=new List<double[]> {(double[])from.Clone()} };
        int samples = Math.Max(2, (int)Math.Ceiling(distance/0.005));
        var result = new Motion { Name=name, Linear=true };
        var q = (double[])from.Clone();
        for (int i=1;i<=samples;i++)
        {
            double t = (double)i/samples;
            var p = target; p.x=start.x+(target.x-start.x)*t; p.y=start.y+(target.y-start.y)*t; p.z=start.z+(target.z-start.z)*t;
            var solution = UR3eKinematics.IKNearest(p,q);
            if (!solution.ok || !Transition(q,solution.q,out _))
                throw new InvalidOperationException(name + ": Cartesian path rejected at " + t.ToString("P0"));
            q=solution.q;
        }
        result.Targets.Add(q);
        return result;
    }

    public static Motion Hover(string name, double[] from, UR3eKinematics.Pose hover, UR3eKinematics.Pose descend)
    {
        // Keep the nearest hover if its descent works, otherwise try alternative IK branches.
        foreach (var s in Candidates(hover, from))
        {
            if (!s.ok || !Facing(s.q,hover.x,hover.y) || !Transition(from,s.q,out _)) continue;
            try { Linear("hover look-ahead",s.q,descend); }
            catch (InvalidOperationException) { continue; }
            return new Motion { Name=name, Targets=new List<double[]> {s.q} };
        }
        throw new InvalidOperationException(name + ": no safe hover followed by descent (including alternative IK).");
    }

    public static Motion Travel(string name, double[] from, UR3eKinematics.Pose target, double clearanceZ)
    {
        var result = new Motion { Name=name };
        var start=UR3eKinematics.FKPose(from);
        // Clearance is a Cartesian lift at the CURRENT orientation. Never lower
        // or rotate a folded arm just to reach a fixed global travel plane.
        clearanceZ=Math.Max(clearanceZ,start.z);
        var raised=start; raised.z=clearanceZ;
        var first=Linear(name+" clearance",from,raised);
        result.Targets.AddRange(first.Targets);
        var q=first.End;
        double dx=target.x-start.x,dy=target.y-start.y;
        double len=dx*dx+dy*dy;
        double t=len>1e-9 ? Math.Max(0,Math.Min(1,-(start.x*dx+start.y*dy)/len)) : 0;
        double closest=Math.Sqrt(Math.Pow(start.x+t*dx,2)+Math.Pow(start.y+t*dy,2));
        if (closest<0.20)
        {
            double a=Math.Atan2(start.y,start.x),b=Math.Atan2(target.y,target.x);
            double delta=Math.Atan2(Math.Sin(b-a),Math.Cos(b-a));
            int n=Math.Max(1,(int)Math.Ceiling(Math.Abs(delta)/(25*Math.PI/180)));
            for(int i=0;i<=n;i++)
            {
                var p=target; double angle=a+delta*i/n;
                p.x=0.28*Math.Cos(angle);p.y=0.28*Math.Sin(angle);p.z=clearanceZ;
                var segment=Joint(name+" base detour",q,p);
                result.Targets.AddRange(segment.Targets);q=segment.End;
            }
        }
        target.z=clearanceZ;
        var last=Joint(name,q,target);result.Targets.AddRange(last.Targets);
        return result;
    }

    public static Motion Home(double[] from, double[] home)
    {
        if (!Transition(from,home,out string error,true)) throw new InvalidOperationException("Home path: "+error);
        return new Motion { Name="go_home", Targets=new List<double[]> {home} };
    }
    public static Motion[] PlanPiece(double[] initial, UR3eKinematics.Pose source,
        UR3eKinematics.Pose target, double approach, double safeHeight, double travelZ, double[] home)
    {
        var m=new Motion[12];
        var srcHover=source;srcHover.z+=approach;
        var tgtHover=target;tgtHover.z+=safeHeight;
        travelZ=Math.Max(travelZ,Math.Max(srcHover.z,tgtHover.z));
        var srcTravel=source;srcTravel.z=travelZ;
        var tgtTravel=target;tgtTravel.z=travelZ;
        m[1]=Travel("source_travel",initial,srcTravel,travelZ);
        m[2]=Hover("source_hover",m[1].End,srcHover,source);
        m[3]=Linear("pick_contact",m[2].End,source);
        m[5]=Linear("source_lift",m[3].End,srcTravel);
        m[6]=Travel("target_travel",m[5].End,tgtTravel,travelZ);
        m[7]=Hover("target_hover",m[6].End,tgtHover,target);
        m[8]=Linear("target_descend",m[7].End,target);
        m[10]=Linear("target_lift",m[8].End,tgtTravel);
        m[11]=Home(m[10].End,home);
        return m;
    }
    static bool Facing(double[] q,double x,double y) =>
        Math.Abs(Math.Atan2(Math.Sin(q[0]-Math.Atan2(y,x)),Math.Cos(q[0]-Math.Atan2(y,x))))<=Math.PI/2;
    public static bool Transition(double[] from,double[] to,out string error,bool home=false)
    {
        double delta=from.Zip(to,(a,b)=>Math.Abs(a-b)).Max();
        // Reject large IK branch switches instead of relying on the known-
        // incomplete capsule model. This guard is NOT full tool-body collision QA.
        if (delta > Math.PI / 2)
        { error="Joint transition exceeds 90 degrees; large IK branch switch rejected"; return false; }
        int count=Math.Max(2,(int)Math.Ceiling(delta*180/Math.PI/2));
        for(int i=0;i<=count;i++)
        {
            double t=(double)i/count;
            var q=from.Zip(to,(a,b)=>a+(b-a)*t).ToArray();
            // Endpoint pick/place radii do not bound a movej's intermediate TCP.
            // The reported collision pose lies at ~138 mm despite legal endpoints.
            var tcp=UR3eKinematics.FKPose(q);
            double tcpRadius=Math.Sqrt(tcp.x*tcp.x+tcp.y*tcp.y);
            if(double.IsNaN(tcpRadius) || double.IsInfinity(tcpRadius) || tcpRadius < 0.16)
            { error="TCP path enters 0.16 m base exclusion at sample "+i+"/"+count+" (radius="+tcpRadius.ToString("F4",System.Globalization.CultureInfo.InvariantCulture)+" m)"; return false; }
            var check=UR3eKinematics.CheckJoints(q);
            // Home has a deliberately singular wrist. Permit only the endpoint.
            if(check!=UR3eKinematics.IKError.None && i!=0 && !(home && i==count))
            { error=UR3eKinematics.Describe(check);return false; }
            if(!ValidateApproximateRobotCollision(q,out error))return false;
        }
        error="";return true;
    }
    static bool ValidateApproximateRobotCollision(double[] q, out string error)
    {
        double[][] p = UR3eKinematics.LinkPoints(q);
        float[] radii = { 0.085f, 0.070f, 0.055f, 0.050f, 0.045f, 0.035f };
        float tableZ = 0.030f;

        // The base/shoulder are mounted through the table; check all moving links
        // after the upper arm against the tabletop with conservative radii.
        for (int segment = 2; segment < 6; segment++)
        {
            double[] segmentEnd = p[segment + 1];
            float minZ = (float)System.Math.Min(p[segment][2], segmentEnd[2]) - radii[segment];
            if (minZ < tableZ - 0.005f)
            {
                error = $"第 {segment} 段連桿撞到桌面（z={minZ:F3}m）";
                return false;
            }
        }

        // Same conservative 2D capsule model as main; demo has no 3D mesh preview.
        for (int a = 0; a < 6; a++)
        {
            for (int b = a + 2; b < 6; b++)
            {
                // Segments 3 and 5 are separated by the fixed d5 wrist spacer
                // (about 85 mm). Their conservative capsules naturally meet at
                // that assembly, so adding the general 8 mm clearance creates a
                // permanent false positive even in the calibrated Ready pose.
                if (a == 3 && b == 5) continue;
                float distance = SegmentDistance(ToVector3(p[a]), ToVector3(p[a + 1]),
                                                 ToVector3(p[b]), ToVector3(p[b + 1]));
                // Pair-specific thresholds; other pairs retain the 8 mm margin.
                // 0↔2: 140 mm, no extra margin beyond the capsule radii.
                // 1↔3: 125 mm, a 5 mm margin beyond the capsule radii.
                float required = (a == 0 && b == 2) ? 0.140f
                    : (a == 1 && b == 3) ? 0.125f
                    : radii[a] + radii[b] + 0.008f;
                if (distance < required)
                {
                    error = $"self collision：連桿 {a}↔{b} 距離 {distance:F3}m，小於需要的 {required:F3}m";
                    return false;
                }
            }
        }
        error = "";
        return true;
    }

    static float Clamp01(float value) => Math.Max(0f, Math.Min(1f, value));
    static Vector3 ToVector3(double[] p) => new Vector3((float)p[0], (float)p[1], (float)p[2]);

    static float SegmentDistance(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
    {
        Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
        float s, t;
        if (a <= 1e-8f && e <= 1e-8f) return Vector3.Distance(p1, p2);
        if (a <= 1e-8f) { s = 0f; t = Clamp01(f / e); }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= 1e-8f) { t = 0f; s = Clamp01(-c / a); }
            else
            {
                float b = Vector3.Dot(d1, d2), denom = a * e - b * b;
                s = denom != 0f ? Clamp01((b * f - c * e) / denom) : 0f;
                t = (b * s + f) / e;
                if (t < 0f) { t = 0f; s = Clamp01(-c / a); }
                else if (t > 1f) { t = 1f; s = Clamp01((b - c) / a); }
            }
        }
        return Vector3.Distance(p1 + d1 * s, p2 + d2 * t);
    }

}
