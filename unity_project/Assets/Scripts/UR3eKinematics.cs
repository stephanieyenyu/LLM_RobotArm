using System;

// UR3e 純運動學模組
//
// 用途：sim pre-flight verifier 的 single source of truth
//   - LLM 產生 target pose → 這裡回傳可行 q[6] 或錯誤原因
//   - 同一組 q[6] 也用來驅動 Unity RobotArm.Angles 和真機 movej
//
// 座標系：robot base frame（右手系 Z-up，公尺、弧度）
//   Unity 座標系轉換由呼叫端負責（RobotArm.Robot2Unity）
//
// DH 參數：Universal Robots 官方 UR3e（Classical DH，Rz(θ)·Tz(d)·Tx(a)·Rx(α)）
//   Ref: https://www.universal-robots.com/articles/ur/application-installation/dh-parameters-for-calculations-of-kinematics-and-dynamics/
//   | i | a_i      | α_i    | d_i     |
//   | 1 |  0       |  π/2   | 0.15185 |
//   | 2 | -0.24355 |  0     | 0       |
//   | 3 | -0.21320 |  0     | 0       |
//   | 4 |  0       |  π/2   | 0.13105 |
//   | 5 |  0       | -π/2   | 0.08535 |
//   | 6 |  0       |  0     | 0.09210 |
//
// IK：Damped Least Squares (Levenberg-Marquardt) 數值解
//   - 從 reference q 開始 iterate，確保連續性（適合 pre-flight verifier）
//   - 遇到 unreachable/singular 明確回報，不會給錯解
public static class UR3eKinematics
{
    // ============ DH 常數 ============
    public const double d1 =  0.15185;
    public const double a2 = -0.24355;
    public const double a3 = -0.21320;
    public const double d4 =  0.13105;
    public const double d5 =  0.08535;
    public const double d6 =  0.09210;

    // 工具長度：flange 到實際 TCP（夾爪指尖）沿 tool Z 軸的距離。
    // 設定之後 FK/IK 的目標就是指尖而非 flange，所以 pick/place 只要把目標設成
    // 方塊頂面即可，不需要任何「夾爪多長」的補償常數散落在動畫程式碼裡。
    // 由 RobotArm.toolOffsetZ 在執行期寫入（真機對應 URScript 的 set_tcp）。
    public static double toolOffsetZ = 0.0;

    // DH 鏈實際使用的末端長度
    public static double D6Effective => d6 + toolOffsetZ;

    // ============ 限制 ============
    public const double JOINT_LIMIT = 2.0 * Math.PI;

    // Singularity thresholds
    public const double SHOULDER_R_MIN = 0.01;  // wrist 到 z0 軸距離下限（m）
    public const double ELBOW_SIN_EPS  = 0.01;  // |sin(q3)| 下限
    public const double WRIST_SIN_EPS  = 0.01;  // |sin(q5)| 下限

    // 數值 IK 參數
    const int    IK_MAX_ITER   = 200;
    const double IK_TOL_POS    = 1e-5;   // 10 μm
    const double IK_TOL_ROT    = 1e-5;   // rad
    const double IK_DAMPING    = 1e-3;   // λ² for DLS

    public enum IKError { None, Unreachable, JointLimit, ShoulderSingular, ElbowSingular, WristSingular, NotConverged }

    public struct Pose
    {
        public double x, y, z;
        public double rx, ry, rz;   // axis-angle rotation vector

        public override string ToString() =>
            $"p[{x:F4},{y:F4},{z:F4}, {rx:F4},{ry:F4},{rz:F4}]";
    }

    public struct IKSolution
    {
        public double[] q;
        public IKError error;
        public string message;
        public bool ok => error == IKError.None;
    }

