using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// URSim solves poses without moving. RTDE receives the results on the same
// outgoing connection, so no inbound firewall rule or callback port is needed.
public static class UrSimIkClient
{
    public static double[] Solve(string host, double[] pose, double[] near, double toolZ,
        CancellationToken cancellation)
    {
        using (var rtde = new TcpClient(AddressFamily.InterNetwork))
        using (var script = new TcpClient(AddressFamily.InterNetwork))
        using (cancellation.Register(() => { rtde.Close(); script.Close(); }))
        {
            rtde.Connect(host, 30004);
            var stream = rtde.GetStream();
            Send(stream, 86, new byte[] { 0, 2 });
            var version = ReceiveType(stream, 86);
            if (version.Length != 1 || version[0] != 1)
                throw new IOException("URSim 不支援 RTDE protocol 2。");
            string names = "output_int_register_24," + string.Join(",",
                Enumerable.Range(24, 6).Select(i => "output_double_register_" + i));
            byte[] frequency = BitConverter.GetBytes(50.0);
            if (BitConverter.IsLittleEndian) Array.Reverse(frequency);
            Send(stream, 79, frequency.Concat(Encoding.ASCII.GetBytes(names)).ToArray());
            var recipe = ReceiveType(stream, 79);
            if (recipe.Length < 2 || Encoding.ASCII.GetString(recipe, 1, recipe.Length - 1) !=
                "INT32,DOUBLE,DOUBLE,DOUBLE,DOUBLE,DOUBLE,DOUBLE")
                throw new IOException("URSim RTDE 無法提供 IK 回傳暫存器 24～29：" + Encoding.ASCII.GetString(recipe));
            byte recipeId = recipe[0];
            Send(stream, 83, new byte[0]);
            var started = ReceiveType(stream, 83);
            if (started.Length != 1 || started[0] != 1) throw new IOException("URSim RTDE 啟動失敗。");

            int requestId = BitConverter.ToInt32(Guid.NewGuid().ToByteArray(), 0) & 0x3fffffff;
            if (requestId == 0) requestId = 1;
            string Values(double[] values) => string.Join(",", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
            string tcp = "p[0,0," + toolZ.ToString("R", CultureInfo.InvariantCulture) + ",0,0,0]";
            var text = new StringBuilder("sec llm_ik_query():\n");
            text.AppendLine($"  if get_inverse_kin_has_solution(p[{Values(pose)}], qnear=[{Values(near)}], tcp={tcp}):");
            text.AppendLine($"    local q = get_inverse_kin(p[{Values(pose)}], qnear=[{Values(near)}], tcp={tcp})");
            for (int i = 0; i < 6; i++) text.AppendLine($"    write_output_float_register({24 + i}, q[{i}])");
            text.AppendLine($"    write_output_integer_register(24, {requestId})");
            text.AppendLine("  else:");
            text.AppendLine($"    write_output_integer_register(24, {-requestId})");
            text.AppendLine("  end\nend");
            script.Connect(host, 30002);
            byte[] code = Encoding.ASCII.GetBytes(text.ToString());
            script.GetStream().Write(code, 0, code.Length);
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                byte[] data = ReceiveType(stream, 85);
                if (data.Length != 53 || data[0] != recipeId) continue;
                int marker = (data[1] << 24) | (data[2] << 16) | (data[3] << 8) | data[4];
                if (marker == -requestId) throw new InvalidOperationException("LLM TCP 姿態在 URSim 無 IK 解。");
                if (marker != requestId) continue;
                var joints = new double[6];
                for (int i = 0; i < 6; i++)
                {
                    var bytes = data.Skip(5 + i * 8).Take(8).ToArray();
                    if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
                    joints[i] = BitConverter.ToDouble(bytes, 0);
                }
                if (joints.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                    throw new IOException("URSim IK 回傳資料無效。");
                return joints;
            }
        }
    }

    static void Send(NetworkStream stream, byte type, byte[] body)
    {
        int size = body.Length + 3;
        stream.Write(new[] { (byte)(size >> 8), (byte)size, type }, 0, 3);
        stream.Write(body, 0, body.Length);
    }

    static byte[] ReceiveType(NetworkStream stream, byte expected)
    {
        while (true)
        {
            var header = ReadExactly(stream, 3);
            int length = (header[0] << 8) | header[1];
            if (length < 3) throw new IOException("URSim RTDE 封包長度無效。");
            var body = ReadExactly(stream, length - 3);
            if (header[2] == expected) return body;
        }
    }

    static byte[] ReadExactly(NetworkStream stream, int count)
    {
        var bytes = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(bytes, offset, count - offset);
            if (read == 0) throw new IOException("URSim RTDE 連線已中斷。");
            offset += read;
        }
        return bytes;
    }
}
