import json
import os
import numpy as np
import cv2


# ============================================================
# Part B: QRCode 3D Coordinate Mapping
# ============================================================

INPUT_JSON = "../sample_json/detected_objects.json"
OUTPUT_JSON = "../sample_json/objects_world.json"


# QRCode 實際邊長，單位：meter
# 如果 QRCode 是 5 cm，就填 0.05
# 如果 QRCode 是 4 cm，就填 0.04
QR_SIZE_M = 0.073


# 物件高度，先假設物件在工作平面上方 3 cm
# （改為高度估計後，此值退為「最低夾取高度 / 估計失敗時的 fallback」）
OBJECT_HEIGHT_OFFSET_M = 0.013


# ============================================================
# 物件高度估計（bounding box scaling）
# 依 Notion comment 調整：RGBD camera 學長姐使用中無法取得，
# 改用 YOLO bbox 像素大小，搭配 QR code 已知實際邊長做比例換算。
# 參考：https://www.ultralytics.com/glossary/3d-object-detection
# ============================================================

# 是否啟用 bbox 高度估計；關閉時回到原本寫死的 OBJECT_HEIGHT_OFFSET_M
ESTIMATE_HEIGHT_FROM_BBOX = True

# 已知物件實際高度表（單位：公尺）。
# 有實測值的物件優先採用（比 bbox 估計準）；新增物件時把量好的高度填進來。
KNOWN_OBJECT_HEIGHTS_M = {
    "cup": 0.090,
    "bottle": 0.200,
    "book": 0.020,
    "cell phone": 0.008,
    "mouse": 0.035,
    "keyboard": 0.025,
    "laptop": 0.020,
}

# bbox 高度換算的校準係數：
# 鏡頭俯視時 bbox 像素高度會混入「深度佔地」成分，不等於純物件高度，
# 需用已知高度物件實測校正（校正方式：實際高度 / bbox 換算出的高度）。
HEIGHT_CALIBRATION = 0.5

# 估出高度的合理範圍（公尺），超出就 clamp，避免異常 bbox 讓手臂亂動
MIN_OBJECT_HEIGHT_M = 0.005
MAX_OBJECT_HEIGHT_M = 0.300

# 夾取點高度 = 物件高度 * 此比例（0.5 = 夾在物件半高處）
GRASP_HEIGHT_RATIO = 0.5


# ============================================================
# Camera parameters
# ============================================================

def build_camera_matrix(image_width, image_height):
    """
    目前先用近似相機內參。
    如果之後有做相機標定，可以把 fx, fy 換成真實值。
    """
    fx = 600.0
    fy = 600.0
    cx = image_width / 2.0
    cy = image_height / 2.0

    camera_matrix = np.array([
        [fx, 0.0, cx],
        [0.0, fy, cy],
        [0.0, 0.0, 1.0]
    ], dtype=np.float64)

    return camera_matrix


def build_dist_coeffs():
    """
    目前先假設沒有鏡頭畸變。
    """
    return np.zeros((5, 1), dtype=np.float64)


# ============================================================
# QRCode model points
# ============================================================

def get_qr_object_points(qr_size_m):
    """
    QRCode 實際 3D 角點。
    順序要對應影像角點：
    左上、右上、右下、左下
    """
    s = qr_size_m / 2.0

    object_points = np.array([
        [-s,  s, 0.0],   # top-left
        [ s,  s, 0.0],   # top-right
        [ s, -s, 0.0],   # bottom-right
        [-s, -s, 0.0]    # bottom-left
    ], dtype=np.float64)

    return object_points


# ============================================================
# Basic vector functions
# ============================================================

def normalize(v):
    norm = np.linalg.norm(v)

    if norm < 1e-9:
        raise ValueError("Cannot normalize zero-length vector.")

    return v / norm


def to_vec3_dict(v):
    return {
        "x": round(float(v[0]), 6),
        "y": round(float(v[1]), 6),
        "z": round(float(v[2]), 6)
    }


def get_qrcode_by_id(qrcodes, qr_id):
    for qr in qrcodes:
        if qr.get("id") == qr_id:
            return qr

    raise ValueError(f"Cannot find {qr_id} in qrcodes.")


