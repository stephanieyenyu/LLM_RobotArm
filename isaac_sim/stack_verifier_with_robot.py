"""
stack_verifier_with_robot.py — Phase 2：加入 UR10 機械手臂

差別於 stack_verifier.py：
  1. 場景加入 UR10（Isaac Sim 內建 asset，UR3e 之後再換 URDF）
  2. 手臂放在原點 (0, 0, 0)，pattern 放在手臂前方
  3. 目前手臂只是靜態站在那裡，物理驗證邏輯與 phase 1 相同
  4. Phase 3 會加動作執行

用法：
  cd isaac_sim
  C:\\Users\\ASUS\\isaac_env\\Scripts\\python.exe stack_verifier_with_robot.py patterns\\example_3layer.json --keep_open

工作區座標系（跟你 csharp_server 一致）：
  - 手臂基座 = 原點 (0, 0, 0)
  - X 軸：手臂前方
  - Y 軸：手臂左方
  - Z 軸：向上
  - Pattern 應該落在 X = 0.30~0.60, Y = -0.15~0.15 這個工作範圍
"""

import argparse
import json
import sys
from pathlib import Path

from isaacsim import SimulationApp
simulation_app = SimulationApp({"headless": False})

# ---- 只有 SimulationApp 啟動後才能 import isaac 相關模組 ----
import numpy as np
from isaacsim.core.api import World
from isaacsim.core.api.objects import DynamicCuboid
from isaacsim.core.api.robots import Robot
from isaacsim.core.utils.stage import add_reference_to_stage
from isaacsim.storage.native import get_assets_root_path

COLOR_MAP = {
    "red":    (0.90, 0.15, 0.15),
    "yellow": (1.00, 0.85, 0.10),
    "blue":   (0.15, 0.35, 0.90),
    "green":  (0.20, 0.75, 0.30),
    "gray":   (0.60, 0.60, 0.60),
    "white":  (0.95, 0.95, 0.95),
}


def load_pattern(path: Path) -> dict:
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def add_ur10(world: World, base_pos=(0.0, 0.0, 0.0)):
    """加入 UR10 機械手臂（Isaac Sim 內建 asset）"""
    assets_root = get_assets_root_path()
    if assets_root is None:
        print("[WARN] Isaac Sim asset root not found; robot skipped.")
        print("       Ensure Nucleus is configured or assets are downloaded.")
        return None

    ur10_usd = assets_root + "/Isaac/Robots/UniversalRobots/ur10/ur10.usd"
    prim_path = "/World/UR10"

    add_reference_to_stage(usd_path=ur10_usd, prim_path=prim_path)

    robot = world.scene.add(Robot(
        prim_path=prim_path,
        name="ur10",
        position=np.array(base_pos, dtype=np.float32),
    ))
    print(f"[stack_verifier] UR10 loaded at {base_pos}")
    return robot


# UR 家族標準 home pose（手臂折起立正，不會擋到工作區）
# joints: [base, shoulder, elbow, wrist_1, wrist_2, wrist_3]
UR_HOME_POSE = np.array([0.0, -1.5708, 1.5708, -1.5708, -1.5708, 0.0], dtype=np.float32)


def set_robot_home(robot):
    """把 UR10 折成 home 姿態（世界後坐好）"""
    if robot is None:
        return
    robot.set_joint_positions(UR_HOME_POSE)
    robot.set_joint_velocities(np.zeros_like(UR_HOME_POSE))
    print(f"[stack_verifier] UR10 set to home pose")


def build_blocks(world: World, pattern: dict):
    """把 pattern 的方塊放到 scene"""
    blocks = []
    for i, spec in enumerate(pattern["blocks"]):
        pos = np.array(spec["pos"], dtype=np.float32)
        size = np.array(spec.get("size", [0.05, 0.05, 0.05]), dtype=np.float32)
        color = COLOR_MAP.get(spec.get("color", "gray"), COLOR_MAP["gray"])
        mass = float(spec.get("mass", 0.05))

        cube = world.scene.add(DynamicCuboid(
            prim_path=f"/World/block_{i}",
            name=f"block_{i}",
            position=pos,
            scale=size,
            color=np.array(color, dtype=np.float32),
            mass=mass,
        ))
        blocks.append((cube, pos.copy()))
    return blocks


