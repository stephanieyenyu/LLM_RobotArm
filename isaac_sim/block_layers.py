"""
block_layers.py — 真實場景投影到 Isaac Sim 時，每塊積木的真實頂面高度（2.5 cm 層高的整數倍）。

跟 csharp_server/LayeredHeights.cs 的 SceneTops 是同一套規則（改一邊要改另一邊）：
依 perception z 由低到高處理，取「z 對齊層高（最少第 1 層）」與「佔地重疊的下層 + 1 層」兩者較高的。
perception 的 z 對黑色積木偏差很大（桌面上實測 −32～+3 mm），桌面上的積木靠第 1 層的下限對齊；
佔地重疊的兩塊不可能都在桌面上，一定排成上下層，模擬裡不會互相穿插。

純 Python、不 import Isaac，可單獨執行自我測試：python block_layers.py
"""
import math

LAYER_M = 0.025             # LayeredGraspGeometry.BlockLayerM
OVERLAP_MARGIN_M = 0.002    # LayeredHeights.OverlapMarginM：並排貼齊的積木不算重疊


def is_block(obj):
    """只有 HSV 偵測的積木（yellow_cube、black_domino …）有固定尺寸；杯子等 YOLO 物件不算。"""
    name = obj.get("name") or ""
    return obj.get("shape") in ("cube", "domino") and (name.endswith("_cube") or name.endswith("_domino"))


def half_extents(shape, orientation):
    half = LAYER_M / 2
    if shape != "domino":
        return half, half
    return (half, 2 * half) if orientation == "vertical" else (2 * half, half)


def snap_top(z, offset):
    """perception 頂面高度 → 對齊層高的真實頂面（最少第 1 層），同 LayeredGraspGeometry.SnapTopToLayer。"""
    return max(1.0, math.floor((z + offset) / LAYER_M + 0.5)) * LAYER_M


def overlaps(block, x, y, half_x, half_y):
    return (abs(block["x"] - x) < block["half_x"] + half_x - OVERLAP_MARGIN_M and
            abs(block["y"] - y) < block["half_y"] + half_y - OVERLAP_MARGIN_M)


def scene_tops(scene, offset):
    """{場景 index: 真實頂面高度}，只含積木。"""
    placed, tops = [], {}
    order = sorted((i for i, o in enumerate(scene) if is_block(o)), key=lambda i: (float(scene[i].get("z", 0.0)), i))
    for i in order:
        o = scene[i]
        x, y, z = float(o["x"]), float(o["y"]), float(o.get("z", 0.0))
        half_x, half_y = half_extents(o.get("shape"), o.get("orientation"))
        top = snap_top(z, offset)
        for below in placed:
            if overlaps(below, x, y, half_x, half_y):
                top = max(top, below["top"] + LAYER_M)
        placed.append({"x": x, "y": y, "half_x": half_x, "half_y": half_y, "top": top})
        tops[i] = top
    return tops


if __name__ == "__main__":
    # 跟 tests/ExperimentChecks 的 LayeredHeights 測試同一組數字
    def block(name, x, y, z, orientation=None):
        return {"name": name, "shape": "domino" if name.endswith("domino") else "cube",
                "x": x, "y": y, "z": z, "orientation": orientation}

    offset = 0.0075
    for z in (-0.0075, 0.0015, 0.013, 0.0179, 0.0277, 0.029):
        assert abs(snap_top(z, offset) - 0.025) < 1e-9, z
    for z in (0.038, 0.042, 0.047, 0.050, 0.0301):
        assert abs(snap_top(z, offset) - 0.050) < 1e-9, z
    assert abs(snap_top(0.0299, offset) - 0.025) < 1e-9 and abs(snap_top(0.0551, offset) - 0.075) < 1e-9
    last_run = [block("black_domino", 0.1679, 0.0342, 0.0173, "horizontal"), block("yellow_cube", 0.1513, 0.1194, 0.0223),
                {"name": "keyboard", "shape": "cube", "x": 0.41, "y": 0.10, "z": -0.0048}]
    assert scene_tops(last_run, offset) == {0: 0.025, 1: 0.025}
    stacked = scene_tops([block("yellow_cube", 0.30, 0.10, 0.022), block("black_domino", 0.305, 0.10, 0.018, "horizontal")], offset)
    assert abs(max(stacked.values()) - 0.050) < 1e-9 and abs(min(stacked.values()) - 0.025) < 1e-9
    beside = scene_tops([block("yellow_cube", 0.225, 0.20, 0.022), block("black_cube", 0.20, 0.20, 0.0015)], offset)
    assert beside == {0: 0.025, 1: 0.025}
    print("block_layers 自我測試通過")
