"""
ft_probe.py — 即時讀 UR3e 手腕的力與扭矩，確認能不能用扭矩分辨是哪一根手指撞到

只讀手臂的 realtime 唯讀埠（30013），不送任何指令，手臂不會動。一般 Python 3 即可執行（只用標準函式庫）：
    python tools/ft_probe.py
    python tools/ft_probe.py --robot_ip 192.168.50.204 --csv ft_log.csv

用法：
  1. 手臂停在空中、夾爪張開、垂直朝下，手先不要碰夾爪。
  2. 程式先量 2 秒當基準（扣掉夾爪重量與感測器偏移），之後每 0.5 秒印一行「跟基準的差」。
  3. 用手指從下往上輕推其中一根手指幾秒、放開，再輕推另一根。
  4. 看 ΔTy 的正負號：兩根手指應該相反；「力臂」約 ±0.02～0.03 m 代表推的位置在手指上。

原理：手指在夾爪中心的 ±X 兩側（夾 cube 時兩指落在 cube 的 ±X 兩側）。往上的力 Fz 作用在離中心 dx 的地方，
對手腕產生繞 Y 軸的扭矩 Ty ≈ −dx·Fz，所以 Ty 的正負號就是哪一側，−ΔTy/ΔFz 就是力作用點離中心多遠（力臂）。
讀的是 realtime 封包的「TCP force」（手臂基座座標，已扣掉 payload；6 個值：Fx Fy Fz [N]、Tx Ty Tz [N·m]）。
正負號的實際對應要以實測為準（感測器座標、夾爪安裝方向都可能影響），這支程式就是用來量這件事。
"""

import argparse
import math
import socket
import struct
import sys
import time

# realtime 封包（URSoftware 5.x）去掉開頭 4 bytes 長度後的位置
Q_ACTUAL = slice(248, 296)      # 6 個關節角（rad）
TOOL_VECTOR = slice(440, 488)   # TCP 位姿 x y z rx ry rz
TCP_FORCE = slice(536, 584)     # Fx Fy Fz Tx Ty Tz


def read_packets(ip, port):
    """持續產生 realtime 封包（約 500 Hz）。只讀不寫。"""
    with socket.create_connection((ip, port), timeout=3) as sock:
        def exact(n):
            buf = b""
            while len(buf) < n:
                chunk = sock.recv(n - len(buf))
                if not chunk:
                    raise ConnectionError("連線中斷")
                buf += chunk
            return buf
        while True:
            size = struct.unpack(">i", exact(4))[0]
            yield exact(size - 4)


def mean(rows):
    return [sum(r[i] for r in rows) / len(rows) for i in range(len(rows[0]))]


def std(rows, m):
    return [math.sqrt(sum((r[i] - m[i]) ** 2 for r in rows) / len(rows)) for i in range(len(rows[0]))]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--robot_ip", default="192.168.50.204")
    ap.add_argument("--port", type=int, default=30013, help="realtime 唯讀埠")
    ap.add_argument("--baseline", type=float, default=2.0, help="開始時量基準的秒數（手不要碰夾爪）")
    ap.add_argument("--interval", type=float, default=0.5, help="每幾秒印一行")
    ap.add_argument("--force_trigger", type=float, default=3.0, help="|ΔFz| 超過幾 N 才判斷是哪一側")
    ap.add_argument("--seconds", type=float, default=0.0, help="跑幾秒後自動結束（0 = 一直跑，Ctrl+C 結束）")
    ap.add_argument("--csv", default=None, help="另存每一行到 CSV")
    args = ap.parse_args()

    print(f"連線 {args.robot_ip}:{args.port}（只讀）…")
    packets = read_packets(args.robot_ip, args.port)
    first = next(packets)
    tool = struct.unpack(">6d", first[TOOL_VECTOR])
    q = struct.unpack(">6d", first[Q_ACTUAL])
    print(f"封包長度 {len(first) + 4} bytes；關節角(deg) {[round(math.degrees(v), 1) for v in q]}")
    print(f"TCP 位姿 x y z = ({tool[0]:.4f}, {tool[1]:.4f}, {tool[2]:.4f}) m，"
          f"rx ry rz = ({tool[3]:.3f}, {tool[4]:.3f}, {tool[5]:.3f})")

    print(f"\n量基準 {args.baseline:.0f} 秒，手先不要碰夾爪…")
    rows, t0 = [], time.time()
    for body in packets:
        rows.append(struct.unpack(">6d", body[TCP_FORCE]))
        if time.time() - t0 >= args.baseline:
            break
    base = mean(rows)
    noise = std(rows, base)
    print(f"基準 Fx Fy Fz = {base[0]:+.2f} {base[1]:+.2f} {base[2]:+.2f} N，Tx Ty Tz = {base[3]:+.3f} {base[4]:+.3f} {base[5]:+.3f} N·m")
    print(f"雜訊（標準差）Fz {noise[2]:.2f} N，Tx {noise[3]:.3f}、Ty {noise[4]:.3f} N·m（{len(rows)} 筆）")
    print("\n開始：輕推一根手指幾秒、放開，再推另一根。Ctrl+C 結束。")
    print("  時間  |   ΔFx    ΔFy    ΔFz  N |   ΔTx     ΔTy     ΔTz  N·m | 力臂 -ΔTy/ΔFz | 判斷")

    csv = open(args.csv, "w", encoding="utf-8") if args.csv else None
    if csv:
        csv.write("t,dFx,dFy,dFz,dTx,dTy,dTz,arm_m,side\n")
    window, last, start = [], time.time(), time.time()
    try:
        for body in packets:
            window.append(struct.unpack(">6d", body[TCP_FORCE]))
            now = time.time()
            if now - last < args.interval:
                continue
            m = mean(window)
            window, last = [], now
            d = [m[i] - base[i] for i in range(6)]
            arm, side = "", ""
            if abs(d[2]) >= args.force_trigger:
                a = -d[4] / d[2]
                arm = f"{a:+.3f} m"
                side = "+X 側（朝 QR2，畫面右邊）" if a > 0 else "−X 側（朝 QR1，畫面左邊）"
            print(f"{now - start:6.1f}s | {d[0]:+6.2f} {d[1]:+6.2f} {d[2]:+6.2f} | "
                  f"{d[3]:+7.3f} {d[4]:+7.3f} {d[5]:+7.3f} | {arm:>12} | {side}")
            if csv:
                csv.write(f"{now - start:.2f},{','.join(f'{v:.4f}' for v in d)},{arm.replace(' m', '')},{side}\n")
                csv.flush()
            if args.seconds and now - start >= args.seconds:
                break
    except KeyboardInterrupt:
        pass
    finally:
        if csv:
            csv.close()
    print("結束。")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ConnectionError) as ex:
        print(f"連不到手臂：{ex}")
        sys.exit(1)
