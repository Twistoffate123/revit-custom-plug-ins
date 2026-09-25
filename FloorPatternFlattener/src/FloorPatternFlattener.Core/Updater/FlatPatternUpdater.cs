using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
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
    /// Loop safety: DirectShape modification/addition is not a trigger. Writing extensible storage on a floor
    /// re-triggers "any change" on that floor, but the stored input signature then matches and nothing is done.
    /// Worksharing: elements not editable by this user are skipped (never modified) and a warning is posted.
    /// </summary>
    public sealed class FlatPatternUpdater : IUpdater
    {
        public static readonly Guid UpdaterGuid = new Guid("3E9D6C21-7A4B-4F85-B1D2-8C0E5A7F9B34");

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
        public static void Register(AddInId addInId)
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
            if (UpdaterRegistry.IsUpdaterRegistered(id)) return;

            UpdaterRegistry.RegisterUpdater(updater, true /* isOptional */);

            var floors = new ElementClassFilter(typeof(Floor));
            UpdaterRegistry.AddTrigger(id, floors, Element.GetChangeTypeGeometry());
            UpdaterRegistry.AddTrigger(id, floors, Element.GetChangeTypeAny());
            UpdaterRegistry.AddTrigger(id, floors,
                Element.GetChangeTypeParameter(new ElementId(BuiltInParameter.ELEM_TYPE_PARAM)));
            UpdaterRegistry.AddTrigger(id, floors, Element.GetChangeTypeElementAddition());
            UpdaterRegistry.AddTrigger(id, floors, Element.GetChangeTypeElementDeletion());

            UpdaterRegistry.AddTrigger(id, new ElementClassFilter(typeof(FloorType)), Element.GetChangeTypeAny());
            UpdaterRegistry.AddTrigger(id, new ElementClassFilter(typeof(Material)), Element.GetChangeTypeAny());
            UpdaterRegistry.AddTrigger(id, new ElementClassFilter(typeof(FillPatternElement)), Element.GetChangeTypeAny());
            UpdaterRegistry.AddTrigger(id, new ElementClassFilter(typeof(View)), Element.GetChangeTypeElementAddition());
            UpdaterRegistry.AddTrigger(id, new ElementClassFilter(typeof(DirectShape)), Element.GetChangeTypeElementDeletion());
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

            try
            {
                Run(doc, data);
            }
            catch (Exception)
            {
                // Never throw out of an updater: Revit would disable it for the session.
            }
        }

        private static void Run(Document doc, UpdaterData data)
        {
            var modified = data.GetModifiedElementIds() ?? new List<ElementId>();
            var added = data.GetAddedElementIds() ?? new List<ElementId>();
            var deleted = data.GetDeletedElementIds() ?? new List<ElementId>();

            var floorsToCheck = new Dictionary<ElementId, Floor>();
            var changedTypeIds = new HashSet<ElementId>();
            var changedDeps = new HashSet<string>();
            var newViews = new List<View>();

            foreach (var id in modified.Concat(added))
            {
                var el = doc.GetElement(id);
                switch (el)
                {
                    case Floor f:
                        if (FpfStorage.IsFlattened(f)) floorsToCheck[f.Id] = f;
                        break;
                    case FloorType ft:
                        changedTypeIds.Add(ft.Id);
                        break;
                    case Material m:
                        changedDeps.Add(m.UniqueId);
                        break;
                    case FillPatternElement fp:
                        changedDeps.Add(fp.UniqueId);
                        break;
                    case View v:
                        if (added.Contains(id)) newViews.Add(v);
                        break;
                }
            }

            var validateAll = deleted.Count > 0;
            var toValidate = new List<Floor>();

            if (changedTypeIds.Count > 0 || changedDeps.Count > 0 || validateAll)
            {
                foreach (var f in FpfStorage.GetFlattenedFloors(doc))
                {
                    if (floorsToCheck.ContainsKey(f.Id)) continue;
                    var hit = changedTypeIds.Contains(f.GetTypeId());
                    if (!hit && changedDeps.Count > 0)
                    {
                        var link = FpfStorage.ReadFloor(f);
                        hit = link != null && link.DependencyUniqueIds.Any(changedDeps.Contains);
                    }
                    if (hit) floorsToCheck[f.Id] = f;
                    else if (validateAll) toValidate.Add(f);
                }
            }

            if (floorsToCheck.Count == 0 && toValidate.Count == 0 && newViews.Count == 0 && !validateAll)
                return;

            var report = new FpfReport();
            var svc = new FlatPatternService(doc, report, id => Editability.CanEdit(doc, id));

            foreach (var f in floorsToCheck.Values)
                svc.Apply(f, ApplyMode.IfChanged, false);

            foreach (var f in toValidate)
                svc.Apply(f, ApplyMode.ValidateLinks, false);

            if (validateAll)
                svc.CleanupOrphans();

            if (newViews.Count > 0)
                svc.ApplyToViews(newViews);

            if (report.NotEditable.Count > 0)
            {
                try
                {
                    var msg = new FailureMessage(WarningId);
                    var ids = report.NotEditableIds.Where(i => doc.GetElement(i) != null).ToList();
                    if (ids.Count > 0) msg.SetFailingElements(ids);
                    doc.PostFailure(msg);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }
}
