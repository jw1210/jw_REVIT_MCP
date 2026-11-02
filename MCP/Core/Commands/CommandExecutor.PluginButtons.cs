using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        #region 代按外掛按鈕（JW 本機修改 2026-10-26）

        // 外掛的 IExternalCommand.Execute 需要 ExternalCommandData，它沒有公開建構子，
        // 只有 Revit 自己按按鈕時才會產生——所以不能直接呼叫別的外掛的命令。
        // 這裡走官方的 UIApplication.PostCommand：請 Revit 自己去按那個 ribbon 按鈕，
        // 效果跟人按一樣（會跳視窗、會要求點選），但只能按、不能傳參數、拿不到執行結果。
        //
        // 只有 MCP/jw-local/plugin-buttons.json（編進 DLL 的允許清單）裡的按鈕能按，
        // 避免 AI 去按 Revit 原生或其他外掛的按鈕。清單外的按鈕一律拒絕。
        //
        // 外掛按鈕的 RevitCommandId 代號沒有官方文件列出格式，所以每個按鈕試兩種寫法，
        // list_plugin_buttons 會回報實際對上的是哪一種（MatchedCommandId）。

        private const string PluginButtonsResourceName = "RevitMCP.PluginButtons.json";

        private sealed class PluginButtonEntry
        {
            public string Id;
            public string Plugin;
            public string Label;
            public string Interaction;
            public string Note;
            public List<string> CandidateCommandIds = new List<string>();
        }

        private static JObject LoadPluginButtonsJson()
        {
            var asm = typeof(CommandExecutor).Assembly;
            using (Stream stream = asm.GetManifestResourceStream(PluginButtonsResourceName))
            {
                if (stream == null)
                {
                    throw new Exception($"找不到內嵌的按鈕清單 {PluginButtonsResourceName}（MCP/jw-local/plugin-buttons.json 沒有編進 DLL，檢查 RevitMCP.csproj 的 EmbeddedResource）");
                }
                using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
                {
                    return JObject.Parse(reader.ReadToEnd());
                }
            }
        }

        private static List<PluginButtonEntry> LoadPluginButtons(JObject root)
        {
            var result = new List<PluginButtonEntry>();
            foreach (JObject plugin in root["plugins"] ?? new JArray())
            {
                string pluginName = plugin["plugin"]?.Value<string>();
                string panel = plugin["panel"]?.Value<string>();
                var tabs = new List<string> { plugin["tab"]?.Value<string>() };
                tabs.AddRange((plugin["fallbackTabs"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());

                foreach (JObject button in plugin["buttons"] ?? new JArray())
                {
                    string buttonName = button["button"]?.Value<string>();
                    var entry = new PluginButtonEntry
                    {
                        Id = button["id"]?.Value<string>(),
                        Plugin = pluginName,
                        Label = button["label"]?.Value<string>(),
                        Interaction = button["interaction"]?.Value<string>(),
                        Note = button["note"]?.Value<string>(),
                    };
                    foreach (string tab in tabs.Where(t => !string.IsNullOrEmpty(t)))
                    {
                        entry.CandidateCommandIds.Add($"CustomCtrl_%CustomCtrl_%{tab}%{panel}%{buttonName}");
                        entry.CandidateCommandIds.Add($"CustomCtrl_%{tab}%{panel}%{buttonName}");
                    }
                    result.Add(entry);
                }
            }
            return result;
        }

        /// <summary>依候選代號逐一查；查到第一個就回傳。外掛沒裝在這版 Revit 時全部查不到。</summary>
        private static RevitCommandId ResolvePluginCommandId(PluginButtonEntry entry, out string matched)
        {
            matched = null;
            foreach (string candidate in entry.CandidateCommandIds)
            {
                RevitCommandId id = null;
                try { id = RevitCommandId.LookupCommandId(candidate); }
                catch (Exception ex) { Logger.Info($"[PluginButtons] LookupCommandId 例外（{candidate}）: {ex.Message}"); }
                if (id != null)
                {
                    matched = candidate;
                    return id;
                }
            }
            return null;
        }

        /// <summary>
        /// 列出允許清單裡的外掛按鈕，以及在目前這個 Revit 找不找得到、現在能不能按。只讀，不改模型。
        /// </summary>
        private object ListPluginButtons(JObject parameters)
        {
            string pluginFilter = parameters?["plugin"]?.Value<string>();
            JObject root = LoadPluginButtonsJson();
            var entries = LoadPluginButtons(root)
                .Where(e => string.IsNullOrEmpty(pluginFilter) || string.Equals(e.Plugin, pluginFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var buttons = entries.Select(e =>
            {
                RevitCommandId id = ResolvePluginCommandId(e, out string matched);
                bool? canPost = null;
                if (id != null)
                {
                    try { canPost = _uiApp.CanPostCommand(id); }
                    catch (Exception ex) { Logger.Info($"[PluginButtons] CanPostCommand 例外（{e.Id}）: {ex.Message}"); }
                }
                return new
                {
                    e.Id,
                    e.Plugin,
                    e.Label,
                    e.Interaction,
                    e.Note,
                    Found = id != null,
                    CanPost = canPost,
                    MatchedCommandId = matched,
                    TriedCommandIds = id == null ? e.CandidateCommandIds : null,
                };
            }).ToList();

            var excluded = (root["excluded"] as JArray)?.Select(x => new
            {
                Plugin = x["plugin"]?.Value<string>(),
                Label = x["label"]?.Value<string>(),
                Reason = x["reason"]?.Value<string>(),
            }).ToList();

            int found = buttons.Count(b => b.Found);
            return new
            {
                RevitVersion = _uiApp.Application.VersionNumber,
                HasActiveDocument = _uiApp.ActiveUIDocument != null,
                Count = buttons.Count,
                FoundCount = found,
                Buttons = buttons,
                Excluded = excluded,
                Message = $"清單 {buttons.Count} 個按鈕，這個 Revit（{_uiApp.Application.VersionNumber}）找得到 {found} 個。找不到通常是外掛沒裝在這一版 Revit。",
            };
        }

        /// <summary>
        /// 代按允許清單裡的一個外掛按鈕（UIApplication.PostCommand）。
        /// PostCommand 只是排進佇列：這次 MCP 指令回傳之後，Revit 才會真正執行那個按鈕。
        /// </summary>
        private object PressPluginButton(JObject parameters)
        {
            string buttonId = parameters?["buttonId"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(buttonId))
            {
                throw new Exception("請指定 buttonId（先用 list_plugin_buttons 查可用的 Id）");
            }

            JObject root = LoadPluginButtonsJson();
            var entries = LoadPluginButtons(root);
            PluginButtonEntry entry = entries.FirstOrDefault(e => string.Equals(e.Id, buttonId, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                throw new Exception($"'{buttonId}' 不在允許清單裡，不能按。可用的 Id：{string.Join(", ", entries.Select(e => e.Id))}");
            }

            RevitCommandId id = ResolvePluginCommandId(entry, out string matched);
            if (id == null)
            {
                throw new Exception($"這個 Revit（{_uiApp.Application.VersionNumber}）找不到「{entry.Label}」按鈕：{entry.Plugin} 可能沒裝在這一版。試過的代號：{string.Join(" | ", entry.CandidateCommandIds)}");
            }

            if (!_uiApp.CanPostCommand(id))
            {
                throw new Exception($"Revit 現在不能執行「{entry.Label}」：可能正在編輯模式、有視窗開著，或這個按鈕目前停用（例如沒有開專案）。");
            }

            _uiApp.PostCommand(id);
            Logger.Info($"[PluginButtons] 已送出 {entry.Id}（{entry.Plugin}／{entry.Label}）: {matched}");

            string next;
            switch (entry.Interaction)
            {
                case "需要點選": next = "接下來 Revit 會要求使用者在畫面上點選，請使用者完成點選（或按 Esc 取消）。"; break;
                case "會開視窗": next = "接下來會開出外掛的視窗，請使用者在視窗裡操作；視窗開著的時候，其他 MCP 指令可能會等到逾時。"; break;
                default: next = "這個按鈕不需要使用者操作，會直接執行。"; break;
            }

            return new
            {
                Posted = true,
                entry.Id,
                entry.Plugin,
                entry.Label,
                entry.Interaction,
                MatchedCommandId = matched,
                Message = $"已請 Revit 按下「{entry.Label}」。Revit 會在這次 MCP 指令結束後才真正執行，結果拿不到回傳值，要事後用查詢工具確認。{next}",
            };
        }

        #endregion
    }
}
