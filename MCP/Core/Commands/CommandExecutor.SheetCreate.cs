using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        #region 新建圖紙並放視圖（JW 本機修改 2026-11-20）

        // 流程（AI 端照 MCP 工具說明做）：先問使用者有沒有範本圖紙可參考。
        //   有範本 → 沿用範本的圖框類型與位置、圖紙參數、視埠類型與位置（依視圖名稱自動對應）；
        //   沒範本 → 用指定圖框（不填＝專案裡最常用的），視圖自動排成格狀。
        // 圖紙座標一律 mm。

        /// <summary>比對視圖名稱用：去掉括號內容（半形／全形）與結尾數字，例如「樓梯D_A-A'剖面1」「樓梯D_3D模擬(B2F)」。</summary>
        private static string JwNormViewName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = Regex.Replace(s, @"\([^)]*\)|（[^）]*）", "");
            t = Regex.Replace(t, @"\d+\s*$", "");
            return t.Trim();
        }

        private static ElementId JwMostUsedTitleBlockType(Document doc)
        {
            var used = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType().Select(e => e.GetTypeId())
                .GroupBy(id => id).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            if (used != null && used != ElementId.InvalidElementId) return used;
            return new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType().FirstElementId();
        }

        private static FamilyInstance JwSheetTitleBlock(Document doc, ViewSheet sheet)
            => new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType().OfType<FamilyInstance>().FirstOrDefault();

        private static object JwMm(XYZ p) => p == null ? null : new { x = Math.Round(p.X * JwFt, 2), y = Math.Round(p.Y * JwFt, 2) };

        private sealed class JwSheetPlanItem
        {
            public View View;
            public Viewport Like;
            public XYZ Center;
            public string MatchedBy;
            public string Problem;
        }

        private static bool JwSetParam(Parameter tp, JToken value)
        {
            switch (tp.StorageType)
            {
                case StorageType.String: return tp.Set(value.Type == JTokenType.Null ? "" : value.ToString());
                case StorageType.Integer: return tp.Set(value.Value<int>());
                case StorageType.Double:
                    return value.Type == JTokenType.String ? tp.SetValueString(value.Value<string>()) : tp.Set(value.Value<double>());
                default: return false;
            }
        }

        /// <summary>
        /// 新建一張圖紙並把指定視圖放上去。有 templateSheetId 時照範本（圖框類型與位置、圖紙參數、視埠類型與位置）；
        /// 沒有時用 titleBlockTypeId（或專案最常用的圖框）並把視圖排成格狀。dryRun 只回報打算怎麼放。
        /// </summary>
        private object CreateSheetWithViews(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            string number = parameters["number"]?.Value<string>();
            string name = parameters["name"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(name)) throw new Exception("number 與 name 必填");
            bool dryRun = parameters["dryRun"]?.Value<bool>() ?? false;
            bool copyParams = parameters["copySheetParameters"]?.Value<bool>() ?? true;
            JObject overrides = parameters["parameterOverrides"] as JObject;
            JArray views = parameters["views"] as JArray ?? throw new Exception("缺少 views");

            if (new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Any(s => s.SheetNumber == number))
                throw new Exception($"圖紙編號 {number} 已經有了");

            ViewSheet template = null;
            IdType templateId = parameters["templateSheetId"]?.Value<IdType>() ?? 0;
            if (templateId > 0)
                template = doc.GetElement(templateId.ToElementId()) as ViewSheet ?? throw new Exception($"找不到範本圖紙 ID: {templateId}");
            FamilyInstance templateTb = template != null ? JwSheetTitleBlock(doc, template) : null;

            IdType tbIn = parameters["titleBlockTypeId"]?.Value<IdType>() ?? 0;
            ElementId tbTypeId = tbIn > 0 ? tbIn.ToElementId() : (templateTb?.GetTypeId() ?? JwMostUsedTitleBlockType(doc));
            if (tbTypeId == null || tbTypeId == ElementId.InvalidElementId) throw new Exception("專案裡找不到圖框類型");
            string tbTypeName = doc.GetElement(tbTypeId)?.Name;

            // 範本視埠
            var templateVps = template == null ? new List<Viewport>()
                : template.GetAllViewports().Select(id => doc.GetElement(id) as Viewport).Where(v => v != null).ToList();
            var usedLike = new HashSet<ElementId>();

            // 規劃每個視圖
            var plan = new List<JwSheetPlanItem>();
            foreach (JToken t in views)
            {
                JObject o = t as JObject;
                IdType vid = o != null ? (o["viewId"]?.Value<IdType>() ?? 0) : t.Value<IdType>();
                var item = new JwSheetPlanItem { View = doc.GetElement(vid.ToElementId()) as View };
                plan.Add(item);
                if (item.View == null) { item.Problem = $"找不到視圖 ID: {vid}"; continue; }

                IdType likeId = o?["likeViewId"]?.Value<IdType>() ?? 0;
                if (likeId > 0)
                {
                    item.Like = templateVps.FirstOrDefault(vp => vp.ViewId.GetIdValue() == likeId);
                    item.MatchedBy = item.Like != null ? "likeViewId" : null;
                    if (item.Like == null) item.Problem = $"範本圖紙上沒有視圖 {likeId}";
                }
                else if (template != null)
                {
                    string key = JwNormViewName(item.View.Name);
                    var cands = templateVps.Where(vp => !usedLike.Contains(vp.Id)
                        && doc.GetElement(vp.ViewId) is View tv
                        && tv.ViewType == item.View.ViewType
                        && JwNormViewName(tv.Name) == key).ToList();
                    if (cands.Count == 1) { item.Like = cands[0]; item.MatchedBy = "name"; }
                    else if (cands.Count > 1) item.Problem = "範本上有多個名稱相同的視圖，請用 likeViewId 指定";
                }
                if (item.Like != null) usedLike.Add(item.Like.Id);

                if (o?["centerMm"] is JObject c)
                {
                    item.Center = new XYZ(c["x"].Value<double>() / JwFt, c["y"].Value<double>() / JwFt, 0);
                    item.MatchedBy = (item.MatchedBy == null ? "" : item.MatchedBy + "+") + "centerMm";
                }
                else if (item.Like != null)
                {
                    item.Center = item.Like.GetBoxCenter();
                }
            }

            var paramPlan = new List<string>();
            if (template != null && copyParams) paramPlan.Add($"沿用範本 {template.SheetNumber} 的可寫圖紙參數");
            if (overrides != null) paramPlan.AddRange(overrides.Properties().Select(pp => $"{pp.Name} = {pp.Value}"));

            if (dryRun)
            {
                return new
                {
                    dryRun = true,
                    number, name,
                    template = template == null ? null : $"{template.SheetNumber} {template.Name}",
                    titleBlockType = tbTypeName,
                    parameters = paramPlan,
                    views = plan.Select(i => new
                    {
                        viewId = i.View == null ? (long?)null : JwId(i.View.Id),
                        viewName = i.View?.Name,
                        likeTemplateView = i.Like == null ? null : doc.GetElement(i.Like.ViewId)?.Name,
                        matchedBy = i.MatchedBy,
                        centerMm = i.Center == null ? (object)"自動排列" : JwMm(i.Center),
                        problem = i.Problem,
                    }).ToList(),
                };
            }

            var results = new List<object>();
            var warnings = new List<string>();
            ViewSheet sheet;
            using (Transaction tx = TransactionHelper.Begin(doc, "新建圖紙並放視圖"))
            {
                tx.Start();
                sheet = ViewSheet.Create(doc, tbTypeId);
                sheet.SheetNumber = number;
                sheet.Name = name;

                // 圖框位置照範本
                FamilyInstance newTb = JwSheetTitleBlock(doc, sheet);
                if (templateTb != null && newTb != null
                    && templateTb.Location is LocationPoint tlp && newTb.Location is LocationPoint nlp)
                {
                    XYZ d = tlp.Point - nlp.Point;
                    if (!d.IsZeroLength()) ElementTransformUtils.MoveElement(doc, newTb.Id, d);
                }

                // 圖紙參數
                var overrideNames = new HashSet<string>(overrides?.Properties().Select(pp => pp.Name) ?? Enumerable.Empty<string>());
                if (template != null && copyParams)
                {
                    foreach (Parameter sp in template.Parameters)
                    {
                        if (sp?.Definition == null || sp.IsReadOnly || overrideNames.Contains(sp.Definition.Name)) continue;
                        if (sp.StorageType == StorageType.ElementId || sp.StorageType == StorageType.None) continue;
                        var bip = (sp.Definition as InternalDefinition)?.BuiltInParameter ?? BuiltInParameter.INVALID;
                        if (bip == BuiltInParameter.SHEET_NUMBER || bip == BuiltInParameter.SHEET_NAME || bip == BuiltInParameter.VIEW_NAME) continue;
                        Parameter tp = sheet.get_Parameter(sp.Definition);
                        if (tp == null || tp.IsReadOnly) continue;
                        try
                        {
                            switch (sp.StorageType)
                            {
                                case StorageType.String: tp.Set(sp.AsString() ?? ""); break;
                                case StorageType.Integer: tp.Set(sp.AsInteger()); break;
                                case StorageType.Double: tp.Set(sp.AsDouble()); break;
                            }
                        }
                        catch (Exception ex) { warnings.Add($"參數 {sp.Definition.Name}: {ex.Message}"); }
                    }
                }
                if (overrides != null)
                {
                    foreach (JProperty pp in overrides.Properties())
                    {
                        Parameter tp = sheet.LookupParameter(pp.Name);
                        if (tp == null) { warnings.Add($"找不到圖紙參數 {pp.Name}"); continue; }
                        if (tp.IsReadOnly) { warnings.Add($"圖紙參數 {pp.Name} 是唯讀"); continue; }
                        try { if (!JwSetParam(tp, pp.Value)) warnings.Add($"圖紙參數 {pp.Name} 沒有設定成功"); }
                        catch (Exception ex) { warnings.Add($"圖紙參數 {pp.Name}: {ex.Message}"); }
                    }
                }

                doc.Regenerate();

                // 沒有位置的視圖：在圖框內（右側 20% 留給標題欄）排成格狀
                var auto = plan.Where(i => i.View != null && i.Problem == null && i.Center == null).ToList();
                if (auto.Count > 0)
                {
                    BoundingBoxXYZ bb = newTb?.get_BoundingBox(sheet);
                    double minX, minY, maxX, maxY;
                    if (bb != null)
                    {
                        double m = 15 / JwFt;
                        minX = bb.Min.X + m; minY = bb.Min.Y + m;
                        maxX = bb.Min.X + (bb.Max.X - bb.Min.X) * 0.8; maxY = bb.Max.Y - m;
                    }
                    else
                    {
                        minX = 0; minY = 0; maxX = 320 / JwFt; maxY = 280 / JwFt;
                        warnings.Add("讀不到圖框範圍，自動排列改用 A3 大小估計");
                    }
                    int cols = (int)Math.Ceiling(Math.Sqrt(auto.Count));
                    int rows = (int)Math.Ceiling(auto.Count / (double)cols);
                    double cw = (maxX - minX) / cols, ch = (maxY - minY) / rows;
                    for (int k = 0; k < auto.Count; k++)
                    {
                        int r = k / cols, cIdx = k % cols;
                        auto[k].Center = new XYZ(minX + cw * (cIdx + 0.5), maxY - ch * (r + 0.5), 0);
                        auto[k].MatchedBy = "自動排列";
                    }
                }

                foreach (var i in plan)
                {
                    if (i.View == null || i.Problem != null)
                    {
                        results.Add(new { viewId = i.View == null ? (long?)null : JwId(i.View.Id), viewName = i.View?.Name, status = "略過", reason = i.Problem });
                        continue;
                    }
                    if (!Viewport.CanAddViewToSheet(doc, sheet.Id, i.View.Id))
                    {
                        results.Add(new { viewId = JwId(i.View.Id), viewName = i.View.Name, status = "略過", reason = "不能放上這張圖紙（可能已經在別張圖紙上，或這種視圖不能用視埠放）" });
                        continue;
                    }
                    Viewport vp = Viewport.Create(doc, sheet.Id, i.View.Id, i.Center);
                    if (i.Like != null)
                    {
                        try { if (vp.GetTypeId() != i.Like.GetTypeId()) vp.ChangeTypeId(i.Like.GetTypeId()); }
                        catch (Exception ex) { warnings.Add($"{i.View.Name} 視埠類型: {ex.Message}"); }
                        try { if (vp.Rotation != i.Like.Rotation) vp.Rotation = i.Like.Rotation; }
                        catch (Exception ex) { warnings.Add($"{i.View.Name} 旋轉: {ex.Message}"); }
                        // 標題位置是相對視埠的偏移；視埠大小跟範本差很多時（例如 3D 視圖沒開裁剪），
                        // 照抄會把標題放到別的視圖上或圖紙外（2026-11-20 ST-06 實測），這時改用 Revit 預設位置。
                        doc.Regenerate();
                        Outline a = vp.GetBoxOutline(), b = i.Like.GetBoxOutline();
                        double aw = a.MaximumPoint.X - a.MinimumPoint.X, ah = a.MaximumPoint.Y - a.MinimumPoint.Y;
                        double bw = b.MaximumPoint.X - b.MinimumPoint.X, bh = b.MaximumPoint.Y - b.MinimumPoint.Y;
                        bool similar = bw > 1e-9 && bh > 1e-9 && Math.Abs(aw / bw - 1) <= 0.05 && Math.Abs(ah / bh - 1) <= 0.05;
                        if (similar)
                        {
                            try { vp.LabelOffset = i.Like.LabelOffset; }
                            catch (Exception ex) { warnings.Add($"{i.View.Name} 標題位置: {ex.Message}"); }
                        }
                        else
                        {
                            warnings.Add($"{i.View.Name} 視埠大小跟範本差超過 5%，標題用預設位置，請確認");
                        }
                    }
                    doc.Regenerate();
                    vp.SetBoxCenter(i.Center);

                    Outline ol = vp.GetBoxOutline();
                    results.Add(new
                    {
                        viewId = JwId(i.View.Id),
                        viewName = i.View.Name,
                        status = "已放",
                        viewportId = JwId(vp.Id),
                        likeTemplateView = i.Like == null ? null : doc.GetElement(i.Like.ViewId)?.Name,
                        matchedBy = i.MatchedBy,
                        centerMm = JwMm(vp.GetBoxCenter()),
                        widthMm = Math.Round((ol.MaximumPoint.X - ol.MinimumPoint.X) * JwFt, 2),
                        heightMm = Math.Round((ol.MaximumPoint.Y - ol.MinimumPoint.Y) * JwFt, 2),
                    });
                }

                TransactionHelper.CommitOrThrow(tx);
            }

            return new
            {
                sheetId = JwId(sheet.Id),
                number = sheet.SheetNumber,
                name = sheet.Name,
                template = template == null ? null : $"{template.SheetNumber} {template.Name}",
                titleBlockType = tbTypeName,
                viewports = results,
                warnings,
            };
        }

        #endregion
    }
}
