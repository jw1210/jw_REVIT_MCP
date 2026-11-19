import { Tool } from "@modelcontextprotocol/sdk/types.js";

/**
 * JW 本機修改（2026-11-19）：樓梯剖面上的尺寸標註與踏板編號，照另一張剖面的做法重做（例如 B3F → B2F）。
 * C# 端在 MCP/Core/Commands/CommandExecutor.StairAnnotations.cs。
 *
 * 座標慣例（五個工具共用）：「視圖座標」mm，u = 點·視圖 RightDirection、v = 點·UpDirection，從世界原點量起；
 * 同一條剖切線的兩張剖面 u/v 可以直接比，剖面的 v 就是高程。
 */
const UV = {
    type: "object",
    properties: { u: { type: "number" }, v: { type: "number" } },
    required: ["u", "v"],
};

export const stairAnnotationTools: Tool[] = [
    {
        name: "get_view_dimension_references",
        description:
            "讀出一張視圖裡每個尺寸標註量的是哪些參考：連結模型或主模型的哪個元件（類別、名稱、所屬樓梯、樓層）、哪個面或邊、在視圖座標的位置（u/v mm），以及參考字串 stable、標註類型、標註線位置、各段數值與文字位置。也列出視圖裡的踏板／豎板編號（掛在哪個梯跑、參考位置 Left/Center/Right…、顯示規則、參數如起始編號）。只讀。用途：要把一張圖的標註照樣做到另一張圖之前，先看原本標了什麼。",
        inputSchema: {
            type: "object",
            properties: {
                viewId: { type: "number", description: "視圖 Element ID" },
            },
            required: ["viewId"],
        },
    },
    {
        name: "get_linked_stairs",
        description:
            "列出主模型與連結模型裡的樓梯（Revit 原生樓梯），每座附底／頂樓層、級數、級高、級深、範圍，以及梯跑（id、級數、起訖高程、寬度、路徑起訖點）與平台（id、高程、厚度）。給 viewId 時只列在該視圖裁剪範圍內的，位置用視圖座標（u/v/w mm）。只讀。",
        inputSchema: {
            type: "object",
            properties: {
                viewId: { type: "number", description: "只列這張視圖裁剪範圍內的樓梯（選填）" },
                linkInstanceId: { type: "number", description: "只看這個連結實體（選填；不填＝主模型＋所有已載入連結）" },
            },
        },
    },
    {
        name: "get_element_faces_in_view",
        description:
            "列出一個元件（主模型或連結模型，例如梯跑、平台、樓板、牆、柱）在某張剖面／立面裡看起來是一條線的平面：axis（U＝法向沿視圖左右、在圖上是直立線；V＝法向沿上下、在圖上是水平線）、位置 pos（mm）、範圍 extent、參考字串 stable（可直接給 create_dimensions_from_references）。元件是 Level／Grid 時回傳它本身的參考——但實測參考 Level 的尺寸標註在剖面上不會顯示，剖面要量樓層高度時改用該高程上看得見的實體面（梁頂、牆頂、樓板面）。只讀。",
        inputSchema: {
            type: "object",
            properties: {
                viewId: { type: "number", description: "視圖 Element ID" },
                elementId: { type: "number", description: "元件 ID（連結模型時填連結模型裡的 ID）" },
                linkInstanceId: { type: "number", description: "元件在連結模型時，連結實體 ID（選填）" },
                axis: { type: "string", enum: ["U", "V", "other"], description: "只列這個方向的面（選填）" },
                withinCrop: { type: "boolean", description: "只列在視圖裁剪範圍內的面（預設 true）" },
                includeEdges: { type: "boolean", description: "也列出沿視線方向、在剖面上看成一點的邊（預設 false）" },
            },
            required: ["viewId", "elementId"],
        },
    },
    {
        name: "create_dimensions_from_references",
        description:
            "用指定的參考字串（get_element_faces_in_view 或 get_view_dimension_references 給的 stable）在視圖裡建線性尺寸標註，可一次建多個。標註線用視圖座標兩點（u1,v1）→（u2,v2）決定方向與位置；可指定標註類型、每段文字位置。每個標註各自成敗，整批是一個復原步驟。回傳每個標註的 id、量到的數值（mm）與 visibleInView（建完後在這張視圖上看不看得到；false 通常是參考了 Level，要刪掉改用實體面重建）——建完要核對數值合不合理。",
        inputSchema: {
            type: "object",
            properties: {
                viewId: { type: "number", description: "視圖 Element ID" },
                dimensions: {
                    type: "array",
                    items: {
                        type: "object",
                        properties: {
                            references: { type: "array", items: { type: "string" }, description: "參考字串，至少 2 個，依標註方向排序" },
                            line: {
                                type: "object",
                                properties: {
                                    u1: { type: "number" }, v1: { type: "number" },
                                    u2: { type: "number" }, v2: { type: "number" },
                                },
                                required: ["u1", "v1", "u2", "v2"],
                                description: "標註線上兩點（視圖座標 mm）",
                            },
                            typeId: { type: "number", description: "尺寸標註類型 ID（選填，不填用預設）" },
                            textPositions: {
                                type: "array",
                                items: { anyOf: [UV, { type: "null" }] },
                                description: "每段文字位置（視圖座標 mm），null＝不動（選填）",
                            },
                        },
                        required: ["references", "line"],
                    },
                },
            },
            required: ["viewId", "dimensions"],
        },
    },
    {
        name: "create_stair_tread_numbers",
        description:
            "在視圖裡替梯跑（可在連結模型）建 Revit 原生的踏板／豎板編號，可一次建多個。referenceOption 決定編號沿梯跑哪條線（Center/Left/Right/LeftQuarter/RightQuarter）；copyFromNumberSystemId 照某個既有編號的類型、顯示規則、對齊、方向、偏移與參數；parameterOverrides 覆寫個別參數（例如起始編號），參數名稱用 get_view_dimension_references 列出的名稱。",
        inputSchema: {
            type: "object",
            properties: {
                viewId: { type: "number", description: "視圖 Element ID" },
                items: {
                    type: "array",
                    items: {
                        type: "object",
                        properties: {
                            runId: { type: "number", description: "梯跑 ID（連結模型時填連結模型裡的 ID）" },
                            linkInstanceId: { type: "number", description: "梯跑在連結模型時，連結實體 ID" },
                            referenceOption: { type: "string", enum: ["Center", "Left", "Right", "LeftQuarter", "RightQuarter"] },
                            copyFromNumberSystemId: { type: "number", description: "照這個既有踏板編號的設定（選填）" },
                            parameterOverrides: { type: "object", description: "參數名稱 → 值（選填）" },
                        },
                        required: ["runId"],
                    },
                },
            },
            required: ["viewId", "items"],
        },
    },
];