def get_qr_corners(qr):
    """
    支援兩種欄位名稱：
    1. corners
    2. corners_pixel
    """
    corners = qr.get("corners", None)

    if corners is None:
        corners = qr.get("corners_pixel", None)

    if corners is None:
        raise ValueError(f"{qr.get('id')} missing corners or corners_pixel.")

    if len(corners) != 4:
        raise ValueError(f"{qr.get('id')} must have exactly 4 corners, but got {len(corners)}.")

    return np.array(corners, dtype=np.float64)


def get_qr_center_pixel(qr):
    """
    取得 QRCode 的中心像素座標。
    優先用 center_pixel 欄位，沒有的話用四個角點平均。
    """
    center = qr.get("center_pixel", None)

    if center is not None and len(center) >= 2:
        return np.array(center[:2], dtype=np.float64)

    corners = get_qr_corners(qr)
    return corners.mean(axis=0)


def compute_meters_per_pixel(qrcodes, frame):
    """
    用 QR1 → QR2 的像素距離對應工作平面實際寬度 width_m，
    求出影像在工作平面附近的「公尺 / 像素」比例，
    作為 bbox scaling 高度估計的比例尺。
    失敗時回傳 None（上層會 fallback 回固定 offset）。
    """
    try:
        c1 = get_qr_center_pixel(get_qrcode_by_id(qrcodes, "QR1"))
        c2 = get_qr_center_pixel(get_qrcode_by_id(qrcodes, "QR2"))

        pixel_dist = float(np.linalg.norm(c2 - c1))

        if pixel_dist < 1e-6:
            return None

        return frame["width_m"] / pixel_dist
    except Exception as e:
        print(f"compute_meters_per_pixel failed: {e}")
        return None


def estimate_object_height_m(name, bbox, meters_per_pixel):
    """
    估計物件實際高度（公尺）。回傳 (height_m, source)：
    1. known_table    ：物件在 KNOWN_OBJECT_HEIGHTS_M 表中，直接用實測值（最準）
    2. bbox_scaling   ：用 bbox 像素高度 * 比例尺 * 校準係數估計
    3. fallback_offset：無法估計時退回原本寫死的 OBJECT_HEIGHT_OFFSET_M
    """
    if name in KNOWN_OBJECT_HEIGHTS_M:
        return KNOWN_OBJECT_HEIGHTS_M[name], "known_table"

    if (not ESTIMATE_HEIGHT_FROM_BBOX
            or bbox is None
            or len(bbox) < 4
            or meters_per_pixel is None):
        return OBJECT_HEIGHT_OFFSET_M, "fallback_offset"

    bbox_height_px = abs(float(bbox[3]) - float(bbox[1]))

    if bbox_height_px < 1e-6:
        return OBJECT_HEIGHT_OFFSET_M, "fallback_offset"

    est = bbox_height_px * meters_per_pixel * HEIGHT_CALIBRATION
    est = max(MIN_OBJECT_HEIGHT_M, min(MAX_OBJECT_HEIGHT_M, est))

    return est, "bbox_scaling"


# ============================================================
# solvePnP: QRCode 2D corners -> QRCode 3D center
# ============================================================

def solve_qr_pose(qr, camera_matrix, dist_coeffs):
    image_points = get_qr_corners(qr)
    object_points = get_qr_object_points(QR_SIZE_M)

    success, rvec, tvec = cv2.solvePnP(
        object_points,
        image_points,
        camera_matrix,
        dist_coeffs,
        flags=cv2.SOLVEPNP_IPPE_SQUARE
    )

    if not success:
        raise RuntimeError(f"solvePnP failed for {qr.get('id')}.")

    # 因為 QRCode 的 3D model 原點在中心，所以 tvec 就是 QRCode 中心點
    center_camera = tvec.reshape(3)

    return center_camera


# ============================================================
# Build workspace frame
# ============================================================

def build_workspace_frame(p1, p2, p3):
    """
    QR1 = origin
    QR2 = -X direction
    QR3 = +Y direction
    Z = vertical height
    """

    origin = p1

    x_vec = p2 - p1
    z_raw = p3 - p1

    width_m = np.linalg.norm(x_vec)

    x_axis = normalize(x_vec)

    # 法向量 normal = x_axis × z_raw
    normal = normalize(np.cross(x_axis, z_raw))

    # 重新計算 z_axis，確保 x、z、normal 正交
    z_axis = normalize(np.cross(normal, x_axis))

    depth_m = np.dot(z_raw, z_axis)

    if depth_m < 0:
        z_axis = -z_axis
        normal = -normal
        depth_m = -depth_m

    center = origin + x_axis * (width_m / 2.0) + z_axis * (depth_m / 2.0)

    return {
        "origin": origin,
        "center": center,
        "x_axis": x_axis,
        "z_axis": z_axis,
        "normal": normal,
        "width_m": width_m,
        "depth_m": depth_m
    }


