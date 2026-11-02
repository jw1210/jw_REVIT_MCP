using System.Collections.Generic;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        #region 多個 Revit 同時開（JW 本機修改 2026-10-26）

        /// <summary>
        /// 回報這個 Revit 是誰：版本、程式編號、MCP 服務用的 port、開著哪些檔案、作用中是哪個。只讀。
        /// MCP Server 的 list_revit_instances 掃 port 時逐一呼叫，讓 AI 分得出哪個 port 是哪個 Revit。
        /// 不註冊成 MCP 工具（AI 用 list_revit_instances 就好），見 MCP-Server/src/jw-instances.ts。
        /// </summary>
        private object GetInstanceInfo(JObject parameters)
        {
            var app = _uiApp.Application;
            Document active = _uiApp.ActiveUIDocument?.Document;

            var documents = new List<object>();
            foreach (Document doc in app.Documents)
            {
                if (doc.IsLinked) continue; // 連結模型不算「開著的檔案」
                documents.Add(new
                {
                    doc.Title,
                    doc.PathName,
                    doc.IsFamilyDocument,
                    IsActive = active != null && doc.Equals(active),
                });
            }

            return new
            {
                RevitVersion = app.VersionNumber,
                app.VersionName,
                app.VersionBuild,
                ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id,
                Port = global::RevitMCP.Application.ActivePort,
                ActiveDocument = active?.Title,
                Documents = documents,
            };
        }

        #endregion
    }
}
