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
LLM 判斷平面或立體 → 目標 bitmap 設計（release 260917）：OpenAI、Gemini 各畫一張、互相審查、加權投票 80/20（每個任務都先畫；立體畫高度圖）
   ↓
LLM 自由拆解子任務 → 自然語言操作計畫（有目標 bitmap 時照它排）
   ↓  本地確定性轉譯（NaturalLanguagePlanAdapter，不呼叫 LLM）→ 內部執行資料
計畫的圖形要跟目標 bitmap 相同 + MotionPlanValidator + 積木重疊檢查
   ↓
模擬驗證（相機只在收到指令時拍一次，之後都看模擬畫面）
   ├─ 2D：Unity 整批模擬 → Unity 畫面重疊率 > 90% ＋ LLM 看 Unity 畫面判 PASS（不需要 Isaac Sim）
   ├─ 3D：Unity 只轉送 URSim、Isaac Sim 跟隨做物理 → 物理檢查 ＋ 畫面重疊率（整體對齊 ±5 mm）> 90% ＋ LLM 看 Isaac 畫面判 PASS
   └─ 未通過：失敗摘要 → 自行生成規則 → 更新 prompt（最多 10 次）
   ↓  通過才送：StreamingAssets/current_step.json（robot function sequence）
Unity JsonExecutor（高階 function → URScript）
   ↓  TCP 30002 URScript
UR3e（做完就算成功，執行後不再用相機驗證）
```

## 檔案總覽

**csharp_server/**
- `perception_server.py` — RealSense 常駐 + YOLO + HSV + QR 偵測 + Part B 3D 座標 + Flask HTTP（`/camera` 提供相機內參與位姿給 Isaac Sim）
- `IsaacSimExecutor.cs` — 疊放規劃先送 Isaac Sim 模擬，存模擬 / 疊合截圖
- `Program.cs` — 任務起點確認（目前桌面穩定，或固定配置比對）、任務內保留現況、十次嘗試與逐操作執行
- `ExperimentLlm.cs` — 自由拆解、自然語言規劃、獨立結果驗證及反思
- `NaturalLanguagePlanAdapter.cs` — 把規劃文字裡明確寫出的「source index N → target (x, y, z)」與「執行路徑開始／結束」之間的函式呼叫轉成內部執行資料（不呼叫 LLM；domino 方向沿用來源，白名單以外的函式會被略過）
- `FigureDimensionJudge.cs` — 每個任務畫目標之前，請 LLM 判斷指令要排平面（2D）還是立體（3D）；沒有關鍵字規則，也不分類動作
- `PatternDesigner.cs`、`BitmapParser.cs` — release 260917 的目標 bitmap 設計（prompt 原封不動）：平面的任務由 OpenAI 與 Gemini 各畫一張最多 5×5 的 bitmap、互相審查、加權投票 80/20；每次呼叫存在任務資料夾的 `design/`，結果存成 `design.json`
- `SpatialPatternDesigner.cs` — release 260917 的立體設計（prompt 是原文，生成的 prompt 最後多一行說明 column_heights 的列、欄方向）：畫正面一排 × 最多 3 欄 × 最高 3 層的高度圖（例如站起來的 L 是 `3 1 1`），pattern審查開著時跟平面一樣雙模型各畫一張、用 260917 的正面方向審查互審、80/20 投票
- `FigureBitmap.cs` — 把計畫放下的物件畫成 bitmap（跟相機畫面同方向，X、Y 格距分開；3D 疊放是俯視高度圖，例如站起來的 L 是 `3 1 1`），規劃轉譯完就印在 terminal 的 LLM 進度後面，也送給 Unity 當畫面比對的預期；排字母時每個字母最多 5×5 格，超過就在送出前退回
- `SimCheckImage.cs` — Unity 畫面比對圖（Unity 俯視畫面 / 預期 / 疊合）加上重疊率、PASS/FAIL 與圖例，3D 標出每處疊幾層，存進每一輪的資料夾
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
- 雙模型設計需要 `setx GEMINI_API_KEY "你的-key"`（可選 GEMINI_MODEL，預設 gemini-3.1-flash-lite）；沒設定時在 Unity 把「pattern審查」關掉，就只用 OpenAI 畫一次目標 bitmap
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

**Debug**：瀏覽器 `http://localhost:5000/debug/live` 看即時偵測畫面。方塊上的洋紅點是定位用的頂面中心（斜拍時整塊黃色包含側面，中心會偏向相機，perception 會扣掉這段偏移）。

