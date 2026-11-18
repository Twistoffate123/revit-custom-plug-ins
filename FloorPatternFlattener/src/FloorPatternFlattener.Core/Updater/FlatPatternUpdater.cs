using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using FloorPatternFlattener.Services;
using FloorPatternFlattener.Storage;

namespace FloorPatternFlattener.Updater
{
    /// <summary>
    /// Dynamic Model Update: keeps flat-pattern DirectShapes in sync with their floors.
    ///
    /// Triggers
    ///  - Floor: geometry, any change (type / parameters / paint), addition (copies), deletion
    ///  - FloorType: any change (layers, materials, thickness)
    ///  - Material: any change (surface patterns)
    ///  - FillPatternElement: any change (pattern definition)
    ///  - View: addition (new views get the native-pattern override / section hiding)
    ///  - DirectShape: deletion only (recreate if a user deletes a flat pattern)
    ///
    /// Output edits (group offset, shape transforms) are ignored. Work is deferred to Idling (never inside the user transaction).
    /// </summary>
    public sealed class FlatPatternUpdater : IUpdater
    {
        public static readonly Guid UpdaterGuid = new Guid("3E9D6C21-7A4B-4F85-B1D2-8C0E5A7F9B34");

        public static UpdaterId RegisteredUpdaterId { get; private set; }

        public static readonly FailureDefinitionId WarningId =
            new FailureDefinitionId(new Guid("9B1F4A7C-2D6E-4C83-A5F0-E7D3B8216C49"));

        private readonly UpdaterId _id;

        public FlatPatternUpdater(AddInId addInId)
        {
            _id = new UpdaterId(addInId, UpdaterGuid);
        }

        public UpdaterId GetUpdaterId() => _id;

        public ChangePriority GetChangePriority() => ChangePriority.FloorsRoofsStructuralWalls;

        public string GetUpdaterName() => "Floor Pattern Flattener - flat pattern sync";

        public string GetAdditionalInformation() =>
            "Regenerates flat floor-pattern lines (DirectShape) when flattened floors, their types, materials or " +
            "fill patterns change. Optional: models open fine without the add-in; hatches then just do not update.";

        /// <summary>Registers the updater (optional) and its triggers, plus the warning failure definition.</summary>
        public static void Register(AddInId addInId, UIControlledApplication uiApp = null)
        {
            try
            {
                FailureDefinition.CreateFailureDefinition(WarningId, FailureSeverity.Warning,
                    "Floor Pattern Flattener: some flat patterns could not be updated because elements are not " +
                    "editable (worksharing). Obtain the elements and run Refresh Flattened Patterns.");
            }
            catch
            {
                // already registered
            }

            var updater = new FlatPatternUpdater(addInId);
            var id = updater.GetUpdaterId();
            // Re-register every startup so an older trigger set (Any-change, DirectShape deletion)
            // cannot keep rebuilding output when the user edits a pattern group.
            try
            {
                if (UpdaterRegistry.IsUpdaterRegistered(id))
                    UpdaterRegistry.UnregisterUpdater(id);
            }
            catch
            {
                // in use
            }

            UpdaterRegistry.RegisterUpdater(updater, true /* isOptional */);
            RegisteredUpdaterId = id;

            var floors = new ElementClassFilter(typeof(Floor));
            UpdaterRegistry.AddTrigger(id, floors, Element.GetChangeTypeGeometry());
            UpdaterRegistry.AddTrigger(id, floors,
                Element.GetChangeTypeParameter(new ElementId(BuiltInParameter.ELEM_TYPE_PARAM)));
            UpdaterRegistry.AddTrigger(id, floors, Element.GetChangeTypeElementAddition());
            UpdaterRegistry.AddTrigger(id, floors, Element.GetChangeTypeElementDeletion());

            UpdaterRegistry.AddTrigger(id, new ElementClassFilter(typeof(FloorType)), Element.GetChangeTypeAny());
            UpdaterRegistry.AddTrigger(id, new ElementClassFilter(typeof(Material)), Element.GetChangeTypeAny());
            UpdaterRegistry.AddTrigger(id, new ElementClassFilter(typeof(FillPatternElement)), Element.GetChangeTypeAny());
            UpdaterRegistry.AddTrigger(id, new ElementClassFilter(typeof(View)), Element.GetChangeTypeElementAddition());

            if (uiApp != null) FlatPatternUpdaterDeferred.Bind(uiApp);
        }

