# LLM Motion Planner（Layer 4A）

每個 `TaskAssigner` assignment 不再由 Unity 固定展開成 12 步，而是先交給 `MotionPlanner`，由 LLM 使用下列高階 robot functions 組合動作：

- `move_above(location, height_m)`
- `descend(location)`
- `grasp()`
- `release()`
- `lift(location, height_m)`
- `wait(seconds)`
- `go_home()`

LLM 不可直接輸出任意 URScript、任意世界座標、速度、加速度或 I/O 指令。
`action_sequence` 產生後先經 `MotionPlanValidator` 做白名單與參數範圍驗證，通過後才寫入 `current_step.json`。Unity `JsonExecutor` 只解讀上述 function，並使用既有的座標轉換與 URScript 安全實作。

若規劃呼叫失敗、回傳非 JSON 或驗證失敗，系統最多要求 LLM 修正三次，之後停止該任務。`Verifier` 回傳 `retry` 或 `replan` 時，其錯誤說明會在下一輪提供給 Motion Planner。

## 資料流程

`TaskAssigner → MotionPlanner → MotionPlanValidator → current_step.json → Unity Executor → UR3e → Verifier`

## 安全限制

- 每個計畫最多 20 個 function calls。
- 安全高度限制在 0.05～0.15 m。
- 動作順序必須符合：接近來源、下降、夾取、抬升、接近目標、下降、釋放、抬升、回 Home。
- 所有位置參數只允許 `source` 或 `target`。
- Unity 不接受 JSON 內的任意 URScript 或世界座標。