## 模擬驗證：2D 用 Unity、3D 用 URSim + Isaac Sim

2026-10-08 起 main、zero-constraint、rulebased 三個版本用同一套驗證流程。相機（純模擬：虛擬場景）只在收到指令時讀一次
場景、照片與相機位姿，之後的驗證都看模擬畫面；手臂做完就算成功，執行後不再拍照驗證。手臂執行到一半失敗時桌面已經變了，
這個任務直接結束。給 LLM 驗證者的畫面附一句說明它看的是 Unity 或 Isaac 的模擬畫面（`ExperimentLlm.UnityImageNote` /
`IsaacImageNote`），驗證者的 system prompt 不變。

- **2D 平面移動**：所有步驟包成一批，Unity 先模擬整批（只預覽，手臂不動），預覽結束用正上方的正交相機只拍方塊，跟計畫畫出的 bitmap（每塊應有的佔地）比畫面重疊率（Σ min ÷ Σ max，2D 就是交集 ÷ 聯集，要大於 90%，`JsonExecutor.bitmapOverlapThreshold`；比對圖存成 `sim_check.png`）。通過後由獨立的結果驗證者（LLM）看 Unity 主相機的模擬畫面（`unity_view.png`）與預覽後的場景判 PASS，兩關都過才把同一批（`skip_preview`，不再預覽）送實體手臂。不需要 Isaac Sim；bitmap 與比對結果印在 csharp_server 的 terminal。
- **3D 疊放**（任一步的目標壓在另一塊積木上，或比來源高出半層）：
  1. csharp_server 把收到指令時的場景投影到 Isaac Sim（積木、相機、桌面與 QR1-4 範圍）；純模擬時 Isaac 本身就是世界。
  2. 整輪步驟以 `robot_target = "ursim"`、`skip_preview` 交給 Unity，Unity 不預覽，只用同一套關節軌跡轉送 **URSim** 執行，實體手臂不動。
  3. Isaac Sim 的手臂即時跟隨 URSim（唯讀埠 30013 的關節角 + DO4 夾爪），積木用物理模擬被夾起、放下。
  4. URSim 跑完，Isaac 做物理檢查（位置、層高、傾斜、撞動其他積木、穩定度、指尖是否低於桌面、手臂有沒有自撞），
     再比畫面重疊率：Isaac 量到的積木位姿依頂面高度換算層數，投影成 1 mm 一格的俯視高度圖，跟計畫的高度圖
     （含壓在底下沒被搬的支撐）比 Σ min ÷ Σ max，要大於 90%（比對圖 `isaac_overlap.png`）。先把整個圖形在 ±5 mm 內
     平移對齊再算：Isaac 的物理落點常整體偏約 2 mm，不對齊時放對也只有約 85%；對齊只容許整體平移，少一層、
     多一塊或轉 8° 仍不通過。最後由 LLM 看 Isaac 的模擬畫面判 PASS。
  5. 都通過才把同一批（同樣的步驟、來源位置與積木高度，`skip_preview`）整批送實體手臂，實機跑的就是 Isaac 驗證過的
     同一條關節軌跡。任何一關不通過就算這次嘗試失敗、進 Reflection，實機不動。步驟之間不重新觀測；逐步送會讓每一步
     結束都回 Ready，那些收尾路徑驗證時沒有，靠近手臂的目標位置常回不去。

