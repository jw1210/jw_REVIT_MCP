import { Tool } from "@modelcontextprotocol/sdk/types.js";

/**
 * JW 本機修改（2026-11-20）：新建圖紙並把指定視圖放上去。
 * C# 端在 MCP/Core/Commands/CommandExecutor.SheetCreate.cs。
 */
export const sheetCreateTools: Tool[] = [
    {
        name: "create_sheet_with_views",
        description:
            "新建一張圖紙，並把指定的視圖放上去（Revit 原生視埠）。呼叫前先問使用者：有沒有範本圖紙可以參考（例如同一系列的上一層圖紙）？" +
            "有範本 → 給 templateSheetId：沿用範本的圖框類型與位置、可寫的圖紙參數（parameterOverrides 可覆寫，例如分組參數），" +
            "每個視圖依名稱自動對應範本上的視埠（去掉結尾數字與括號內容後相同、且視圖類型相同，例如「樓梯D_A-A'剖面1」對「樓梯D_A-A'剖面」、「樓梯D_3D模擬(B2F)」對「樓梯D_3D模擬」），" +
            "放在同一個中心位置並套同一個視埠類型與標題位置；也可用 likeViewId 指定對應哪個範本視圖、用 centerMm 直接給圖紙座標。" +
            "沒有範本 → 用 titleBlockTypeId（不填＝專案裡最常用的圖框，可用 get_titleblocks 查），視圖在圖框內（右側 20% 留給標題欄）自動排成格狀，之後可用 position_viewports_on_sheet 微調。" +
            "建議先 dryRun:true 看對應與位置給使用者確認。一個視圖只能放在一張圖紙上（已在別張圖紙上的會略過並說明）。圖紙座標單位 mm。",
        inputSchema: {
            type: "object",
            properties: {
                number: { type: "string", description: "圖紙編號，例如 ST-05（不能跟既有的重複）" },
                name: { type: "string", description: "圖紙名稱，例如 B2F_ST(D)" },
                templateSheetId: { type: "number", description: "範本圖紙 Element ID（選填；使用者說沒有範本就不填）" },
                titleBlockTypeId: { type: "number", description: "圖框類型 ID（選填；有範本時預設用範本的圖框，沒範本時預設用專案最常用的）" },
                views: {
                    type: "array",
                    description: "要放的視圖。可直接給視圖 ID，或物件 {viewId, likeViewId?, centerMm?}",
                    items: {
                        anyOf: [
                            { type: "number" },
                            {
                                type: "object",
                                properties: {
                                    viewId: { type: "number" },
                                    likeViewId: { type: "number", description: "對應範本圖紙上的哪個視圖（視圖 ID）" },
                                    centerMm: {
                                        type: "object",
                                        properties: { x: { type: "number" }, y: { type: "number" } },
                                        required: ["x", "y"],
                                        description: "視埠中心的圖紙座標（mm），優先於範本位置",
                                    },
                                },
                                required: ["viewId"],
                            },
                        ],
                    },
                },
                copySheetParameters: { type: "boolean", description: "有範本時是否沿用範本的圖紙參數（預設 true）" },
                parameterOverrides: { type: "object", description: "圖紙參數名稱 → 值，例如 {\"FU_專案圖紙次分類\": \"02_B2F\"}" },
                dryRun: { type: "boolean", description: "true＝只回報打算怎麼建、視圖對應哪個範本視埠、放哪裡，不修改模型" },
            },
            required: ["number", "name", "views"],
        },
    },
];