# ============================================================
# Pixel ray and plane intersection
# ============================================================

def pixel_to_camera_ray(pixel, camera_matrix):
    px, py = pixel

    pixel_h = np.array([px, py, 1.0], dtype=np.float64)
    ray = np.linalg.inv(camera_matrix) @ pixel_h
    ray = normalize(ray)

    return ray


def intersect_ray_with_plane(ray_origin, ray_dir, plane_point, plane_normal):
    denom = np.dot(ray_dir, plane_normal)

    if abs(denom) < 1e-9:
        raise ValueError("Ray is parallel to workspace plane.")

    t = np.dot(plane_point - ray_origin, plane_normal) / denom

    if t < 0:
        raise ValueError("Intersection is behind the camera.")

    point = ray_origin + t * ray_dir

    return point


def project_point_to_workspace(point_3d, frame):
    rel = point_3d - frame["origin"]

    local_x = np.dot(rel, frame["x_axis"])
    local_y = np.dot(rel, frame["normal"])
    local_z = np.dot(rel, frame["z_axis"])

    u = local_x / frame["width_m"] if frame["width_m"] > 1e-9 else 0.0
    v = local_z / frame["depth_m"] if frame["depth_m"] > 1e-9 else 0.0

    return local_x, local_y, local_z, u, v


# ============================================================
# Main
# ============================================================

