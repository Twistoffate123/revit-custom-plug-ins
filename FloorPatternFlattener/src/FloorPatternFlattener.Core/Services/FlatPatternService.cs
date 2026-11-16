using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using FloorPatternFlattener.Geometry;
using FloorPatternFlattener.Storage;

namespace FloorPatternFlattener.Services
{
    public enum ApplyMode
    {
        /// <summary>Only rebuild if the linked DirectShape(s) are missing / not linked back (cheap, no geometry).</summary>
        ValidateLinks,

        /// <summary>Rebuild if the input signature changed or links are broken.</summary>
        IfChanged,

        /// <summary>Always rebuild.</summary>
        Force
    }

    /// <summary>
    /// Creates, updates and deletes the flat-pattern DirectShapes for floors and keeps the per-view overrides in
    /// sync. One instance per command / updater run. Every public method must be called while the document is
    /// modifiable (inside a Transaction, or inside IUpdater.Execute).
    /// </summary>
    public sealed class FlatPatternService
    {
        private readonly Document _doc;
        private readonly FpfReport _report;
        private readonly Func<ElementId, bool> _canEdit;
        private readonly PatternReader _patterns;
        private readonly PatternStyles _styles;
        private List<View> _views;
        private Dictionary<string, List<DirectShape>> _shapesBySource;

        public FlatPatternService(Document doc, FpfReport report, Func<ElementId, bool> canEdit)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _report = report ?? new FpfReport();
            _canEdit = canEdit ?? (id => Editability.CanEdit(doc, id));
            _patterns = new PatternReader(doc);
            _styles = new PatternStyles(doc);
        }

        public FpfReport Report => _report;

        private List<View> Views => _views ?? (_views = ViewGraphics.CollectOverrideViews(_doc));

        private bool CanEdit(ElementId id) => _canEdit(id);

        // =============================================================== apply

        /// <summary>
        /// Ensures the floor is flattened and its flat pattern is current. <paramref name="requestedStyle"/> (from the
        /// Flatten dialog) sets / switches the floor's style; null keeps the stored one (Refresh, updater).
        /// Returns true if the model was modified.
        /// </summary>
        public bool Apply(Floor floor, ApplyMode mode, bool applyViewGraphics, FlattenStyle? requestedStyle = null,
            PatternPlacement requestedPlacement = null, bool? requestedHideLive = null)
        {
            if (floor == null || !floor.IsValidObject) return false;
            try
            {
                return ApplyCore(floor, mode, applyViewGraphics, requestedStyle, requestedPlacement, requestedHideLive);
            }
            catch (Exception ex)
            {
                _report.AddSkipped(floor, ex.Message);
                return false;
            }
        }

