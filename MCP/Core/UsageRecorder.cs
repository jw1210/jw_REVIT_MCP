using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Core
{
    /// <summary>
    /// JW 本機修改（2026-10-26）：JW 外掛（TilePlanner、LaneRamp、RevitRebar）的使用紀錄「錄音機」。
    /// 紀錄格式的正本在 JW agent 的 500_Project/0000_tools/revit-usage-log/README.md，這裡照它寫。
    ///
    /// 只有 JW 的外掛有動靜時才錄：外掛記錄點寫進 usage 資料夾的事件，或這個 Revit 的 journal 出現
    /// 外掛的按鈕／外部事件，就開一段；最後一次外掛動靜後 idleMinutes（預設 10）分鐘沒動靜就結束。
    /// 段落中記 Revit 看得到的事：模型變動、警告、對話框、開檔存檔、切換視圖、選取。
    ///
    /// 執行緒分工：Revit 事件在 UI 執行緒，只擷取資料、丟進佇列；寫檔、讀 journal、判斷結束都在背景
    /// （計時器＋寫檔執行緒），不碰 Revit API。DocumentChanged 這類事件裡只做輕量的事——沒在錄就第一行
    /// 返回、查類別有上限——同 Application.cs 相機管理那段的教訓（docs/camera-manager/adr.zh-TW.md D-008）。
    /// 任何錯誤都只寫 Logger，不影響 Revit 與其他功能。
    /// </summary>
    internal static class UsageRecorder
    {
        private sealed class PluginDef
        {
            public string Name;
            public string ClassPrefix;
            public string[] AddInIds;
            public string[] AssemblyNames;
        }

        // 外掛對照表：以三個外掛的 .addin 為準（2026-10-26 查），正本表格在 revit-usage-log/README.md〈怎麼認出是哪個外掛〉
        private static readonly PluginDef[] Plugins =
        {
            new PluginDef
            {
                Name = "TilePlanner",
                ClassPrefix = "TilePlanner.",
                // App 本身＋.addin 裡另外登記成外部工具的命令
                AddInIds = new[]
                {
                    "7890ABCD-E5F6-4321-ABCD-EF1234567890", "A1B2C3D4-E5F6-7890-ABCD-EF1234567890",
                    "B2C3D4E5-F6A7-8901-BCDE-F12345678901", "C3D4E5F6-A7B8-9012-CDEF-123456789012",
                    "D4E5F6A7-B8C9-0123-DEAF-234567890123", "E5F6A7B8-C9D0-1234-EFAB-345678901234",
                    "F6A7B8C9-D0E1-2345-FABC-456789012345",
                },
                AssemblyNames = new[] { "TilePlanner" },
            },
            new PluginDef
            {
                Name = "LaneRamp",
                ClassPrefix = "LaneRamp.",
                AddInIds = new[] { "a8f3c2e1-9b4d-4f6a-8c7e-2d1b0a9e8f7c" },
                // 記錄點小程式放在 LaneRamp.Core，跟外掛自己記的版本讀同一顆 DLL，指紋才一致
                AssemblyNames = new[] { "LaneRamp.Core", "LaneRamp.Revit", "LaneRamp.Addin" },
            },
            new PluginDef
            {
                Name = "RevitRebar",
                ClassPrefix = "RevitRebar.",
                AddInIds = new[] { "6E3B0B7C-6F2E-4F0B-9B4C-2B1E8C1A7D41" },
                AssemblyNames = new[] { "RevitRebar.Addin" },
            },
        };

        private const int MaxIdsPerList = 50;
        private const int MaxSelectionIds = 20;
        private const int CategoryLookupBudget = 500;
        private const int MaxJournalBytesPerTick = 1024 * 1024;
        private static readonly TimeSpan ExternalEventThrottle = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan SelectionQuiet = TimeSpan.FromSeconds(1);
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        // journal 裡的中文是系統 ANSI（Big5）；這裡只認 ASCII 的類別名與 GUID，用 Latin-1 一位元組對一字元解碼就夠，
        // Big5 的第二個位元組不會落在 ':'、'%'、'"' 這些分隔字元上
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        private static readonly object Gate = new object();
        private static BlockingCollection<KeyValuePair<string, string>> _queue;
        private static Thread _writer;
        private static Timer _timer;
        private static int _ticking;
        private static bool _installed;

        private static string _root;
        private static int _pid;
        private static string _revitVersion;
        private static TimeSpan _idle = TimeSpan.FromMinutes(10);

        private static string _journalPath;
        private static long _journalOffset;
        private static string _journalRemainder = "";
        private static readonly Dictionary<string, long> _pluginFileOffsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> _lastExternalEventLogUtc = new Dictionary<string, DateTime>();
        private static readonly Dictionary<string, JObject> _versionCache = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

        private static volatile string _session;
        private static DateTime _lastActivityUtc;
        private static volatile string _activeDocTitle;
        private static JObject _pendingSelection;
        private static DateTime _pendingSelectionUtc;
        private static bool _loggedWriteError;

        private static bool IsActive => _session != null;

        #region 安裝與移除

        public static void Install(UIControlledApplication application)
        {
            try
            {
                _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitMCP", "usage");
                if (!LoadSettings())
                {
                    Logger.Info("[Usage] 使用紀錄已關閉（usage\\settings.json 的 enabled 是 false）");
                    return;
                }

                _pid = Process.GetCurrentProcess().Id;
                _revitVersion = application.ControlledApplication.VersionNumber;
                _journalPath = application.ControlledApplication.RecordingJournalFilename;
                // 只看安裝之後才寫進 journal 的，之前的不回頭讀
                _journalOffset = File.Exists(_journalPath) ? new FileInfo(_journalPath).Length : 0;
                InitPluginFileOffsets();

                _queue = new BlockingCollection<KeyValuePair<string, string>>();
                _writer = new Thread(WriterLoop) { IsBackground = true, Name = "RevitMCP-UsageWriter" };
                _writer.Start();

                var app = application.ControlledApplication;
                app.DocumentChanged += OnDocumentChanged;
                app.FailuresProcessing += OnFailuresProcessing;
                app.DocumentOpened += OnDocumentOpened;
                app.DocumentClosing += OnDocumentClosing;
                app.DocumentSaved += OnDocumentSaved;
                application.ViewActivated += OnViewActivated;
                application.DialogBoxShowing += OnDialogBoxShowing;
#if REVIT2023_OR_GREATER
                application.SelectionChanged += OnSelectionChanged;
#endif

                _timer = new Timer(OnTick, null, 1000, 1000);
                _installed = true;
                Logger.Info($"[Usage] 使用紀錄已啟動：{_root}（{_idle.TotalMinutes:0} 分鐘沒動靜結束一段；journal {_journalPath}）");
            }
            catch (Exception ex)
            {
                Logger.Error("[Usage] 啟動使用紀錄失敗", ex);
            }
        }

        public static void Uninstall(UIControlledApplication application)
        {
            if (!_installed) return;
            _installed = false;
            try
            {
                var app = application.ControlledApplication;
                app.DocumentChanged -= OnDocumentChanged;
                app.FailuresProcessing -= OnFailuresProcessing;
                app.DocumentOpened -= OnDocumentOpened;
                app.DocumentClosing -= OnDocumentClosing;
                app.DocumentSaved -= OnDocumentSaved;
                application.ViewActivated -= OnViewActivated;
                application.DialogBoxShowing -= OnDialogBoxShowing;
#if REVIT2023_OR_GREATER
                application.SelectionChanged -= OnSelectionChanged;
#endif
                _timer?.Dispose();
                lock (Gate)
                {
                    FlushPendingSelection(force: true);
                    if (_session != null) EndSession("revit_closing");
                }
                _queue?.CompleteAdding();
                _writer?.Join(2000);
            }
            catch (Exception ex)
            {
                Logger.Error("[Usage] 停止使用紀錄失敗", ex);
            }
        }

        /// <summary>讀 usage\settings.json（可省略）：{"enabled": true, "idleMinutes": 10}。回傳是否啟用。</summary>
        private static bool LoadSettings()
        {
            string path = Path.Combine(_root, "settings.json");
            if (!File.Exists(path)) return true;
            try
            {
                var s = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                double minutes = s["idleMinutes"]?.Value<double>() ?? 10;
                if (minutes >= 1 && minutes <= 240) _idle = TimeSpan.FromMinutes(minutes);
                return s["enabled"]?.Value<bool>() ?? true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[Usage] settings.json 讀不懂，用預設值: {ex.Message}");
                return true;
            }
        }

        #endregion

        #region Revit 事件（UI 執行緒：只擷取、丟佇列）

        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            if (!IsActive) return;
            try
            {
                Document doc = e.GetDocument();
                var added = e.GetAddedElementIds();
                var modified = e.GetModifiedElementIds();
                var deleted = e.GetDeletedElementIds();
                int budget = CategoryLookupBudget;
                var data = new JObject
                {
                    ["op"] = e.Operation.ToString(),
                    ["tx"] = new JArray(e.GetTransactionNames().Cast<object>().ToArray()),
                    ["added"] = CountByCategory(doc, added, ref budget),
                    ["modified"] = CountByCategory(doc, modified, ref budget),
                    ["deleted"] = deleted.Count,
                    ["ids"] = new JObject
                    {
                        ["added"] = Ids(added, MaxIdsPerList),
                        ["modified"] = Ids(modified, MaxIdsPerList),
                        ["deleted"] = Ids(deleted, MaxIdsPerList),
                    },
                };
                Write("doc_changed", data, doc?.Title);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Usage] 記錄模型變動失敗: {ex.Message}");
            }
        }

        private static void OnFailuresProcessing(object sender, FailuresProcessingEventArgs e)
        {
            if (!IsActive) return;
            try
            {
                FailuresAccessor accessor = e.GetFailuresAccessor();
                string title = accessor.GetDocument()?.Title;
                foreach (FailureMessageAccessor message in accessor.GetFailureMessages())
                {
                    Write("warning", new JObject
                    {
                        ["severity"] = message.GetSeverity().ToString(),
                        ["text"] = message.GetDescriptionText(),
                        ["ids"] = Ids(message.GetFailingElementIds(), MaxIdsPerList),
                    }, title);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Usage] 記錄警告失敗: {ex.Message}");
            }
        }

        private static void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs e)
        {
            if (!IsActive) return;
            try
            {
                var data = new JObject { ["id"] = e.DialogId };
                if (e is TaskDialogShowingEventArgs task) data["message"] = task.Message;
                Write("dialog", data, null);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Usage] 記錄對話框失敗: {ex.Message}");
            }
        }

        private static void OnDocumentOpened(object sender, DocumentOpenedEventArgs e)
        {
            try
            {
                Document doc = e.Document;
                if (doc == null) return;
                _activeDocTitle = doc.Title;
                if (IsActive) Write("doc_opened", new JObject { ["title"] = doc.Title, ["path"] = doc.PathName }, doc.Title);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Usage] 記錄開檔失敗: {ex.Message}");
            }
        }

        private static void OnDocumentClosing(object sender, DocumentClosingEventArgs e)
        {
            if (!IsActive) return;
            try
            {
                Document doc = e.Document;
                if (doc != null) Write("doc_closing", new JObject { ["title"] = doc.Title, ["path"] = doc.PathName }, doc.Title);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Usage] 記錄關檔失敗: {ex.Message}");
            }
        }

        private static void OnDocumentSaved(object sender, DocumentSavedEventArgs e)
        {
            if (!IsActive) return;
            try
            {
                Document doc = e.Document;
                if (doc != null) Write("doc_saved", new JObject { ["title"] = doc.Title, ["path"] = doc.PathName }, doc.Title);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Usage] 記錄存檔失敗: {ex.Message}");
            }
        }

        private static void OnViewActivated(object sender, ViewActivatedEventArgs e)
        {
            try
            {
                string title = e.Document?.Title;
                if (title != null) _activeDocTitle = title;
                if (!IsActive) return;
                View view = e.CurrentActiveView;
                if (view == null) return;
                Write("view_activated", new JObject { ["view"] = view.Name, ["viewType"] = view.ViewType.ToString() }, title);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Usage] 記錄切換視圖失敗: {ex.Message}");
            }
        }

