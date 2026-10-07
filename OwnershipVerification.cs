using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CalendarWidget
{
    public static class OwnershipVerification
    {
        private static bool Rejects(Action action) { try { action(); return false; } catch { return true; } }
        private static WorkbookEvents Book(string document, string id, DateTimeOffset start)
        {
            return new WorkbookEvents { DocumentId = document, FileHash = "synthetic-ownership", Events = new List<SourceEvent> {
                new SourceEvent { ExplicitId = id, Alias = document + id, Title = "합성 동일 제목", Phase = "EVENT", Start = start, End = start.AddMinutes(1) }
            } };
        }
        public static async Task Run(string root, DateTimeOffset now, Action<string, bool> check)
        {
            var repository = new SyncRepository(Path.Combine(root, "ownership-" + Guid.NewGuid().ToString("N") + ".json"));
            var engine = new SyncEngine(repository); var gateway = new FakeCalendarGateway();
            var a = Book("one-document", "original-a", now.AddDays(5)); repository.ResolveIdentities(a);
            var original = new RemoteEvent { Id = "google-owned-event", Summary = a.Events[0].Title, Start = a.Events[0].Start, End = a.Events[0].End.Value, ETag = "v1", ColorId = "" };
            gateway.Seed("calendar", original);
            var observed = await gateway.List("calendar", now, CancellationToken.None);
            var accepted = engine.Plan(a, "verified-subject", "calendar", observed, "", now);
            await engine.Execute(accepted, gateway, a.FileHash, CancellationToken.None);
            var b = Book("one-document", "original-b", a.Events[0].Start); repository.ResolveIdentities(b);
            var blocked = engine.Plan(b, "verified-subject", "calendar", observed, "", now);
            check("mapped_remote_cannot_be_adopted_by_second_source", blocked.Items[0].Action == "검토" && blocked.Items[0].Reason.Contains("1:1"));
            await engine.Execute(blocked, gateway, b.FileHash, CancellationToken.None);
            check("ownership_conflict_keeps_original_mapping_and_zero_writes", repository.State.Mappings.Count == 1 && repository.State.Mappings[0].StableId == a.Events[0].StableId && gateway.Creates == 0 && gateway.Updates == 0);
            var secondDocument = Book("different-document", "original-a", a.Events[0].Start); repository.ResolveIdentities(secondDocument);
            check("remote_ownership_applies_across_source_documents", engine.Plan(secondDocument, "verified-subject", "calendar", observed, "", now).Items[0].Action == "검토");
            check("ownership_is_separate_for_other_account", engine.Plan(secondDocument, "other-subject", "calendar", observed, "", now).Items[0].Action == "채택");
            check("ownership_is_separate_for_other_calendar", engine.Plan(secondDocument, "verified-subject", "other-calendar", observed, "", now).Items[0].Action == "채택");
            var clean = new SyncRepository(Path.Combine(root, "batch-" + Guid.NewGuid().ToString("N") + ".json"));
            var batch = Book("batch", "batch-a", a.Events[0].Start); batch.Events.Add(Book("batch", "batch-b", a.Events[0].Start).Events[0]); clean.ResolveIdentities(batch);
            var batchEngine = new SyncEngine(clean); var batchPlan = batchEngine.Plan(batch, "verified-subject", "calendar", observed, "", now);
            check("one_preview_reserves_remote_id_for_single_source", batchPlan.Items.Count(i => i.Action == "채택") == 1 && batchPlan.Items.Count(i => i.Action == "검토") == 1);
            check("identity_binding_rejects_id_used_by_another_row", Rejects(delegate { clean.BindIdentity(batch, batch.Events[1], batch.Events[0].StableId); }));
            var duplicate = Book("duplicate", "duplicate-a", a.Events[0].Start); duplicate.Events[0].StableId = "same-stable"; var same = Book("duplicate", "duplicate-b", a.Events[0].Start).Events[0]; same.StableId = "same-stable"; duplicate.Events.Add(same);
            check("duplicate_source_stable_ids_are_all_held", batchEngine.Plan(duplicate, "verified-subject", "calendar", observed, "", now).Items.All(i => i.Action == "검토"));
            var journalRepo = new SyncRepository(Path.Combine(root, "journal-owner-" + Guid.NewGuid().ToString("N") + ".json")); journalRepo.ResolveIdentities(b);
            journalRepo.State.Journal.Add(new SyncJournal { Scope = SyncEngine.Scope("verified-subject", "calendar", "other-document"), StableId = "reserved-original", GoogleEventId = original.Id, AccountId = "verified-subject", CalendarId = "calendar", DocumentId = "other-document", State = "pending" });
            check("pending_journal_reserves_remote_across_documents", new SyncEngine(journalRepo).Plan(b, "verified-subject", "calendar", observed, "", now).Items[0].Action == "검토");
            string import = Path.Combine(root, "ownership-import.json"); File.WriteAllText(import, JsonData.Serialize(new MappingImport { SchemaVersion = 1, Events = new List<MappingImportItem> { new MappingImportItem { StableSourceId = b.Events[0].StableId, GoogleEventId = original.Id } } }));
            check("mapping_import_cannot_steal_existing_remote_id", Rejects(delegate { repository.Import(import, blocked.Scope, b, "verified-subject", "calendar"); }));
            check("mapping_import_checks_pending_journal_owner", Rejects(delegate { journalRepo.Import(import, blocked.Scope, b, "verified-subject", "calendar"); }));
            var staleRepo = new SyncRepository(Path.Combine(root, "stale-owner-" + Guid.NewGuid().ToString("N") + ".json")); staleRepo.ResolveIdentities(b); var staleEngine = new SyncEngine(staleRepo); var stalePlan = staleEngine.Plan(b, "verified-subject", "calendar", observed, "", now);
            staleRepo.State.Mappings.Add(new EventMapping { Scope = stalePlan.Scope, AccountId = "verified-subject", CalendarId = "calendar", DocumentId = b.DocumentId, StableId = a.Events[0].StableId, GoogleEventId = original.Id });
            var result = await staleEngine.Execute(stalePlan, gateway, b.FileHash, CancellationToken.None);
            check("execution_rechecks_ownership_changed_after_preview", result.Any(r => r.Contains("충돌")) && staleRepo.State.Mappings.Count == 1 && staleRepo.State.Journal.Count == 0 && gateway.Creates == 0 && gateway.Updates == 0);
            var inverse = new SyncRepository(Path.Combine(root, "inverse-" + Guid.NewGuid().ToString("N") + ".json")); inverse.ResolveIdentities(a);
            string scope = SyncEngine.Scope("verified-subject", "calendar", a.DocumentId);
            inverse.State.Journal.Add(new SyncJournal { Scope = scope, AccountId = "verified-subject", CalendarId = "calendar", DocumentId = a.DocumentId, StableId = a.Events[0].StableId, GoogleEventId = "first-id", State = "pending" });
            check("one_source_cannot_change_id_reserved_in_journal", inverse.BindingConflict("verified-subject", "calendar", a.DocumentId, scope, a.Events[0].StableId, "second-id"));
            var legacy = new SyncRepository(Path.Combine(root, "legacy-owner-" + Guid.NewGuid().ToString("N") + ".json")); legacy.State.Mappings.Add(new EventMapping { Scope = "unknown-legacy-scope", StableId = "unknown", GoogleEventId = original.Id });
            check("unknown_legacy_target_is_held_until_verified", new SyncEngine(legacy).Plan(b, "verified-subject", "calendar", observed, "", now).Items[0].Action == "검토");
        }
    }
}