        private bool ApplyCore(Floor floor, ApplyMode mode, bool applyViewGraphics, FlattenStyle? requestedStyle,
            PatternPlacement requestedPlacement, bool? requestedHideLive)
        {
            // "Hide live floor pattern and fold lines": a mark in the floor's Comments that the
            // "FPF - Live Floor (hidden pattern)" view filter keys on (works through links, "By Host View").
            var markChanged = false;
            if (requestedHideLive.HasValue && FpfStorage.ReadHideLive(floor) != requestedHideLive.Value)
            {
                if (CanEdit(floor.Id))
                {
                    try { markChanged = FpfStorage.WriteHideLive(floor, requestedHideLive.Value); }
                    catch (Exception ex) { _report.Errors.Add(FpfReport.Describe(floor) + ": Comments mark - " + ex.Message); }
                    if (markChanged) _report.HideLiveChanged++;
                }
                else _report.AddNotEditable(floor, "floor not editable (hide live pattern mark)");
            }

            try
            {
                if (floor.GroupId != null && floor.GroupId != ElementId.InvalidElementId)
                {
                    _report.AddSkipped(floor, "inside a model group; ungroup it or edit the group before flattening");
                    return markChanged;
                }
            }
            catch { /* GroupId not available */ }

            try
            {
                if (floor.DesignOption != null)
                    _report.AddWarning(floor, "in design option \"" + floor.DesignOption.Name +
                        "\". The flat pattern is created in the main model and may not follow that option's visibility.");
            }
            catch { /* ignore */ }

            var link = FpfStorage.ReadFloor(floor) ?? new FloorLinkData();
            var wasFlattened = link.Flattened;
            var storedStyle = FpfStorage.ReadStyle(floor);
            var style = requestedStyle ?? storedStyle;
            // Floors flattened with the removed Surface style: always rebuild as Lines and drop the old surface shape.
            var legacySurface = FpfStorage.HasLegacySurfaceStyle(floor);
            if (legacySurface && mode != ApplyMode.Force) mode = ApplyMode.Force;
            var storedPlacement = FpfStorage.ReadPlacement(floor);
            var placement = requestedPlacement ?? storedPlacement;
            var placementChanged = !placement.SameAs(storedPlacement);
            _currentPlacement = placement;
            LastFloorSegments = 0;
            var existing = ResolveLinkedShapes(floor, link, out var linksOk);

            if (mode == ApplyMode.ValidateLinks && wasFlattened && linksOk && style == storedStyle && !placementChanged)
                return false;

            FloorHatchInput input;
            try
            {
                input = FloorHatchBuilder.ReadInput(floor, _patterns);
            }
            catch (Exception ex)
            {
                _report.AddSkipped(floor, ex.Message);
                return markChanged;
            }

            var signature = EffectiveSignature(input.Signature, style, placement);

            if (mode != ApplyMode.Force && wasFlattened && linksOk && link.Signature == signature)
            {
                _report.Unchanged++;
                var touched = false;
                if (applyViewGraphics)
                {
                    SyncNativePatterns(floor.Id);
                    HideShapesInSectionViews(existing);
                }
                return applyViewGraphics || touched || markChanged;
            }

            // ---- editability (worksharing)
            if (!CanEdit(floor.Id))
            {
                _report.AddNotEditable(floor, "floor not editable");
                return false;
            }
            foreach (var ds in existing)
            {
                if (!CanEdit(ds.Id))
                {
                    _report.AddNotEditable(ds, "flat pattern shape of Floor " + floor.Id + " not editable");
                    return false;
                }
            }

            if (input.Pieces.Count == 0)
            {
                _report.AddSkipped(floor, string.IsNullOrEmpty(input.FailureReason)
                    ? "no upward-facing top face"
                    : input.FailureReason);
                return markChanged;
            }
            _report.NonPlanarFaces += input.NonPlanarFaces;
            foreach (var note in input.Notes) _report.AddWarning(floor, note);

            var build = FloorHatchBuilder.Build(input, _doc.Application.ShortCurveTolerance, _currentPlacement);
            if (build.Segments.Count == 0)
            {
                var why = "pattern produced no lines";
                if (build.SkippedGrids > 0) why += "; " + build.SkippedGrids + " grids had degenerate spacing";
                if (build.SkippedShort > 0) why += "; " + build.SkippedShort + " fragments were shorter than Revit can store";
                foreach (var n in build.Notes) why += "; " + n;
                _report.AddSkipped(floor, why);
                return markChanged;
            }
            if (build.SkippedShort > 0)
                _report.AddWarning(floor, build.SkippedShort + " degenerate fragments could not be stored");
            foreach (var n in build.Notes) _report.AddWarning(floor, n);

            if (legacySurface)
            {
                // The old surface DirectShape (type "FPF Flat Pattern - Surface") is not reused: delete it so the
                // Lines rebuild starts clean with fresh shapes.
                foreach (var ds in existing)
                {
                    try { _doc.Delete(ds.Id); }
                    catch { /* ignore */ }
                }
                existing = new List<DirectShape>();
                _report.LegacySurfaceReplaced++;
            }

            var shapes = new List<DirectShape>();
            var created = new List<DirectShape>();
            BuildLineShapes(floor, input, build, existing, shapes, created);

            if (shapes.Count == 0 || LastFloorSegments == 0)
            {
                _report.AddSkipped(floor, "Revit rejected the hatch lines (DirectShape could not be created).");
                return markChanged;
            }

            var used = new HashSet<string>();
            foreach (var s in shapes) used.Add(s.UniqueId);
            foreach (var ds in existing)
            {
                if (ds != null && ds.IsValidObject && !used.Contains(ds.UniqueId))
                {
                    try { _doc.Delete(ds.Id); } catch { /* ignore */ }
                }
            }

            if (LastFloorSegments > FpfOptions.HeavyPatternConfirmSegments)
                _report.Heavy.Add(FpfReport.Describe(floor) + ": " + LastFloorSegments.ToString("N0") +
                    " lines, split across " + shapes.Count + " shapes. The full hatch was written.");

            // ---- floor link (written last; signature guards the updater against re-trigger loops)
            if (style != storedStyle || legacySurface) FpfStorage.WriteStyle(floor, style);
            if (placementChanged) FpfStorage.WritePlacement(floor, placement);
            link.Flattened = true;
            link.Version = FpfOptions.SchemaDataVersion;
            link.DirectShapeUniqueIds = shapes.Select(s => s.UniqueId).ToList();
            link.Signature = signature;
            link.DependencyUniqueIds = input.DependencyUniqueIds.ToList();
            FpfStorage.WriteFloor(floor, link);
            InvalidateShapeIndex();

            if (wasFlattened) _report.Updated++;
            else _report.Created++;
            _report.LineFloors++;

            // ---- view graphics (full pass for new floors, copied floors and recreated shapes)
            if (applyViewGraphics || !wasFlattened || !linksOk)
            {
                SyncNativePatterns(floor.Id);
                HideShapesInSectionViews(shapes);
            }
            else if (created.Count > 0)
            {
                HideShapesInSectionViews(created);
            }

            return true;
        }

