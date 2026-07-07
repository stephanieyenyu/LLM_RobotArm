**Step 1：開 Terminal 1 啟動感知服務**



cd "C:\\Users\\ASUS\\OneDrive\\桌面\\P\\LLM\_RobotArm\\csharp\_server"

yolo11\_env\\Scripts\\python.exe perception\_server.py



**Step 2：瀏覽器檢視即時相機畫面**



http://localhost:5000/debug/live 



**Step 3：開 Terminal 2 啟動 csharp\_server**



cd "C:\\Users\\ASUS\\OneDrive\\桌面\\P\\LLM\_RobotArm\\csharp\_server"

dotnet run



**Step 4：開 Unity Hub 啟動 Unity 專案**



打開 unity\_project，Editor 進入後點左上角 Play 按鈕。Unity Console 應該出現：