**模擬與實機各動一次**（2026-10-01 起）：Unity 的手臂只在預覽時動；預覽還原後，URSim 或實體手臂執行期間，
Unity 的手臂與方塊都不動（以前手臂會跟著實機再動一次，夾爪同步還會把畫面上的方塊夾著走）。整批結束才一次對齊實機最後的姿勢，
方塊由感知或模擬世界刷新。2D：Unity 預覽一次、實體手臂一次；3D：URSim + Isaac 一次、實體手臂一次，Unity 都不預覽
（2026-10-08 起；之前 3D 也先在 Unity 預覽、逐層比對高度圖，送實機前再確認來源積木沒被移動超過 1 cm）。

**夾爪**：實體夾爪張開時兩指內側只有約 3.5 cm，每根手指厚、寬各約 1.5 cm（2026-09-30 實測），只跨得住 2.5 cm 的邊。
手指沿工具 X 開合：夾 cube 時在 ±X 兩側；domino 一律跨短邊夾，橫放（長邊沿 X）的夾爪轉 90°、直放的用 0°
（`JsonExecutor.EffectiveOrientation`）。張開時手指外側離夾爪中心 32.5 mm，垂直閉合方向佔中心線兩側各 7.5 mm：
閉合方向上，旁邊 2.5 cm 寬的積木中心離開不到約 45 mm 就會被手指碰到（規劃與反思的 prompt 有寫這些尺寸）。

Isaac 啟動時把資產手指換成實測大小的直方塊（`--finger_thickness_m` / `--finger_width_m`，預設各 0.015；
設 0 用資產原本的手指）：內側面整條放在資產指尖內側、下緣放在資產指尖，所以張開時內側 3.5 cm
（`--gripper_open_m`）與 flange → 指尖 179 mm 都不變。資產手指（跟 Unity 的夾爪模型相同）末端外張，PhysX 用的
是凸包：在指尖上方 19 mm 處內側間距只有 28.5 mm、外側離中心 38.5 mm，比實物更容易碰到旁邊的積木。
Unity 的夾爪模型沒有改，只用來顯示；Unity 的碰撞檢查只看手臂連桿對桌面與連桿之間，不看手指跟旁邊積木。

Isaac Sim 不連實體手臂。URSim / Isaac 無法使用時記為 `infrastructure_error`（不計成功率）。
URSim 驗證途中觸發安全停止時，Unity 最多等 300 秒讓人在 URSim 解除，逾時這一輪算失敗（`simulation`）並進 Reflection；
實體手臂的安全停止則無限期等待人工在 Teach Pendant 解除。

**3D 分層夾取（只有 3D 批次）**：3D 的每一批（URSim 與通過後的實機）帶 `layered_grasp`，每一步帶
`source_top_m` / `target_top_m`。descend 時指尖停在積木真實頂面下 12.5 mm（積木高度正中間；2026-10-10 之前是 19 mm，離下層只有 6 mm），取代 2D 用的
「感知頂面 + Z_CORRECTION」。真實頂面由 `csharp_server/LayeredHeights.cs` 依場景結構算：
- 佔地跟目標重疊的積木就是支撐，放置高度至少是最高支撐 + 1 層；同一批前面步驟放下的積木也算。
- 感知 z 對齊 2.5 cm 層高，只當作下限參考（黑色積木的感知 z 在桌面上實測偏差 −32～+3 mm，超過一層）。
- LLM 的 target.z 只能把放開高度往上調，不會讓夾爪壓進支撐。
- 目前不支援從疊好的積木中間抽出。

**3D 放置：碰到就停（可開關，預設關，2026-10-10 加，三版相同）**：JsonExecutor 的 `contactStopOnPlace` 打開時，實機在 3D 批次放方塊（手上有方塊、descend 到目標）的最後一段，先停在計算高度上方 `contactApproachM`（10 mm），再以 `contactSpeedMps`（10 mm/s）往下，Z 方向受力超過 `contactForceN`（8 N）就停下放開，最多降到計算高度。用來吸收「下層實際比計算高」的誤差，避免擠壓下層造成保護性停止或推倒。URSim、Isaac 驗證與 Unity 預覽照原本的軌跡；Unity Console 會印出每次比計算高度提早幾 mm 碰到（`[ContactPlace]`）。門檻要先在空中慢速下降量力的雜訊再設。