def run_physics(world: World, seconds: float):
    dt = world.get_physics_dt()
    for _ in range(int(seconds / dt)):
        world.step(render=True)


def report_stability(blocks, tolerance_m=0.005):
    results = []
    unstable = 0
    for i, (cube, planned) in enumerate(blocks):
        current, _ = cube.get_world_pose()
        current = np.array(current)
        displacement = float(np.linalg.norm(current - planned))
        is_stable = displacement < tolerance_m
        if not is_stable:
            unstable += 1
        results.append({
            "index": i,
            "planned": planned.tolist(),
            "final": current.tolist(),
            "displacement_mm": round(displacement * 1000, 2),
            "stable": is_stable,
        })
    return {
        "verdict": "STABLE" if unstable == 0 else "UNSTABLE",
        "total": len(blocks),
        "stable": len(blocks) - unstable,
        "unstable": unstable,
        "tolerance_mm": tolerance_m * 1000,
        "blocks": results,
    }


def print_report(report):
    print()
    print("=" * 60)
    print("  PATTERN STABILITY REPORT (with UR10)")
    print("=" * 60)
    print(f"  Verdict: {report['verdict']}")
    print(f"  Stable:  {report['stable']}/{report['total']}"
          f"  (tolerance = {report['tolerance_mm']}mm)")
    print("-" * 60)
    for b in report["blocks"]:
        mark = "OK " if b["stable"] else "!! "
        planned = ", ".join(f"{x:+.3f}" for x in b["planned"])
        final = ", ".join(f"{x:+.3f}" for x in b["final"])
        print(f"  {mark} block_{b['index']:02d}  "
              f"planned=({planned})  final=({final})  "
              f"moved={b['displacement_mm']}mm")
    print("=" * 60)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("pattern", type=str, help="pattern JSON file")
    ap.add_argument("--settle", type=float, default=None)
    ap.add_argument("--tol_mm", type=float, default=5.0)
    ap.add_argument("--keep_open", action="store_true")
    ap.add_argument("--no_robot", action="store_true", help="skip loading UR10")
    args = ap.parse_args()

    pattern_path = Path(args.pattern)
    if not pattern_path.exists():
        print(f"[ERROR] pattern file not found: {pattern_path}")
        simulation_app.close()
        sys.exit(1)

    pattern = load_pattern(pattern_path)
    settle = args.settle if args.settle is not None else pattern.get("settle_seconds", 3.0)

    print(f"[stack_verifier] Loading pattern: {pattern_path}")
    print(f"[stack_verifier] Blocks: {len(pattern['blocks'])}, settle: {settle}s")

    world = World(stage_units_in_meters=1.0)
    world.scene.add_default_ground_plane()

    robot = None
    if not args.no_robot:
        robot = add_ur10(world, base_pos=(0.0, 0.0, 0.0))

    blocks = build_blocks(world, pattern)

    world.reset()

    # reset 完 articulation 才存在，這時才能設 joint 姿態
    set_robot_home(robot)
    # 走幾步物理讓手臂進入 home
    for _ in range(10):
        world.step(render=False)

    print(f"[stack_verifier] Running physics for {settle}s ...")
    run_physics(world, seconds=settle)

    report = report_stability(blocks, tolerance_m=args.tol_mm / 1000.0)
    print_report(report)

    out_path = pattern_path.with_suffix(".report.json")
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2, ensure_ascii=False)
    print(f"[stack_verifier] Report saved: {out_path}")

    if args.keep_open:
        print("[stack_verifier] Keeping viewer open, Ctrl+C to quit ...")
        try:
            while simulation_app.is_running():
                world.step(render=True)
        except KeyboardInterrupt:
            pass

    simulation_app.close()


if __name__ == "__main__":
    main()
