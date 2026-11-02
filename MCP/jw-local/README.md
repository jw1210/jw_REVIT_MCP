# jw-local：JW 的本機修改

這個資料夾不是作者（shuotao）的，是 JW 在本機加的。作者的 repo 裡沒有，`git pull` 不會動到它。
計畫書：`JW agent/600_Plan/0001_計畫書/工具與程式/2026-10-26_用MCP操作自己的Revit外掛.md`

## 本機改過的地方

2026-11-02 起這些修改已 commit，推到 JW 的 fork `jw1210/jw_REVIT_MCP`（分支 main）。遠端：`origin`＝作者 `shuotao/REVIT_MCP_study`、`jw`＝JW 的 fork。

| 日期 | 檔案 | 改了什麼 |
| :-- | :-- | :-- |
| 2026-10-26 | `MCP/Application.cs` | 開 Revit 自動啟動 MCP 服務；拿掉啟動提示視窗；每個 Revit 從 8964 往上找空的 port（`PortSearchCount`=10、`ActivePort`），不寫回共用設定；按鈕提示顯示實際 port |
| 2026-10-26 | `MCP/Commands/MCPCommands.cs` | 啟動訊息與「MCP 設定」視窗顯示實際 port（原本寫死 8964） |
| 2026-10-26 | `MCP/Core/UsageRecorder.cs`（新） | JW 外掛使用紀錄的「錄音機」：外掛有動靜才錄、10 分鐘沒動靜結束；格式正本在 JW agent `500_Project/0000_tools/revit-usage-log/README.md` |
| 2026-10-26 | `MCP/Application.cs`（同上那份） | `OnStartup` 加 `UsageRecorder.Install`、`OnShutdown` 加 `UsageRecorder.Uninstall` |
| 2026-10-26 | `MCP/Core/Commands/CommandExecutor.Instance.cs`（新） | `get_instance_info`：回報版本、PID、port、開著的檔案（不註冊成 MCP 工具） |
| 2026-10-26 | `MCP-Server/src/socket.ts` | `getPort()`、`switchPort()`（連不上就退回原本的 port）、`probeRevitInstance()`（短連線問完就關） |
| 2026-10-26 | `MCP-Server/src/jw-instances.ts`（新） | `list_revit_instances`、`switch_revit_instance` 的實作 |
| 2026-10-26 | `MCP-Server/src/tools/instance-tools.ts`（新） | 上面兩個工具的定義 |
| 2026-10-26 | `MCP-Server/src/index.ts` | 上面兩個工具先攔下來處理，不走「先連 8964」那段 |
| 2026-10-26 | `MCP/jw-local/plugin-buttons.json`（新） | 代按按鈕的允許清單 |
| 2026-10-26 | `MCP/Core/Commands/CommandExecutor.PluginButtons.cs`（新） | `list_plugin_buttons`、`press_plugin_button` 的 Revit 端 |
| 2026-10-26 | `MCP/Core/CommandExecutor.cs` | 分派表加 `list_plugin_buttons`、`press_plugin_button`、`get_instance_info` 三個 `case` |
| 2026-10-26 | `MCP/RevitMCP.csproj` | 把 `plugin-buttons.json` 編進 DLL（EmbeddedResource） |
| 2026-10-26 | `MCP-Server/src/tools/plugin-button-tools.ts`（新） | 兩個工具的定義 |
| 2026-10-26 | `MCP-Server/src/tools/index.ts` | `pluginButtonTools`、`instanceTools` 註冊到 `full` profile |

看全部差異：在 repo 根目錄跑 `git status` 與 `git diff`。

## 改允許清單

1. 改 `plugin-buttons.json`（`tab`／`panel`／`button` 要跟外掛 ribbon 程式碼一字不差）
2. 重新編譯：`MCP` 底下 `dotnet build -c Release.R24 RevitMCP.csproj`（2025 用 `Release.R25`）
3. Revit 關掉，把 `MCP/bin/Release.R{YY}/*.dll` 複製到 `%APPDATA%\Autodesk\Revit\Addins\20{YY}\RevitMCP\`

清單是編進 DLL 的，只改 json 不重新編譯不會生效。

## 從作者那邊抓新版

修改已 commit，不用再 stash：`git pull origin main`（拉作者新版、合進 JW 的修改），合完 `git push jw main`。新檔案不會衝突；作者的檔案（`Application.cs`、`MCPCommands.cs`、`CommandExecutor.cs`、`RevitMCP.csproj`、`socket.ts`、`src/index.ts`、`tools/index.ts`）可能衝突，照上表手動合併回去。`socket.ts` 是作者的連線核心，他改重連邏輯時特別要小心合併。不要推標籤（`v*` 標籤會觸發作者的發布流程）。

## 作者 QA 會報的

作者的 `scripts/verify-qaqc.ps1` 會因為多了 4 個工具而報錯（工具數 239 跟文件寫的不符、`tools-index.html` 少卡片；`list_revit_instances`／`switch_revit_instance` 在 C# 分派表沒有 `case`，因為它們由 MCP Server 自己處理）。這是本機修改，不改作者的文件與檢查，所以不用理會這幾項。
