"""
phase3_pick_place.py — Phase 3b：完整 pick-and-place

UR10 + 真空吸盤 preset，讀 pattern JSON，逐顆從 source area 抓到目標位置，
建完 pattern 後跑物理穩定性驗證。

用法：
  cd isaac_sim
  C:\\Users\\ASUS\\isaac_env\\Scripts\\python.exe phase3_pick_place.py patterns\\example_3layer.json

流程：
  1. 建 UR10 (內建 suction gripper preset)
  2. 在 source area 產生 N 顆 source cubes（N = pattern 方塊數）
  3. 對每一顆 pattern block：
       PickPlaceController 自動處理：
         approach → descend → suction on → lift → move → descend → suction off → lift
  4. 最後跑物理，回報 pattern 是否穩定

備註：
  - UR10 preset 用真空吸盤（跟論文一致）
  - end_effector_offset 已經調到適合 5cm cube
  - source area 在手臂右前方（Y=-0.30）避開 pattern
"""

import argparse
import json
import sys
from pathlib import Path

from isaacsim import SimulationApp
simulation_app = SimulationApp({"headless": False})

import numpy as np
from isaacsim.core.api import World
from isaacsim.core.api.objects import DynamicCuboid

# UR10 + PickPlaceController (Isaac Sim 內建 preset)
from isaacsim.robot.manipulators.examples.universal_robots import UR10
from isaacsim.robot.manipulators.examples.universal_robots.controllers.pick_place_controller import (
    PickPlaceController,
)

COLOR_MAP = {
    "red":    (0.90, 0.15, 0.15),
    "yellow": (1.00, 0.85, 0.10),
    "blue":   (0.15, 0.35, 0.90),
    "green":  (0.20, 0.75, 0.30),
    "gray":   (0.60, 0.60, 0.60),
    "white":  (0.95, 0.95, 0.95),
}

# source area 佈局
SOURCE_ORIGIN = np.array([0.40, -0.30, 0.025], dtype=np.float32)  # 第一顆 source cube 位置
SOURCE_SPACING = 0.06  # 相鄰 source cube 間距（避免碰撞）
SOURCE_ROW_LEN = 5     # source 每列幾顆，超過換下一列

END_EFFECTOR_OFFSET = np.array([0.0, 0.0, 0.02])  # suction 吸盤到 TCP 的偏移

# events_dt 語義：每步 phase 進度增量。**越小 = phase 越慢/越多步**（不是相反）
# 標準 Franka 是 [0.008, 0.005, 0.1, 0.1, 0.05, 0.05, 0.0025, 1, 0.008, 0.08]
# 我們微調：grip open (phase 6) 給非常多步，讓吸盤確實鬆開 + 方塊落地
PLACE_EVENTS_DT = [
    0.008,   # phase 0: 移動到 above pick
    0.005,   # phase 1: 下降到 pick（慢，精準）
    0.1,     # phase 2: 吸盤 close（10 步夠吸住）
    0.1,     # phase 3: 抬起
    0.05,    # phase 4: 移動到 above place
    0.005,   # phase 5: 下降到 place（很慢，減少衝擊）
    0.0025,  # phase 6: 吸盤 open（400 步，給充足時間鬆開 + 方塊落地）
    1.0,     # phase 7: 抬回（不需要慢）
    0.008,   # phase 8: 回 home
    0.08,
]

# release Z 補償：controller descend phase 結束時 EE 仍會在 place 上方一小段
# 把 placing_position.Z 往下推這個量，讓 cube 更接近地面才鬆開
# 注意：不能超過 cube 半高（2.5cm），否則會穿地被彈出
RELEASE_Z_COMPENSATION = -0.005  # 往下 5mm（cube 邊緣輕貼地面才鬆開）

# UR10 標準 home pose（folded upright）
UR_HOME = np.array([0.0, -1.5708, 1.5708, -1.5708, -1.5708, 0.0], dtype=np.float32)