        /// <summary>
        /// Lines keep the original signature (no rebuild of existing floors after the upgrade).
        /// </summary>
        private static string EffectiveSignature(string inputSignature, FlattenStyle style, PatternPlacement placement)
        {
            var sig = inputSignature;
            // Identity adds nothing, so floors flattened before rotation existed are not rebuilt.
            if (placement != null && !placement.IsIdentity) sig += "|P" + placement.ToStorage();
            return sig;
        }

        private PatternPlacement _currentPlacement = PatternPlacement.Identity;

        /// <summary>Line segments written for the floor of the last <see cref="Apply"/> call (Lines mode).</summary>
        public int LastFloorSegments { get; private set; }

        /// <summary>Reuses <paramref name="slot"/> or creates one. The same slot returns the same element.</summary>
        private DirectShape ShapeAt(Floor floor, List<DirectShape> existing, List<DirectShape> created, int slot)
        {
            if (slot < existing.Count) return existing[slot];
            var extra = slot - existing.Count;
            if (extra < created.Count) return created[extra];
            var ds = CreateShape();
            if (ds == null)
            {
                _report.AddSkipped(floor, "DirectShape could not be created");
                return null;
            }
            created.Add(ds);
            return ds;
        }

        // ------------------------------------------------------------ lines mode

