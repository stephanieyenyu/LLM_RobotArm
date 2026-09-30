# LLM_RobotArm

以中文自然語言指令控制 UR3e 機械手臂的框架。RealSense D435i 即時偵測工作台物件 → OpenAI gpt-5 解析指令 → Unity 送 URScript 到手臂。

目前採自由規劃與失敗反思實驗流程；每個任務以收到指令時的桌面為起點（設 `FIXED_BASELINE=1` 則要求每個任務先恢復同一個固定配置），任務內不重置，最多嘗試十次。操作方式、重置基準與評分限制見 [實驗協定](docs/experiment_protocol.md)。

## 系統流程

```
Unity UI（輸入指令）
   ↓  StreamingAssets/user_input.txt
csharp_server (dotnet)
   ↓  HTTP GET localhost:5000/scene
perception_server (Python + Flask)
   ├─ RealSense 常駐串流
   ├─ YOLO11n（COCO 物件） + HSV 立方體 + ArUco QR
   └─ 每 200ms 更新場景，回傳 3D 世界座標
   ↓
LLM 自由拆解子任務 → 自然語言操作計畫
   ↓  忠實轉譯（內部執行資料）
MotionPlanValidator → Unity 逐操作執行
   ↓
局部觀測檢查 + 獨立視覺模型整體驗證
   └─ 未達標：失敗摘要 → 自行生成規則 → 更新 prompt（最多 10 次）
   ↓  StreamingAssets/current_step.json（robot function sequence）
Unity JsonExecutor（高階 function → URScript）
   ↓  TCP 30002 URScript
UR3e
```

## 檔案總覽