這時 Unity 的碰撞模型只在 3D 批次把手指段改成「指尖在桌面上方 3 mm」的檢查，前提是實體手指至少 30 mm 長。
**手臂自撞（2026-10-07 起，只有 3D）**：Unity 規劃 3D 批次時，自撞改用畫面上手臂模型（含夾爪）的 mesh 檢查
（`ArmMeshSelfCollision.cs`：底座與 6 個關節各帶動的一段各取凸包，相鄰兩段與手臂直立時就重疊的組合不檢查，
排除了哪些印在 Console）。原本的膠囊沿 DH 骨架走，沒算到上臂實際往側邊偏約 12 cm，疊到 5 層時夾爪貼著上臂下降會漏掉；
2D 照舊用膠囊。Isaac 驗證期間每一步也用 USD 碰撞體與 physics 的連桿位姿做凸包相交（GJK），一碰到就在 Isaac 的
console 印出哪兩段、當時的 URSim 關節角，驗證判不通過（`手臂沒有自撞`），實機不動；自撞檢查建不起來時 3D 驗證一律不通過。
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
`isaac_verify.json`（每項檢查的結果）、`isaac_before.jpg`、`isaac_after.jpg`、`isaac_overlay_before.jpg`、`isaac_overlap.png`（畫面重疊率比對圖）。

## 純模擬：不接相機與實體手臂

Unity 下方指令列的「模式」按鈕一鍵切換實機 / 純模擬（寫 `unity_project/Assets/StreamingAssets/run_mode.json`），
csharp_server 每個任務開始時讀一次，整個任務都用同一個模式。LLM 的 prompt 與整個規劃、驗證流程兩種模式完全相同。

| | 實機 | 純模擬 |
|---|---|---|
| 場景與照片 | perception_server（相機） | Isaac Sim 的 `/perception/*`（同格式）；沒開 Isaac 時 2D 用 csharp_server 內建的虛擬世界（俯視示意圖） |
| 初始桌面 | 收到指令時的真實桌面 | 虛擬場景檔 `sim_scenes/*.json`：切換的當下載入並顯示，每個任務開始時再重建 Isaac 世界 |
| 動作 | 實體 UR3e（2D 先在 Unity、3D 先在 URSim + Isaac 驗證） | 2D：Unity 模擬驗證（畫面重疊率＋LLM 看 Unity 畫面）通過就算執行（不連 URSim、不需要 Isaac），模擬世界照預覽落點更新；3D：Isaac 驗證通過後正式執行也送 URSim，Isaac 跟隨 URSim 讓積木依物理移動 |
| 紀錄 | `csharp_server/outputs/experiments/` | `csharp_server/outputs/experiments_sim/`（成功率另算） |

沒開 Isaac Sim 時，csharp_server 每個任務開始連不到 Isaac 就改用內建的虛擬世界（`VirtualSimWorld.cs`），只支援 2D：起點是場景檔，2D 整批通過 Unity 模擬驗證（畫面重疊率＋LLM 看 Unity 畫面）就把方塊移到預覽的落點（沒有物理，不會被推動或傾倒）；收到指令時給 LLM 的畫面是依座標畫的俯視示意圖（`TopViewRenderer.cs`，跟相機同方向）。世界寫在 `StreamingAssets/sim_world.json`，Unity 連不上 Isaac 時從這裡顯示積木、做預覽。遇到 3D 疊放會停下來並說明需要 Isaac（記為 infrastructure_error）。每個任務用哪一種記在 `sim_backend.txt`。

啟動（純模擬不用開 perception_server）：
1. URSim 虛擬機開機、切 Remote Control（3D 疊放才需要）。
2. `D:\isaacsim\python.bat isaac_sim\isaac_sim_server.py --ursim_ip 192.168.50.221 --gui`（3D 疊放才需要；2D 沒開 Isaac 時自動改用內建的虛擬世界）
3. `cd csharp_server` 後 `dotnet run`
4. Unity Play → 下方指令列的「模式」切成純模擬 →「場景」按鈕輪流切換 `sim_scenes/` 裡的檔案 → 輸入指令。

