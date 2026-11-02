import { Tool } from "@modelcontextprotocol/sdk/types.js";

/**
 * JW 本機修改（2026-10-26）：代按 JW 自己的 Revit 外掛按鈕（TilePlanner、LaneRamp、RevitRebar）。
 * C# 端在 MCP/Core/Commands/CommandExecutor.PluginButtons.cs，允許清單在 MCP/jw-local/plugin-buttons.json。
 */
export const pluginButtonTools: Tool[] = [
    {
        name: "list_plugin_buttons",
        description:
            "列出可以代按的 JW 自己的外掛按鈕（TilePlanner 磁磚計畫、LaneRamp 車道坡道、RevitRebar 鋼筋建模），以及在目前這個 Revit 找不找得到、現在能不能按。只讀。每個按鈕附 interaction：「直接執行」「需要點選」（Revit 會要使用者在畫面上點）「會開視窗」（使用者要在外掛視窗裡操作）。press_plugin_button 之前先用這個查 Id。",
        inputSchema: {
            type: "object",
            properties: {
                plugin: {
                    type: "string",
                    enum: ["TilePlanner", "LaneRamp", "RevitRebar"],
                    description: "只列某一個外掛（不填就全部列）。",
                },
            },
        },
    },
    {
        name: "press_plugin_button",
        description:
            "代按 JW 外掛的一個 ribbon 按鈕（Revit 官方 PostCommand，效果跟使用者自己按一樣）。只有 list_plugin_buttons 列出的按鈕能按。規則：只在使用者這次對話明確要求使用那個功能時才按，不要自己決定按；按之前用一句話告訴使用者要按哪個按鈕、接下來要他做什麼（依 interaction）。限制：不能傳參數、拿不到執行結果——Revit 會在這次指令回傳後才真正執行，結果要事後用查詢工具確認；「會開視窗」的按鈕開著視窗時，其他 MCP 指令可能等到逾時，等使用者說操作完再繼續。一次只按一個，不要連續按。實測（2026-10-26）：Revit 沒有人在操作時，按下去可能排隊不執行（曾等 80 秒），按完要提醒使用者：沒反應就把滑鼠移到 Revit 視窗上或點一下 Revit。",
        inputSchema: {
            type: "object",
            properties: {
                buttonId: {
                    type: "string",
                    description: "list_plugin_buttons 回傳的 Id，例如 tile.toggle_grid。",
                },
            },
            required: ["buttonId"],
        },
    },
];