        /// <summary>Lines mode: hatch lines per pattern layer, one or more DirectShapes per layer. No segment cap.</summary>
        private void BuildLineShapes(Floor floor, FloorHatchInput input, FloorHatchResult build, List<DirectShape> existing,
            List<DirectShape> shapes, List<DirectShape> created)
        {
            if (input.UsedDefaultGrid) _report.DefaultGrid.Add(FpfReport.Describe(floor));
            if (input.UsedDraftingPattern) _report.DraftingScaled.Add(FpfReport.Describe(floor));
            _report.SkippedGrids += build.SkippedGrids;

            // One group per pattern layer (background first, then foreground; per colour), each split into chunks.
            // Every chunk becomes its own DirectShape so foreground and background can be filtered separately.
            var chunks = new List<List<Segment3>>();
            var chunkLayers = new List<PatternLayer>();
            var groups = new List<KeyValuePair<PatternLayer, List<Segment3>>>();
            var groupIndex = new Dictionary<string, int>();
            foreach (var seg in build.Segments)
            {
                var key = seg.Layer?.Key ?? "-";
                if (!groupIndex.TryGetValue(key, out var gi))
                {
                    gi = groups.Count;
                    groupIndex[key] = gi;
                    groups.Add(new KeyValuePair<PatternLayer, List<Segment3>>(seg.Layer, new List<Segment3>()));
                }
                groups[gi].Value.Add(seg);
            }
            foreach (var g in groups.OrderBy(g => g.Key == null ? 1 : (int)g.Key.Kind))
            {
                for (var start = 0; start < g.Value.Count; start += FpfOptions.SegmentsPerDirectShape)
                {
                    chunks.Add(g.Value.GetRange(start, Math.Min(FpfOptions.SegmentsPerDirectShape, g.Value.Count - start)));
                    chunkLayers.Add(g.Key);
                }
            }

            for (var i = 0; i < chunks.Count; i++)
                CommitChunk(floor, chunks[i], chunkLayers[i], existing, created, shapes);
        }

        /// <summary>
        /// Writes one chunk. If Revit rejects the shape, the chunk is split in half and retried so one bad
        /// line cannot drop the rest of the floor. There is no maximum number of shapes.
        /// </summary>
        private void CommitChunk(Floor floor, List<Segment3> segs, PatternLayer layer, List<DirectShape> existing,
            List<DirectShape> created, List<DirectShape> shapes)
        {
            if (segs == null || segs.Count == 0) return;
            var ds = ShapeAt(floor, existing, created, shapes.Count);
            if (ds == null) return;
            var styleId = _styles.GetLineStyleId(layer);
            if (!TrySetShape(ds, segs, styleId))
            {
                if (segs.Count <= 1)
                {
                    _report.AddWarning(floor, "1 hatch line was rejected by Revit and left out");
                    return;
                }
                var mid = segs.Count / 2;
                CommitChunk(floor, segs.GetRange(0, mid), layer, existing, created, shapes);
                CommitChunk(floor, segs.GetRange(mid, segs.Count - mid), layer, existing, created, shapes);
                return;
            }

            var kind = layer?.Kind ?? PatternLayerKind.Foreground;
            ConfigureShape(ds, floor, shapes.Count, shapes.Count + 1, _styles.GetTypeId(kind), kind.ToString());
            shapes.Add(ds);
            _report.Segments += segs.Count;
            LastFloorSegments += segs.Count;
            if (layer != null && layer.Kind == PatternLayerKind.Background) _report.BackgroundSegments += segs.Count;
        }

        private bool TrySetShape(DirectShape ds, List<Segment3> segs, ElementId styleId)
        {
            try
            {
                var geo = MakeLines(segs, styleId);
                if (geo.Count == 0) return false;
                ds.SetShape(geo);
                return true;
            }
            catch
            {
                if (styleId != null && styleId != ElementId.InvalidElementId)
                {
                    try
                    {
                        var geo = MakeLines(segs, ElementId.InvalidElementId);
                        if (geo.Count == 0) return false;
                        ds.SetShape(geo);
                        _report.StyleFallbacks++;
                        return true;
                    }
                    catch { return false; }
                }
                return false;
            }
        }

        private DirectShape CreateShape()
        {
            var cat = new ElementId(BuiltInCategory.OST_GenericModel);
            if (!DirectShape.IsValidCategoryId(cat, _doc)) return null;
            var ds = DirectShape.CreateElement(_doc, cat);
            ds.ApplicationId = FpfOptions.ApplicationId;
            return ds;
        }

        private static List<GeometryObject> MakeLines(List<Segment3> segments, ElementId styleId)
        {
            var list = new List<GeometryObject>(segments.Count);
            var styled = styleId != null && styleId != ElementId.InvalidElementId;
            foreach (var seg in segments)
            {
                Line line;
                try { line = Line.CreateBound(seg.A, seg.B); }
                catch { continue; }
                if (styled) line.SetGraphicsStyleId(styleId);
                list.Add(line);
            }
            return list;
        }