**csharp_server/**
- `perception_server.py` — RealSense 常駐 + YOLO + HSV + QR 偵測 + Part B 3D 座標 + Flask HTTP（`/camera` 提供相機內參與位姿給 Isaac Sim）
- `IsaacSimExecutor.cs` — 疊放規劃先送 Isaac Sim 模擬，存模擬 / 疊合截圖
- `Program.cs` — 任務起點確認（目前桌面穩定，或固定配置比對）、任務內保留現況、十次嘗試與逐操作執行
- `ExperimentLlm.cs` — 自由拆解、自然語言規劃、轉譯、獨立結果驗證及反思
- `ExperimentChecks.cs` — 初始桌面一對一比對與來源身分檢查
- `ExperimentMetrics.cs` — 首次／十次內成功率及各次累積成功率
- `MotionPlanValidator.cs` — 執行前安全狀態機驗證
- `RobotPlan.cs` — plan / SceneObject 資料類別
- `models/pliers.pt`、`yolo11n.pt` — YOLO 權重
- `QRcode/aruco_1~4.png` — 可列印定位碼

**isaac_sim/**
- `isaac_sim_server.py` — Isaac Sim 常駐服務：真實場景 → 3D 數位孿生（積木、相機、手臂關節）+ UR3e 抓放模擬
- `sync_check.py` — 把目前真實場景投影到 Isaac Sim，輸出疊合圖檢查對位

**unity_project/Assets/Scripts/**
- `UIManager.cs` — 指令輸入 UI、監看計畫更新
- `JsonExecutor.cs` — 解譯 LLM robot function sequence、送 URScript
- `URPackageListener.cs` — UR TCP client（port 30002）
- `URUtil.cs`、`Util.cs` — 封包型別工具

## 前置

- .NET SDK 8+
- Python 3.10+（用 `csharp_server/yolo11_env` 這個 venv）
- Unity 2022.3 LTS
- Intel RealSense D435i（USB 3 直接接筆電）
- `setx OPENAI_API_KEY "sk-你的-key"` 後重開 PowerShell
- 可選：ROBOT_MODEL 指定本次實驗的 OpenAI 模型
- UR3e 或 URSim（Teach Pendant 切 Remote Control、TCP Z offset 設 0.170、速度滑桿 100%）
- 工作台貼四張 ArUco（QR1 左下、QR2 右下、QR3 左上、QR4 右上）

## 每次執行

**Terminal 1**（感知）：
```powershell
cd csharp_server
yolo11_env\Scripts\python.exe perception_server.py
```

**Terminal 2**（LLM planner）：
```powershell
cd csharp_server
dotnet run
```

**Unity**：Hub 開 `unity_project` → Play → Executor 的 `Ur IP` 填 UR3e IP。

**Debug**：瀏覽器 `http://localhost:5000/debug/live` 看即時偵測畫面。

## 3D 疊放驗證：URSim + Isaac Sim

- **2D 平面移動**：照舊，Unity 模擬預覽驗證後直接送實體手臂。
- **3D 疊放**（任一步的目標壓在另一塊積木上，或比來源高出半層）：
  1. csharp_server 把真實場景投影到 Isaac Sim（積木、相機、桌面與 QR1-4 範圍）。
  2. 整輪步驟以 `robot_target = "ursim"` 交給 Unity，Unity 用同一套關節軌跡只在 **URSim** 執行，實體手臂不動。
  3. Isaac Sim 的手臂即時跟隨 URSim（唯讀埠 30013 的關節角 + DO4 夾爪），積木用物理模擬被夾起、放下。
  4. URSim 跑完，Isaac 做幾何檢查（位置、層高、傾斜、撞動其他積木、穩定度、指尖是否低於桌面），
     再由 LLM 看模擬畫面；都通過才逐步送實體手臂。不通過就算這次嘗試失敗、進 Reflection。

Isaac Sim 不連實體手臂。URSim / Isaac 無法使用時記為 `infrastructure_error`（不計成功率）。

**3D 分層夾取（只有 3D 批次）**：3D 的每一批（URSim 與通過後的實機）帶 `layered_grasp`，每一步帶
`source_top_m` / `target_top_m`。descend 時指尖停在積木真實頂面下 19 mm（離下層 6 mm），取代 2D 用的
「感知頂面 + Z_CORRECTION」。真實頂面由 `csharp_server/LayeredHeights.cs` 依場景結構算：
- 佔地跟目標重疊的積木就是支撐，放置高度至少是最高支撐 + 1 層；同一批前面步驟放下的積木也算。
- 感知 z 對齊 2.5 cm 層高，只當作下限參考（黑色積木的感知 z 在桌面上實測偏差 −32～+3 mm，超過一層）。
- LLM 的 target.z 只能把放開高度往上調，不會讓夾爪壓進支撐。
- 目前不支援從疊好的積木中間抽出。

這時 Unity 的碰撞模型只在 3D 批次把手指段改成「指尖在桌面上方 3 mm」的檢查，前提是實體手指至少 30 mm 長。
2D 批次不帶這些欄位，送給 Unity 的 JSON 跟以前逐字相同，計算也完全相同。
層高與夾取深度的數字在 `unity_project/Assets/Scripts/LayeredGraspGeometry.cs`，Unity 與 csharp_server 共用；
Isaac 的投影用同規則的 `isaac_sim/block_layers.py`。

**Terminal 3**（Isaac Sim，第一次啟動約 1 分鐘）：
```powershell
D:\isaacsim\python.bat isaac_sim\isaac_sim_server.py --ursim_ip 192.168.50.221 --gui
```
Unity Inspector 的 JsonExecutor 多一個 `Ursim IP`（預設 192.168.50.221）。Isaac Sim 在別台電腦時，
csharp_server 那邊 `setx ISAAC_SIM_URL "http://<IP>:6000/"`。

必須跟 Unity 一致的參數：`--qr1`（X/Y = JsonExecutor.cs QR1_X/Y；Z = 3D 用的實測桌面高度
`QR1_Z + LayeredGraspGeometry.TableZCorrectionM`，預設 0.000）、`--gripper_do`（夾爪 DO 編號）、
`--ready_q`（Inspector 的 Ready Joints Rad；URSim 到 Ready 之後才開始記錄指尖最低點，之前 URSim 從上一次停留的
姿勢移過來的過程不算）、`--layer_snap_offset`（LayeredGraspGeometry.cs LayerSnapOffsetM）。
驗證報告的物件 z 維持 perception 慣例：每塊積木加回投影時自己的量測偏差，沒動過的積木回報值跟輸入相同。
`--fingertip_m`（預設 0.179）是實體夾爪法蘭面 → 指尖的實測距離（2026-09-29 實測 179 mm）；Isaac 啟動時量資產的
指尖位置，不同就把整支夾爪（手指 + 本體）沿工具軸移到這個長度。3D 批次在 Unity 也用同一個長度算關節角
（`LayeredGraspGeometry.FingertipLengthM`），URSim、Isaac 與實機的指尖才會一致；2D 照舊用 UR3 物件 RobotArm 的
`Tool Offset Z`（0.123）。換夾爪或手指時要重新量，兩邊一起改。
桌面高度同理：2026-09-29 實測同一組關節角下實機比模型高約 30 mm（法蘭與指尖一起高），代表桌面比 QR1_Z 低，
3D 批次改用 `QR1_Z + TableZCorrectionM`（-0.030）規劃，Unity 在 3D 批次預覽與執行期間把畫面上的桌面整組下移同樣距離，
Isaac 的 `--qr1` Z 也用這個值；2D 照舊用 QR1_Z。
UR 基座 → Isaac 世界在啟動時用 FK 自動校正（本資產實測差 180°）；`GET /status` 看是否跟上 URSim。

每次 3D 驗證的紀錄在 `attempt_XX/isaac_sim/`：`ursim_batch.json`、`ursim_execution.json`、
`isaac_verify.json`（每項檢查的結果）、`isaac_before.jpg`、`isaac_after.jpg`、`isaac_overlay_before.jpg`。

**對位檢查**（perception 與 Isaac Sim 都啟動後）：
```powershell
csharp_server\yolo11_env\Scripts\python.exe isaac_sim\sync_check.py
```
輸出 `isaac_sim/sync_check_output/overlay.jpg`（真實 / 模擬半透明疊合）。積木整片平移 → QR1 偏移不準；
旋轉或越遠越偏 → 相機位姿 / 內參。

## 指令範例

- 「排 H」
- 「把黃色方塊往前移 5 公分」
- 「把黃色方塊往左移 10 公分」
- 「把黑色方塊疊在黃色方塊上面」

任務拆解、來源選擇、布局、相對方向與疊放目標均由模型根據觀測規劃，程式不再預先計算解法。執行前驗證仍可拒絕不可達、碰撞或不安全的操作，拒絕結果會進入下一輪反思。

## 支援的物件

YOLO11n COCO 白名單：cup、cell phone、bottle、book、mouse、keyboard、laptop
HSV：5cm 黃色立方體、5cm 黑色立方體
QR：QR1-4（ArUco Dict4X4_50）

## 座標校準

`unity_project/Assets/Scripts/JsonExecutor.cs` 頂部三個常數：
```csharp
QR1_X, QR1_Y, QR1_Z   // Teach Pendant 手動 jog TCP 到 QR1 上方 5cm 讀值，Z 減 0.05 填入
Z_CORRECTION = 0.02f  // 2D：descend 時 TCP 停在感知頂面上方這個距離（3D 改用分層夾取，見上）
SAFE_Z_OFFSET = 0.08f // 抓取前後在物件上方留 8cm 安全空間
```
換場地或重貼 QRCode 一定要重新量測。

## 常見問題

- **「無法連線 perception_server」** → Terminal 1 沒起或還在載入 model
- **「場景中沒有帶有效座標的物件」** → QR1-3 沒都在鏡頭裡
- **等待 robot_plan.json 逾時（120 秒）** → OpenAI API 慢
- **手臂完全不動** → Teach Pendant 沒切 Remote Control、速度滑桿在 0、或 IP 錯

# Part A：YOLO 物件偵測與 QRCode 定位點輸出

Part A 的目標是讀取一張場景圖片，偵測其中的物件與 QRCode 定位點，並輸出 JSON 檔案給下一階段的座標轉換模組使用。

目前系統會讀取：

```text
csharp_server/images/test_scene.jpg
````

並輸出：

```text
csharp_server/outputs/detection_result.json
csharp_server/outputs/visual_result.jpg
```

---

## 目前功能

目前版本已完成以下功能：

1. 讀取 `images/test_scene.jpg`
2. 偵測 QRCode 定位點 `QR1`、`QR2`、`QR3`
3. 使用 YOLO ONNX 模型偵測常見物件
4. 輸出偵測結果到 `outputs/detection_result.json`
5. 輸出視覺化檢查圖到 `outputs/visual_result.jpg`

`detection_result.json` 會給 Part B 使用，Part B 可以從中取得 QRCode 和物件的影像座標。

`visual_result.jpg` 是除錯用圖片，用來確認 QRCode 和物件框是否正確畫出來。

---

## 測試圖片要求

測試圖片必須放在：

```text
csharp_server/images/test_scene.jpg
```

圖片中需要包含：

* `QR1`
* `QR2`
* `QR3`
* 至少一個 YOLO 可辨識的常見物件，例如 cup、bottle、book、cell phone、laptop、mouse、keyboard

QRCode 需要形成三角形，不能排成一直線。建議擺放方式如下：

```text
QR3

QR1                 QR2
```

目前設定中，建議：

* `QR1` 放左下
* `QR2` 放右下
* `QR3` 放左上

這樣 Part B 可以用三個 QRCode 建立工作平面與座標方向。

---

## 輸出格式

程式會輸出以下 JSON 格式：

```json
{
  "image_width": 1280,
  "image_height": 720,
  "objects": [
    {
      "name": "cup",
      "confidence": 0.823,
      "bbox": [779.42, 34.17, 1081.2, 328.82],
      "center_pixel": [930.31, 181.5],
      "source": "yolo_coco"
    }
  ],
  "qrcodes": [
    {
      "id": "QR1",
      "center_pixel": [310.5, 503.33],
      "corners": [[264, 596], [264, 457], [403.5, 457]]
    },
    {
      "id": "QR2",
      "center_pixel": [908.83, 503.67],
      "corners": [[862.5, 596.5], [862.5, 457.5], [1001.5, 457]]
    },
    {
      "id": "QR3",
      "center_pixel": [308.67, 162.83],
      "corners": [[260, 260.5], [260, 114], [406, 114]]
    }
  ]
}
```

欄位說明：

```text
image_width      圖片寬度
image_height     圖片高度

objects          YOLO 偵測到的物件清單
name             物件名稱
confidence       模型信心分數
bbox             物件框座標，格式為 [x1, y1, x2, y2]
center_pixel     物件中心點影像座標
source           偵測來源，目前為 yolo_coco

qrcodes          偵測到的 QRCode 清單
id               QRCode 內容，例如 QR1、QR2、QR3
center_pixel     QRCode 中心點影像座標
corners          QRCode 角點座標
```

Part B 目前主要可以使用：

```text
qrcodes[].id
qrcodes[].center_pixel
objects[].name
objects[].center_pixel
objects[].bbox
```

---

## YOLO 模型限制

目前使用的模型是：

```text
models/yolo11n.onnx
```

這是以 COCO 類別為基礎的 YOLO 預訓練模型。

COCO 是常見物件資料集，所以目前模型可以辨識一些日常物件，例如：

* person
* bottle
* cup
* book
* cell phone
* laptop
* mouse
* keyboard
* chair

目前模型不能真正辨識任意自訂物件，例如：

* red cube
* blue cube
* custom metal part
* robot component
* unknown tool

注意：不能只修改 `yolo_detector.cs` 裡面的 `classNames` 來新增物件類別。

`classNames` 只是把模型輸出的 class ID 轉換成可讀名稱。模型本身沒有訓練過的物件，單純改名稱不會讓模型真的學會辨識。

如果後續需要辨識自訂物件，需要新增以下其中一種方法：

1. 訓練 custom YOLO model
2. 加入 open-vocabulary detection，例如 OWL-ViT 或 Grounding DINO

目前 Part A 第一版先完成穩定的 QRCode 定位點輸出與 COCO 常見物件偵測。

---

## 如何執行

從 repo 根目錄進入 `csharp_server`：

```powershell
cd csharp_server
```

還原套件：

```powershell
dotnet restore
```

執行程式：

```powershell
dotnet run
```

執行後會產生：

```text
outputs/detection_result.json
outputs/visual_result.jpg
```

如果 `outputs` 資料夾不存在，程式會自動建立。

---

## 測試方式

執行後請檢查：

```text
outputs/detection_result.json
```

確認 JSON 中有：

* 至少一個 object
* `QR1`
* `QR2`
* `QR3`

也要打開：

```text
outputs/visual_result.jpg
```

確認圖片上有：

* QRCode 標記
* 物件綠色框
* 物件名稱，例如 cup

---

## 目前完成狀態

Part A 基本版已完成。

目前版本可以穩定輸出 QRCode 定位點與 YOLO 常見物件偵測結果，並已可交給 Part B 做座標轉換。

目前尚未支援任意自訂物件辨識。這部分會作為後續擴充。

````

更新後照這樣 commit：

```powershell
cd C:\Users\steph\source\repos\stephanieyenyu\LLM_RobotArm

git add csharp_server/README.md
git commit -m "Add Chinese README for Part A detection pipeline"
git push
````

如果你還沒有加 README 檔，就在 Visual Studio 右鍵 `csharp_server`，新增 `README.md`，再貼上這份。
