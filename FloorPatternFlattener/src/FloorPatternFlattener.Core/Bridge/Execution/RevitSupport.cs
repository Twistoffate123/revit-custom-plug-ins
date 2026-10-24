using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using FloorPatternFlattener.Services;

namespace FloorPatternFlattener.Bridge
{
    internal sealed class Ownership
    {
        public long Id;
        public string Name;
        public string Category;
        public bool Editable;
        public string Reason;
        public string Owner;
        public bool Pinned;
        public bool InGroup;
        public string Message;
    }

    internal static class RevitSupport
    {
        public static BridgeResponse Fail(ValidatedCommand cmd, string code, string message, JsonValue details)
        {
            return BridgeResponse.Fail(cmd == null ? null : cmd.Id, cmd == null ? null : cmd.Name, code, message, details);
        }

        public static BridgeResponse Ok(ValidatedCommand cmd, JsonValue result)
        {
            return BridgeResponse.Success(cmd.Id, cmd.Name, result);
        }

        public static bool TryGetDocument(UIApplication app, ValidatedCommand cmd, bool mutating,
            out UIDocument uidoc, out Document doc, out BridgeResponse error)
        {
            uidoc = app == null ? null : app.ActiveUIDocument;
            doc = uidoc == null ? null : uidoc.Document;
            error = null;
            if (doc == null)
            {
                error = Fail(cmd, "no_document", "Open a project model in Revit first. Nothing was changed.", null);
                return false;
            }
            if (doc.IsFamilyDocument)
            {
                error = Fail(cmd, "family_document",
                    "This helper works in a project model, not in the Family Editor. Open the project and try again.", null);
                return false;
            }
            if (mutating && doc.IsReadOnly)
            {
                error = Fail(cmd, "read_only",
                    "This model is open read-only. Open it so you can edit it, then try again. Nothing was changed.", null);
                return false;
            }
            return true;
        }

        public static string Describe(Element e)
        {
            if (e == null) return "an element";
            var cat = e.Category != null && !string.IsNullOrEmpty(e.Category.Name) ? e.Category.Name : "Element";
            string name = "";
            try { name = e.Name; } catch { /* some elements throw */ }
            var id = ElementIdUtil.GetValue(e.Id);
            if (string.IsNullOrEmpty(name)) return cat + " " + id;
            return cat + " " + id + " (" + name + ")";
        }

        public static Ownership Inspect(Document doc, Element e)
        {
            var info = new Ownership
            {
                Id = ElementIdUtil.GetValue(e.Id),
                Category = e.Category != null ? e.Category.Name : "",
                Pinned = SafePinned(e),
                InGroup = e.GroupId != null && e.GroupId != ElementId.InvalidElementId
            };
            try { info.Name = e.Name; } catch { info.Name = ""; }

            if (!doc.IsWorkshared)
            {
                info.Editable = true;
                info.Reason = "not_workshared";
                info.Message = Describe(e) + " can be edited. This model is not workshared.";
                return info;
            }

            try
            {
                var status = WorksharingUtils.GetCheckoutStatus(doc, e.Id);
                var updates = WorksharingUtils.GetModelUpdatesStatus(doc, e.Id);
                try
                {
                    var tip = WorksharingUtils.GetWorksharingTooltipInfo(doc, e.Id);
                    if (tip != null) info.Owner = tip.Owner ?? "";
                }
                catch { info.Owner = ""; }

                if (status == CheckoutStatus.OwnedByOtherUser)
                {
                    info.Editable = false;
                    info.Reason = "owned_by_other";
                    var who = string.IsNullOrEmpty(info.Owner) ? "someone else" : info.Owner;
                    info.Message = Describe(e) + " is owned by " + who +
                        ". Ask them to relinquish it, or place a borrowing request in Revit, then try again. Nothing was changed.";
                    return info;
                }
                if (updates == ModelUpdatesStatus.DeletedInCentral || updates == ModelUpdatesStatus.UpdatedInCentral)
                {
                    info.Editable = false;
                    info.Reason = updates == ModelUpdatesStatus.DeletedInCentral ? "deleted_in_central" : "out_of_date";
                    info.Message = Describe(e) + " is out of date with the central model" +
                        (updates == ModelUpdatesStatus.DeletedInCentral ? " (it was deleted in central)." : ".") +
                        " Use Reload Latest in Revit yourself, then try again. This helper will not reload or sync. Nothing was changed.";
                    return info;
                }
                info.Editable = Editability.CanEdit(doc, e.Id);
                info.Reason = info.Editable ? "editable" : "not_editable";
                info.Message = info.Editable
                    ? Describe(e) + " can be edited."
                    : Describe(e) + " cannot be edited in this workshared model. Nothing was changed.";
                return info;
            }
            catch (Exception ex)
            {
                info.Editable = false;
                info.Reason = "checkout_failed";
                info.Message = "Could not check " + Describe(e) + " against the central or cloud model. " + ex.Message +
                    " Nothing was changed.";
                return info;
            }
        }

        public static JsonValue ToJson(Ownership info)
        {
            return new JsonObj()
                .Set("id", info.Id)
                .Set("name", info.Name ?? "")
                .Set("category", info.Category ?? "")
                .Set("editable", info.Editable)
                .Set("reason", info.Reason ?? "")
                .Set("owner", info.Owner ?? "")
                .Set("pinned", info.Pinned)
                .Set("inGroup", info.InGroup)
                .Set("message", info.Message ?? "")
                .Build();
        }

