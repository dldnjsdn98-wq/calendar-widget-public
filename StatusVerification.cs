using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace CalendarWidget
{
    internal sealed class FakeStatusEndpoint : IStatusEndpoint
    {
        public bool IsReady = true, LostReply, Fail, TrackDescription; public int Writes; public Action AfterRead;
        public StatusRemoteState State; public List<string> LastChanged;
        public bool Ready { get { return IsReady; } }
        public Task<StatusRemoteState> Read(StatusDraft draft, CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); if (AfterRead != null) AfterRead(); return Task.FromResult(new StatusRemoteState { TargetKey = State.TargetKey, BindingSignature = State.BindingSignature, Version = State.Version, Exact = State.Exact, Values = new Dictionary<string, string>(State.Values), DescriptionTracked = TrackDescription, Description = State.Description }); }
        public Task Apply(StatusDraft draft, Dictionary<string, string> changed, string version, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); if (Fail) throw new IOException("synthetic offline");
            if (version != State.Version) throw new StatusConflictException("합성 변경 충돌");
            Writes++; LastChanged = changed.Keys.ToList(); foreach (var value in changed) State.Values[value.Key] = value.Value; State.Version += "+";
            if (TrackDescription) { State.Description = StatusPayloadPolicy.MergeDescription(State.Description, draft); State.BindingSignature = draft.Binding.Signature; }
            if (LostReply) { LostReply = false; throw new IOException("synthetic lost reply"); } return Task.FromResult(0);
        }
    }
    public static class StatusVerification
    {
        internal static StatusBinding Binding() { return new StatusBinding { DocumentId = "synthetic-document", Worksheet = "이벤트확인", SourceHash = "synthetic-hash", EventAlias = "synthetic-round-phase", Phase = "OPEN", RoundSignature = "synthetic-round", Exact = true, Cells = new Dictionary<string, StatusCell> {
            { "setup", new StatusCell { Field = "setup", Address = "D7", OriginalValue = "" } }, { "alphaCheck", new StatusCell { Field = "alphaCheck", Address = "E7", OriginalValue = "" } }, { "alphaTester", new StatusCell { Field = "alphaTester", Address = "F7", OriginalValue = "" } } } }; }
        internal static StatusDraft Draft() { return new StatusEditContext { Scope = "synthetic-scope", AccountId = "synthetic-account", CalendarId = "synthetic-calendar", GoogleEventId = "synthetic-event", GoogleVersion = "v1", Binding = Binding() }.CreateDraft(); }
        internal static FakeStatusEndpoint Endpoint(StatusDraft d, bool document) { return new FakeStatusEndpoint { State = new StatusRemoteState { TargetKey = document ? d.DocumentKey : d.GoogleKey, BindingSignature = document ? d.Binding.Signature : null, Version = "v1", Values = new Dictionary<string, string>(d.Values) } }; }
        private static bool Rejects(Action action) { try { action(); return false; } catch { return true; } }
        public static void Run(string output, Action<string, bool> check)
        {
            string storePath = Path.Combine(output, "status-drafts-" + Guid.NewGuid().ToString("N") + ".json"); var store = new StatusDraftStore(storePath); var coordinator = new StatusSaveCoordinator(store);
            var d = Draft(); var doc = Endpoint(d, true); var cal = Endpoint(d, false); d.Values["setup"] = "완료"; store.Put(d);
            coordinator.Save(d, doc, cal, CancellationToken.None).GetAwaiter().GetResult();
            check("status_complete_requires_both_readbacks_and_changes_only_one_document_cell", d.State == "complete" && d.DocumentDone && d.GoogleDone && doc.Writes == 1 && cal.Writes == 1 && doc.LastChanged.SequenceEqual(new[] { "setup" }));
            coordinator.Save(d, doc, cal, CancellationToken.None).GetAwaiter().GetResult(); check("status_duplicate_save_is_idempotent", doc.Writes == 1 && cal.Writes == 1);
            d = Draft(); doc = Endpoint(d, true); cal = Endpoint(d, false); d.Values["setup"] = "완료"; store.Put(d); cal.Fail = true;
            coordinator.Save(d, doc, cal, CancellationToken.None).GetAwaiter().GetResult(); check("status_document_only_success_stays_pending", d.State == "pending" && d.DocumentDone && !d.GoogleDone && doc.Writes == 1 && cal.Writes == 0);
            check("status_partial_save_cannot_be_replaced_by_new_draft", Rejects(delegate { store.Put(Draft()); })); cal.Fail = false;
            coordinator.Save(d, doc, cal, CancellationToken.None).GetAwaiter().GetResult(); check("status_retry_completes_without_rewriting_document", d.State == "complete" && doc.Writes == 1 && cal.Writes == 1);
            d = Draft(); doc = Endpoint(d, true); cal = Endpoint(d, false); d.Values["alphaTester"] = "합성 담당자"; store.Put(d); doc.LostReply = true;
            coordinator.Save(d, doc, cal, CancellationToken.None).GetAwaiter().GetResult(); check("status_lost_document_reply_never_claims_completion", d.State == "pending" && !d.GoogleDone);
            check("status_unknown_document_write_cannot_be_replaced", d.DocumentAttempted && Rejects(delegate { store.Put(Draft()); }) && new StatusDraftStore(storePath).Drafts.Last().DocumentAttempted);
            coordinator.Save(d, doc, cal, CancellationToken.None).GetAwaiter().GetResult(); check("status_lost_reply_retry_adopts_verified_document_result", d.State == "complete" && doc.Writes == 1 && cal.Writes == 1);
            d = Draft(); doc = Endpoint(d, true); cal = Endpoint(d, false); d.Values["setup"] = "완료"; store.Put(d); cal.LostReply = true;
            coordinator.Save(d, doc, cal, CancellationToken.None).GetAwaiter().GetResult(); coordinator.Save(d, doc, cal, CancellationToken.None).GetAwaiter().GetResult();
            check("status_lost_calendar_reply_retry_is_idempotent", d.State == "complete" && doc.Writes == 1 && cal.Writes == 1);
            d = Draft(); doc = Endpoint(d, true); cal = Endpoint(d, false); d.Values["setup"] = "완료"; store.Put(d); doc.State.Values["setup"] = "다른 사람 변경";
            coordinator.Save(d, doc, cal, CancellationToken.None).GetAwaiter().GetResult(); check("status_document_conflict_blocks_both_writes", d.State == "conflict" && doc.Writes == 0 && cal.Writes == 0);
            d = Draft(); doc = Endpoint(d, true); cal = Endpoint(d, false); d.Values["setup"] = "완료"; store.Put(d); cal.State.Version = "external-change";
            coordinator.Save(d, doc, cal, CancellationToken.None).GetAwaiter().GetResult(); check("status_calendar_version_conflict_blocks_document_write", d.State == "conflict" && doc.Writes == 0 && cal.Writes == 0);
            d = Draft(); doc = Endpoint(d, true); cal = Endpoint(d, false); d.Values["setup"] = "완료"; store.Put(d); cal.State.TargetKey = "wrong-account-calendar-event";
            coordinator.Save(d, doc, cal, CancellationToken.None).GetAwaiter().GetResult(); check("status_wrong_account_calendar_or_event_blocks_writes", d.State == "conflict" && doc.Writes == 0 && cal.Writes == 0);
            d = Draft(); doc = Endpoint(d, true); cal = Endpoint(d, false); d.Values["setup"] = "완료"; store.Put(d); doc.IsReady = false;
            coordinator.Save(d, doc, cal, CancellationToken.None).GetAwaiter().GetResult(); check("status_missing_document_connection_never_writes_google", d.State == "waiting-connection" && doc.Writes == 0 && cal.Writes == 0 && d.Message.Contains("양쪽 미반영"));
            d.Binding.Exact = false; check("status_unmapped_round_or_phase_rejected", Rejects(d.Validate)); d.Binding.Exact = true; d.Binding.Cells["setup"].Formula = true;
            check("status_formula_cell_cannot_be_overwritten", Rejects(d.Validate)); d.Binding.Cells["setup"].Formula = false; d.Binding.Cells["setup"].SharedMerge = true;
            check("status_shared_merge_cell_cannot_be_overwritten", Rejects(d.Validate)); d.Binding.Cells["setup"].SharedMerge = false; d.Values["setup"] = "=1+1"; check("status_formula_input_rejected", Rejects(d.Validate)); d.Values["setup"] = "완료";
            var payload = StatusPayloadPolicy.Build(d); var encoded = JsonData.Serialize(payload);
            check("status_google_payload_only_approved_description_and_private_metadata", payload.Keys.OrderBy(k => k).SequenceEqual(new[] { "description", "extendedProperties" }) && !encoded.Contains("summary") && !encoded.Contains("attendees") && !encoded.Contains("conferenceData") && !encoded.Contains("http"));
            check("status_description_has_only_status_labels_blank_and_unlinked_values", StatusPayloadPolicy.HasStatus((string)payload["description"], d) && ((string)payload["description"]).StartsWith(StatusPayloadPolicy.BeginMarker));
            d.Values["setup"] = "https://synthetic.sharepoint.invalid/source"; check("status_description_rejects_source_links", Rejects(delegate { StatusPayloadPolicy.Build(d); })); d.Values["setup"] = "완료";
            var described = Draft(); described.Binding.Cells["realCheck"] = new StatusCell { Field = "realCheck", Address = "K7", OriginalValue = "" }; described.Binding.Cells["realTester"] = new StatusCell { Field = "realTester", Address = "L7", OriginalValue = "" };
            described.Values["alphaCheck"] = "합성 알파 확인"; described.Values["alphaTester"] = "합성 알파 테스터"; described.Values["realCheck"] = "합성 리얼 확인"; described.Values["realTester"] = "합성 리얼 테스터";
            check("status_description_includes_both_checks_and_testers_without_source_metadata", StatusPayloadPolicy.Description(described) == "세팅: 빈칸\n확인: 알파 합성 알파 확인 · 리얼 합성 리얼 확인\n알파 및 테스터: 합성 알파 테스터\n리얼 및 테스터: 합성 리얼 테스터" && !StatusPayloadPolicy.Description(described).Contains(described.Binding.DocumentId));
            var reload = new StatusDraftStore(storePath);
            check("status_pending_drafts_survive_restart", reload.Drafts.Any(x => x.State == "waiting-connection" && x.Values["setup"] == "완료"));
            d = Draft(); doc = Endpoint(d, true); cal = Endpoint(d, false); d.Values["setup"] = "완료"; store.Put(d); cal.State.BindingSignature = "other-round";
            coordinator.Save(d, doc, cal, CancellationToken.None).GetAwaiter().GetResult(); check("status_wrong_calendar_binding_blocks_both_writes", d.State == "conflict" && doc.Writes == 0 && cal.Writes == 0);
            string rollbackPath = Path.Combine(output, "status-rollback-" + Guid.NewGuid().ToString("N") + ".json"); var rollback = new StatusDraftStore(rollbackPath); var originalDraft = Draft(); rollback.Put(originalDraft); Directory.CreateDirectory(rollbackPath + ".tmp");
            check("status_failed_local_replace_preserves_previous_memory_and_disk", Rejects(delegate { rollback.Put(Draft()); }) && rollback.Drafts.Count == 1 && !originalDraft.Superseded && !new StatusDraftStore(rollbackPath).Drafts.Single().Superseded);
            d = Draft(); doc = Endpoint(d, true); cal = Endpoint(d, false); d.Values["setup"] = "완료"; store.Put(d); int sourceReads = 0;
            doc.AfterRead = delegate { if (++sourceReads == 3) doc.State.Values["setup"] = "다른 사용자 수정"; };
            coordinator.Save(d, doc, cal, CancellationToken.None).GetAwaiter().GetResult(); check("status_final_readback_mismatch_never_claims_completion", d.State == "conflict" && d.DocumentDone && d.GoogleDone && doc.Writes == 1 && cal.Writes == 1);
            string sorted = Path.Combine(output, "status-binding-synthetic.xlsx"); SortedFixture.Write(sorted, null);
            var book = ExcelEventParser.Read(sorted, new ExcelReadOptions { DocumentId = "synthetic-sorted", PhaseDurationMinutes = 1 }, DateTimeOffset.Parse("2026-10-02T15:00:00+09:00"));
            check("status_binding_uses_exact_phase_row_and_unmapped_is_disabled", book.Events.Single(e => e.Phase == "OPEN").StatusBinding.Cells["setup"].Address == "D1" && book.Events.Single(e => e.Phase == "CLOSE").StatusBinding.Cells["setup"].Address == "D2" && book.Events.Single(e => e.Phase == "RESULT_OPEN").StatusBinding == null);
        }
        public static void RunUi(Widget widget, string output, Action<string, bool> check, Action<Window, string, double> capture)
        {
            var old = widget.Source; var now = widget.Now;
            var data = new CalendarSnapshot { SchemaVersion = 1, Mode = "google", TimeZone = "Asia/Seoul", ObservedAt = now.ToString("o"), Calendars = new List<CalendarProfile>(), Events = new List<CalendarEvent>() };
            for (int i = 0; i < 2; i++) {
                string key = ConnectedCalendarSource.CalendarKey("synthetic-account-" + i, "synthetic-calendar-" + i);
                data.Calendars.Add(new CalendarProfile { Id = key, Name = "합성 Shared", Color = "#b99aff", AccountId = "synthetic-account-" + i, AccountName = "person" + i + "@example.invalid", Fields = new List<FieldDefinition> { new FieldDefinition { Key = "setup", Label = "세팅" } } });
                var ctx = new StatusEditContext { Scope = "synthetic-scope-" + i, AccountId = "synthetic-account-" + i, CalendarId = "synthetic-calendar-" + i, GoogleEventId = "same-remote-id", GoogleVersion = "v1", Binding = Binding() };
                data.Events.Add(new CalendarEvent { Id = key + ":same-remote-id", CalendarId = key, Title = "합성 상태 편집 테스트 " + i, Start = now.AddHours(1).ToString("o"), End = now.AddHours(2).ToString("o"), Fields = new Dictionary<string, string> { { "setup", "합성" } }, StatusContext = ctx });
            }
            CalendarRules.Validate(data); var choices = CalendarSelection.Choices(data);
            check("calendar_selector_uses_actual_names_and_account_for_duplicates", choices[0].Name == "전체 캘린더" && choices.Skip(1).Select(c => c.Name).SequenceEqual(new[] { "합성 Shared (person0@example.invalid)", "합성 Shared (person1@example.invalid)" }));
            widget.Source = new SnapshotSource(data); widget.Configuration.SelectedProfileId = ""; widget.Reload();
            var selector = widget.Find<ComboBox>("CalendarSelect"); var list = widget.Find<ItemsControl>("EventList"); check("multi_calendar_all_view_and_duplicate_remote_ids", list.Items.Count == 2);
            selector.SelectedValue = data.Calendars[1].Id; check("multi_calendar_individual_filter_retains_correct_edit_target", list.Items.Count == 1 && ((EventView)list.Items[0]).StatusContext.AccountId == "synthetic-account-1");
            widget.Reload(); check("multi_calendar_selection_survives_reload", (string)selector.SelectedValue == data.Calendars[1].Id);
            var view = (EventView)list.Items[0]; var editor = new StatusEditor(widget.Window, view, widget.StatusDrafts); editor.Window.Show(); editor.Inputs["setup"].Text = "합성 수정";
            editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("status_editor_explicit_save_marks_pending_not_online_saved", editor.Result.Text.Contains("양쪽 미반영") && widget.StatusDrafts.Drafts.Last().Values["setup"] == "합성 수정" && widget.StatusDrafts.Drafts.Last().AccountId == "synthetic-account-1");
            int countBefore = widget.StatusDrafts.Drafts.Count; editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("status_editor_unchanged_save_keeps_single_operation", widget.StatusDrafts.Drafts.Count == countBefore);
            editor.Inputs["setup"].Text = "합성 수정 2";
            check("status_editor_pending_values_remain_editable_and_mark_unsaved", !editor.Inputs["setup"].IsReadOnly && editor.HasUnsavedChanges && editor.Result.Text.Contains("미저장 입력"));
            editor.CloseDecision = delegate { return MessageBoxResult.Cancel; }; editor.Window.Close();
            check("status_editor_cancel_close_preserves_unsaved_input", editor.Window.IsVisible && editor.Inputs["setup"].Text == "합성 수정 2" && widget.StatusDrafts.Drafts.Last().Values["setup"] == "합성 수정");
            editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("status_editor_second_edit_explicitly_replaces_local_pending", widget.StatusDrafts.Drafts.Count == countBefore + 1 && widget.StatusDrafts.Drafts[widget.StatusDrafts.Drafts.Count - 2].Superseded && widget.StatusDrafts.Drafts.Last().Values["setup"] == "합성 수정 2" && !editor.HasUnsavedChanges);
            capture(editor.Window, Path.Combine(output, "status-editor-pending.png"), 96); editor.Window.Close();
            var reopened = new StatusEditor(widget.Window, view, widget.StatusDrafts); check("status_editor_reopens_pending_values_and_missing_real_fields_readonly", reopened.Inputs["setup"].Text == "합성 수정 2" && reopened.Inputs["realTester"].IsReadOnly);
            reopened.Window.Show(); reopened.Inputs["setup"].Text = "닫기 저장 입력"; reopened.CloseDecision = delegate { return MessageBoxResult.Yes; }; reopened.Window.Close();
            check("status_editor_close_save_preserves_input_in_local_pending", !reopened.Window.IsVisible && widget.StatusDrafts.Drafts.Last().Values["setup"] == "닫기 저장 입력" && widget.StatusDrafts.Drafts.Last().State == "waiting-connection");
            var freshContext = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<StatusEditContext>(JsonData.Serialize(data.Events[1].StatusContext)); freshContext.GoogleVersion = "v2"; data.Events[1].StatusContext = freshContext; widget.Reload();
            widget.OpenStatusEditor(view); check("status_stale_remote_version_rejected_before_editor_opens", widget.Find<TextBlock>("StatusText").Text.Contains("편집 대상이 바뀌었습니다"));
            var full = Draft(); full.GoogleEventId = "full-fields-event"; full.Binding.Cells["setup"].Formula = true; full.Binding.Cells["alphaCheck"].SharedMerge = true;
            full.Binding.Cells["realCheck"] = new StatusCell { Field = "realCheck", Address = "K7", OriginalValue = "" }; full.Binding.Cells["realTester"] = new StatusCell { Field = "realTester", Address = "L7", OriginalValue = "" }; full.Values["realCheck"] = ""; full.Values["realTester"] = "";
            var fullView = new EventView { Title = "합성 긴 제목 · 세팅/확인/알파와 테스터/리얼과 테스터 편집 창 표시 검증", CalendarName = "합성 전체 필드", Schedule = "합성 일정", StatusContext = new StatusEditContext { Scope = full.Scope, AccountId = full.AccountId, CalendarId = full.CalendarId, GoogleEventId = full.GoogleEventId, GoogleVersion = full.ExpectedGoogleVersion, Binding = full.Binding } };
            var fullEditor = new StatusEditor(widget.Window, fullView, widget.StatusDrafts); fullEditor.Window.Width = 400; fullEditor.Window.Height = 550; fullEditor.Window.Show();
            if (ThemeManager.IsDark(widget.Configuration)) check("dark_status_readonly_input_has_visible_text", fullEditor.Inputs["setup"].Foreground.ToString() != fullEditor.Inputs["setup"].Background.ToString() && fullEditor.Window.FontSize == widget.Configuration.DisplayFontSize);
            check("status_editor_formula_and_merge_readonly_real_fields_editable", fullEditor.Inputs["setup"].IsReadOnly && fullEditor.Inputs["alphaCheck"].IsReadOnly && !fullEditor.Inputs["realCheck"].IsReadOnly && !fullEditor.Inputs["realTester"].IsReadOnly);
            capture(fullEditor.Window, Path.Combine(output, "status-editor-minimum.png"), 96); ((ScrollViewer)fullEditor.Window.Content).ScrollToBottom(); capture(fullEditor.Window, Path.Combine(output, "status-editor-minimum-bottom-150pct.png"), 144); fullEditor.Window.Close();
            data.Calendars.RemoveAt(1); data.Events.RemoveAt(1); widget.Reload(); check("removed_calendar_selection_falls_back_to_all", (string)selector.SelectedValue == "" && widget.Configuration.SelectedProfileId == "" && list.Items.Count == 1);
            widget.OpenStatusEditor(view); check("stale_card_edit_target_is_rejected_after_calendar_removal", widget.Find<TextBlock>("StatusText").Text.Contains("대상이 바뀌"));
            var native = ConnectedCalendarSource.ProjectNative(new RemoteCalendar { Id = "synthetic-native", Name = "Sample Secondary", AccessRole = "writer" },
                new List<RemoteEvent> { new RemoteEvent { Id = "manual-event", Summary = "Manual event", Start = now.AddHours(1), End = now.AddHours(2) } }, now, "synthetic-account", "synthetic@example.invalid");
            var nativeView = CalendarRules.Project(native, native.Calendars.Single().Id, now).Single();
            var nativePopup = new DayAgendaPopup(widget.Window, widget.Root, now.Date, new List<EventView> { nativeView }, delegate(EventView ignored) { throw new Exception("native edit must remain disabled"); });
            nativePopup.ShowAt(new Point(widget.Window.Left + 20, widget.Window.Top + 20)); nativePopup.AgendaList.SelectedIndex = 0;
            check("native_calendar_popup_offers_google_only_status_add", nativePopup.Find<ItemsControl>("DetailFields").Items.Count == 0 && nativePopup.Find<TextBlock>("NativeCalendarNotice").Visibility == Visibility.Visible && nativePopup.EditButton.Visibility == Visibility.Visible && nativePopup.EditButton.Content.ToString() == "상태 추가");
            capture(nativePopup.Window, Path.Combine(output, "native-calendar-event.png"), 96); nativePopup.Window.Close();
            widget.Source = old; widget.Configuration.SelectedProfileId = "brownfarm"; widget.Reload();
        }
    }
}
