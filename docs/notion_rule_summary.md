# 目前版本：人為條件限制 Rules 整理

## 目的

這份整理描述目前系統中「人工設定、用來限制 LLM 或機械手臂行為」的規則。重點是讓 LLM 只能在安全、可驗證、可執行的範圍內產生圖案與動作；真正送到 Unity / UR3e 前，會再經過 deterministic validation，不只靠 LLM 自覺遵守。

## 整體防線

1. **LLM 不直接控制機械手臂**
   - LLM 不可輸出任意 URScript。
   - LLM 不可輸出任意世界座標、joint angles、速度、加速度或 I/O 指令。
   - LLM 只能用白名單內的高階 robot functions 組合動作。

2. **所有 LLM 輸出都要經過格式與安全驗證**
   - 圖案 bitmap 會先檢查尺寸、字元、庫存。
   - motion plan 會先檢查 function 白名單、參數範圍、動作順序、避障高度。
   - 驗證失敗就不寫入 Unity 執行檔案，會要求重新規劃或停止。

3. **Unity 端仍有獨立硬體安全檢查**
   - 即使關閉實驗用的 bitmap 模擬比對，Unity 的動作規劃檢查、軌跡奇點 / 碰撞檢查、底座安全範圍、最大伸展範圍與 protective stop 處理仍會生效。

## Layer 1：圖案生成限制

### 2D bitmap

- bitmap 不可以是 null 或空陣列。
- 每一列不可以是空字串。
- 每一列長度必須相同。
- 每個字元只能是 `0` 或 `1`。
  - `0` = 空白
  - `1` = 需要放置積木
- parser 的硬上限是 20 rows × 20 columns。
- 實際目前工作區設定為最多 5 rows × 5 columns。
- occupied cells 不可以是 0。
- 需要的格子數不能超過可用庫存：
  - cube 覆蓋 1 格。
  - domino 覆蓋 2 個正交相鄰格。
- 如果圖案在目前庫存、尺寸、可辨識度下不可行，LLM 應回傳 infeasible，而不是硬湊一個錯的圖案。

### 雙模型審查

- OpenAI 與 Gemini 各自生成候選 bitmap。
- 兩個模型互相審查對方候選圖。
- 候選必須同時通過：
  - deterministic format / inventory check
  - reviewer accept
  - reviewer recognizable
- 多個候選通過時，使用匿名投票分數選擇：
  - OpenAI 權重 0.80
  - Gemini 權重 0.20
- 最多審查 2 輪。
- Unity UI 可關閉 pattern 審查；關閉時只呼叫 OpenAI 生成一次，但仍會做本地格式與庫存檢查。

### 3D voxel glyph

- 固定體積：
  - rows = 1
  - cols = 3
  - layers = 3
- 每個 cell 用 column height 表示，高度必須是 0..3。
- 每個 column 必須從桌面往上連續堆疊，不允許空中懸浮、洞或 overhang。
- 總 cube 數不能超過可用 cube budget。
- 方向固定：
  - 從正面閱讀
  - +Z 朝上
  - 底邊接觸桌面
  - 不可旋轉
  - 不可鏡射
- 若在支撐、尺寸、高度與庫存限制下無法保持可辨識，必須判定不可行。

## Layer 2：目標座標與工作區限制

### 工作區分區

- supply zone：
  - X = 0.05..0.35 m
  - Y = 0.02..0.25 m
- target zone：
  - X 必須大於等於 0.35 m
  - target placement 額外限制：
    - X ≤ 0.74 m
    - Y = 0.00..0.45 m
- 2D 圖案最多 5 × 5。
- cell size = 0.052 m。
- default block Z = 0.025 m。
- target 右下角固定：
  - TargetRightX = 0.728 m
  - TargetBottomY = 0.02 m

### UR 可達半徑限制

- target 位置換算到 UR base 後，半徑必須在 0.18..0.48 m。
- 如果固定右下角的圖案超出擺放區或 UR 可達半徑，不會自動平移，也不會送出實體手臂。

### domino packing

- 先嘗試橫向 domino。
- 再嘗試縱向 domino。
- 剩下的 `1` 才用 cube。
- cube / domino 數量不足時直接失敗。

## Layer 3：供料選擇限制

- 一般 pattern 執行時，只能從 supply zone 取料。
- source 必須符合目標需要的顏色與形狀。
- source 必須在 UR 安全可達範圍內：
  - source 半徑 = 0.10..0.47 m
- target 排序採遠端優先：
  - Y 較大優先
  - Y 相同時 X 較大優先
  - 目的是降低後續手臂跨過已完成區域的機率。
- 同色同形候選 source 中，選距離 target 最近者。
- recovery mode 才允許從非 supply zone 回收同色同形物件。
- recovery 不可移走已驗證成功的 protected target。
- 若有 failed source，只有在候選數量多於剩餘需求時才避開，避免誤判成供料不足。
- RealSense 可能低估頂面高度，因此實際 command Z 會取 `max(measured source Z, target Z)`。

## Layer 4：LLM motion planner 限制

### 允許的 high-level functions

LLM 只能輸出以下 function：

- `move_above(location, height_m)`
- `descend(location)`
- `grasp()`
- `release()`
- `lift(location, height_m)`
- `wait(seconds)`
- `go_home()`

### function 參數限制

- location 只能是：
  - `source`
  - `target`
- `height_m` 必須在 0.05..0.15 m。
- `wait(seconds)` 必須在 0.1..3.0 秒。
- 如果 LLM 給了 `wait` 但沒給秒數，系統補預設 0.1 秒。
- 每個 action sequence 最多 20 個 function calls。
- `go_home()` 只在明確需要時使用；batch 中每一步預設不回 Home。