#if REVIT2023_OR_GREATER
        private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsActive) return;
            try
            {
                Document doc = e.GetDocument();
                var ids = e.GetSelectedElements();
                int budget = CategoryLookupBudget;
                var data = new JObject
                {
                    ["count"] = ids.Count(),
                    ["categories"] = CountByCategory(doc, ids, ref budget),
                    ["ids"] = Ids(ids, MaxSelectionIds),
                };
                // 選取一拖會連發好幾十次：先暫存，背景計時器等 1 秒沒再變才寫最後那一筆
                lock (Gate)
                {
                    data["doc"] = doc?.Title;
                    _pendingSelection = data;
                    _pendingSelectionUtc = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Usage] 記錄選取失敗: {ex.Message}");
            }
        }
#endif

        #endregion

        #region 背景：讀 journal、看外掛事件檔、判斷結束

        private static void OnTick(object state)
        {
            if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
            try
            {
                PollPluginFiles();
                PollJournal();
                lock (Gate)
                {
                    FlushPendingSelection(force: false);
                    if (_session != null && DateTime.UtcNow - _lastActivityUtc > _idle)
                    {
                        EndSession("idle");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Usage] 背景檢查失敗: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _ticking, 0);
            }
        }

        private static string PluginFilePath(DateTime local, string plugin)
            => Path.Combine(_root, local.ToString("yyyy-MM-dd"), $"{_pid}_{plugin}.jsonl");

        /// <summary>安裝時記下外掛事件檔目前多長，之後只看新增的部分（Revit 重開前寫的不算這次的動靜）。</summary>
        private static void InitPluginFileOffsets()
        {
            foreach (var p in Plugins)
            {
                string path = PluginFilePath(DateTime.Now, p.Name);
                _pluginFileOffsets[path] = File.Exists(path) ? new FileInfo(path).Length : 0;
            }
        }

        private static void PollPluginFiles()
        {
            foreach (var p in Plugins)
            {
                string path = PluginFilePath(DateTime.Now, p.Name);
                if (!File.Exists(path)) continue;
                long length = new FileInfo(path).Length;
                _pluginFileOffsets.TryGetValue(path, out long offset);
                if (length <= offset) continue;

                string appended = ReadAppended(path, offset, length, Utf8NoBom);
                _pluginFileOffsets[path] = length;

                string cmd = null;
                foreach (string line in appended.Split('\n'))
                {
                    if (line.Trim().Length == 0) continue;
                    try { cmd = JObject.Parse(line)["cmd"]?.Value<string>() ?? cmd; }
                    catch { /* 外掛寫了一半的行，下一輪再說 */ }
                }
                OnPluginActivity(p, cmd, null, null, "plugin_event");
            }
        }

        private static void PollJournal()
        {
            if (string.IsNullOrEmpty(_journalPath) || !File.Exists(_journalPath)) return;
            long length = new FileInfo(_journalPath).Length;
            if (length < _journalOffset) _journalOffset = 0; // journal 換了一份
            if (length == _journalOffset) return;

            long end = Math.Min(length, _journalOffset + MaxJournalBytesPerTick);
            string text = _journalRemainder + ReadAppended(_journalPath, _journalOffset, end, Latin1);
            _journalOffset = end;

            int lastNewline = text.LastIndexOf('\n');
            if (lastNewline < 0)
            {
                _journalRemainder = text;
                return;
            }
            _journalRemainder = text.Substring(lastNewline + 1);
            foreach (string line in text.Substring(0, lastNewline).Split('\n'))
            {
                ParseJournalLine(line);
            }
        }

        private static void ParseJournalLine(string line)
        {
            const string ribbonMarker = "Execute external command:";
            int i = line.IndexOf(ribbonMarker, StringComparison.Ordinal);
            if (i >= 0)
            {
                // Jrn.RibbonEvent "Execute external command:CustomCtrl_%CustomCtrl_%<頁籤>%<面板>%<按鈕代號>:<類別>"
                string body = line.Substring(i + ribbonMarker.Length);
                int quote = body.IndexOf('"');
                if (quote >= 0) body = body.Substring(0, quote);
                int colon = body.LastIndexOf(':');
                if (colon < 0) return;
                string cls = body.Substring(colon + 1).Trim();
                string commandId = body.Substring(0, colon);
                string button = commandId.Substring(commandId.LastIndexOf('%') + 1);
                var plugin = Plugins.FirstOrDefault(p => cls.StartsWith(p.ClassPrefix, StringComparison.Ordinal));
                if (plugin != null) OnPluginActivity(plugin, button, cls, "ribbon", "journal");
                return;
            }

            const string externalMarker = "Rvt.Attr.AddInId: ";
            if (line.IndexOf("[Jrn.ExternalEventExecution]", StringComparison.Ordinal) >= 0)
            {
                int j = line.IndexOf(externalMarker, StringComparison.Ordinal);
                if (j < 0 || line.Length < j + externalMarker.Length + 36) return;
                string addInId = line.Substring(j + externalMarker.Length, 36);
                var plugin = Plugins.FirstOrDefault(p => p.AddInIds.Any(id => string.Equals(id, addInId, StringComparison.OrdinalIgnoreCase)));
                if (plugin != null) OnPluginActivity(plugin, null, null, "external_event", "journal");
            }
        }

        private static void OnPluginActivity(PluginDef plugin, string cmd, string cls, string how, string trigger)
        {
            lock (Gate)
            {
                _lastActivityUtc = DateTime.UtcNow;
                if (_session == null) StartSession(trigger, plugin.Name, cmd);
                if (how == null) return; // 外掛自己的事件檔已經有細節，不重複記

                if (how == "external_event")
                {
                    // 浮動視窗可能一直送外部事件：同一個外掛 5 秒內只記一筆，但每一筆都算動靜
                    if (_lastExternalEventLogUtc.TryGetValue(plugin.Name, out DateTime last) && DateTime.UtcNow - last < ExternalEventThrottle) return;
                    _lastExternalEventLogUtc[plugin.Name] = DateTime.UtcNow;
                }
                Write("plugin_activity", new JObject { ["plugin"] = plugin.Name, ["cmd"] = cmd, ["class"] = cls, ["how"] = how }, null);
            }
        }

        // 以下三個都在持有 Gate 時呼叫
        private static void StartSession(string trigger, string plugin, string cmd)
        {
            _session = $"{DateTime.Now:yyyyMMdd-HHmmss}-{_pid}";
            Write("session_start", new JObject
            {
                ["trigger"] = trigger,
                ["plugin"] = plugin,
                ["cmd"] = cmd,
                ["idleMinutes"] = _idle.TotalMinutes,
                ["plugins"] = ScanPluginVersions(),
            }, null);
        }

        private static void EndSession(string reason)
        {
            Write("session_end", new JObject
            {
                ["reason"] = reason,
                ["lastActivity"] = new DateTimeOffset(_lastActivityUtc).ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
            }, null);
            _session = null;
            _lastExternalEventLogUtc.Clear();
        }

        private static void FlushPendingSelection(bool force)
        {
            if (_pendingSelection == null) return;
            if (!force && DateTime.UtcNow - _pendingSelectionUtc < SelectionQuiet) return;
            JObject data = _pendingSelection;
            _pendingSelection = null;
            string doc = data["doc"]?.Value<string>();
            data.Remove("doc");
            if (_session != null) Write("selection", data, doc);
        }

        private static string ReadAppended(string path, long from, long to, Encoding encoding)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(from, SeekOrigin.Begin);
                var buffer = new byte[to - from];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = fs.Read(buffer, read, buffer.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                return encoding.GetString(buffer, 0, read);
            }
        }

        #endregion

        #region 版本資訊、寫檔

        /// <summary>這個 Revit 目前載入的三個外掛各是哪一版（沒加記錄點的外掛也查得到）。</summary>
        private static JObject ScanPluginVersions()
        {
            var result = new JObject();
            Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var p in Plugins)
            {
                Assembly asm = p.AssemblyNames
                    .Select(n => loaded.FirstOrDefault(a => string.Equals(a.GetName().Name, n, StringComparison.OrdinalIgnoreCase)))
                    .FirstOrDefault(a => a != null);
                if (asm != null) result[p.Name] = VersionInfo(asm);
            }
            return result;
        }

        private static JObject VersionInfo(Assembly asm)
        {
            string location = asm.Location;
            if (_versionCache.TryGetValue(location, out JObject cached)) return (JObject)cached.DeepClone();

            var info = new JObject { ["asm"] = asm.GetName().Version?.ToString() };
            string informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(informational))
            {
                info["info"] = informational;
                int plus = informational.IndexOf('+');
                if (plus >= 0 && informational.Length > plus + 1)
                {
                    string commit = informational.Substring(plus + 1);
                    info["commit"] = commit.Length > 7 ? commit.Substring(0, 7) : commit;
                }
            }
            if (!string.IsNullOrEmpty(location) && File.Exists(location))
            {
                using (var sha = SHA256.Create())
                using (var fs = new FileStream(location, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    info["sha"] = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").Substring(0, 12).ToLowerInvariant();
                }
                info["built"] = new DateTimeOffset(File.GetLastWriteTime(location)).ToString("yyyy-MM-ddTHH:mm:sszzz");
            }
            _versionCache[location] = info;
            return (JObject)info.DeepClone();
        }

        private static JObject CountByCategory(Document doc, IEnumerable<ElementId> ids, ref int budget)
        {
            var counts = new Dictionary<string, int>();
            foreach (ElementId id in ids)
            {
                string name;
                if (doc == null || budget <= 0)
                {
                    name = "(超過查詢上限)";
                }
                else
                {
                    budget--;
                    Element element = doc.GetElement(id);
                    name = element?.Category?.Name ?? "(無類別)";
                }
                counts.TryGetValue(name, out int n);
                counts[name] = n + 1;
            }
            var result = new JObject();
            foreach (var kv in counts.OrderByDescending(kv => kv.Value)) result[kv.Key] = kv.Value;
            return result;
        }

        private static JArray Ids(IEnumerable<ElementId> ids, int max)
            => new JArray(ids.Take(max).Select(id => (object)(long)id.GetIdValue()).ToArray());

        private static void Write(string type, JObject data, string doc)
        {
            try
            {
                DateTimeOffset now = DateTimeOffset.Now;
                var evt = new JObject
                {
                    ["t"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
                    ["src"] = "RevitMCP",
                    ["pid"] = _pid,
                    ["revit"] = _revitVersion,
                };
                string title = doc ?? _activeDocTitle;
                if (!string.IsNullOrEmpty(title)) evt["doc"] = title;
                evt["session"] = _session;
                evt["type"] = type;
                evt["data"] = data;
                _queue?.TryAdd(new KeyValuePair<string, string>(now.ToString("yyyy-MM-dd"), evt.ToString(Formatting.None)));
            }
            catch (Exception ex)
            {
                Logger.Error($"[Usage] 組事件失敗（{type}）: {ex.Message}");
            }
        }

        private static void WriterLoop()
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                try
                {
                    string dir = Path.Combine(_root, item.Key);
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, $"{_pid}_RevitMCP.jsonl"), item.Value + "\n", Utf8NoBom);
                }
                catch (Exception ex)
                {
                    if (!_loggedWriteError)
                    {
                        _loggedWriteError = true; // 寫不進去只記一次，免得洗版日誌
                        Logger.Error($"[Usage] 寫紀錄檔失敗（之後同樣的錯不再記）: {ex.Message}");
                    }
                }
            }
        }

        #endregion
    }
}
