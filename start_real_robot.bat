@echo off
rem Switch the console to UTF-8 first, then run this file again: cmd misreads the rest of a batch file
rem when the code page changes halfway through it. Everything above the relaunch must stay ASCII.
if /i "%~1"=="__utf8" goto main
for /f "tokens=2 delims=:." %%a in ('"%SystemRoot%\System32\chcp.com"') do set "ORIGINAL_CP=%%a"
"%SystemRoot%\System32\chcp.com" 65001 >nul
"%ComSpec%" /c ""%~f0" __utf8"
if defined ORIGINAL_CP "%SystemRoot%\System32\chcp.com" %ORIGINAL_CP% >nul
exit /b

:main
setlocal EnableExtensions
title 實機啟動 - main
rem 用 Windows 自己的工具（PATH 裡有 Git 的 find、curl 時不會用錯）
set "SYS=%SystemRoot%\System32"

rem 一鍵啟動實機：開三個視窗（相機 perception_server、Isaac Sim、csharp_server），相機就緒後自動開 debug live。
rem 三個版本都用 main 資料夾的 perception_server（只有 main 有 yolo11_env 與 yolo11n.pt，介面三版相同）。
rem 已經在跑的相機或 Isaac 不會重開。

rem ===== 依你的電腦調整 =====
set "ISAAC_PY=D:\isaacsim\python.bat"
set "URSIM_IP=192.168.50.221"
set "WAIT_CAMERA_SEC=180"
rem ==========================

set "ROOT=%~dp0"
set "ROOT=%ROOT:~0,-1%"
for %%I in ("%ROOT%\csharp_server") do set "PERCEPTION_DIR=%%~fI"
set "RUN_MODE_FILE=%ROOT%\unity_project\Assets\StreamingAssets\run_mode.json"

echo ==================================================
echo  LLM_RobotArm 實機啟動：main
echo  程式資料夾：%ROOT%
echo  相機感知：%PERCEPTION_DIR%
echo ==================================================
echo.

if not exist "%PERCEPTION_DIR%\yolo11_env\Scripts\python.exe" goto no_camera_env
if not defined OPENAI_API_KEY echo [警告] 沒有設定 OPENAI_API_KEY，csharp_server 會無法呼叫 LLM。設定後要重新開這個檔案。
if not defined GEMINI_API_KEY echo [提醒] 沒有設定 GEMINI_API_KEY，需要 OpenAI＋Gemini 雙模型設計 bitmap 時會失敗。
if exist "%RUN_MODE_FILE%" "%SYS%\findstr.exe" /r /c:"\"mode\": *\"sim\"" "%RUN_MODE_FILE%" >nul && echo [注意] Unity 目前是「模式：純模擬」：Unity 按 Play 後到下方指令列按「模式」切回「模式：實機」，動作才會送實體手臂。

rem ---------- 1. 相機 perception_server ----------
"%SYS%\curl.exe" -s -f -o nul --max-time 2 http://127.0.0.1:5000/health
if not errorlevel 1 (
    echo [相機] perception_server 已經在跑，不再開新的。
) else (
    echo [相機] 開啟 perception_server...
    start "相機 perception_server" /d "%PERCEPTION_DIR%" "%ComSpec%" /k "yolo11_env\Scripts\python.exe perception_server.py"
)

rem ---------- 2. Isaac Sim（3D 疊放驗證用）----------
"%SYS%\curl.exe" -s -f -o nul --max-time 2 http://127.0.0.1:6000/status
if not errorlevel 1 (
    echo [Isaac] isaac_sim_server 已經在跑，不再開新的。
) else if not exist "%ISAAC_PY%" (
    echo [警告] 找不到 %ISAAC_PY%，不開 Isaac Sim：只能做 2D，3D 疊放需要 Isaac。
) else (
    echo [Isaac] 開啟 isaac_sim_server，第一次啟動約 1 分鐘。3D 疊放要先開 URSim 並切到 Remote Control。
    start "Isaac Sim" /d "%ROOT%" "%ComSpec%" /k ""%ISAAC_PY%" isaac_sim\isaac_sim_server.py --ursim_ip %URSIM_IP% --gui"
)

rem ---------- 等相機就緒，開 debug live ----------
echo [相機] 等 perception_server 就緒，最多 %WAIT_CAMERA_SEC% 秒...
set /a WAITED=0
:wait_camera
"%SYS%\curl.exe" -s -f -o nul --max-time 2 http://127.0.0.1:5000/health && goto camera_ready
if %WAITED% geq %WAIT_CAMERA_SEC% goto camera_timeout
"%SYS%\ping.exe" -n 3 127.0.0.1 >nul
set /a WAITED+=2
goto wait_camera

:camera_ready
echo [相機] perception_server 已就緒，開啟 debug live。
start "" "http://localhost:5000/debug/live"
goto start_csharp

:camera_timeout
echo [警告] %WAIT_CAMERA_SEC% 秒內 perception_server 沒有回應，請看「相機 perception_server」視窗的錯誤，例如 RealSense 有沒有接好。
echo        相機好了以後自己打開 http://localhost:5000/debug/live

rem ---------- 3. csharp_server ----------
:start_csharp
"%SYS%\tasklist.exe" /fi "imagename eq csharp_server.exe" 2>nul | "%SYS%\find.exe" /i "csharp_server.exe" >nul
if errorlevel 1 goto launch_csharp
echo [注意] 已經有 csharp_server 在跑，可能是別的版本。一次只跑一個版本，建議先關掉舊的 csharp_server 視窗。
"%SYS%\choice.exe" /c YN /m "還是要開 main 的 csharp_server 嗎"
if errorlevel 2 goto done

:launch_csharp
echo [csharp] 開啟 main 的 csharp_server...
start "csharp_server - main" /d "%ROOT%\csharp_server" "%ComSpec%" /k "dotnet run"

:done
echo.
echo 接下來：
echo   1. Unity Hub 打開 %ROOT%\unity_project，按 Play
echo   2. 下方指令列要顯示「模式：實機」，JsonExecutor 的 Ur IP 是 UR3e 的 IP
echo   3. 3D 疊放要先開 URSim 並切到 Remote Control
echo   debug live：http://localhost:5000/debug/live
echo.
pause
exit /b 0

:no_camera_env
echo [錯誤] 找不到 %PERCEPTION_DIR%\yolo11_env\Scripts\python.exe
echo        相機感知用 main 資料夾 LLM_RobotArm 的 Python 環境，請確認它跟這個資料夾放在同一層。
pause
exit /b 1
