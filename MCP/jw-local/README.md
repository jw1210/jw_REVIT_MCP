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
| 2026-11-19 | `MCP/Core/SocketService.cs`、`MCP/Application.cs` | 修兩個 Revit 同時開時搶同一個 port：後登錄的跳「與電腦上的現有登錄衝突」（HttpListener 錯誤 183），或被誤判成 HTTP.sys 殘留去跑 `net stop http`。`ConflictMeansNextPort`＋`PortConflictException`：撞到就換下一個 port，不殺程式、不動 HTTP.sys、不彈框 |
| 2026-11-19 | `MCP/Core/Commands/CommandExecutor.StairAnnotations.cs`（新）、`MCP/Core/CommandExecutor.cs`、`MCP-Server/src/tools/stair-annotation-tools.ts`（新）、`MCP-Server/src/tools/index.ts` | 樓梯剖面的尺寸標註與踏板編號照另一張圖重做（B3F → B2F）：`get_view_dimension_references`、`get_linked_stairs`、`get_element_faces_in_view`（只讀）、`create_dimensions_from_references`、`create_stair_tread_numbers`。座標用視圖座標 u/v（mm）。尺寸標註黏在連結模型的樓梯面上，不能直接複製（會量回原本那座樓梯），所以是讀原標註的參考 → 找新樓梯對應的面 → 用新參考建。實測：參考 Level 的尺寸標註在剖面上建得出來但不顯示，`create_dimensions_from_references` 建完會檢查並回報 `visibleInView` |
| 2026-11-20 | `MCP/Core/Commands/CommandExecutor.SheetCreate.cs`（新）、`MCP/Core/CommandExecutor.cs`、`MCP-Server/src/tools/sheet-create-tools.ts`（新）、`MCP-Server/src/tools/index.ts` | `create_sheet_with_views`：新建圖紙並放視圖。先問使用者有沒有範本圖紙：有 → 沿用範本圖框類型與位置、圖紙參數、視埠類型／位置／標題位置（視圖名稱去掉結尾數字與括號後自動對應）；沒有 → 最常用圖框＋格狀自動排列。可 dryRun |

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
