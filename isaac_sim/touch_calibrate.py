"""
touch_calibrate.py — 用實體手臂的指尖輕碰 QR 標記中心，量出 QR 座標系在 UR 基座座標的真正位置與轉角

只從手臂的 realtime 唯讀埠（30013）讀關節角，不送任何指令；手臂由人用 Teach Pendant 低速移動。
一般 Python 3 即可執行（只用標準函式庫）：
    python isaac_sim/touch_calibrate.py --robot_ip 192.168.50.204
    python isaac_sim/touch_calibrate.py --simulate            # 用模擬資料測試解算

流程：
  1. 從 perception /camera 取 QR1-4 中心在 QR 座標系的位置（相機量測；QR1 是原點）
  2. 把指尖輕碰 QR 標記中心（手臂搆得到的才碰；QR1、QR2 離底座太遠通常碰不到），每碰好一個按 Enter
  3. 再用黃色方塊當額外參考點：方塊放在桌上任一處，指尖輕碰方塊頂面中心，方塊位置取 perception /scene
  4. 用指尖長度（預設 179 mm）的正向運動學算出指尖在基座座標的位置，跟目前設定
     （base = QR + (QR1_X, QR1_Y, QR1_Z)，軸向平行、沒有轉角）預測的位置比較
  5. 至少兩個點就能解出轉角與平移（點越分散越準）；桌面高度 = 各點（方塊點扣掉 25 mm）的平均
"""

import argparse
import json
import math
import sys
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from sync_check import fk, read_robot_joints  # noqa: E402

# 目前 Unity 的設定（JsonExecutor.cs QR1_X/Y/Z；3D 批次的桌面另加 LayeredGraspGeometry.TableZCorrectionM）
QR1_X, QR1_Y, QR1_Z = -0.38824, -0.35973 + 0.005, 0.030
TABLE_Z_CORRECTION_3D = -0.030
DEFAULT_MARKERS = {"QR1": [0.0, 0.0], "QR2": [0.805, 0.0], "QR3": [0.0, 0.371], "QR4": [0.805, 0.371]}


def fingertip_base(q, tip):
    flange, tool_z = fk(q)
    return [flange[i] + tool_z[i] * tip for i in range(3)]


def fit_rigid_2d(qr_points, base_points):
    """最小平方解 base ≈ R(θ)·qr + t（2D 剛體，Kabsch）。回傳 (θ 弧度, t, 每點殘差 mm)。"""
    n = len(qr_points)
    cq = [sum(p[i] for p in qr_points) / n for i in range(2)]
    cb = [sum(p[i] for p in base_points) / n for i in range(2)]
    sxx = sxy = syx = syy = 0.0
    for q, b in zip(qr_points, base_points):
        qx, qy = q[0] - cq[0], q[1] - cq[1]
        bx, by = b[0] - cb[0], b[1] - cb[1]
        sxx += qx * bx; sxy += qx * by; syx += qy * bx; syy += qy * by
    theta = math.atan2(sxy - syx, sxx + syy)
    c, s = math.cos(theta), math.sin(theta)
    t = [cb[0] - (c * cq[0] - s * cq[1]), cb[1] - (s * cq[0] + c * cq[1])]
    residuals = []
    for q, b in zip(qr_points, base_points):
        px, py = c * q[0] - s * q[1] + t[0], s * q[0] + c * q[1] + t[1]
        residuals.append(math.hypot(px - b[0], py - b[1]) * 1000)
    return theta, t, residuals


def report(points):
    """points: [(名稱, QR (x, y), 指尖基座 (x, y, z), 該點的表面高度)]"""
    print("\n=== 結果（單位 mm；QR 座標 +X = QR1→QR2，+Y = QR1→QR3）")
    for name, (qx, qy), (bx, by, bz), surface in points:
        mx, my = QR1_X + qx, QR1_Y + qy
        print(f"  {name}: QR ({qx:.3f},{qy:.3f})，目前設定預測指尖應在基座 ({mx*1000:7.1f}, {my*1000:7.1f})，"
              f"實際碰到 ({bx*1000:7.1f}, {by*1000:7.1f}) → 偏差 x {(bx-mx)*1000:+6.1f}、y {(by-my)*1000:+6.1f}；"
              f"桌面 z {(bz - surface)*1000:+6.1f}")
    if len(points) < 2:
        print("  至少要兩個點才能解轉角與平移。")
        return
    theta, t, res = fit_rigid_2d([p[1] for p in points], [p[2][:2] for p in points])
    table_z = sum(p[2][2] - p[3] for p in points) / len(points)
    print(f"\n  QR 座標軸相對手臂基座的轉角：{math.degrees(theta):+.2f}°")
    print(f"  QR1 在基座座標的位置：x {t[0]:.5f}、y {t[1]:.5f}（目前 QR1_X {QR1_X:.5f}、QR1_Y {QR1_Y:.5f}，"
          f"差 {(t[0]-QR1_X)*1000:+.1f}、{(t[1]-QR1_Y)*1000:+.1f} mm）")
    print(f"  桌面高度：z {table_z:.4f}（目前 2D 用 {QR1_Z:.3f}，3D 用 {QR1_Z+TABLE_Z_CORRECTION_3D:.3f}）")
    print("  擬合殘差：" + "、".join(f"{p[0]} {r:.1f}" for p, r in zip(points, res)) +
          "（每點幾 mm 以內代表碰得準、量測一致；很大就重碰那一點）")
    print(f"  只修平移、不修轉角的話，離 QR1 60 cm 處還會差約 {abs(math.sin(theta))*0.6*1000:.1f} mm")


