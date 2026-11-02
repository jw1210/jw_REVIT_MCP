/**
 * JW 本機修改（2026-10-26）：同一台電腦同時開好幾個 Revit 時，列出它們、切換要操作哪一個。
 *
 * Revit 端每個 Revit 從 8964 往上找第一個空的 port（MCP/Application.cs 的 PortSearchCount），
 * 這裡掃同一個範圍。這兩個工具在 index.ts 裡先攔下來處理，不走「先連 Revit 再轉送」那段，
 * 所以就算 8964 上沒有 Revit 也能用。
 */

import { RevitSocketClient, probeRevitInstance } from "./socket.js";

/** 要跟 MCP/Application.cs 的 PortSearchCount 一致 */
export const INSTANCE_PORT_COUNT = 10;

function basePort(): number {
    const env = parseInt(process.env.REVIT_MCP_PORT || "", 10);
    return !isNaN(env) && env >= 1024 && env <= 65535 ? env : 8964;
}

export function isInstanceTool(name: string): boolean {
    return name === "list_revit_instances" || name === "switch_revit_instance";
}

async function describeCurrent(client: RevitSocketClient): Promise<any> {
    const response = await client.sendCommand("get_instance_info", {}, 10000);
    return response.data;
}

async function listInstances(client: RevitSocketClient): Promise<object> {
    const base = basePort();
    const ports = Array.from({ length: INSTANCE_PORT_COUNT }, (_, i) => base + i);
    const currentPort = client.getPort();

    const results = await Promise.all(ports.map(async (port) => {
        // 目前連著的那個不能再開短連線（會被自己的獨占鎖回 409），直接用主連線問
        if (port === currentPort && client.isConnected()) {
            try {
                return { port, status: "ok", connected: true, data: await describeCurrent(client) };
            } catch (e) {
                return { port, status: "error", connected: true, error: e instanceof Error ? e.message : String(e) };
            }
        }
        const probe = await probeRevitInstance(port, client.clientName);
        return { port, connected: false, ...probe };
    }));

    const instances = results
        .filter((r: any) => r.status !== "no-service")
        .map((r: any) => ({
            Port: r.port,
            Connected: r.connected,
            Status: r.status === "ok" ? "可用" : r.status === "locked" ? "被別的 AI 佔用" : "錯誤",
            Error: r.error,
            RevitVersion: r.data?.RevitVersion,
            ProcessId: r.data?.ProcessId,
            ActiveDocument: r.data?.ActiveDocument,
            Documents: r.data?.Documents,
        }));

    return {
        CurrentPort: currentPort,
        CurrentlyConnected: client.isConnected(),
        ScannedPorts: `${base}～${base + INSTANCE_PORT_COUNT - 1}`,
        Count: instances.length,
        Instances: instances,
        Message: instances.length === 0
            ? "沒有找到開著 MCP 服務的 Revit。"
            : `找到 ${instances.length} 個 Revit。要操作別的，用 switch_revit_instance 指定 Port。`,
    };
}

async function switchInstance(client: RevitSocketClient, args: Record<string, any>): Promise<object> {
    const port = Number(args.port);
    const base = basePort();
    if (!Number.isInteger(port) || port < base || port >= base + INSTANCE_PORT_COUNT) {
        throw new Error(`port 要在 ${base}～${base + INSTANCE_PORT_COUNT - 1} 之間（先用 list_revit_instances 查）`);
    }

    const previous = client.getPort();
    try {
        await client.switchPort(port);
    } catch (e: any) {
        // Node 22 連線被拒時丟的是 AggregateError，message 是空字串、只有 code，所以要另外翻成人話
        const message = e instanceof Error ? e.message : "";
        const reason = e?.code === "ECONNREFUSED"
            ? "那個 port 沒有開著 MCP 服務的 Revit"
            : message.includes("409")
                ? "那個 Revit 正被別的 AI 連線佔用（要用的話，在那個 Revit 按「切換/釋放連線」）"
                : message || e?.code || String(e);
        throw new Error(`切換到 port ${port} 失敗：${reason}。已嘗試連回原本的 port ${previous}（目前${client.isConnected() ? "已連上" : "沒有連線"}）。`);
    }

    const info = await describeCurrent(client);
    return {
        SwitchedTo: port,
        PreviousPort: previous,
        RevitVersion: info?.RevitVersion,
        ProcessId: info?.ProcessId,
        ActiveDocument: info?.ActiveDocument,
        Documents: info?.Documents,
        Message: `已切換到 port ${port}（Revit ${info?.RevitVersion ?? "?"}，作用中：${info?.ActiveDocument ?? "沒有開檔"}）。之前查到的視圖、樓層、元素 Id 都是另一個 Revit 的，操作前先用 get_active_view 重新確認。`,
    };
}

export async function handleInstanceTool(
    name: string,
    args: Record<string, any>,
    client: RevitSocketClient
): Promise<object> {
    if (name === "list_revit_instances") {
        return listInstances(client);
    }
    return switchInstance(client, args);
}