    // ============ FK ============
    public static double[,] FK(double[] q)
    {
        double[] aArr = { 0,   a2,  a3,  0,   0,    0    };
        double[] al   = { PI2, 0,   0,   PI2, -PI2, 0    };
        double[] dArr = { d1,  0,   0,   d4,  d5,   D6Effective };

        var T = Identity();
        for (int i = 0; i < 6; i++)
            T = Mul(T, DH(aArr[i], al[i], dArr[i], q[i]));
        return T;
    }

    public static Pose FKPose(double[] q)
    {
        return MatrixToPose(FK(q));
    }

    // ============ IK ============
    // 從 reference q 出發用 Damped Least Squares 收斂到 target。
    // 適合 pre-flight：連續動作維持同一分支解。
    public static IKSolution IKNearest(Pose target, double[] reference)
    {
        var T_target = PoseToMatrix(target);
        return IKNearest(T_target, reference);
    }

    // 單一初值的 DLS 在手臂接近伸直時容易卡在局部極小，會把搆得到的點誤判為 Unreachable，
    // 所以 reference 解不出來時改從結構化初值多點起步，取最接近 reference 的合法解。
    public static IKSolution IKNearest(double[,] T_target, double[] reference)
    {
        var first = IKFromSeed(T_target, reference);
        if (first.ok) return Unwrapped(first, reference);

        IKSolution fallback = first;
        foreach (var seed in StructuredSeeds(T_target, reference))
        {
            var s = IKFromSeed(T_target, seed);
            if (s.ok) return Unwrapped(s, reference);
            // 收斂但奇點/超限，比「沒收斂」更有資訊量，優先回報
            if (fallback.error == IKError.Unreachable && s.error != IKError.Unreachable && Converged(T_target, s.q))
                fallback = s;
        }
        return fallback;
    }

    // base 對準目標方位的四個方向 × 四種肩肘彎法 × 手腕翻轉兩種，依與 reference 的距離排序（先試最近的）
    static System.Collections.Generic.List<double[]> StructuredSeeds(double[,] T_target, double[] reference)
    {
        double az = Math.Atan2(T_target[1, 3], T_target[0, 3]);
        var seeds = new System.Collections.Generic.List<double[]>();
        foreach (double dq1 in new[] { 0.0, PI2, -PI2, Math.PI })
            foreach (var (q2, q3) in new[] { (-PI2 / 2, PI2), (-3 * PI2 / 2, PI2), (-PI2 / 2, -PI2), (-3 * PI2 / 2, -PI2) })
                foreach (double q5 in new[] { -PI2, PI2 })
                    seeds.Add(new[] { az + dq1, q2, q3, -PI2 - q2 - q3, q5, 0.0 });
        seeds.Sort((a, b) => JointDist(a, reference).CompareTo(JointDist(b, reference)));
        return seeds;
    }

    static bool Converged(double[,] T_target, double[] q)
    {
        var e = PoseError(T_target, FK(q));
        return Math.Sqrt(e[0] * e[0] + e[1] * e[1] + e[2] * e[2]) < IK_TOL_POS * 10
            && Math.Sqrt(e[3] * e[3] + e[4] * e[4] + e[5] * e[5]) < IK_TOL_ROT * 10;
    }

    // 各 joint 取與 reference 最近的等價角度（差 2π 是同一姿態），避免動畫繞遠路；超出 joint limit 就保留原值
    static IKSolution Unwrapped(IKSolution s, double[] reference)
    {
        var q = new double[6];
        for (int k = 0; k < 6; k++)
        {
            double cand = reference[k] + ShortestAngleDiff(s.q[k], reference[k]);
            q[k] = Math.Abs(cand) <= JOINT_LIMIT ? cand : s.q[k];
        }
        s.q = q;
        return s;
    }

    static double JointDist(double[] a, double[] b)
    {
        double sum = 0;
        for (int k = 0; k < 6; k++) { double d = ShortestAngleDiff(a[k], b[k]); sum += d * d; }
        return sum;
    }

    static double ShortestAngleDiff(double a, double b)
    {
        double d = (a - b) % (2 * Math.PI);
        if (d > Math.PI) d -= 2 * Math.PI;
        if (d < -Math.PI) d += 2 * Math.PI;
        return d;
    }