切到純模擬、按「場景」換檔，或在純模擬下重開 Unity 的當下，桌面都重置成場景檔：Unity 先畫出場景檔的積木，同時呼叫 Isaac 的 `/sim/load` 重建模擬世界（連不上 Isaac 時，作廢上次留下的內建虛擬世界 `sim_world.json`），
載入完改顯示 Isaac 回報的位置（物理落定後）；結果顯示在狀態列。Isaac 還沒開時畫面先顯示場景檔，等 Isaac 開好、
世界裡還沒有積木時自動載入。手臂執行中兩個按鈕都不給按；Isaac 正在 3D 驗證時也不重建（`if_idle`）。
但任務在 LLM 規劃階段時切換或換場景仍會重設模擬世界，任務進行中不要按。切回實機時畫面先清空，等相機的場景。

3D 疊放在純模擬裡照樣先驗證：驗證前 Isaac 記下積木位姿（`/sim/snapshot`），驗證跑完不論結果都放回去
（`/sim/restore`），正式執行從驗證前的狀態開始，跟實機模式「驗證不會動到真實積木」一致。
URSim（只有 3D 會用到）在純模擬途中安全停止時，Unity 最多等 300 秒，逾時這一輪算失敗。

虛擬場景檔格式（座標跟相機場景相同：QR 座標、公尺，z 是頂面高度，桌上一層 0.025、第二層 0.05）：
```json
{
  "description": "說明",
  "objects": [
    { "name": "yellow_cube", "shape": "cube", "x": 0.30, "y": 0.10, "z": 0.025, "orientation": null },
    { "name": "black_domino", "shape": "domino", "x": 0.20, "y": 0.10, "z": 0.025, "orientation": "horizontal" }
  ],
  "camera": null
}
```
- name 要是 `顏色_cube` 或 `顏色_domino`；domino 要給 orientation（horizontal = 長邊沿 X）。
- camera 省略時用 `sim_scenes/camera/default.json`（實機相機的內參與位姿，模擬畫面跟實拍同一個視角；
  目前是從 2026-09-30 實拍畫面的 QR1-4 估計的）。相機或 QR 貼紙移動過，接上相機後執行
  `python isaac_sim/save_sim_camera.py` 換成 RealSense 的真實參數。
- `run_mode.json` 的 `reset_each_task` 改成 false，之後的任務就接續目前的模擬世界，不重建。
- 內建兩個，三個版本（main、zero-constraint、rulebased）的檔案完全相同：`yellow_cubes_15`（預設；15 塊黃方塊排成 5 欄 × 3 排，中心間距 6 cm，X 0.08～0.32、Y 0.06～0.18）、`domino_cube`（一塊橫放的黃 domino 在 (0.20, 0.10)、一塊黃方塊在 (0.12, 0.18)）。`run_mode.json` 指定的場景檔不存在時改用預設場景（2026-10-08 起；之前是 `two_cubes`、`letter_blocks`、`domino_cubes`）。

限制：模擬回報的是 Isaac 的精確位置，沒有相機的偵測誤差、遮擋與高度偏差，結果會比實機樂觀；
適合測 LLM 的規劃能力，不能取代實機成功率。

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
換場地或重貼 QRCode 一定要重新量測。QR1_X/Y 改了之後，`isaac_sim/isaac_sim_server.py` 的 `--qr1` 預設、
`isaac_sim/touch_calibrate.py` 的 QR1_X/Y，以及 MainScene 裡 SceneSyncer 的 `Arm Base At Qr X/Y`（= -QR1_X / -QR1_Y，
只影響 Unity 畫面上工作區的位置）要一起改。

用實體指尖量 QR 座標系的偏移與轉角（只讀手臂關節角，不送指令；手臂用 Teach Pendant 低速移動）：
```powershell
python isaac_sim\touch_calibrate.py --robot_ip 192.168.50.204
```
依序用指尖輕碰 QR 標記中心與黃色方塊頂面，輸出 QR1 在基座座標的位置、QR 座標軸的轉角與桌面高度。

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