        private void ConfigureShape(DirectShape ds, Floor floor, int chunkIndex, int chunkCount, ElementId typeId,
            string label)
        {
            // Type per layer / mode: lets view filters target "Type Name" / "Type Comments" =
            // FPF Flat Pattern - Foreground / Background.
            try
            {
                if (typeId != ElementId.InvalidElementId && ds.GetTypeId() != typeId) ds.SetTypeId(typeId);
            }
            catch { /* ignore - shape keeps no type */ }

            try
            {
                ds.ApplicationId = FpfOptions.ApplicationId;
                ds.ApplicationDataId = floor.UniqueId + (chunkIndex > 0 ? ":" + chunkIndex : string.Empty);
            }
            catch { /* ignore */ }

            try
            {
                var name = FpfOptions.DirectShapeNamePrefix + floor.Id + " " + label +
                           (chunkCount > 1 ? " (" + (chunkIndex + 1) + "/" + chunkCount + ")" : string.Empty);
                ds.SetName(name);
            }
            catch { /* ignore */ }

            SetString(ds, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, FpfOptions.CommentsTag);

            // Phases: match the floor.
            try
            {
                if (ds.ArePhasesModifiable())
                {
                    // Clear demolition first so a later created-phase is not rejected as "after demolition".
                    var created = floor.CreatedPhaseId;
                    var demo = floor.DemolishedPhaseId;
                    var none = ElementId.InvalidElementId;
                    if (ds.DemolishedPhaseId != none && ds.DemolishedPhaseId != demo)
                        ds.DemolishedPhaseId = none;
                    if (ds.CreatedPhaseId != created) ds.CreatedPhaseId = created;
                    if (ds.DemolishedPhaseId != demo) ds.DemolishedPhaseId = demo;
                }
            }
            catch { /* ignore - e.g. demolished before created while switching */ }

            // Workset: match the floor.
            try
            {
                if (_doc.IsWorkshared)
                {
                    var p = ds.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    var target = floor.WorksetId.IntegerValue;
                    if (p != null && !p.IsReadOnly && p.AsInteger() != target) p.Set(target);
                }
            }
            catch { /* ignore */ }

            // Level: DirectShapes usually have no settable level; try the schedule level where exposed.
            TrySetLevel(ds, floor.LevelId);

            try
            {
                FpfStorage.WriteDirectShape(ds, new DirectShapeLinkData
                {
                    SourceFloorUniqueId = floor.UniqueId,
                    ChunkIndex = chunkIndex
                });
            }
            catch (Exception ex)
            {
                _report.Errors.Add("Could not write link data on " + FpfReport.Describe(ds) + ": " + ex.Message);
            }

            try { if (!ds.Pinned) ds.Pinned = true; } catch { /* ignore */ }
        }

        private static void SetString(Element e, BuiltInParameter bip, string value)
        {
            try
            {
                var p = e.get_Parameter(bip);
                if (p != null && !p.IsReadOnly && p.AsString() != value) p.Set(value);
            }
            catch { /* ignore */ }
        }

        private static void TrySetLevel(Element e, ElementId levelId)
        {
            if (levelId == null || levelId == ElementId.InvalidElementId) return;
            foreach (var bip in new[]
                     {
                         BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM,
                         BuiltInParameter.SCHEDULE_LEVEL_PARAM,
                         BuiltInParameter.FAMILY_LEVEL_PARAM
                     })
            {
                try
                {
                    var p = e.get_Parameter(bip);
                    if (p == null || p.IsReadOnly || p.StorageType != StorageType.ElementId) continue;
                    if (p.AsElementId() != levelId) p.Set(levelId);
                    return;
                }
                catch { /* try next */ }
            }
        }

        // =============================================================== clear

