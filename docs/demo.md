# L demo：恢復原版實機移動

## 本次恢復範圍

實機運動引擎為 `legacy-controller-qnear-v2-io-feedback`。Unity 開始 batch 時會印出這個名稱、固定 travel Z 與 hover 高度。夾爪沿用舊版單行 DO4 指令，等新的 Masterboard 回報確認輸出狀態後，再等 1.5 秒；不以 isProgramRunning 判斷夾爪 I/O 完成。

- movej 恢復 `get_inverse_kin(..., qnear=get_actual_joint_positions())`，由 UR 控制器依當下關節與工具設定求解。
- 下降與抬升恢復原版 movel；Home 指令、速度、加速度與幾何等價角容許值沿用原版。
- 固定橫移 TCP 高度是 `QR1_Z + 0.22 = 0.25 m`，來源與目標 hover 是接觸高度上方 8 cm。
- 實機不呼叫 C# IK/FK、替代解搜尋、PlanPiece、額外 clearance／繞底座路徑；不再把 Home 的 0.6939 m 高度當成來源橫移高度。
- 改壞的自算 IK 與膠囊程式只保留作事故離線診斷，沒有被實機執行器呼叫。原先針對該程式加的 90° 啟發式不是這版控制器運動的驗證。
- 由替代 IK 實現的 hover look-ahead 暫停，避免為預演換掉原本姿態；不能宣稱這版有完整連桿／工具外型驗證。

## Demo 主流程

原本方式開啟 perception_server.py、dotnet run 與 Unity MainScene Play Mode，在 UI 輸入「排黃色 L」。只需要 OPENAI_API_KEY，不需要 Gemini key。

一次 `POST /demo/snapshot` 取得同一影格照片與物件座標 → 一次 CommandRouter LLM 任務分類 → 固定圖案與來源配對 → 一次傳送完整 batch → Unity 依序執行。

圖案固定 `100 / 100 / 111`，需要五顆同色 cube，不合併 domino。每個來源只用一次，遠端目標優先。來源不足或目標被占用會在送出 batch 前拒絕。

相對移動、兩顆疊放也使用同樣固定動作；3D 字形與多層塔不在範圍。PatternDesigner、SpatialPatternDesigner、MotionPlanner、Verifier 不由 demo 主流程建立或呼叫。

## 每顆 12 段 URScript

| # | command | 行為 |
|---|---|---|
| 1 | open | 開夾爪，沿用 1.5 秒等待 |
| 2 | source_travel | 原版 controller-qnear movej 至固定橫移高度 |
| 3 | source_hover | 原版 movel 至來源接觸高度上方 8 cm |
| 4 | pick_contact | movel 至接觸點上方 25 mm，再慢速接觸下降與扭矩補位 |
| 5 | grasp | 閉合夾爪，沿用 1.5 秒等待 |
| 6 | source_lift | 保持補位後的實際 XY 與方向，movel 抬至來源接觸高度上方 8 cm |
| 7 | target_travel | 原版 controller-qnear movej 至目標 XY、固定橫移高度 |
| 8 | target_hover | 原版 movel 至目標接觸高度上方 8 cm |
| 9 | target_descend | 原版 movel 至放置高度 |
| 10 | release | 開夾爪，沿用 1.5 秒等待 |
| 11 | target_lift | 原版 movel 抬至目標接觸高度上方 8 cm |
| 12 | go_home | 原版固定 Home movej |

12 段是 12 次 URScript 下發：動作為 program，開／夾／放為舊版單行 set_standard_digital_out。第 4 段內含有界補位迴圈；第 6 段從控制器讀取實際 pose 後只改 Z。移動確認執行與到位，I/O 則確認控制器 DO4 位元並等 1.5 秒，再發下一段。DO4 沒確認就中止，不會繼續抬升；DO4 確認僅代表電氣輸出，不是物理閉合或夾到物件的感測。沒有逐顆拍照、視覺重試或 LLM 動作規劃。

## 保留的 main 功能

