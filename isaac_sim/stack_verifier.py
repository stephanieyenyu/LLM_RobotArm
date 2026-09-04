"""
stack_verifier.py — Isaac Sim 物理堆疊驗證

用途：
  給一個 3D pattern JSON（描述每顆方塊/domino 要放的位置與大小），
  在 Isaac Sim 裡按規劃放好，跑 3 秒物理，回報哪些方塊移動/掉了，
  判斷整個 pattern 是否物理可行。

用法：
  cd isaac_sim
  C:\\Users\\ASUS\\isaac_env\\Scripts\\python.exe stack_verifier.py patterns\\example_3layer.json

Pattern JSON 格式（單位：公尺）：
{
  "blocks": [
    {"pos": [x, y, z], "size": [dx, dy, dz], "color": "yellow", "mass": 0.05},
    ...
  ],
  "settle_seconds": 3.0
}

回報：STABLE / UNSTABLE + 每顆方塊的位移量
"""

import argparse
import json
import sys
from pathlib import Path

# 必須第一步啟動 Isaac Sim
from isaacsim import SimulationApp
simulation_app = SimulationApp({"headless": False})

# ---- 只有 SimulationApp 啟動後才能 import isaac 相關模組 ----
import numpy as np
from isaacsim.core.api import World
from isaacsim.core.api.objects import DynamicCuboid

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


def build_scene(world: World, pattern: dict):
    """把 JSON 描述的方塊放到 Isaac Sim scene 裡"""
    world.scene.add_default_ground_plane()

    blocks = []
    for i, spec in enumerate(pattern["blocks"]):
        pos = np.array(spec["pos"], dtype=np.float32)
        size = np.array(spec.get("size", [0.05, 0.05, 0.05]), dtype=np.float32)
        color = COLOR_MAP.get(spec.get("color", "gray"), COLOR_MAP["gray"])
        mass = float(spec.get("mass", 0.05))  # 預設 50g

        cube = world.scene.add(
            DynamicCuboid(
                prim_path=f"/World/block_{i}",
                name=f"block_{i}",
                position=pos,
                scale=size,
                color=np.array(color, dtype=np.float32),
                mass=mass,
            )
        )
        blocks.append((cube, pos.copy()))

    return blocks


def run_physics(world: World, seconds: float, render: bool = True):
    """跑指定秒數的物理"""
    dt = world.get_physics_dt()
    steps = int(seconds / dt)
    for _ in range(steps):
        world.step(render=render)


def report_stability(blocks, tolerance_m: float = 0.005) -> dict:
    """比較每顆方塊當前位置 vs 規劃位置，判斷是否穩定"""
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

    verdict = "STABLE" if unstable == 0 else "UNSTABLE"
    return {
        "verdict": verdict,
        "total": len(blocks),
        "stable": len(blocks) - unstable,
        "unstable": unstable,
        "tolerance_mm": tolerance_m * 1000,
        "blocks": results,
    }


def print_report(report: dict):
    print()
    print("=" * 60)
    print("  PATTERN STABILITY REPORT")
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
    ap.add_argument("--settle", type=float, default=None,
                    help="physics settle seconds (default: from JSON or 3.0)")
    ap.add_argument("--tol_mm", type=float, default=5.0,
                    help="tolerance in mm (default: 5mm)")
    ap.add_argument("--keep_open", action="store_true",
                    help="keep viewer open after report")
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
    blocks = build_scene(world, pattern)

    # reset 讓 physics 初始化
    world.reset()

    print(f"[stack_verifier] Running physics for {settle}s ...")
    run_physics(world, seconds=settle, render=True)

    report = report_stability(blocks, tolerance_m=args.tol_mm / 1000.0)
    print_report(report)

    # 存報告
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