        /// <summary>Deletes the floor's flat pattern, removes the link and restores native pattern visibility.</summary>
        public bool Clear(Floor floor)
        {
            if (floor == null || !floor.IsValidObject) return false;
            if (!CanEdit(floor.Id))
            {
                _report.AddNotEditable(floor, "floor not editable");
                return false;
            }

            var shapes = new Dictionary<ElementId, DirectShape>();
            var link = FpfStorage.ReadFloor(floor);
            if (link != null)
            {
                foreach (var uid in link.DirectShapeUniqueIds)
                {
                    if (_doc.GetElement(uid) is DirectShape ds)
                    {
                        var dl = FpfStorage.ReadDirectShape(ds);
                        if (dl != null && dl.SourceFloorUniqueId == floor.UniqueId) shapes[ds.Id] = ds;
                    }
                }
            }
            if (ShapeIndex.TryGetValue(floor.UniqueId, out var more))
                foreach (var ds in more) shapes[ds.Id] = ds;

            foreach (var ds in shapes.Values)
            {
                if (!CanEdit(ds.Id))
                {
                    _report.AddNotEditable(ds, "flat pattern shape of Floor " + floor.Id + " not editable");
                    return false;
                }
            }

            if (shapes.Count > 0) _doc.Delete(shapes.Keys.ToList());
            FpfStorage.DeleteFloor(floor);
            FpfStorage.DeleteStyle(floor);
            FpfStorage.DeletePlacement(floor);
            try { FpfStorage.WriteHideLive(floor, false); } catch { /* ignore */ }
            InvalidateShapeIndex();

            var id = floor.Id;
            ViewGraphics.ForEachView(Views, CanEdit, _report,
                v => ViewGraphics.SetNativeSurfacePatternVisible(v, id, true));

            _report.Cleared++;
            return true;
        }

        // =============================================================== housekeeping

        /// <summary>
        /// Deletes generated DirectShapes whose source floor is gone, no longer flattened, or does not link back to
        /// them (e.g. a DirectShape that was copy/pasted on its own).
        /// </summary>
        public int CleanupOrphans()
        {
            var deleted = 0;
            var toDelete = new List<ElementId>();
            var linkCache = new Dictionary<string, FloorLinkData>();

            foreach (var ds in FpfStorage.GetGeneratedDirectShapes(_doc))
            {
                var dl = FpfStorage.ReadDirectShape(ds);
                var orphan = true;
                if (dl != null && !string.IsNullOrEmpty(dl.SourceFloorUniqueId))
                {
                    if (!linkCache.TryGetValue(dl.SourceFloorUniqueId, out var fl))
                    {
                        fl = _doc.GetElement(dl.SourceFloorUniqueId) is Floor f ? FpfStorage.ReadFloor(f) : null;
                        linkCache[dl.SourceFloorUniqueId] = fl;
                    }
                    orphan = fl == null || !fl.Flattened || !fl.DirectShapeUniqueIds.Contains(ds.UniqueId);
                }

                if (!orphan) continue;
                if (!CanEdit(ds.Id))
                {
                    _report.AddNotEditable(ds, "orphaned flat pattern shape not editable");
                    continue;
                }
                toDelete.Add(ds.Id);
            }

            if (toDelete.Count > 0)
            {
                try
                {
                    _doc.Delete(toDelete);
                    deleted = toDelete.Count;
                }
                catch (Exception ex)
                {
                    _report.Errors.Add("Cleanup failed: " + ex.Message);
                }
                InvalidateShapeIndex();
            }

            _report.OrphansDeleted += deleted;
            return deleted;
        }

        /// <summary>
        /// New views: makes sure the floors' live pattern is visible (a view duplicated from one that an older version
        /// of the add-in overrode would otherwise inherit the hidden pattern) and hides the shapes in section views.
        /// </summary>
        public void ApplyToViews(IEnumerable<View> views)
        {
            var targets = views.Where(ViewGraphics.IsOverrideCandidate).ToList();
            if (targets.Count == 0) return;

            var floors = FpfStorage.GetFlattenedFloors(_doc);
            if (floors.Count == 0) return;

            var shapes = FpfStorage.GetGeneratedDirectShapes(_doc).Cast<Element>().ToList();
            ViewGraphics.ForEachView(targets, CanEdit, _report, v =>
            {
                foreach (var f in floors)
                {
                    try { ViewGraphics.SyncNativeSurfacePattern(v, f.Id); }
                    catch { _report.ViewFailures++; }
                }

                if (FpfOptions.HideInSectionViews && ViewGraphics.IsSectionLike(v) && shapes.Count > 0)
                    ViewGraphics.HideInView(v, shapes);
            });
        }