        public static void Unregister(AddInId addInId)
        {
            try
            {
                var id = new UpdaterId(addInId, UpdaterGuid);
                if (UpdaterRegistry.IsUpdaterRegistered(id)) UpdaterRegistry.UnregisterUpdater(id);
            }
            catch
            {
                // shutting down
            }
        }

        public void Execute(UpdaterData data)
        {
            Document doc;
            try { doc = data.GetDocument(); } catch { return; }
            if (doc == null || doc.IsFamilyDocument) return;

            var modified = data.GetModifiedElementIds() ?? new List<ElementId>();
            var added = data.GetAddedElementIds() ?? new List<ElementId>();
            var deleted = data.GetDeletedElementIds() ?? new List<ElementId>();
            FpfRefreshLog.LogUpdater("updater-trigger ids=" + CountIds(modified) + "/" + CountIds(added) +
                                     " deleted=" + deleted.Count.ToString(CultureInfo.InvariantCulture) +
                                     " sample=" + FirstIds(modified, added, 8));

            if (!AutoUpdateSetting.Enabled)
            {
                FpfRefreshLog.LogUpdater("action=auto-off");
                return;
            }

            if (FlatPatternUpdaterGate.IsSuppressed)
            {
                Services.FpfRefreshLog.Current?.UpdaterBlocked();
                FpfRefreshLog.LogUpdater("action=suppressed-bulk");
                return;
            }

            try
            {
                PlanAndEnqueue(doc, data);
            }
            catch (Exception ex)
            {
                FpfRefreshLog.LogUpdater("action=execute-error msg=" + ex.Message);
            }
        }

        private static void PlanAndEnqueue(Document doc, UpdaterData data)
        {
            var modified = data.GetModifiedElementIds() ?? new List<ElementId>();
            var added = data.GetAddedElementIds() ?? new List<ElementId>();
            var deleted = data.GetDeletedElementIds() ?? new List<ElementId>();
            var allChanged = modified.Concat(added).ToList();

            var triggerLog = new StringBuilder();
            triggerLog.Append("ids=");
            AppendIds(triggerLog, allChanged, 24);
            triggerLog.Append(" deleted=");
            AppendIds(triggerLog, deleted, 12);

            if (IsOutputOnlyChange(doc, modified.Concat(added).ToList()))
            {
                FpfRefreshLog.LogUpdater("action=ignore-output-edits " + triggerLog);
                return;
            }

            var work = FlatPatternUpdaterDeferred.CreateWork(doc);
            var meaningful = new List<ElementId>();
            var floorsToCheck = new Dictionary<ElementId, Floor>();

            foreach (var id in allChanged)
            {
                var el = doc.GetElement(id);
                if (FpfOutputElements.IsIgnorableOutputElement(el, doc)) continue;
                meaningful.Add(id);

                switch (el)
                {
                    case Floor f:
                        if (!FpfStorage.IsFlattened(f)) break;
                        if (FpfOutputElements.OnlyOutputHierarchyTouched(f, doc, allChanged))
                            break;
                        floorsToCheck[f.Id] = f;
                        work.FloorIds.Add(f.Id);
                        break;
                    case FloorType ft:
                        work.ChangedTypeIds.Add(ft.Id);
                        break;
                    case Material m:
                        work.ChangedDeps.Add(m.UniqueId);
                        break;
                    case FillPatternElement fp:
                        work.ChangedDeps.Add(fp.UniqueId);
                        break;
                    case View v:
                        if (added.Contains(id))
                            work.NewViewIds.Add(v.Id);
                        break;
                }
            }

            if (deleted.Count > 0)
                FpfRefreshLog.LogUpdater("action=ignore-deletes count=" + deleted.Count.ToString(CultureInfo.InvariantCulture));

            if (work.ChangedTypeIds.Count > 0 || work.ChangedDeps.Count > 0)
            {
                foreach (var f in FpfStorage.GetFlattenedFloors(doc))
                {
                    if (floorsToCheck.ContainsKey(f.Id)) continue;
                    var hit = work.ChangedTypeIds.Contains(f.GetTypeId());
                    if (!hit && work.ChangedDeps.Count > 0)
                    {
                        var link = FpfStorage.ReadFloor(f);
                        hit = link != null && link.DependencyUniqueIds.Any(work.ChangedDeps.Contains);
                    }

                    if (hit)
                    {
                        floorsToCheck[f.Id] = f;
                        work.FloorIds.Add(f.Id);
                    }
                }
            }

            if (work.FloorIds.Count == 0 && work.NewViewIds.Count == 0)
            {
                FpfRefreshLog.LogUpdater("action=no-work " + triggerLog);
                return;
            }

            var action = work.FloorIds.Count > 0
                ? "defer-rebuild floors=" + work.FloorIds.Count.ToString(CultureInfo.InvariantCulture)
                : work.NewViewIds.Count > 0 ? "defer-views" : "defer-validate";
            FpfRefreshLog.LogUpdater("action=" + action + " " + triggerLog + " meaningful=" + meaningful.Count);

            FlatPatternUpdaterDeferred.Enqueue(work);
        }