def perceived_block(perception, name="yellow_cube", frames=5):
    """perception /scene 連續幾幀的平均位置（QR 座標）；找不到回傳 None。"""
    import time
    xs = []
    for _ in range(frames):
        scene = json.loads(urllib.request.urlopen(f"{perception}/scene", timeout=5).read())
        for o in scene.get("objects", []):
            p = o.get("position")
            if o.get("name") == name and isinstance(p, dict):
                xs.append((p["x"], p["y"]))
                break
        time.sleep(0.2)
    if len(xs) < max(2, frames // 2):
        return None
    return [sum(v[0] for v in xs) / len(xs), sum(v[1] for v in xs) / len(xs)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--robot_ip", default=None)
    ap.add_argument("--perception", default="http://localhost:5000")
    ap.add_argument("--tip", type=float, default=0.179, help="法蘭 → 指尖（公尺）")
    ap.add_argument("--simulate", action="store_true", help="不接手臂，用已知的轉角 / 偏移產生模擬資料測試解算")
    args = ap.parse_args()

    markers = dict(DEFAULT_MARKERS)
    try:
        cam = json.loads(urllib.request.urlopen(f"{args.perception.rstrip('/')}/camera", timeout=5).read())
        markers.update({k: v for k, v in (cam.get("qr_markers") or {}).items() if k in markers})
        print("QR 標記中心（相機量測，QR 座標）：" + "、".join(f"{k}({v[0]:.3f},{v[1]:.3f})" for k, v in markers.items()))
    except Exception as ex:
        print(f"取不到 perception /camera，改用預設 QR 位置：{ex}")

    points = []
    if args.simulate:
        # 模擬：真實的 QR 相對基座轉了 1.5°、QR1 差 (+8, -12) mm、桌面低 30 mm；用搆得到的 QR3、QR4
        # 加兩個方塊點（頂面 25 mm），解算應該還原這些數字
        th, dx, dy, z = math.radians(1.5), 0.008, -0.012, QR1_Z - 0.030
        def real(qx, qy, surface):
            return [QR1_X + dx + math.cos(th) * qx - math.sin(th) * qy,
                    QR1_Y + dy + math.sin(th) * qx + math.cos(th) * qy, z + surface]
        for n in ("QR3", "QR4"):
            points.append((n, markers[n], real(*markers[n], 0.0), 0.0))
        for i, xy in enumerate(([0.30, 0.12], [0.52, 0.10])):
            points.append((f"方塊{i + 1}", xy, real(*xy, 0.025), 0.025))
        print("模擬資料：轉角 +1.50°、QR1 偏移 x +8.0 mm、y -12.0 mm、桌面 z 0.000")
    else:
        if not args.robot_ip:
            ap.error("需要 --robot_ip（或用 --simulate 測試）")
        perception = args.perception.rstrip("/")
        print(f"\n指尖長度 {args.tip*1000:.0f} mm。用 Teach Pendant 低速移動，夾爪張開、垂直朝下，"
              "讓兩指中間（夾爪中心）對準目標中心，指尖輕碰表面。")
        for n in ("QR3", "QR4", "QR1", "QR2"):
            qx, qy = markers[n]
            reach = math.hypot(QR1_X + qx, QR1_Y + qy)
            note = f"（離底座約 {reach*100:.0f} cm" + ("，可能搆不到，建議跳過）" if reach > 0.47 else "）")
            ans = input(f"  指尖碰好 {n} 中心後按 Enter{note}，輸入 s 跳過：").strip().lower()
            if ans == "s":
                continue
            q = read_robot_joints(args.robot_ip)
            points.append((n, markers[n], fingertip_base(q, args.tip), 0.0))
            print(f"    關節角 {[round(math.degrees(v), 2) for v in q]} → 指尖 {[round(v, 4) for v in points[-1][2]]}")
        print("\n  接著用黃色方塊加參考點（放在不同位置、越分散越好，至少再加 1～2 個）：")
        while True:
            ans = input("  把黃色方塊放好、手移開，指尖輕碰方塊頂面中心後按 Enter（輸入 q 結束）：").strip().lower()
            if ans == "q":
                break
            q = read_robot_joints(args.robot_ip)
            tip_pos = fingertip_base(q, args.tip)
            input("  先把手臂抬離方塊（相機才看得到整塊），抬好按 Enter：")
            xy = perceived_block(perception)
            if xy is None:
                print("    相機看不到 yellow_cube，這一點不算")
                continue
            points.append((f"方塊{len([p for p in points if p[3] > 0]) + 1}", xy, tip_pos, 0.025))
            print(f"    方塊 QR ({xy[0]:.4f},{xy[1]:.4f}) → 指尖 {[round(v, 4) for v in tip_pos]}")
    report(points)


if __name__ == "__main__":
    main()