        /// <summary>Hides all generated shapes in all section-like views (used by Refresh).</summary>
        public void HideAllShapesInSectionViews()
        {
            var shapes = FpfStorage.GetGeneratedDirectShapes(_doc);
            HideShapesInSectionViews(shapes);
        }

        // =============================================================== helpers

        /// <summary>
        /// Keeps the floor's live surface pattern visible in every view (plans, 3D, sections, and so sheets too):
        /// removes the "pattern hidden" element override that earlier versions set. Element overrides beat view
        /// filters, so that override also stopped filters from controlling the floor's pattern.
        /// </summary>
        private void SyncNativePatterns(ElementId floorId)
        {
            ViewGraphics.ForEachView(Views, CanEdit, _report,
                v => ViewGraphics.SyncNativeSurfacePattern(v, floorId));
        }

        private void HideShapesInSectionViews(IEnumerable<DirectShape> shapes)
        {
            if (!FpfOptions.HideInSectionViews) return;
            var list = shapes.Where(s => s != null && s.IsValidObject).Cast<Element>().ToList();
            if (list.Count == 0) return;
            ViewGraphics.ForEachView(ViewGraphics.SectionLike(Views), CanEdit, _report,
                v => ViewGraphics.HideInView(v, list));
        }

        /// <summary>
        /// Resolves the DirectShapes listed on the floor, keeping only ones that link back to this floor
        /// (a copied floor carries its original's link, which must not be reused).
        /// </summary>
        private List<DirectShape> ResolveLinkedShapes(Floor floor, FloorLinkData link, out bool allResolved)
        {
            var list = new List<DirectShape>();
            allResolved = true;
            foreach (var uid in link.DirectShapeUniqueIds)
            {
                var ds = string.IsNullOrEmpty(uid) ? null : _doc.GetElement(uid) as DirectShape;
                var dl = ds == null ? null : FpfStorage.ReadDirectShape(ds);
                if (dl != null && dl.SourceFloorUniqueId == floor.UniqueId) list.Add(ds);
                else allResolved = false;
            }
            return list;
        }

        private Dictionary<string, List<DirectShape>> ShapeIndex
        {
            get
            {
                if (_shapesBySource != null) return _shapesBySource;
                _shapesBySource = new Dictionary<string, List<DirectShape>>();
                foreach (var ds in FpfStorage.GetGeneratedDirectShapes(_doc))
                {
                    var dl = FpfStorage.ReadDirectShape(ds);
                    if (dl == null || string.IsNullOrEmpty(dl.SourceFloorUniqueId)) continue;
                    if (!_shapesBySource.TryGetValue(dl.SourceFloorUniqueId, out var l))
                        _shapesBySource[dl.SourceFloorUniqueId] = l = new List<DirectShape>();
                    l.Add(ds);
                }
                return _shapesBySource;
            }
        }

        private void InvalidateShapeIndex() => _shapesBySource = null;

        /// <summary>Read-only staleness check used on document open (no model changes).</summary>
        public static bool IsStale(Floor floor, PatternReader patterns)
        {
            var link = FpfStorage.ReadFloor(floor);
            if (link == null || !link.Flattened) return false;
            foreach (var uid in link.DirectShapeUniqueIds)
            {
                var ds = floor.Document.GetElement(uid) as DirectShape;
                var dl = ds == null ? null : FpfStorage.ReadDirectShape(ds);
                if (dl == null || dl.SourceFloorUniqueId != floor.UniqueId) return true;
            }
            try
            {
                var sig = EffectiveSignature(FloorHatchBuilder.ReadInput(floor, patterns).Signature, FpfStorage.ReadStyle(floor),
                    FpfStorage.ReadPlacement(floor));
                return sig != link.Signature;
            }
            catch
            {
                return false;
            }
        }
    }
}
