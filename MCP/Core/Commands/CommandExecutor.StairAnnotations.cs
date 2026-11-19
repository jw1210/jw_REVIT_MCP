using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
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
        #region 剖面尺寸標註／踏板編號照另一張圖再做一次（JW 本機修改 2026-11-19）

        // 用途：樓梯剖面上的尺寸標註與踏板編號，照 B3F 那張的做法在 B2F 那張重做。
        // 尺寸標註黏在連結模型的樓梯面上，不能直接複製（會量到原本那座樓梯），
        // 所以分成：讀出原標註量哪些面（get_view_dimension_references）→ 找新樓梯
        // （get_linked_stairs）與它的面（get_element_faces_in_view）→ 用指定的參考建標註
        // （create_dimensions_from_references）、照原設定建踏板編號（create_stair_tread_numbers）。
        //
        // 座標一律用「視圖座標」mm：u = 點·RightDirection、v = 點·UpDirection、w = 點·ViewDirection，
        // 從世界原點量起。同一條剖切線的兩張剖面 u/v 可以直接比；剖面的 v 就是高程（Z）。

        private const double JwFt = 304.8;

        private sealed class JwViewFrame
        {
            public readonly XYZ Right;
            public readonly XYZ Up;
            public readonly XYZ Dir;
            public readonly double PlaneW; // ft

            public JwViewFrame(View v)
            {
                Right = v.RightDirection;
                Up = v.UpDirection;
                Dir = v.ViewDirection;
                PlaneW = v.Origin.DotProduct(Dir);
            }

            public double U(XYZ p) => Math.Round(p.DotProduct(Right) * JwFt, 1);
            public double V(XYZ p) => Math.Round(p.DotProduct(Up) * JwFt, 1);
            public double W(XYZ p) => Math.Round(p.DotProduct(Dir) * JwFt, 1);
            public object UV(XYZ p) => p == null ? null : new { u = U(p), v = V(p) };

            public XYZ FromUV(double uMm, double vMm)
                => Right * (uMm / JwFt) + Up * (vMm / JwFt) + Dir * PlaneW;

            public string Axis(XYZ n)
            {
                if (Math.Abs(n.DotProduct(Right)) > 0.999) return "U";
                if (Math.Abs(n.DotProduct(Up)) > 0.999) return "V";
                if (Math.Abs(n.DotProduct(Dir)) > 0.999) return "W";
                return "other";
            }

            public object Vec(XYZ n) => new
            {
                u = Math.Round(n.DotProduct(Right), 4),
                v = Math.Round(n.DotProduct(Up), 4),
                w = Math.Round(n.DotProduct(Dir), 4),
            };
        }

        private sealed class JwRange
        {
            public double MinU = double.MaxValue, MaxU = double.MinValue;
            public double MinV = double.MaxValue, MaxV = double.MinValue;
            public double MinW = double.MaxValue, MaxW = double.MinValue;
            public bool Empty => MinU > MaxU;

            public void Add(JwViewFrame f, XYZ p)
            {
                double u = f.U(p), v = f.V(p), w = f.W(p);
                MinU = Math.Min(MinU, u); MaxU = Math.Max(MaxU, u);
                MinV = Math.Min(MinV, v); MaxV = Math.Max(MaxV, v);
                MinW = Math.Min(MinW, w); MaxW = Math.Max(MaxW, w);
            }

            public bool OverlapsUV(JwRange o, double tolMm)
                => !(Empty || o.Empty)
                   && MinU <= o.MaxU + tolMm && o.MinU <= MaxU + tolMm
                   && MinV <= o.MaxV + tolMm && o.MinV <= MaxV + tolMm;

            public object ToJson() => Empty ? null : new
            {
                u = new[] { MinU, MaxU },
                v = new[] { MinV, MaxV },
                w = new[] { MinW, MaxW },
            };
        }

        private static long JwId(ElementId id) => id == null ? -1 : (long)id.GetIdValue();

        private static View JwGetView(Document doc, JObject parameters)
        {
            IdType viewId = parameters["viewId"]?.Value<IdType>() ?? 0;
            View view = doc.GetElement(viewId.ToElementId()) as View;
            if (view == null) throw new Exception($"找不到視圖 ID: {viewId}");
            return view;
        }

        private static JwRange JwBoxRange(JwViewFrame f, BoundingBoxXYZ bb, Transform extra)
        {
            var r = new JwRange();
            if (bb == null) return r;
            Transform t = bb.Transform ?? Transform.Identity;
            if (extra != null) t = extra.Multiply(t);
            foreach (double x in new[] { bb.Min.X, bb.Max.X })
                foreach (double y in new[] { bb.Min.Y, bb.Max.Y })
                    foreach (double z in new[] { bb.Min.Z, bb.Max.Z })
                        r.Add(f, t.OfPoint(new XYZ(x, y, z)));
            return r;
        }

        private static JwRange JwCropRange(View view, JwViewFrame f)
        {
            if (!view.CropBoxActive) return null;
            return JwBoxRange(f, view.CropBox, null);
        }

        private static string JwLevelName(Document d, Element e)
        {
            if (e == null) return null;
            try
            {
                ElementId lid = e.LevelId;
                if ((lid == null || lid == ElementId.InvalidElementId) && e is Stairs st)
                    lid = st.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)?.AsElementId();
                if (lid == null || lid == ElementId.InvalidElementId) return null;
                return d.GetElement(lid)?.Name;
            }
            catch { return null; }
        }

        private static long? JwStairsParent(Element e)
        {
            try
            {
                if (e is StairsRun r) return JwId(r.GetStairs()?.Id);
                if (e is StairsLanding l) return JwId(l.GetStairs()?.Id);
            }
            catch { }
            return null;
        }

        private static JwRange JwFaceRange(Face face, Transform t, JwViewFrame f)
        {
            var r = new JwRange();
            foreach (EdgeArray loop in face.EdgeLoops)
                foreach (Edge ed in loop)
                    foreach (XYZ p in ed.Tessellate())
                        r.Add(f, t.OfPoint(p));
            return r;
        }

        /// <summary>參考的幾何：種類、法向、在視圖的位置（posAlongDim＝沿標註方向的位置，用來排序）。</summary>
        private static object JwDescribeGeometry(GeometryObject go, Element el, Transform tf, JwViewFrame f, XYZ dimDir)
        {
            double? Along(XYZ p) => dimDir == null || p == null ? (double?)null : Math.Round(p.DotProduct(dimDir) * JwFt, 1);

            if (el is Level lv)
            {
                XYZ p = tf.OfPoint(new XYZ(0, 0, lv.ProjectElevation));
                return new { kind = "Level", v = f.V(p), posAlongDim = Along(p) };
            }
            if (el is Grid g && g.Curve != null)
            {
                XYZ a = tf.OfPoint(g.Curve.GetEndPoint(0)), b = tf.OfPoint(g.Curve.GetEndPoint(1));
                return new { kind = "Grid", start = f.UV(a), end = f.UV(b), posAlongDim = Along((a + b) / 2) };
            }
            if (go is PlanarFace pf)
            {
                XYZ n = tf.OfVector(pf.FaceNormal).Normalize();
                XYZ o = tf.OfPoint(pf.Origin);
                string axis = f.Axis(n);
                return new
                {
                    kind = "PlanarFace",
                    axis,
                    normal = f.Vec(n),
                    u = axis == "U" ? f.U(o) : (double?)null,
                    v = axis == "V" ? f.V(o) : (double?)null,
                    posAlongDim = Along(o),
                    extent = JwFaceRange(pf, tf, f).ToJson(),
                };
            }
            if (go is Face face)
            {
                BoundingBoxUV bb = face.GetBoundingBox();
                UV mid = (bb.Min + bb.Max) / 2;
                XYZ p = tf.OfPoint(face.Evaluate(mid));
                XYZ n = tf.OfVector(face.ComputeNormal(mid)).Normalize();
                return new { kind = "Face", normal = f.Vec(n), point = f.UV(p), posAlongDim = Along(p), extent = JwFaceRange(face, tf, f).ToJson() };
            }
            if (go is Edge edge)
            {
                Curve c = edge.AsCurve();
                XYZ a = tf.OfPoint(c.GetEndPoint(0)), b = tf.OfPoint(c.GetEndPoint(1));
                return new { kind = "Edge", start = f.UV(a), end = f.UV(b), posAlongDim = Along((a + b) / 2) };
            }
            if (go is Curve cv)
            {
                XYZ a = tf.OfPoint(cv.GetEndPoint(0)), b = tf.OfPoint(cv.GetEndPoint(1));
                return new { kind = "Curve", start = f.UV(a), end = f.UV(b), posAlongDim = Along((a + b) / 2) };
            }
            if (el is ReferencePlane rp)
            {
                XYZ a = tf.OfPoint(rp.BubbleEnd), b = tf.OfPoint(rp.FreeEnd);
                return new { kind = "ReferencePlane", start = f.UV(a), end = f.UV(b), posAlongDim = Along((a + b) / 2) };
            }
            return new { kind = go == null ? "Unknown" : go.GetType().Name };
        }

        private static object JwDescribeReference(Document doc, Reference r, JwViewFrame f, XYZ dimDir)
        {
            string stable = null;
            try { stable = r.ConvertToStableRepresentation(doc); } catch { }

            bool linked = r.LinkedElementId != null && r.LinkedElementId != ElementId.InvalidElementId;
            Element hostEl = doc.GetElement(r.ElementId);
            RevitLinkInstance li = linked ? hostEl as RevitLinkInstance : null;
            Document edoc = li?.GetLinkDocument() ?? doc;
            Element el = linked ? edoc?.GetElement(r.LinkedElementId) : hostEl;
            Transform tf = li?.GetTotalTransform() ?? Transform.Identity;

            GeometryObject go = null;
            try { go = linked ? el?.GetGeometryObjectFromReference(r.CreateReferenceInLink()) : el?.GetGeometryObjectFromReference(r); }
            catch { }

            object geometry;
            try { geometry = JwDescribeGeometry(go, el, tf, f, dimDir); }
            catch (Exception ex) { geometry = new { kind = "Error", error = ex.Message }; }

            return new
            {
                stable,
                linked,
                linkInstanceId = linked ? JwId(r.ElementId) : (long?)null,
                elementId = el == null ? (long?)null : JwId(el.Id),
                category = el?.Category?.Name,
                className = el?.GetType().Name,
                name = el?.Name,
                typeName = el == null ? null : (edoc.GetElement(el.GetTypeId()) as ElementType)?.Name,
                levelName = JwLevelName(edoc, el),
                stairsId = JwStairsParent(el),
                refType = r.ElementReferenceType.ToString(),
                geometry,
            };
        }

        private static object JwTryUV(JwViewFrame f, Func<XYZ> get)
        {
            try { return f.UV(get()); } catch { return null; }
        }

        private static object JwParamValue(Parameter p)
        {
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.Integer: return p.AsInteger();
                    case StorageType.Double: return p.AsValueString() ?? p.AsDouble().ToString();
                    case StorageType.String: return p.AsString();
                    case StorageType.ElementId: return p.AsValueString() ?? JwId(p.AsElementId()).ToString();
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 讀出一張視圖裡每個尺寸標註量的是哪些參考（連結模型的哪個元件、哪個面、在視圖的位置），
        /// 以及踏板／豎板編號掛在哪個梯跑、用哪個參考位置、各項設定。只讀。
        /// </summary>
        private object GetViewDimensionReferences(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            View view = JwGetView(doc, parameters);
            var f = new JwViewFrame(view);

            var dims = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(Dimension))
                .Cast<Dimension>()
                .Where(d => d.OwnerViewId == view.Id)
                .ToList();

            var dimList = new List<object>();
            foreach (Dimension dim in dims)
            {
                Line line = null;
                try { line = dim.Curve as Line; } catch { }
                XYZ dir = line?.Direction;

                var segs = new List<object>();
                if (dim.NumberOfSegments > 0)
                {
                    foreach (DimensionSegment s in dim.Segments)
                    {
                        segs.Add(new
                        {
                            value = s.Value.HasValue ? Math.Round(s.Value.Value * JwFt, 1) : (double?)null,
                            valueString = s.ValueString,
                            origin = JwTryUV(f, () => s.Origin),
                            textPosition = JwTryUV(f, () => s.TextPosition),
                            leaderEnd = JwTryUV(f, () => s.LeaderEndPosition),
                            s.ValueOverride, s.Prefix, s.Suffix, s.Above, s.Below,
                        });
                    }
                }
                else
                {
                    segs.Add(new
                    {
                        value = dim.Value.HasValue ? Math.Round(dim.Value.Value * JwFt, 1) : (double?)null,
                        valueString = dim.ValueString,
                        origin = JwTryUV(f, () => dim.Origin),
                        textPosition = JwTryUV(f, () => dim.TextPosition),
                        leaderEnd = JwTryUV(f, () => dim.LeaderEndPosition),
                        dim.ValueOverride, dim.Prefix, dim.Suffix, dim.Above, dim.Below,
                    });
                }

                var refs = new List<object>();
                foreach (Reference r in dim.References)
                    refs.Add(JwDescribeReference(doc, r, f, dir));

                bool? hasLeader = null;
                try { hasLeader = dim.HasLeader; } catch { }

                dimList.Add(new
                {
                    id = JwId(dim.Id),
                    className = dim.GetType().Name,
                    typeId = JwId(dim.GetTypeId()),
                    typeName = dim.DimensionType?.Name,
                    shape = dim.DimensionShape.ToString(),
                    line = line == null ? null : new { origin = f.UV(line.Origin), direction = f.Vec(dir) },
                    hasLeader,
                    segments = segs,
                    references = refs,
                });
            }

            var numbers = new List<object>();
            foreach (NumberSystem ns in new FilteredElementCollector(doc, view.Id)
                         .OfClass(typeof(NumberSystem)).Cast<NumberSystem>()
                         .Where(n => n.OwnerViewId == view.Id))
            {
                LinkElementId nid = ns.NumberedElementId;
                bool linked = nid.LinkInstanceId != ElementId.InvalidElementId;
                RevitLinkInstance li = linked ? doc.GetElement(nid.LinkInstanceId) as RevitLinkInstance : null;
                Document rdoc = li?.GetLinkDocument() ?? doc;
                Element numbered = linked ? rdoc?.GetElement(nid.LinkedElementId) : doc.GetElement(nid.HostElementId);
                StairsRun run = numbered as StairsRun;

                // 用哪個參考位置：拿梯跑五種位置的參考跟 ReferencePick 比
                string option = null;
                Reference pick = null;
                try { pick = ns.GetReferencePick(); } catch { }
                if (run != null && pick != null)
                {
                    string pickHost = null, pickInLink = null;
                    try { pickHost = pick.ConvertToStableRepresentation(doc); } catch { }
                    try { if (li != null) pickInLink = pick.CreateReferenceInLink().ConvertToStableRepresentation(rdoc); } catch { }
                    foreach (StairsNumberSystemReferenceOption opt in Enum.GetValues(typeof(StairsNumberSystemReferenceOption)))
                    {
                        try
                        {
                            Reference cand = run.GetNumberSystemReference(opt);
                            if (cand == null) continue;
                            string inRun = cand.ConvertToStableRepresentation(rdoc);
                            string asHost = li != null ? cand.CreateLinkReference(li).ConvertToStableRepresentation(doc) : inRun;
                            if (inRun == pickInLink || asHost == pickHost || inRun == pickHost)
                            {
                                option = opt.ToString();
                                break;
                            }
                        }
                        catch { }
                    }
                }

                LinkElementId plid = ns.PlacementLevelId;
                bool levelLinked = plid.LinkInstanceId != ElementId.InvalidElementId;
                Element level = levelLinked
                    ? (doc.GetElement(plid.LinkInstanceId) as RevitLinkInstance)?.GetLinkDocument()?.GetElement(plid.LinkedElementId)
                    : doc.GetElement(plid.HostElementId);

                var prms = new List<object>();
                foreach (Parameter p in ns.Parameters)
                {
                    if (p?.Definition == null) continue;
                    prms.Add(new { name = p.Definition.Name, storage = p.StorageType.ToString(), readOnly = p.IsReadOnly, value = JwParamValue(p) });
                }

                numbers.Add(new
                {
                    id = JwId(ns.Id),
                    typeId = JwId(ns.GetTypeId()),
                    typeName = doc.GetElement(ns.GetTypeId())?.Name,
                    linkInstanceId = linked ? JwId(nid.LinkInstanceId) : (long?)null,
                    numberedElementId = linked ? JwId(nid.LinkedElementId) : JwId(nid.HostElementId),
                    numberedClass = numbered?.GetType().Name,
                    stairsId = JwStairsParent(numbered),
                    runRisers = run?.ActualRisersNumber,
                    runBaseElevationMm = run == null ? (double?)null : Math.Round(run.BaseElevation * JwFt, 1),
                    placementLevel = level?.Name,
                    placementLevelLinked = levelLinked,
                    referenceOption = option,
                    displayRule = ns.NumberDisplayRule.ToString(),
                    justify = ns.JustifyOption.ToString(),
                    justifyOffsetMm = Math.Round(ns.JustifyOffset * JwFt, 2),
                    orientation = ns.NumberOrientation.ToString(),
                    referenceOffsetMm = Math.Round(ns.ReferenceOffset * JwFt, 2),
                    parameters = prms,
                });
            }

            return new
            {
                viewId = JwId(view.Id),
                viewName = view.Name,
                frame = new { right = f.Vec(view.RightDirection), up = f.Vec(view.UpDirection), viewPlaneWMm = Math.Round(f.PlaneW * JwFt, 1) },
                crop = JwCropRange(view, f)?.ToJson(),
                dimensionCount = dimList.Count,
                dimensions = dimList,
                numberSystemCount = numbers.Count,
                numberSystems = numbers,
            };
        }

        /// <summary>
        /// 列出主模型或連結模型裡的樓梯（含梯跑、平台），可用 viewId 只列在該視圖裁剪範圍內的，位置用視圖座標。只讀。
        /// </summary>
        private object GetLinkedStairs(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            View view = parameters["viewId"] != null ? JwGetView(doc, parameters) : null;
            JwViewFrame f = view != null ? new JwViewFrame(view) : null;
            JwRange crop = view != null ? JwCropRange(view, f) : null;
            long linkFilter = parameters["linkInstanceId"]?.Value<long>() ?? 0;

            var sources = new List<(RevitLinkInstance li, Document d)>();
            if (linkFilter <= 0) sources.Add((null, doc));
            foreach (RevitLinkInstance li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                if (linkFilter > 0 && JwId(li.Id) != linkFilter) continue;
                Document ld = li.GetLinkDocument();
                if (ld != null) sources.Add((li, ld));
            }

            // 沒有視圖時用平面座標（u=X、v=Z）
            JwViewFrame frame = f;
            var result = new List<object>();
            foreach (var (li, d) in sources)
            {
                Transform tf = li?.GetTotalTransform() ?? Transform.Identity;
                foreach (Stairs st in new FilteredElementCollector(d).OfCategory(BuiltInCategory.OST_Stairs)
                             .WhereElementIsNotElementType().ToElements().OfType<Stairs>())
                {
                    BoundingBoxXYZ bb = st.get_BoundingBox(null);
                    JwRange range = frame != null ? JwBoxRange(frame, bb, tf) : null;
                    if (crop != null && range != null && !range.OverlapsUV(crop, 1.0)) continue;

                    object Box(Element e) => frame == null ? null : JwBoxRange(frame, e.get_BoundingBox(null), tf).ToJson();
                    XYZ worldMin = bb == null ? null : tf.OfPoint(bb.Min), worldMax = bb == null ? null : tf.OfPoint(bb.Max);

                    var runs = new List<object>();
                    foreach (ElementId rid in st.GetStairsRuns())
                    {
                        if (!(d.GetElement(rid) is StairsRun run)) continue;
                        object path = null;
                        try
                        {
                            CurveLoop cl = run.GetStairsPath();
                            List<Curve> cs = cl.ToList();
                            if (cs.Count > 0 && frame != null)
                                path = new { start = frame.UV(tf.OfPoint(cs.First().GetEndPoint(0))), end = frame.UV(tf.OfPoint(cs.Last().GetEndPoint(1))) };
                        }
                        catch { }
                        runs.Add(new
                        {
                            id = JwId(run.Id),
                            risers = run.ActualRisersNumber,
                            treads = run.ActualTreadsNumber,
                            baseElevationMm = Math.Round(run.BaseElevation * JwFt, 1),
                            topElevationMm = Math.Round(run.TopElevation * JwFt, 1),
                            beginsWithRiser = run.BeginsWithRiser,
                            endsWithRiser = run.EndsWithRiser,
                            widthMm = Math.Round(run.ActualRunWidth * JwFt, 1),
                            path,
                            box = Box(run),
                        });
                    }

                    var landings = new List<object>();
                    foreach (ElementId lid in st.GetStairsLandings())
                    {
                        if (!(d.GetElement(lid) is StairsLanding ld)) continue;
                        landings.Add(new
                        {
                            id = JwId(ld.Id),
                            baseElevationMm = Math.Round(ld.BaseElevation * JwFt, 1),
                            thicknessMm = Math.Round(ld.Thickness * JwFt, 1),
                            box = Box(ld),
                        });
                    }

                    result.Add(new
                    {
                        linkInstanceId = li == null ? (long?)null : JwId(li.Id),
                        linkName = li?.Name,
                        id = JwId(st.Id),
                        typeName = (d.GetElement(st.GetTypeId()) as ElementType)?.Name,
                        baseLevel = JwLevelName(d, st),
                        topLevel = d.GetElement(st.get_Parameter(BuiltInParameter.STAIRS_TOP_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId)?.Name,
                        baseElevationMm = Math.Round(st.BaseElevation * JwFt, 1),
                        topElevationMm = Math.Round(st.TopElevation * JwFt, 1),
                        risers = st.ActualRisersNumber,
                        riserHeightMm = Math.Round(st.ActualRiserHeight * JwFt, 2),
                        treadDepthMm = Math.Round(st.ActualTreadDepth * JwFt, 2),
                        multistoryStairsId = JwId(st.MultistoryStairsId),
                        box = range?.ToJson(),
                        worldBox = worldMin == null ? null : new
                        {
                            min = new { x = Math.Round(worldMin.X * JwFt), y = Math.Round(worldMin.Y * JwFt), z = Math.Round(worldMin.Z * JwFt) },
                            max = new { x = Math.Round(worldMax.X * JwFt), y = Math.Round(worldMax.Y * JwFt), z = Math.Round(worldMax.Z * JwFt) },
                        },
                        runs,
                        landings,
                    });
                }
            }

            return new
            {
                viewId = view == null ? (long?)null : JwId(view.Id),
                crop = crop?.ToJson(),
                count = result.Count,
                stairs = result,
            };
        }

        /// <summary>
        /// 列出一個元件（主模型或連結模型）在某視圖裡「看起來是一條線」的平面（法向垂直視線），
        /// 附位置（u 或 v）、範圍、可直接拿去建尺寸標註的參考字串。Level／Grid 回傳本身的參考。只讀。
        /// </summary>
        private object GetElementFacesInView(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            View view = JwGetView(doc, parameters);
            var f = new JwViewFrame(view);
            long linkId = parameters["linkInstanceId"]?.Value<long>() ?? 0;
            IdType elemId = parameters["elementId"]?.Value<IdType>() ?? 0;
            string axisFilter = parameters["axis"]?.Value<string>();
            bool withinCrop = parameters["withinCrop"]?.Value<bool>() ?? true;
            bool includeEdges = parameters["includeEdges"]?.Value<bool>() ?? false;

            RevitLinkInstance li = null;
            Document edoc = doc;
            if (linkId > 0)
            {
                li = doc.GetElement(((IdType)linkId).ToElementId()) as RevitLinkInstance
                     ?? throw new Exception($"找不到連結實體 ID: {linkId}");
                edoc = li.GetLinkDocument() ?? throw new Exception("連結模型沒有載入");
            }
            Transform tf = li?.GetTotalTransform() ?? Transform.Identity;
            Element el = edoc.GetElement(elemId.ToElementId()) ?? throw new Exception($"找不到元件 ID: {elemId}");

            string Stable(Reference r)
            {
                if (r == null) return null;
                if (li != null) r = r.CreateLinkReference(li);
                return r.ConvertToStableRepresentation(doc);
            }

            if (el is Level lv)
            {
                return new
                {
                    elementId = JwId(el.Id), className = "Level", name = el.Name,
                    datum = new { v = f.V(tf.OfPoint(new XYZ(0, 0, lv.ProjectElevation))), stable = Stable(lv.GetPlaneReference()) },
                };
            }
            if (el is Grid gr)
            {
                XYZ a = tf.OfPoint(gr.Curve.GetEndPoint(0)), b = tf.OfPoint(gr.Curve.GetEndPoint(1));
                return new
                {
                    elementId = JwId(el.Id), className = "Grid", name = el.Name,
                    datum = new { start = f.UV(a), end = f.UV(b), stable = Stable(new Reference(gr)) },
                };
            }

            var opt = new Options { ComputeReferences = true, IncludeNonVisibleObjects = false };
            if (li == null) opt.View = view;
            else opt.DetailLevel = view.DetailLevel == ViewDetailLevel.Undefined ? ViewDetailLevel.Fine : view.DetailLevel;

            GeometryElement ge = el.get_Geometry(opt);
            if (ge == null) throw new Exception("這個元件沒有幾何");

            JwRange crop = withinCrop ? JwCropRange(view, f) : null;
            var faces = new List<(string axis, double pos, object json)>();
            var edges = new List<object>();
            int skippedNoRef = 0;

            void AddFace(Face fc, Transform t)
            {
                if (!(fc is PlanarFace pf)) return;
                XYZ n = t.OfVector(pf.FaceNormal).Normalize();
                if (Math.Abs(n.DotProduct(f.Dir)) > 0.01) return; // 不是側看成線的面
                string axis = f.Axis(n);
                if (!string.IsNullOrEmpty(axisFilter) && !string.Equals(axis, axisFilter, StringComparison.OrdinalIgnoreCase)) return;
                JwRange rg = JwFaceRange(pf, t, f);
                if (crop != null && !rg.OverlapsUV(crop, 1.0)) return;
                if (pf.Reference == null) { skippedNoRef++; return; }
                XYZ o = t.OfPoint(pf.Origin);
                double pos = axis == "U" ? f.U(o) : axis == "V" ? f.V(o) : 0;
                faces.Add((axis, pos, new
                {
                    axis,
                    pos,
                    normal = f.Vec(n),
                    extent = rg.ToJson(),
                    areaM2 = Math.Round(pf.Area * JwFt * JwFt / 1e6, 4),
                    stable = Stable(pf.Reference),
                }));
            }

            void AddEdge(Edge ed, Transform t)
            {
                if (ed.Reference == null) return;
                Curve c = ed.AsCurve();
                if (!(c is Line ln)) return;
                XYZ d = t.OfVector(ln.Direction).Normalize();
                if (Math.Abs(d.DotProduct(f.Dir)) < 0.999) return; // 只要沿視線方向、在剖面上看成一點的邊
                XYZ p = t.OfPoint(ln.GetEndPoint(0));
                if (crop != null)
                {
                    double u = f.U(p), v = f.V(p);
                    if (u < crop.MinU - 1 || u > crop.MaxU + 1 || v < crop.MinV - 1 || v > crop.MaxV + 1) return;
                }
                edges.Add(new { point = f.UV(p), stable = Stable(ed.Reference) });
            }

            void Walk(GeometryElement g, Transform t)
            {
                foreach (GeometryObject o in g)
                {
                    if (o is Solid s && s.Faces.Size > 0)
                    {
                        foreach (Face fc in s.Faces) AddFace(fc, t);
                        if (includeEdges) foreach (Edge ed in s.Edges) AddEdge(ed, t);
                    }
                    else if (o is GeometryInstance gi)
                    {
                        Walk(gi.GetSymbolGeometry(), t.Multiply(gi.Transform));
                    }
                }
            }
            Walk(ge, tf);

            return new
            {
                elementId = JwId(el.Id),
                className = el.GetType().Name,
                category = el.Category?.Name,
                name = el.Name,
                linkInstanceId = li == null ? (long?)null : JwId(li.Id),
                box = JwBoxRange(f, el.get_BoundingBox(null), tf).ToJson(),
                faceCount = faces.Count,
                faces = faces.OrderBy(x => x.axis).ThenBy(x => x.pos).Select(x => x.json).ToList(),
                edgeCount = edges.Count,
                edges,
                skippedNoReference = skippedNoRef,
            };
        }

        /// <summary>
        /// 用指定的參考字串（get_element_faces_in_view／get_view_dimension_references 給的 stable）建線性尺寸標註。
        /// 標註線位置用視圖座標 u/v（mm）。每個標註各自一個交易、整批包成一個復原步驟。
        /// </summary>
        private object CreateDimensionsFromReferences(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            View view = JwGetView(doc, parameters);
            var f = new JwViewFrame(view);
            JArray items = parameters["dimensions"] as JArray ?? throw new Exception("缺少 dimensions");

            var results = new List<object>();
            int okCount = 0;
            using (var tg = new TransactionGroup(doc, "依參考建立尺寸標註"))
            {
                tg.Start();
                for (int i = 0; i < items.Count; i++)
                {
                    JObject it = items[i] as JObject;
                    try
                    {
                        JArray refs = it?["references"] as JArray;
                        if (refs == null || refs.Count < 2) throw new Exception("references 至少要 2 個");
                        JObject ln = it["line"] as JObject ?? throw new Exception("缺少 line");

                        var ra = new ReferenceArray();
                        foreach (JToken s in refs)
                            ra.Append(Reference.ParseFromStableRepresentation(doc, s.Value<string>()));

                        XYZ p1 = f.FromUV(ln["u1"].Value<double>(), ln["v1"].Value<double>());
                        XYZ p2 = f.FromUV(ln["u2"].Value<double>(), ln["v2"].Value<double>());
                        Line line = Line.CreateBound(p1, p2);

                        IdType typeId = it["typeId"]?.Value<IdType>() ?? 0;
                        DimensionType dt = typeId > 0 ? doc.GetElement(typeId.ToElementId()) as DimensionType : null;

                        Dimension dim;
                        using (Transaction t = TransactionHelper.Begin(doc, "建立尺寸標註"))
                        {
                            t.Start();
                            dim = dt != null ? doc.Create.NewDimension(view, line, ra, dt) : doc.Create.NewDimension(view, line, ra);
                            if (dim == null) throw new Exception("Revit 沒有建立標註（參考可能不平行或不在此視圖）");
                            TransactionHelper.CommitOrThrow(t);
                        }
                        if (!dim.IsValidObject) throw new Exception("標註建立後被 Revit 刪除（參考無效）");

                        // 文字位置（選填）：每段一個 {u,v}，null 表示不動
                        var textWarnings = new List<string>();

                        // 2026-11-19 實測：參考 Level（GetPlaneReference）的標註在剖面裡建得出來、量得到數值，
                        // 但不會顯示，用視圖篩選也找不到。建完就檢查，看不到要讓呼叫端知道（改用實體面重建）。
                        ElementId newId = dim.Id;
                        bool visibleInView = new FilteredElementCollector(doc, view.Id)
                            .OfClass(typeof(Dimension)).ToElementIds().Contains(newId);
                        if (!visibleInView)
                            textWarnings.Add("標註建立了但在這張視圖上不顯示（常見原因：參考了 Level）。請刪掉，改用該高程上看得見的實體面重建");
                        JArray tps = it["textPositions"] as JArray;
                        if (tps != null && tps.Count > 0)
                        {
                            using (Transaction t = TransactionHelper.Begin(doc, "調整標註文字位置"))
                            {
                                t.Start();
                                if (dim.NumberOfSegments > 0)
                                {
                                    for (int k = 0; k < tps.Count && k < dim.NumberOfSegments; k++)
                                    {
                                        if (!(tps[k] is JObject tp)) continue;
                                        DimensionSegment seg = dim.Segments.get_Item(k);
                                        if (seg.IsTextPositionAdjustable()) seg.TextPosition = f.FromUV(tp["u"].Value<double>(), tp["v"].Value<double>());
                                        else textWarnings.Add($"第 {k} 段文字位置不能調整");
                                    }
                                }
                                else if (tps[0] is JObject tp0)
                                {
                                    if (dim.IsTextPositionAdjustable()) dim.TextPosition = f.FromUV(tp0["u"].Value<double>(), tp0["v"].Value<double>());
                                    else textWarnings.Add("文字位置不能調整");
                                }
                                TransactionHelper.CommitOrThrow(t);
                            }
                        }

                        var values = new List<double?>();
                        if (dim.NumberOfSegments > 0)
                            foreach (DimensionSegment s in dim.Segments) values.Add(s.Value.HasValue ? Math.Round(s.Value.Value * JwFt, 1) : (double?)null);
                        else
                            values.Add(dim.Value.HasValue ? Math.Round(dim.Value.Value * JwFt, 1) : (double?)null);

                        results.Add(new { index = i, success = true, id = JwId(dim.Id), visibleInView, valuesMm = values, textWarnings });
                        okCount++;
                    }
                    catch (Exception ex)
                    {
                        results.Add(new { index = i, success = false, error = ex.Message });
                    }
                }
                tg.Assimilate();
            }

            return new
            {
                viewId = JwId(view.Id),
                viewName = view.Name,
                created = okCount,
                failed = results.Count - okCount,
                results,
            };
        }

        /// <summary>
        /// 在剖面／平面上替梯跑（可在連結模型）建踏板／豎板編號。可指定照某個既有編號的類型與設定（copyFromNumberSystemId），
        /// parameterOverrides 覆寫個別參數（例如起始編號）。
        /// </summary>
        private object CreateStairTreadNumbers(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            View view = JwGetView(doc, parameters);
            JArray items = parameters["items"] as JArray ?? throw new Exception("缺少 items");

            var results = new List<object>();
            using (var tg = new TransactionGroup(doc, "建立踏板編號"))
            {
                tg.Start();
                for (int i = 0; i < items.Count; i++)
                {
                    JObject it = items[i] as JObject;
                    try
                    {
                        long linkId = it?["linkInstanceId"]?.Value<long>() ?? 0;
                        IdType runId = it?["runId"]?.Value<IdType>() ?? 0;
                        RevitLinkInstance li = linkId > 0 ? doc.GetElement(((IdType)linkId).ToElementId()) as RevitLinkInstance : null;
                        if (linkId > 0 && li == null) throw new Exception($"找不到連結實體 ID: {linkId}");
                        Document rdoc = li?.GetLinkDocument() ?? doc;
                        StairsRun run = rdoc.GetElement(runId.ToElementId()) as StairsRun
                                        ?? throw new Exception($"找不到梯跑 ID: {runId}");

                        string optText = it["referenceOption"]?.Value<string>() ?? "Center";
                        if (!Enum.TryParse(optText, true, out StairsNumberSystemReferenceOption opt))
                            throw new Exception($"referenceOption 不認得: {optText}（Center/Left/Right/LeftQuarter/RightQuarter）");

                        NumberSystem src = null;
                        IdType srcId = it["copyFromNumberSystemId"]?.Value<IdType>() ?? 0;
                        if (srcId > 0)
                            src = doc.GetElement(srcId.ToElementId()) as NumberSystem ?? throw new Exception($"找不到來源編號 ID: {srcId}");

                        var numbered = li != null ? new LinkElementId(li.Id, run.Id) : new LinkElementId(run.Id);
                        Stairs st = run.GetStairs();
                        ElementId baseLevel = st?.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;
                        var levelLid = li != null ? new LinkElementId(li.Id, baseLevel) : new LinkElementId(baseLevel);

                        NumberSystem ns;
                        string createdWith;
                        var paramWarnings = new List<string>();
                        using (Transaction t = TransactionHelper.Begin(doc, "建立踏板編號"))
                        {
                            t.Start();
                            try
                            {
                                ns = NumberSystem.Create(doc, view.Id, numbered, opt, levelLid);
                                createdWith = "option+level";
                            }
                            catch (Exception ex1)
                            {
                                Reference r = run.GetNumberSystemReference(opt);
                                if (li != null) r = r.CreateLinkReference(li);
                                ns = NumberSystem.Create(doc, view.Id, numbered, r);
                                createdWith = "reference（option+level 失敗：" + ex1.Message + "）";
                            }

                            JObject overrides = it["parameterOverrides"] as JObject;
                            var overrideNames = new HashSet<string>(overrides?.Properties().Select(p => p.Name) ?? Enumerable.Empty<string>());

                            if (src != null)
                            {
                                if (src.GetTypeId() != ns.GetTypeId()) ns.ChangeTypeId(src.GetTypeId());
                                ns.NumberDisplayRule = src.NumberDisplayRule;
                                ns.JustifyOption = src.JustifyOption;
                                ns.JustifyOffset = src.JustifyOffset;
                                ns.NumberOrientation = src.NumberOrientation;
                                ns.ReferenceOffset = src.ReferenceOffset;
                                foreach (Parameter sp in src.Parameters)
                                {
                                    if (sp?.Definition == null || sp.IsReadOnly || overrideNames.Contains(sp.Definition.Name)) continue;
                                    if (sp.StorageType == StorageType.ElementId || sp.StorageType == StorageType.None) continue;
                                    Parameter tp = ns.LookupParameter(sp.Definition.Name);
                                    if (tp == null || tp.IsReadOnly) continue;
                                    try
                                    {
                                        switch (sp.StorageType)
                                        {
                                            case StorageType.Integer: if (tp.AsInteger() != sp.AsInteger()) tp.Set(sp.AsInteger()); break;
                                            case StorageType.Double: if (Math.Abs(tp.AsDouble() - sp.AsDouble()) > 1e-9) tp.Set(sp.AsDouble()); break;
                                            case StorageType.String: if (tp.AsString() != sp.AsString()) tp.Set(sp.AsString() ?? ""); break;
                                        }
                                    }
                                    catch (Exception pex) { paramWarnings.Add($"{sp.Definition.Name}: {pex.Message}"); }
                                }
                            }

                            if (overrides != null)
                            {
                                foreach (JProperty p in overrides.Properties())
                                {
                                    Parameter tp = ns.LookupParameter(p.Name);
                                    if (tp == null) { paramWarnings.Add($"找不到參數 {p.Name}"); continue; }
                                    try
                                    {
                                        switch (tp.StorageType)
                                        {
                                            case StorageType.Integer: tp.Set(p.Value.Value<int>()); break;
                                            case StorageType.Double:
                                                if (p.Value.Type == JTokenType.String) tp.SetValueString(p.Value.Value<string>());
                                                else tp.Set(p.Value.Value<double>());
                                                break;
                                            case StorageType.String: tp.Set(p.Value.Value<string>()); break;
                                            default: paramWarnings.Add($"{p.Name} 型別 {tp.StorageType} 不支援覆寫"); break;
                                        }
                                    }
                                    catch (Exception pex) { paramWarnings.Add($"{p.Name}: {pex.Message}"); }
                                }
                            }

                            TransactionHelper.CommitOrThrow(t);
                        }

                        results.Add(new
                        {
                            index = i,
                            success = true,
                            id = JwId(ns.Id),
                            runId = JwId(run.Id),
                            risers = run.ActualRisersNumber,
                            referenceOption = opt.ToString(),
                            createdWith,
                            paramWarnings,
                        });
                    }
                    catch (Exception ex)
                    {
                        results.Add(new { index = i, success = false, error = ex.Message });
                    }
                }
                tg.Assimilate();
            }

            return new { viewId = JwId(view.Id), viewName = view.Name, results };
        }

        #endregion
    }
}