        public static BridgeResponse NotEditable(ValidatedCommand cmd, IList<Ownership> denied)
        {
            var arr = new JsonArr();
            string first = "An element cannot be edited.";
            if (denied != null && denied.Count > 0)
            {
                first = denied[0].Message;
                for (var i = 0; i < denied.Count; i++) arr.Add(ToJson(denied[i]));
            }
            var details = new JsonObj().Set("elements", arr.Build()).Build();
            return Fail(cmd, "not_editable", first, details);
        }

        /// <summary>Checks ownership and borrows. Returns an error response, or null when editing may proceed. Call outside a transaction.</summary>
        public static BridgeResponse Borrow(ValidatedCommand cmd, Document doc, IList<Element> elements)
        {
            if (doc == null || elements == null || elements.Count == 0) return null;
            if (!doc.IsWorkshared) return null;

            var denied = new List<Ownership>();
            var ids = new HashSet<ElementId>();
            for (var i = 0; i < elements.Count; i++)
            {
                var e = elements[i];
                if (e == null || !e.IsValidObject) continue;
                var info = Inspect(doc, e);
                if (!info.Editable) denied.Add(info);
                else ids.Add(e.Id);
            }
            if (denied.Count > 0) return NotEditable(cmd, denied);
            if (ids.Count == 0) return null;

            try
            {
                WorksharingUtils.CheckoutElements(doc, ids);
            }
            catch (Exception ex)
            {
                return Fail(cmd, "not_editable",
                    "Could not borrow the elements from the central or cloud model. " + ex.Message +
                    " Nothing was changed and nothing was synced.", null);
            }

            for (var i = 0; i < elements.Count; i++)
            {
                var e = elements[i];
                if (e == null || !e.IsValidObject) continue;
                if (!Editability.CanEdit(doc, e.Id)) denied.Add(Inspect(doc, e));
            }
            if (denied.Count > 0) return NotEditable(cmd, denied);
            return null;
        }

        public static string LockedReason(Element e, bool allowUnpin)
        {
            if (e == null) return null;
            if (e.GroupId != null && e.GroupId != ElementId.InvalidElementId)
                return Describe(e) + " is inside a group, so it cannot be edited from here. Edit the group in Revit, or ungroup it, then try again. Nothing was changed.";
            if (SafePinned(e) && !allowUnpin)
                return Describe(e) + " is pinned. Unpin it in Revit, or pass unpin true if you want this command to unpin it first. Nothing was changed.";
            return null;
        }

        public static bool Run(Document doc, string name, IList<string> errors, Action body)
        {
            if (doc.IsModifiable)
            {
                errors.Add("Revit is already in the middle of another change. Wait until it finishes, then try again. Nothing was changed.");
                return false;
            }
            using (var t = new Transaction(doc, name))
            {
                var opts = t.GetFailureHandlingOptions();
                opts.SetClearAfterRollback(true);
                opts.SetFailuresPreprocessor(new WarningSink(errors));
                t.SetFailureHandlingOptions(opts);
                if (t.Start() != TransactionStatus.Started)
                {
                    errors.Add("Could not start the change '" + name + "'. Nothing was changed.");
                    return false;
                }
                try
                {
                    body();
                }
                catch (Exception ex)
                {
                    errors.Add(ex.Message);
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    return false;
                }
                var status = t.Commit();
                if (status != TransactionStatus.Committed)
                {
                    if (errors.Count == 0)
                        errors.Add("Revit did not keep the change '" + name + "' (" + status + "). Nothing was changed.");
                    return false;
                }
                return true;
            }
        }

        public static BridgeResponse Failed(ValidatedCommand cmd, IList<string> errors)
        {
            string text;
            if (errors == null || errors.Count == 0) text = "The change was not made.";
            else
            {
                var parts = new string[errors.Count];
                for (var i = 0; i < errors.Count; i++) parts[i] = errors[i];
                text = string.Join(" ", parts);
            }
            if (text.IndexOf("Nothing was changed", StringComparison.OrdinalIgnoreCase) < 0 &&
                text.IndexOf("Nothing was synced", StringComparison.OrdinalIgnoreCase) < 0)
                text = text.TrimEnd() + " Nothing was changed.";
            return Fail(cmd, "failed", text, null);
        }

        private static bool SafePinned(Element e)
        {
            try { return e.Pinned; }
            catch { return false; }
        }

        private sealed class WarningSink : IFailuresPreprocessor
        {
            private readonly IList<string> _errors;
            public WarningSink(IList<string> errors) { _errors = errors; }

            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                var messages = accessor.GetFailureMessages();
                var hasError = false;
                if (messages != null)
                {
                    foreach (var failure in messages)
                    {
                        if (failure.GetSeverity() == FailureSeverity.Warning)
                        {
                            accessor.DeleteWarning(failure);
                        }
                        else
                        {
                            hasError = true;
                            try { _errors.Add(failure.GetDescriptionText()); }
                            catch { _errors.Add("Revit reported an error and rolled the change back."); }
                        }
                    }
                }
                if (hasError) return FailureProcessingResult.ProceedWithRollBack;
                return FailureProcessingResult.Continue;
            }
        }
    }
}