### motion plan 安全順序

動作順序必須符合：

1. 移動到 source 上方。
2. 垂直下降到 source。
3. 夾取。
4. 在 source 垂直抬升。
5. 只在安全高度移動到 target 上方。
6. 垂直下降到 target。
7. 釋放。
8. 在 target 垂直抬升。
9. 可選擇回 Home，但 batch 預設不做。

禁止事項：

- 禁止在 tool 已下降時做 lateral move。
- 禁止還沒夾取就移往 target。
- 禁止還拿著物件就結束 plan。
- 必須 release 並 retreat 到 target 上方後才算安全結束。

### motion plan 空間限制

- source / target 必須在 QR workspace：
  - X = 0.00..0.72 m
  - Y = 0.00..0.45 m
  - 允許 0.025 m 量測容差。
- 單次 source 到 target 的 transfer distance 不可超過 0.70 m。
- travel clearance 至少 0.08 m。
- 如果場景中有更高物件，會要求：
  - 最高障礙物高度 + 0.03 m clearance
  - 並 clamp 在 0.08..0.15 m。
- target 若已被佔用，只有在 stack 且 target Z 高於下方物件頂面時才允許。
- target occupancy 半徑 = 0.020 m。
- source identity 排除半徑 = 0.030 m，避免把原本 source 誤當成 target 障礙物。
- stack 高度量測容差 = 0.012 m。

## Single Object 指令限制

### move relative

- direction 只能是：
  - left
  - right
  - forward
  - backward
- 預設移動距離 = 5 cm。
- distance_cm 必須在 1..30 cm。
- direction 對應 QR frame：
  - left = +X
  - right = -X
  - forward = -Y
  - backward = +Y
- target 必須在 QR workspace：
  - X = 0.00..0.65 m
  - Y = 0.00..0.45 m。

### stack

- source 量測高度必須在 0.005..0.100 m。
- reference top Z 必須有效且大於 0.005 m。
- stack target Z = reference top Z + source height + 0.008 m release clearance。
- 多層堆疊時，tower XY 固定在原始塔軸；新的 perception 只用來確認塔仍存在與更新可見頂面。
- 找 tower top 的半徑 = 0.045 m。
- 若找不到 tower top，停止並要求重新取得場景。

## Unity Executor / 硬體安全限制

- QR1 校正座標目前設定：
  - QR1_X = -0.39337 m
  - QR1_Y = -0.35247 m
  - QR1_Z = 0.030 m
- 基本安全抬升 offset = 0.08 m。
- workspace 上方 travel Z = 0.24 m。
- source base exclusion radius = 0.10 m。
- target base exclusion radius = 0.16 m。
- source 最大可達半徑 = 0.47 m。
- target 最大可達半徑 = 0.48 m。
- 需要繞過底座時 detour radius = 0.28 m。
- detour arc 每段最大角度 = 25 degrees。
- TCP position tolerance = 0.012 m。
- home joint tolerance = 0.04 rad。
- motion timeout = 180 s。
- safety recovery timeout = 300 s。
- safety stable time = 1 s。
- manual home retry 最多 1 次。
- 執行前會掃整個 batch：
  - 任一步 source / target 不可達就 abort。
  - 任一步奇點或 joint transition 不安全就 abort。
  - abort 時不送實體手臂動作。
- 執行中若 UR 進入 safety stop / protective stop：
  - 停止目前 batch。
  - 等待人工排除安全狀態。
  - 復原成功後才允許繼續收尾或回 Home。

## Unity 驗證開關限制

- `Unity驗證：開`：
  - 模擬結束 bitmap 比對不通過，就不送實體手臂。
- `Unity驗證：關`：
  - bitmap 比對只記錄，不阻擋。
- 這個開關只控制「模擬結束 bitmap 比對」。
- 以下安全檢查不受開關影響：
  - motion plan validation
  - pre-flight kinematics
  - singularity / collision check
  - base exclusion radius
  - max reach radius
  - protective stop / safety stop handling

## Layer 5：結果驗證限制

### pattern step 驗證

- target 位置 tolerance = 0.020 m。
- source 是否還在原地的 match 半徑 = 0.030 m。
- target 上的物件必須符合：
  - shape 正確
  - color 正確
  - 位置誤差 ≤ 15 mm 才算 ok
- 狀態分類：
  - source 沒消失且 target 沒東西：retry
  - source 消失但 target 沒東西：retry
  - shape 錯：abort
  - color 錯：abort
  - 位置偏移太大：retry
  - 狀況不明：replan

### stack 驗證

- stack XY tolerance = 0.040 m。
- stack height tolerance = 0.015 m。
- tower search radius = 0.080 m。
- frame-to-frame tracking radius = 0.060 m。
- 最小高度增加量 = 0.008 m。
- 若下方物件被遮蔽，可用以下 evidence 輔助判定：
  - top surface 高度符合
  - tower frame-to-frame 高度增加
  - source 消失且固定塔軸上出現同色 merged domino silhouette

## 簡短總結

目前版本的核心策略是：LLM 只負責「提出可讀圖案」與「組合高階動作」，但不直接控制座標與 URScript。所有可能影響安全的部分都被限制在固定工作區、固定 function API、固定高度範圍、固定動作順序、固定可達半徑與 Unity 端 pre-flight 檢查內。實驗用的驗證開關只影響 bitmap 模擬比對，不會關閉硬體安全防線。