    public static IKSolution IKFromSeed(double[,] T_target, double[] seed)
    {
        var q = new double[6];
        Array.Copy(seed, q, 6);

        double posErrLast = double.PositiveInfinity, rotErrLast = double.PositiveInfinity;

        for (int iter = 0; iter < IK_MAX_ITER; iter++)
        {
            var T_cur = FK(q);
            var err = PoseError(T_target, T_cur);
            double posErr = Math.Sqrt(err[0]*err[0] + err[1]*err[1] + err[2]*err[2]);
            double rotErr = Math.Sqrt(err[3]*err[3] + err[4]*err[4] + err[5]*err[5]);

            if (posErr < IK_TOL_POS && rotErr < IK_TOL_ROT)
            {
                var check = CheckJoints(q);
                var sol = new IKSolution { q = q, error = check };
                if (check != IKError.None) sol.message = $"solution valid but {check}";
                return sol;
            }

            var J = NumericJacobian(q);
            var dq = DLS(J, err, IK_DAMPING);

            // Step size adaptive: 大 error 時放慢避免超越
            double stepScale = 1.0;
            double totalErr = posErr + rotErr;
            if (totalErr > 0.5) stepScale = 0.3;
            for (int k = 0; k < 6; k++) q[k] += stepScale * dq[k];

            // 檢測發散
            if (iter > 20 && posErr > posErrLast * 1.5 && rotErr > rotErrLast * 1.5)
            {
                return new IKSolution
                {
                    q = q,
                    error = IKError.Unreachable,
                    message = $"IK diverged (posErr {posErr*1000:F2}mm rotErr {rotErr:F4}rad after {iter} iter)"
                };
            }
            posErrLast = posErr;
            rotErrLast = rotErr;
        }

        // 未收斂 → 判斷是 unreachable 還是卡在 singularity
        var T_end = FK(q);
        var e = PoseError(T_target, T_end);
        double pe = Math.Sqrt(e[0]*e[0] + e[1]*e[1] + e[2]*e[2]);
        double re = Math.Sqrt(e[3]*e[3] + e[4]*e[4] + e[5]*e[5]);
        var singCheck = CheckJoints(q);
        var result = new IKSolution
        {
            q = q,
            error = singCheck != IKError.None ? singCheck : IKError.Unreachable,
            message = $"not converged after {IK_MAX_ITER} iter (posErr {pe*1000:F2}mm rotErr {re:F4}rad)"
        };
        return result;
    }

    // 檢查一組 q 是否合法（joint limit + singularity）
    public static IKError CheckJoints(double[] q)
    {
        for (int k = 0; k < 6; k++)
            if (Math.Abs(q[k]) > JOINT_LIMIT)
                return IKError.JointLimit;

        double s3 = Math.Sin(q[2]);
        double s5 = Math.Sin(q[4]);
        if (Math.Abs(s5) < WRIST_SIN_EPS) return IKError.WristSingular;
        if (Math.Abs(s3) < ELBOW_SIN_EPS) return IKError.ElbowSingular;

        var T = FK(q);
        double p05x = T[0, 3] - D6Effective * T[0, 2];
        double p05y = T[1, 3] - D6Effective * T[1, 2];
        if (Math.Sqrt(p05x*p05x + p05y*p05y) < Math.Abs(d4) - SHOULDER_R_MIN)
            return IKError.ShoulderSingular;

        return IKError.None;
    }

    // ============ 6-vec pose error (position + axis-angle) ============
    static double[] PoseError(double[,] T_target, double[,] T_cur)
    {
        var e = new double[6];
        e[0] = T_target[0, 3] - T_cur[0, 3];
        e[1] = T_target[1, 3] - T_cur[1, 3];
        e[2] = T_target[2, 3] - T_cur[2, 3];

        // R_err = R_target · R_cur^T
        var Rerr = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double s = 0;
                for (int k = 0; k < 3; k++) s += T_target[i, k] * T_cur[j, k]; // T_cur[j,k] because R^T
                Rerr[i, j] = s;
            }