        internal static void RunDeferred(FlatPatternUpdaterDeferred.WorkItem work)
        {
            var doc = work.Doc;
            if (doc == null || !doc.IsValidObject || doc.IsModifiable) return;

            using (FpfRefreshLog.BeginSession("Updater deferred"))
            using (FlatPatternUpdaterGate.EnterBulkOperation())
            using (var t = new Transaction(doc, "FPF Auto Update"))
            {
                if (t.Start() != TransactionStatus.Started) return;
                try
                {
                    var report = new FpfReport();
                    var svc = new FlatPatternService(doc, report, id => Editability.CanEdit(doc, id));

                    foreach (var fid in work.FloorIds)
                    {
                        if (doc.GetElement(fid) is Floor f && f.IsValidObject)
                            svc.Apply(f, ApplyMode.IfChanged, false);
                    }

                    if (work.NewViewIds.Count > 0)
                    {
                        var views = work.NewViewIds
                            .Select(id => doc.GetElement(id) as View)
                            .Where(v => v != null && v.IsValidObject)
                            .ToList();
                        if (views.Count > 0) svc.ApplyToViews(views);
                    }

                    if (report.NotEditable.Count > 0)
                    {
                        try
                        {
                            var msg = new FailureMessage(WarningId);
                            var ids = report.NotEditableIds.Where(i => doc.GetElement(i) != null).ToList();
                            if (ids.Count > 0) msg.SetFailingElements(ids);
                            doc.PostFailure(msg);
                        }
                        catch { /* ignore */ }
                    }

                    t.Commit();
                }
                catch (Exception ex)
                {
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    FpfRefreshLog.LogUpdater("action=deferred-failed " + ex.Message);
                }
            }
        }

        private static bool IsOutputOnlyChange(Document doc, IEnumerable<ElementId> changedIds)
        {
            if (changedIds == null || !changedIds.Any()) return false;
            foreach (var id in changedIds)
            {
                var el = doc.GetElement(id);
                if (!FpfOutputElements.IsIgnorableOutputElement(el, doc))
                {
                    if (el is Floor f && FpfStorage.IsFlattened(f) &&
                        FpfOutputElements.OnlyOutputHierarchyTouched(f, doc, changedIds))
                        continue;
                    return false;
                }
            }

            return true;
        }

        private static string CountIds(ICollection<ElementId> ids) =>
            (ids?.Count ?? 0).ToString(CultureInfo.InvariantCulture);

        private static string FirstIds(ICollection<ElementId> modified, ICollection<ElementId> added, int max)
        {
            var sb = new StringBuilder();
            var n = 0;
            void Take(ICollection<ElementId> ids)
            {
                if (ids == null) return;
                foreach (var id in ids)
                {
                    if (n >= max) return;
                    if (n > 0) sb.Append(',');
                    sb.Append(id.ToString());
                    n++;
                }
            }
            Take(modified);
            Take(added);
            if (n == 0) sb.Append('0');
            return sb.ToString();
        }

        private static void AppendIds(StringBuilder sb, ICollection<ElementId> ids, int max)
        {
            if (ids == null || ids.Count == 0) { sb.Append('0'); return; }
            var i = 0;
            foreach (var id in ids)
            {
                if (i >= max) break;
                if (i > 0) sb.Append(',');
                sb.Append(id.ToString());
                i++;
            }

            if (ids.Count > max) sb.Append("+").Append((ids.Count - max).ToString(CultureInfo.InvariantCulture));
        }
    }
}