def load_pattern(path: Path) -> dict:
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def source_position(index: int) -> np.ndarray:
    """回傳第 index 顆 source cube 的位置"""
    row = index // SOURCE_ROW_LEN
    col = index % SOURCE_ROW_LEN
    return SOURCE_ORIGIN + np.array([row * SOURCE_SPACING, col * SOURCE_SPACING, 0.0])


def build_source_cubes(world: World, n: int, size=0.05):
    """在 source area 產生 n 顆 cubes（初期給 UR10 抓）"""
    cubes = []
    for i in range(n):
        pos = source_position(i)
        # 用 pattern block 對應 index 的顏色，讓視覺上能看出對應關係
        cube = world.scene.add(DynamicCuboid(
            prim_path=f"/World/source_{i}",
            name=f"source_{i}",
            position=pos,
            scale=np.array([size, size, size], dtype=np.float32),
            color=np.array(COLOR_MAP["gray"], dtype=np.float32),
            mass=0.05,
        ))
        cubes.append(cube)
    return cubes


def print_progress(step, total, action, verbose=True):
    if verbose:
        print(f"[phase3]  step {step+1}/{total} — {action}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("pattern", type=str, help="pattern JSON file")
    ap.add_argument("--settle", type=float, default=None)
    ap.add_argument("--tol_mm", type=float, default=8.0)
    ap.add_argument("--keep_open", action="store_true", default=True,
                    help="keep viewer open after finish (default: on)")
    ap.add_argument("--max_steps_per_block", type=int, default=800,
                    help="safety cap on physics steps per pick-place (prevent infinite loop)")
    args = ap.parse_args()

    pattern_path = Path(args.pattern)
    if not pattern_path.exists():
        print(f"[ERROR] pattern file not found: {pattern_path}")
        simulation_app.close()
        sys.exit(1)

    pattern = load_pattern(pattern_path)
    settle = args.settle if args.settle is not None else pattern.get("settle_seconds", 3.0)
    blocks_spec = pattern["blocks"]
    n_blocks = len(blocks_spec)

    print(f"[phase3] Pattern: {pattern_path}")
    print(f"[phase3] Blocks to place: {n_blocks}")
    print(f"[phase3] Source area origin: {SOURCE_ORIGIN}, spacing={SOURCE_SPACING}m")

    # ===== 建 world + robot + source cubes =====
    world = World(stage_units_in_meters=1.0)
    ground = world.scene.add_default_ground_plane()

    # 提高地面摩擦係數，減少方塊落地打滑
    try:
        from isaacsim.core.api.materials import PhysicsMaterial
        from pxr import UsdShade
        pm = PhysicsMaterial(
            prim_path="/World/PhysicsMaterials/HighFriction",
            static_friction=1.5, dynamic_friction=1.2, restitution=0.0,
        )
        # 套到 ground 和之後所有方塊
        ground.apply_physics_material(pm)
    except Exception as e:
        print(f"[phase3] physics material apply skipped: {e}")

    ur10 = world.scene.add(UR10(
        prim_path="/World/UR10",
        name="ur10",
        position=np.array([0.0, 0.0, 0.0]),
        attach_gripper=True,
    ))

    source_cubes = build_source_cubes(world, n=n_blocks)

    world.reset()

    # ===== 主迴圈：逐顆 pick-place =====
    # 用一個 controller，每 block reset 一次（每個 controller 都有自己的 gripper 狀態機）
    ctrl = PickPlaceController(
        name="pick_place",
        gripper=ur10.gripper,
        robot_articulation=ur10,
        events_dt=PLACE_EVENTS_DT,
    )

    ur10.set_joint_positions(UR_HOME)
    for _ in range(10):
        world.step(render=False)

    for i, spec in enumerate(blocks_spec):
        source_pos = source_position(i)
        target_pos = np.array(spec["pos"], dtype=np.float32)

        picking = source_pos.copy()
        placing = target_pos.copy()
        placing[2] += RELEASE_Z_COMPENSATION  # 下降更低才鬆開

        print_progress(i, n_blocks, f"pick source_{i} @ {picking.round(3)} → place @ {placing.round(3)}")

        # 每 block 開始前 reset controller + 強制打開吸盤
        ctrl.reset()
        ur10.gripper.open()
        for _ in range(5):  # 讓 open 動作生效
            world.step(render=True)

        step_count = 0
        while not ctrl.is_done():
            world.step(render=True)
            step_count += 1
            if step_count > args.max_steps_per_block:
                print(f"[phase3]  WARN: block {i} exceeded {args.max_steps_per_block} steps, moving on")
                break

            actions = ctrl.forward(
                picking_position=picking,
                placing_position=placing,
                current_joint_positions=ur10.get_joint_positions(),
                end_effector_offset=END_EFFECTOR_OFFSET,
            )
            ur10.apply_action(actions)

        # 保險：block 結束後強制打開吸盤（避免下顆 pick 之前還黏著）
        ur10.gripper.open()
        for _ in range(30):
            world.step(render=True)

        # debug: 印當下吸盤狀態
        try:
            gr_closed = ur10.gripper.is_closed() if hasattr(ur10.gripper, "is_closed") else "?"
            print(f"[phase3]  block {i} done — gripper.is_closed={gr_closed}, steps={step_count}")
        except Exception:
            pass

    # ===== 全部擺完，跑穩定性驗證 =====
    print(f"[phase3] All blocks placed. Running settle physics for {settle}s ...")

    # 拿回 UR10 到 home 避免壓到 pattern
    ur10.set_joint_positions(UR_HOME)
    for _ in range(10):
        world.step(render=False)

    dt = world.get_physics_dt()
    for _ in range(int(settle / dt)):
        world.step(render=True)

    # 對比每顆 source cube 的最終位置 vs pattern 規劃位置
    tol = args.tol_mm / 1000.0
    results = []
    unstable = 0
    for i, cube in enumerate(source_cubes):
        planned = np.array(blocks_spec[i]["pos"], dtype=np.float32)
        final, _ = cube.get_world_pose()
        final = np.array(final)
        d = float(np.linalg.norm(final - planned))
        ok = d < tol
        if not ok:
            unstable += 1
        results.append({
            "index": i, "planned": planned.tolist(), "final": final.tolist(),
            "displacement_mm": round(d * 1000, 2), "stable": ok,
        })

    verdict = "STABLE" if unstable == 0 else "UNSTABLE"

    print()
    print("=" * 60)
    print("  PATTERN STABILITY REPORT (phase 3b)")
    print("=" * 60)
    print(f"  Verdict: {verdict}")
    print(f"  Stable:  {n_blocks - unstable}/{n_blocks}  (tolerance = {args.tol_mm}mm)")
    print("-" * 60)
    for r in results:
        mark = "OK " if r["stable"] else "!! "
        planned = ", ".join(f"{x:+.3f}" for x in r["planned"])
        final = ", ".join(f"{x:+.3f}" for x in r["final"])
        print(f"  {mark} cube_{r['index']:02d}  "
              f"planned=({planned})  final=({final})  "
              f"moved={r['displacement_mm']}mm")
    print("=" * 60)

    out_path = pattern_path.with_suffix(".phase3_report.json")
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump({
            "verdict": verdict, "stable": n_blocks - unstable, "unstable": unstable,
            "tolerance_mm": args.tol_mm, "blocks": results,
        }, f, indent=2, ensure_ascii=False)
    print(f"[phase3] Report saved: {out_path}")

    if args.keep_open:
        print("[phase3] Viewer open, Ctrl+C to quit.")
        try:
            while simulation_app.is_running():
                world.step(render=True)
        except KeyboardInterrupt:
            pass

    simulation_app.close()


if __name__ == "__main__":
    main()