        // log(R) = axis · angle
        double trace = Rerr[0, 0] + Rerr[1, 1] + Rerr[2, 2];
        double cos_theta = Clamp((trace - 1) / 2, -1, 1);
        double theta = Math.Acos(cos_theta);

        if (theta < 1e-9)
        {
            e[3] = e[4] = e[5] = 0;
        }
        else if (Math.PI - theta < 1e-6)
        {
            // 180° 特殊 case
            double rx = Math.Sqrt(Math.Max(0, (Rerr[0, 0] + 1) / 2));
            double ry = Math.Sqrt(Math.Max(0, (Rerr[1, 1] + 1) / 2));
            double rz = Math.Sqrt(Math.Max(0, (Rerr[2, 2] + 1) / 2));
            if (Rerr[0, 1] < 0) ry = -ry;
            if (Rerr[0, 2] < 0) rz = -rz;
            e[3] = theta * rx; e[4] = theta * ry; e[5] = theta * rz;
        }
        else
        {
            double s = 2 * Math.Sin(theta);
            e[3] = theta * (Rerr[2, 1] - Rerr[1, 2]) / s;
            e[4] = theta * (Rerr[0, 2] - Rerr[2, 0]) / s;
            e[5] = theta * (Rerr[1, 0] - Rerr[0, 1]) / s;
        }
        return e;
    }

    // ============ 6x6 numeric Jacobian ============
    // J[:,i] = (FK(q + δe_i) - FK(q)) / δ  作為 6-vec pose 誤差
    static double[,] NumericJacobian(double[] q)
    {
        const double delta = 1e-6;
        var J = new double[6, 6];
        var T0 = FK(q);
        var qp = new double[6];
        Array.Copy(q, qp, 6);

        for (int i = 0; i < 6; i++)
        {
            qp[i] = q[i] + delta;
            var Ti = FK(qp);
            // δpose / δq_i = PoseError(Ti, T0) / delta  (但注意符號)
            var e = PoseError(Ti, T0);
            for (int j = 0; j < 6; j++) J[j, i] = e[j] / delta;
            qp[i] = q[i];
        }
        return J;
    }

    // ============ Damped Least Squares ============
    // dq = (J^T J + λ² I)^-1 J^T · err
    static double[] DLS(double[,] J, double[] err, double lambda2)
    {
        // A = J^T J + λ² I  (6x6)
        var A = new double[6, 6];
        for (int i = 0; i < 6; i++)
            for (int j = 0; j < 6; j++)
            {
                double s = 0;
                for (int k = 0; k < 6; k++) s += J[k, i] * J[k, j];
                A[i, j] = s;
            }
        for (int i = 0; i < 6; i++) A[i, i] += lambda2;

        // b = J^T · err (6)
        var b = new double[6];
        for (int i = 0; i < 6; i++)
        {
            double s = 0;
            for (int k = 0; k < 6; k++) s += J[k, i] * err[k];
            b[i] = s;
        }

        // Solve A · dq = b via Gauss-Jordan
        return Solve6x6(A, b);
    }

    static double[] Solve6x6(double[,] A, double[] b)
    {
        // 增廣矩陣
        var M = new double[6, 7];
        for (int i = 0; i < 6; i++)
        {
            for (int j = 0; j < 6; j++) M[i, j] = A[i, j];
            M[i, 6] = b[i];
        }

        for (int col = 0; col < 6; col++)
        {
            // pivot
            int pivot = col;
            double best = Math.Abs(M[col, col]);
            for (int i = col + 1; i < 6; i++)
            {
                double a = Math.Abs(M[i, col]);
                if (a > best) { best = a; pivot = i; }
            }
            if (best < 1e-12) return new double[6]; // singular

            if (pivot != col)
                for (int j = 0; j < 7; j++) (M[col, j], M[pivot, j]) = (M[pivot, j], M[col, j]);

            // Normalize
            double p = M[col, col];
            for (int j = 0; j < 7; j++) M[col, j] /= p;

            // Eliminate
            for (int i = 0; i < 6; i++)
            {
                if (i == col) continue;
                double f = M[i, col];
                for (int j = 0; j < 7; j++) M[i, j] -= f * M[col, j];
            }
        }

        var x = new double[6];
        for (int i = 0; i < 6; i++) x[i] = M[i, 6];
        return x;
    }

    // ============ 內部工具 ============
    const double PI2 = Math.PI / 2;

    static double[,] DH(double a, double alpha, double d, double theta)
    {
        double ct = Math.Cos(theta), st = Math.Sin(theta);
        double ca = Math.Cos(alpha), sa = Math.Sin(alpha);
        return new double[4, 4]
        {
            { ct,   -st * ca,   st * sa,   a * ct },
            { st,    ct * ca,  -ct * sa,   a * st },
            { 0,     sa,         ca,        d      },
            { 0,     0,          0,         1      }
        };
    }

    static double[,] Identity()
    {
        var m = new double[4, 4];
        for (int i = 0; i < 4; i++) m[i, i] = 1;
        return m;
    }

    static double[,] Mul(double[,] A, double[,] B)
    {
        var C = new double[4, 4];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                double s = 0;
                for (int k = 0; k < 4; k++) s += A[i, k] * B[k, j];
                C[i, j] = s;
            }
        return C;
    }

    static double[,] PoseToMatrix(Pose p)
    {
        var T = Identity();
        T[0, 3] = p.x; T[1, 3] = p.y; T[2, 3] = p.z;
        double theta = Math.Sqrt(p.rx * p.rx + p.ry * p.ry + p.rz * p.rz);
        if (theta < 1e-9) return T;

        double ux = p.rx / theta, uy = p.ry / theta, uz = p.rz / theta;
        double c = Math.Cos(theta), s = Math.Sin(theta), C = 1 - c;

        T[0, 0] = c + ux * ux * C;
        T[0, 1] = ux * uy * C - uz * s;
        T[0, 2] = ux * uz * C + uy * s;
        T[1, 0] = uy * ux * C + uz * s;
        T[1, 1] = c + uy * uy * C;
        T[1, 2] = uy * uz * C - ux * s;
        T[2, 0] = uz * ux * C - uy * s;
        T[2, 1] = uz * uy * C + ux * s;
        T[2, 2] = c + uz * uz * C;
        return T;
    }

    static Pose MatrixToPose(double[,] T)
    {
        var p = new Pose { x = T[0, 3], y = T[1, 3], z = T[2, 3] };
        double trace = T[0, 0] + T[1, 1] + T[2, 2];
        double cosT = Clamp((trace - 1) / 2, -1, 1);
        double theta = Math.Acos(cosT);

        if (theta < 1e-9)
        {
            p.rx = p.ry = p.rz = 0;
        }
        else if (Math.PI - theta < 1e-6)
        {
            double rx = Math.Sqrt(Math.Max(0, (T[0, 0] + 1) / 2));
            double ry = Math.Sqrt(Math.Max(0, (T[1, 1] + 1) / 2));
            double rz = Math.Sqrt(Math.Max(0, (T[2, 2] + 1) / 2));
            if (T[0, 1] < 0) ry = -ry;
            if (T[0, 2] < 0) rz = -rz;
            p.rx = theta * rx; p.ry = theta * ry; p.rz = theta * rz;
        }
        else
        {
            double s = 2 * Math.Sin(theta);
            p.rx = (T[2, 1] - T[1, 2]) / s * theta;
            p.ry = (T[0, 2] - T[2, 0]) / s * theta;
            p.rz = (T[1, 0] - T[0, 1]) / s * theta;
        }
        return p;
    }

    static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
}
