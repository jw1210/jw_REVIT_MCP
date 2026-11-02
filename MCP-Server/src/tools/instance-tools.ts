import { Tool } from "@modelcontextprotocol/sdk/types.js";

/**
 * JW 本機修改（2026-10-26）：同一台電腦同時開好幾個 Revit 時，列出與切換。
 * 不轉送給 Revit 的命令分派表，由 MCP-Server/src/index.ts 攔下來交給 src/jw-instances.ts 處理。
 */
export const instanceTools: Tool[] = [
    {
        name: "list_revit_instances",
        description:
            "列出這台電腦上開著 MCP 服務的所有 Revit（可能是不同版本，也可能同版本開好幾個），每個附 Port、Revit 版本、程式編號、開著的檔案、作用中檔案，以及目前連的是哪一個（Connected）。只讀。使用者提到特定版本或特定檔案、或其他工具回報連不上 Revit 時先用這個查。",
        inputSchema: {
            type: "object",
            properties: {},
        },
    },
    {
        name: "switch_revit_instance",
        description:
            "改成操作另一個 Revit（指定 list_revit_instances 回傳的 Port）。一次只連一個 Revit，切過去之後原本那個就斷線了。切換後，之前查到的視圖、樓層、元素 Id 都屬於另一個 Revit，不能沿用，操作前先用 get_active_view 重新確認。只在使用者要求操作某個版本或某個檔案、而它不在目前連的 Revit 時才切換。",
        inputSchema: {
            type: "object",
            properties: {
                port: {
                    type: "number",
                    description: "list_revit_instances 回傳的 Port，例如 8965。",
                },
            },
            required: ["port"],
        },
    },
];