參考 [main 4b422a8](https://github.com/stephanieyenyu/LLM_RobotArm/commit/4b422a80d7bb9c8d151320119e703f5406400014)。

- 接觸下降：最後 25 mm 以 10 mm/s 下移；Z 力超過 15 N 時停、抬高 30 mm，用 `r_x=-Ty/Fz, r_y=Tx/Fz` 判斷閉合方向上的碰觸側，向該側補 2 mm，最多 7 次。工具 X 方向直接從控制器當下 TCP pose 計算，不用 C# FK。補位失敗不進行 grasp。
- Pick radius：來源 base-frame 半徑 0.20–0.47 m，目標 0.16–0.47 m。
- Plan 格式：每顆以 `source index N -> target (x, y, z)` 開頭，再列固定 12 段；index 對應 initial_scene.json 的零起算位置。

保留此分支 QR1 `(-0.38824, -0.35473, 0.030) m`、格距 5.5 cm、目標左下中心 QR `(0.49, 0.00)`，以及接觸高度 `QR1_Z + block.Z + 0.020 m`。控制器直接使用原本校正好的 TCP，沒有 C# 額外假設軸向工具長度的 preflight。

## 執行狀態與限制

來源與目標邊界、初始影格新鮮度、UR 控制器狀態、TCP／Home 到位檢查仍保留。執行中依回饋監看 TCP 的 160 mm 基座排除半徑，進入時要求停止；這是回饋監看，不是預先證明整條路徑無自撞。

保停即中止整批、建立持續性鎖；不自動解除、不自動 Home、不重送。未知狀態或回報逾時會停止後端接受新任務。成功只代表動作執行完，沒有相機確認圖案正確。

本次修改沒有啟動實機。恢復原版動作不等於已完成新 batch、接觸補位與實際工具外型的實機驗證。120–180 秒仍是 demo 目標，需現場量測。

事故來源、使用者示教器角度與舊模型漏判保存在 `tests/incidents/`。上一次失敗 batch 已封存到 `outputs/legacy_restore_*/current_step.json`，StreamingAssets/current_step.json 已改為 done=true，需重新送新指令。

## 輸出與離線驗證

### 完成、初始化與中斷（新版）

- 服務啟動用 `control_event=startup_reset`，只顯示「初始化／清理舊任務」，不顯示完成。
- 最後一個 Home 仍需照原本條件確認到位及控制器程式結束；確認後立即保存完成紀錄、寫 step_done.json 及顯示完成，之後才通知 perception 回 idle，避免 HTTP 延遲擋住完成回報。
- 後端收到成功結果後才送 `batch_completed`；失敗送 `batch_failed`。舊版無事件類型的 done 只顯示「結束／重設，不能據此判定成功」。
- 每批在 `csharp_server/outputs/execution_receipts/batch_ID.json` 保存執行權、目前步驟與最終結果。相同 batch ID 無論完成、失敗或中斷都不重播；若原回報檔遺失，後端可直接讀已保存的結果。
- Unity 重載、停用或退出 Play 時，OnDisable 同步保存 `interrupted`，並回報最後一個動作與未知執行狀態，後端會結束等待並明確顯示中斷。若突然崩潰而來不及觸發 OnDisable，Unity 下次啟動會恢復紀錄並標中斷；完全沒有任何回報時仍以逾時判未知，不會冒充完成。
- 上一批仍標為 executing 時不派發新批次；重新啟動後，批次 ID 從持久化紀錄的最大值接續，避免撞到舊 ID。
- 後端會印目前動作；最後階段明確顯示「等待控制器確認 Home 到位」，而非沒有訊息地等待。執行中未調短到位條件或改動手臂軌跡。

每次任務存於 `csharp_server/outputs/demo_日期時間_batchId/`：initial.jpg、initial_scene.json、intent.json、batch.json、plan.txt、result.json。Unity 的 demo_urscript.log 記錄實際送出的指令，以及每次發送前的原始六軸角與 base-frame TCP。

```powershell
dotnet run --project tests/DemoChecks/DemoChecks.csproj
dotnet run --project tests/DemoChecks/DemoChecks.csproj -- --incident-only
dotnet build tests/UnityCompile/UnityCompile.csproj
python tests/test_demo_snapshot.py
```

預設測試驗證固定 L、五個來源與恢復後的控制器 URScript；incident-only 只重現已退役的數值規劃器事故案例，不代表目前路徑通過實體碰撞驗證。所有離線檢查都不連相機、LLM 或手臂。
