using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CalendarWidget
{
    public static class ManualEventVerification
    {
        private static bool Rejects(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
        private static ManualEventInput Input() { return new ManualEventInput { AccountSubject = "synthetic-subject", CalendarId = "synthetic-writer", Title = "Synthetic widget event",
            StartDate = new DateTime(2026, 10, 8), EndDate = new DateTime(2026, 10, 8), StartTime = "10:00", EndTime = "11:00" }; }
        public static void Run(string output, Action<string, bool> check)
        {
            var time = Input(); time.Validate();
            check("manual_timed_event_uses_seoul_offset_and_exact_hour", time.Start.Offset == TimeSpan.FromHours(9) && time.End - time.Start == TimeSpan.FromHours(1));
            var allDay = Input(); allDay.AllDay = true; allDay.EndDate = new DateTime(2026, 10, 9); allDay.Validate();
            check("manual_all_day_end_is_exclusive_next_day", allDay.Start.Date == new DateTime(2026, 10, 8) && allDay.End.Date == new DateTime(2026, 10, 10));
            var invalid = Input(); invalid.EndTime = "09:59"; check("manual_end_must_follow_start", Rejects(invalid.Validate));
            invalid = Input(); invalid.EndDate = invalid.StartDate.AddDays(367); check("manual_period_over_366_days_rejected", Rejects(invalid.Validate));
            invalid = Input(); invalid.StartTime = "25:00"; check("manual_invalid_clock_rejected", Rejects(invalid.Validate));
            var path = Path.Combine(output, "manual-events-" + Guid.NewGuid().ToString("N") + ".json");
            var store = new ManualEventStore(path); var fake = new FakeCalendarGateway();
            int roleReads = 0;
            Func<CancellationToken, Task<List<RemoteCalendar>>> writer = token => { roleReads++; return Task.FromResult(new List<RemoteCalendar> { new RemoteCalendar { Id = "synthetic-writer", AccessRole = "writer" } }); };
            var noScope = new ManualEventService(fake, writer, store, false);
            bool scopeBlocked = false; try { noScope.CreateOrReconcile(Input(), CancellationToken.None).GetAwaiter().GetResult(); } catch (InvalidOperationException) { scopeBlocked = true; }
            check("manual_create_requires_actual_event_write_scope_before_network", scopeBlocked && roleReads == 0 && fake.Creates == 0 && store.Records.Count == 0);
            var reader = new ManualEventService(fake, token => Task.FromResult(new List<RemoteCalendar> { new RemoteCalendar { Id = "synthetic-writer", AccessRole = "reader" } }), store, true);
            bool readerBlocked = false; try { reader.CreateOrReconcile(Input(), CancellationToken.None).GetAwaiter().GetResult(); } catch (InvalidOperationException) { readerBlocked = true; }
            check("manual_reader_role_rejects_create_without_pending_record", readerBlocked && store.Records.Count == 0 && fake.Creates == 0);
            var service = new ManualEventService(fake, writer, store, true);
            fake.FailAfterPersist = true;
            var created = service.CreateOrReconcile(Input(), CancellationToken.None).GetAwaiter().GetResult();
            check("manual_lost_create_reply_reconciles_fixed_id_once", created.State == "complete" && ManualEventService.IsManualId(created.EventId) && fake.Creates == 1 && fake.Count(created.CalendarId) == 1);
            var repeat = service.CreateOrReconcile(Input(), CancellationToken.None).GetAwaiter().GetResult();
            check("manual_repeat_click_can_only_create_after_previous_complete", repeat.EventId != created.EventId && fake.Creates == 2);
            var retryPath = Path.Combine(output, "manual-retry-" + Guid.NewGuid().ToString("N") + ".json");
            var retryStore = new ManualEventStore(retryPath);
            var retryGateway = new FakeCalendarGateway { FailBeforePersist = 1 };
            var retryService = new ManualEventService(retryGateway, writer, retryStore, true);
            var pending = retryService.CreateOrReconcile(Input(), CancellationToken.None).GetAwaiter().GetResult();
            string fixedId = pending.EventId;
            check("manual_failed_create_is_pending_with_precommitted_id", pending.State == "pending" && ManualEventService.IsManualId(fixedId) && retryGateway.Creates == 0 && new ManualEventStore(retryPath).Records.Single().EventId == fixedId);
            var adopted = retryService.CreateOrReconcile(Input(), CancellationToken.None).GetAwaiter().GetResult();
            check("manual_retry_reuses_id_and_creates_only_one_event", adopted.State == "complete" && adopted.EventId == fixedId && retryGateway.Creates == 1 && retryGateway.Count(adopted.CalendarId) == 1);
            var statusInput = Input(); statusInput.StatusValues["setup"] = "prepared"; statusInput.StatusValues["realTester"] = "reviewer";
            var statusGateway = new FakeCalendarGateway();
            var statusRecords = new ManualEventStore(Path.Combine(output, "manual-status-" + Guid.NewGuid().ToString("N") + ".json"));
            var statusDrafts = new StatusDraftStore(Path.Combine(output, "manual-status-drafts-" + Guid.NewGuid().ToString("N") + ".json"));
            FakeStatusEndpoint statusEndpoint = null;
            var statusService = new ManualEventService(statusGateway, writer, statusRecords, true, record => {
                if (statusEndpoint == null) statusEndpoint = new FakeStatusEndpoint { TrackDescription = true,
                    State = new StatusRemoteState { TargetKey = record.AccountSubject + "|" + record.CalendarId + "|" + record.EventId,
                        Version = statusGateway.Get(record.CalendarId, record.EventId, CancellationToken.None).Result.ETag, Exact = true, Description = "External note" } };
                return statusEndpoint;
            }, statusDrafts);
            var withStatus = statusService.CreateOrReconcile(statusInput, CancellationToken.None).GetAwaiter().GetResult();
            check("manual_create_optional_status_patches_once_after_fixed_id_create", withStatus.State == "complete" && statusGateway.Creates == 1 && statusEndpoint.Writes == 1 &&
                statusDrafts.Drafts.Single().GoogleOnly && statusDrafts.Drafts.Single().State == "complete" && statusEndpoint.State.Description.StartsWith("External note\n\n"));
            check("manual_create_without_status_keeps_no_status_block", created.State == "complete" && created.StatusValues.Count == 0);
            var config = new WidgetConfiguration { Accounts = new List<AccountConfiguration> { new AccountConfiguration { Id = "account-key", GoogleSubject = "synthetic-subject", VerifiedEmail = "one@example.invalid" } },
                Connections = new List<CalendarConnectionProfile> { new CalendarConnectionProfile { Id = "excel", AccountId = "account-key", CalendarId = "synthetic-writer", Mode = "excel" },
                    new CalendarConnectionProfile { Id = "direct", AccountId = "account-key", CalendarId = "display-only", Mode = "google-direct", DisplayOnly = true },
                    new CalendarConnectionProfile { Id = "reader", AccountId = "account-key", CalendarId = "reader", Mode = "google-direct" } } };
            var discovered = ManualCalendarDiscovery.Discover(config, account => Task.FromResult(new List<RemoteCalendar> {
                new RemoteCalendar { Id = "synthetic-writer", Name = "Actual writer", AccessRole = "writer" },
                new RemoteCalendar { Id = "display-only", Name = "Sample Secondary", AccessRole = "writer" },
                new RemoteCalendar { Id = "unconfigured-writer", Name = "Another writer", AccessRole = "writer" },
                new RemoteCalendar { Id = "reader", Name = "Reader", AccessRole = "reader" } })).GetAwaiter().GetResult();
            check("manual_live_discovery_uses_current_writer_role_independent_of_display_mode", discovered.Count == 3 && discovered.Any(c => c.AccountId == "account-key" && c.CalendarId == "synthetic-writer" && c.Label.Contains("Actual writer")) && discovered.Any(c => c.CalendarId == "display-only") && discovered.Any(c => c.CalendarId == "unconfigured-writer") && !discovered.Any(c => c.CalendarId == "reader"));
        }
    }
}