def main():
    print("=== Part B: QRCode 3D Coordinate Mapping ===")

    if not os.path.exists(INPUT_JSON):
        raise FileNotFoundError(f"Cannot find input file: {INPUT_JSON}")

    with open(INPUT_JSON, "r", encoding="utf-8") as f:
        detection = json.load(f)

    image_width = detection.get("image_width")
    image_height = detection.get("image_height")
    objects = detection.get("objects", [])
    qrcodes = detection.get("qrcodes", [])

    if image_width is None or image_height is None:
        raise ValueError("image_width and image_height are required.")

    if len(qrcodes) < 3:
        raise ValueError("Need QR1, QR2, and QR3.")

    print(f"Image size: {image_width} x {image_height}")
    print(f"Objects: {len(objects)}")
    print(f"QRCodes: {len(qrcodes)}")

    camera_matrix = build_camera_matrix(image_width, image_height)
    dist_coeffs = build_dist_coeffs()

    print("Camera matrix:")
    print(camera_matrix)

    qr1 = get_qrcode_by_id(qrcodes, "QR1")
    qr2 = get_qrcode_by_id(qrcodes, "QR2")
    qr3 = get_qrcode_by_id(qrcodes, "QR3")

    p1 = solve_qr_pose(qr1, camera_matrix, dist_coeffs)
    p2 = solve_qr_pose(qr2, camera_matrix, dist_coeffs)
    p3 = solve_qr_pose(qr3, camera_matrix, dist_coeffs)

    print()
    print("QRCode centers in camera coordinate:")
    print("QR1:", p1)
    print("QR2:", p2)
    print("QR3:", p3)

    frame = build_workspace_frame(p1, p2, p3)

    print()
    print("Workspace frame:")
    print("Origin:", frame["origin"])
    print("Center:", frame["center"])
    print("X axis:", frame["x_axis"])
    print("Z axis:", frame["z_axis"])
    print("Normal:", frame["normal"])
    print("Width:", frame["width_m"])
    print("Depth:", frame["depth_m"])

    # bbox scaling 高度估計用的比例尺（公尺 / 像素）
    meters_per_pixel = compute_meters_per_pixel(qrcodes, frame)

    if meters_per_pixel is not None:
        print(f"Meters per pixel (near workspace plane): {meters_per_pixel:.6f}")
    else:
        print("Meters per pixel unavailable; height estimation falls back to fixed offset.")

    camera_origin = np.array([0.0, 0.0, 0.0], dtype=np.float64)

    converted_objects = []

    for obj in objects:
        name = obj.get("name", "unknown")
        confidence = obj.get("confidence", None)
        bbox = obj.get("bbox", None)
        center_pixel = obj.get("center_pixel", None)
        source = obj.get("source", None)

        if center_pixel is None:
            print(f"Skipping {name}: missing center_pixel.")
            continue

        try:
            ray_dir = pixel_to_camera_ray(center_pixel, camera_matrix)

            point_on_plane = intersect_ray_with_plane(
                camera_origin,
                ray_dir,
                frame["origin"],
                frame["normal"]
            )

            local_x, local_y, local_z, u, v = project_point_to_workspace(
                point_on_plane,
                frame
            )

            inside_workspace = bool((0.0 <= u <= 1.0) and (0.0 <= v <= 1.0))

            # 估計此物件的實際高度，取代原本寫死的 OBJECT_HEIGHT_OFFSET_M，
            # 讓機械手臂能在 Z 軸依物件高低做不同高度的移動
            object_height_m, height_source = estimate_object_height_m(
                name, bbox, meters_per_pixel
            )

            # 夾取點 z = 物件半高處，且不低於安全下限 OBJECT_HEIGHT_OFFSET_M
            grasp_z = max(object_height_m * GRASP_HEIGHT_RATIO, OBJECT_HEIGHT_OFFSET_M)

            # Part B output coordinate:
            # x = QR1 → QR2 direction
            # y = QR1 → QR3 direction
            # z = vertical height
            position = np.array([
                local_x,                       # x：QR1 → QR2 方向距離
                local_z,                       # y：QR1 → QR3 方向距離
                grasp_z                        # z：夾取高度（依物件高度估計）
            ], dtype=np.float64)

            converted_obj = {
                "name": name,
                "confidence": confidence,
                "source": source,
                "bbox": bbox,
                "center_pixel": center_pixel,
                "camera_point_on_plane": to_vec3_dict(point_on_plane),
                "workspace_local": {
                    "x": round(float(local_x), 6),
                    "y": round(float(local_y), 6),
                    "z": round(float(local_z), 6)
                },
                "workspace_ratio": {
                    "u": round(float(u), 6),
                    "v": round(float(v), 6)
                },
                "inside_workspace": bool(inside_workspace),
                "object_height_m": round(float(object_height_m), 4),
                "height_source": height_source,
                "position": to_vec3_dict(position)
            }

            converted_objects.append(converted_obj)

            print()
            print(f"Object: {name}")
            print(f"  center_pixel: {center_pixel}")
            print(f"  workspace_ratio: u={u:.4f}, v={v:.4f}")
            print(f"  inside_workspace: {inside_workspace}")
            print(f"  object_height_m: {object_height_m:.4f} ({height_source})")
            print(f"  position: {position}")

        except Exception as e:
            print(f"Failed to convert object {name}: {e}")

    output = {
        "workspace": {
            "type": "qr_3d_pose_mapping",
            "description": "3D workspace mapping using QRCode corners, solvePnP, plane normal, and ray-plane intersection.",
            "qr_size_m": QR_SIZE_M,
            "object_height_offset_m": OBJECT_HEIGHT_OFFSET_M,
            "height_estimation": {
                "enabled": ESTIMATE_HEIGHT_FROM_BBOX,
                "method": "bbox_scaling_with_known_table",
                "height_calibration": HEIGHT_CALIBRATION,
                "grasp_height_ratio": GRASP_HEIGHT_RATIO
            },
            "camera_matrix": camera_matrix.tolist(),
            "dist_coeffs": dist_coeffs.reshape(-1).tolist(),
            "origin_camera": to_vec3_dict(frame["origin"]),
            "center_camera": to_vec3_dict(frame["center"]),
            "x_axis_camera": to_vec3_dict(frame["x_axis"]),
            "z_axis_camera": to_vec3_dict(frame["z_axis"]),
            "normal_camera": to_vec3_dict(frame["normal"]),
            "width_m": round(float(frame["width_m"]), 6),
            "depth_m": round(float(frame["depth_m"]), 6),
            "qrcode_centers_camera": {
                "QR1": to_vec3_dict(p1),
                "QR2": to_vec3_dict(p2),
                "QR3": to_vec3_dict(p3)
            }
        },
        "objects": converted_objects
    }

    os.makedirs(os.path.dirname(OUTPUT_JSON), exist_ok=True)

    with open(OUTPUT_JSON, "w", encoding="utf-8") as f:
        json.dump(output, f, indent=2, ensure_ascii=False)

    print()
    print(f"Exported: {OUTPUT_JSON}")
    print("=== Part B finished ===")


if __name__ == "__main__":
    main()