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
        private List<View> _views;
        private Dictionary<string, List<DirectShape>> _shapesBySource;

        public FlatPatternService(Document doc, FpfReport report, Func<ElementId, bool> canEdit)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _report = report ?? new FpfReport();
            _canEdit = canEdit ?? (id => Editability.CanEdit(doc, id));
            _patterns = new PatternReader(doc);
        }

        public FpfReport Report => _report;

        private List<View> Views => _views ?? (_views = ViewGraphics.CollectOverrideViews(_doc));

        private bool CanEdit(ElementId id) => _canEdit(id);

        // =============================================================== apply

        /// <summary>
        /// Ensures the floor is flattened and its hatch is current. Returns true if the model was modified.
        /// </summary>
        public bool Apply(Floor floor, ApplyMode mode, bool applyViewGraphics)
        {
            if (floor == null || !floor.IsValidObject) return false;

            var link = FpfStorage.ReadFloor(floor) ?? new FloorLinkData();
            var wasFlattened = link.Flattened;
            var existing = ResolveLinkedShapes(floor, link, out var linksOk);

            if (mode == ApplyMode.ValidateLinks && wasFlattened && linksOk)
                return false;

            FloorHatchInput input;
            try
            {
                input = FloorHatchBuilder.ReadInput(floor, _patterns);
            }
            catch (Exception ex)
            {
                _report.Errors.Add(FpfReport.Describe(floor) + ": " + ex.Message);
                return false;
            }

            if (mode != ApplyMode.Force && wasFlattened && linksOk && link.Signature == input.Signature)
            {
                _report.Unchanged++;
                if (applyViewGraphics)
                {
                    HideNativePatterns(floor.Id);
                    HideShapesInSectionViews(existing);
                }
                return applyViewGraphics;
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

            // ---- geometry
            var build = FloorHatchBuilder.Build(input, _doc.Application.ShortCurveTolerance);
            if (input.Pieces.Count == 0) _report.NoTopFace.Add(FpfReport.Describe(floor));
            if (build.Truncated) _report.Truncated.Add(FpfReport.Describe(floor));
            if (input.UsedDefaultGrid) _report.DefaultGrid.Add(FpfReport.Describe(floor));
            if (input.UsedDraftingPattern) _report.DraftingScaled.Add(FpfReport.Describe(floor));
            _report.NonPlanarFaces += input.NonPlanarFaces;
            _report.SkippedGrids += build.SkippedGrids;

            var chunks = new List<List<GeometryObject>>();
            var current = new List<GeometryObject>();
            foreach (var seg in build.Segments)
            {
                Line line;
                try { line = Line.CreateBound(seg.A, seg.B); }
                catch { continue; }
                current.Add(line);
                if (current.Count >= FpfOptions.SegmentsPerDirectShape)
                {
                    chunks.Add(current);
                    current = new List<GeometryObject>();
                }
            }
            if (current.Count > 0) chunks.Add(current);

            // ---- DirectShapes (reuse existing elements so ids, hide state and user overrides survive)
            var shapes = new List<DirectShape>();
            var created = new List<DirectShape>();
            for (var i = 0; i < chunks.Count; i++)
            {
                DirectShape ds;
                if (i < existing.Count)
                {
                    ds = existing[i];
                }
                else
                {
                    ds = CreateShape();
                    if (ds == null)
                    {
                        _report.Errors.Add(FpfReport.Describe(floor) + ": DirectShape could not be created.");
                        break;
                    }
                    created.Add(ds);
                }

                try
                {
                    ds.SetShape(chunks[i]);
                }
                catch (Exception ex)
                {
                    _report.Errors.Add(FpfReport.Describe(floor) + ": SetShape failed - " + ex.Message);
                }

                ConfigureShape(ds, floor, i, chunks.Count);
                shapes.Add(ds);
                _report.Segments += chunks[i].Count;
            }

            for (var i = chunks.Count; i < existing.Count; i++)
            {
                try { _doc.Delete(existing[i].Id); }
                catch { /* ignore */ }
            }

            // ---- floor link (written last; signature guards the updater against re-trigger loops)
            link.Flattened = true;
            link.Version = FpfOptions.SchemaDataVersion;
            link.DirectShapeUniqueIds = shapes.Select(s => s.UniqueId).ToList();
            link.Signature = input.Signature;
            link.DependencyUniqueIds = input.DependencyUniqueIds.ToList();
            FpfStorage.WriteFloor(floor, link);
            InvalidateShapeIndex();

            if (wasFlattened) _report.Updated++;
            else _report.Created++;

            // ---- view graphics (full pass for new floors, copied floors and recreated shapes)
            if (applyViewGraphics || !wasFlattened || !linksOk)
            {
                HideNativePatterns(floor.Id);
                HideShapesInSectionViews(shapes);
            }
            else if (created.Count > 0)
            {
                HideShapesInSectionViews(created);
            }

            return true;
        }

        private DirectShape CreateShape()
        {
            var cat = new ElementId(BuiltInCategory.OST_GenericModel);
            if (!DirectShape.IsValidCategoryId(cat, _doc)) return null;
            var ds = DirectShape.CreateElement(_doc, cat);
            ds.ApplicationId = FpfOptions.ApplicationId;
            return ds;
        }

        private void ConfigureShape(DirectShape ds, Floor floor, int chunkIndex, int chunkCount)
        {
            try
            {
                ds.ApplicationId = FpfOptions.ApplicationId;
                ds.ApplicationDataId = floor.UniqueId + (chunkIndex > 0 ? ":" + chunkIndex : string.Empty);
            }
            catch { /* ignore */ }

            try
            {
                var name = FpfOptions.DirectShapeNamePrefix + floor.Id +
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
                    if (ds.CreatedPhaseId != floor.CreatedPhaseId) ds.CreatedPhaseId = floor.CreatedPhaseId;
                    if (ds.DemolishedPhaseId != floor.DemolishedPhaseId) ds.DemolishedPhaseId = floor.DemolishedPhaseId;
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

        /// <summary>Applies native-pattern hiding (and section hiding of shapes) in newly created views.</summary>
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
                    try { ViewGraphics.SetNativeSurfacePatternVisible(v, f.Id, false); }
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

        private void HideNativePatterns(ElementId floorId)
        {
            ViewGraphics.ForEachView(Views, CanEdit, _report,
                v => ViewGraphics.SetNativeSurfacePatternVisible(v, floorId, false));
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
                return FloorHatchBuilder.ReadInput(floor, patterns).Signature != link.Signature;
            }
            catch
            {
                return false;
            }
        }
    }
}
