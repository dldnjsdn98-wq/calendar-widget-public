using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;

namespace CalendarWidget
{
    public static class SortedWorkbookVerification
    {
        private static bool Rejects(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
        public static void Run(string output, Action<string, bool> check)
        {
            string directory = Path.Combine(output, "sorted-synthetic"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "sorted.xlsx");
            var options = new ExcelReadOptions { DocumentId = "synthetic-sorted", PhaseDurationMinutes = 1 };
            var frozen = DateTimeOffset.Parse("2026-10-02T15:00:00+09:00");
            SortedFixture.Write(path, null);
            var book = ExcelEventParser.Read(path, options, frozen);
            check("sorted_cache_crosscheck_and_two_maintenance_points", book.Events.Count == 6 && book.Events.Count(e => e.Phase.StartsWith("MAINTENANCE_")) == 2);
            check("sorted_unknown_time_is_excluded", book.Warnings.Any(w => w.Contains("시각 미정 메모 1건")));
            var today = ExcelEventParser.Read(path, options, DateTimeOffset.Parse("2026-10-06T01:00:00Z"));
            check("sorted_frozen_fixture_and_live_seoul_cutoff_separated", today.Events.Count == 5 && today.Events.All(e => e.Start.Date >= new DateTime(2026, 10, 6)));
            check("sorted_explicit_source_fields_and_missing_stage", book.Events.Single(e => e.Phase == "OPEN").Fields["setup"] == "합성 세팅" && book.Events.Single(e => e.Phase == "CLOSE").Fields["setup"] == "빈칸" && book.Events.Single(e => e.Phase == "RESULT_OPEN").MappingStatus.Contains("행 없음"));
            var confirmation = XlsxReader.Read(path, "이벤트확인");
            check("vertical_declared_merge_propagates_without_filling_unmerged", confirmation.Rows.Single(r => r.Number == 3).Cell("B").Text == confirmation.Rows.Single(r => r.Number == 2).Cell("B").Text && confirmation.Rows.Single(r => r.Number == 5).Cell("B").Text == "");
            check("sorted_result_stage_uses_own_date", book.Events.Single(e => e.Phase == "RESULT_CLOSE").MappingStatus == "원본 단계 행 연결" && book.Events.Single(e => e.Phase == "RESULT_CLOSE").Start.Day == 9);
            check("sorted_source_whitespace_after_slash_supported", book.Events.Single(e => e.Phase == "OPEN").Start == DateTimeOffset.Parse("2026-10-05T11:00:00+09:00"));
            var mutations = new Dictionary<string, Action<Dictionary<string, XElement>>> {
                { "cached_time", sheets => SortedFixture.Set(sheets["이벤트 정렬"], "B7", "0.5", true) },
                { "cached_date_moved_to_past", sheets => SortedFixture.Set(sheets["이벤트 정렬"], "A7", new DateTime(2026, 9, 1).ToOADate().ToString(CultureInfo.InvariantCulture), true) },
                { "cached_body", sheets => SortedFixture.Set(sheets["이벤트 정렬"], "D7", "changed") },
                { "row_key", sheets => SortedFixture.Set(sheets["이벤트 정렬"], "M7", "wrong") },
                { "out_of_range_reference", sheets => SortedFixture.Set(sheets["이벤트 정렬"], "L7", "Z3") },
                { "missing_reference", sheets => SortedFixture.Set(sheets["이벤트 정렬"], "L7", "") },
                { "source_formula", sheets => SortedFixture.Cell(sheets["원본 달력"], "C3").Add(new XElement(SortedFixture.Ns + "f", "1+1")) },
                { "maintenance_grid_day", sheets => SortedFixture.Set(sheets["원본 달력"], "F9", "9") },
                { "wrong_year", sheets => SortedFixture.Set(sheets["이벤트 정렬"], "A4", "기준 2025년 / 원본 원본 달력 B1:H20 /") },
                { "duplicate_semantic_row", sheets => {
                    var duplicate = new XElement(sheets["이벤트 정렬"].Descendants(SortedFixture.Ns + "row").Single(r => (string)r.Attribute("r") == "7"));
                    duplicate.SetAttributeValue("r", 13); foreach (var cell in duplicate.Elements()) cell.SetAttributeValue("r", new string(((string)cell.Attribute("r")).Where(Char.IsLetter).ToArray()) + "13");
                    sheets["이벤트 정렬"].Element(SortedFixture.Ns + "sheetData").Add(duplicate);
                } }
            };
            foreach (var test in mutations) {
                SortedFixture.Write(path, test.Value);
                check("sorted_rejects_" + test.Key, Rejects(delegate { ExcelEventParser.Read(path, options, frozen); }));
            }
            SortedFixture.Write(path, sheets => SortedFixture.Set(sheets["이벤트 정렬"], "L7", ""));
            var displayOptions = new ExcelReadOptions { DocumentId = "synthetic-sorted", PhaseDurationMinutes = 1, AllowUnlinkedSortedRows = true };
            var partial = ExcelEventParser.Read(path, displayOptions, frozen);
            check("sorted_display_keeps_unlinked_future_row_without_source_binding", partial.Events.Count == 6 && partial.Events.Count(e => e.UnlinkedSource && e.StatusBinding == null) == 1 && partial.Warnings.Any(w => w.Contains("7")) && Rejects(delegate { ExcelEventParser.Read(path, options, frozen); }));
            var unlinkedRepo = new SyncRepository(Path.Combine(directory, "unlinked-" + Guid.NewGuid().ToString("N") + ".json"));
            unlinkedRepo.ResolveIdentities(partial);
            var unlinkedPlan = new SyncEngine(unlinkedRepo).Plan(partial, "synthetic", "calendar", new List<RemoteEvent>(), "", frozen);
            check("sorted_unlinked_row_cannot_be_auto_synced", unlinkedPlan.Items.Count(i => i.Source != null && i.Source.UnlinkedSource && i.Reason.Contains("원본 셀 연결") && i.GoogleId == null) == 1);
            SortedFixture.Write(path, null);
            var repo = new SyncRepository(Path.Combine(directory, "state-" + Guid.NewGuid().ToString("N") + ".json"));
            book = ExcelEventParser.Read(path, options, frozen); repo.ResolveIdentities(book);
            check("sorted_formula_spill_verified_but_new_identity_needs_review", book.Events.All(e => !e.UnsafeFormula && e.IdentityNeedsReview));
            var source = book.Events.First(); var other = new RemoteEvent { Id = "unrelated", Summary = "different event", Start = source.Start, End = source.Start.AddMinutes(1), ColorId = "", ETag = "v1" };
            var engine = new SyncEngine(repo);
            var before = engine.Plan(book, "synthetic-account", "synthetic-calendar", new List<RemoteEvent> { other }, "", frozen);
            check("same_time_unconfirmed_source_still_held", before.Items.First().Action == "검토");
            repo.ConfirmNew(book, source);
            var after = engine.Plan(book, "synthetic-account", "synthetic-calendar", new List<RemoteEvent> { other }, "", frozen);
            check("confirmed_distinct_title_can_share_time_with_existing", after.Items.First().Action == "신규");
            other.Summary = source.Title;
            var matching = engine.Plan(book, "synthetic-account", "synthetic-calendar", new List<RemoteEvent> { other }, "", frozen);
            check("same_title_and_time_adopts_existing_after_confirmation", matching.Items.First().Action == "채택" && matching.Items.First().GoogleId == "unrelated");
            other.ColorId = "3";
            var colored = engine.Plan(book, "synthetic-account", "synthetic-calendar", new List<RemoteEvent> { other }, "", frozen);
            check("existing_event_color_is_preserved_when_unspecified", colored.Items.First().Action == "채택" && !colored.Items.First().Payload.ContainsKey("colorId"));
            var fake = new FakeCalendarGateway(); fake.Seed("synthetic-calendar", other);
            engine.Execute(colored, fake, book.FileHash, CancellationToken.None).GetAwaiter().GetResult();
            check("colored_adoption_and_repeat_make_no_remote_writes", fake.Creates == 0 && fake.Updates == 0 && engine.Plan(book, "synthetic-account", "synthetic-calendar", new List<RemoteEvent> { other }, "", frozen).Items.First().Action == "유지");
            var cycles = new WorkbookEvents { DocumentId = "synthetic-cycles", FileHash = "synthetic-hash" };
            var previous = new SourceEvent { ExplicitId = "previous-cycle", Alias = "previous", Title = "[Close] 합성 반복 이벤트", BaseTitle = "합성 반복 이벤트", Phase = "CLOSE", Start = DateTimeOffset.Parse("2026-10-15T10:59:00+09:00"), End = DateTimeOffset.Parse("2026-10-15T11:00:00+09:00") };
            var next = new SourceEvent { ExplicitId = "next-cycle", Alias = "next", Title = previous.Title, BaseTitle = previous.BaseTitle, Phase = previous.Phase, Start = DateTimeOffset.Parse("2026-11-02T10:59:00+09:00"), End = DateTimeOffset.Parse("2026-11-02T11:00:00+09:00") };
            cycles.Events.Add(previous); cycles.Events.Add(next);
            var cycleRepo = new SyncRepository(Path.Combine(directory, "cycles-" + Guid.NewGuid().ToString("N") + ".json")); cycleRepo.ResolveIdentities(cycles);
            var cycleEngine = new SyncEngine(cycleRepo);
            var oldRemote = new RemoteEvent { Id = "existing-cycle", Summary = previous.Title, Start = previous.Start, End = previous.End.Value, ColorId = "", ETag = "v1" };
            var cycleRemote = new List<RemoteEvent> { oldRemote };
            check("same_title_distinct_date_without_source_period_proof_is_held", cycleEngine.Plan(cycles, "synthetic", "cycles", cycleRemote, "", frozen).Items[1].Action == "검토");
            previous.CycleStart = DateTimeOffset.Parse("2026-10-01T11:00:00+09:00"); previous.CycleEnd = previous.Start;
            next.CycleStart = DateTimeOffset.Parse("2026-10-26T11:00:00+09:00"); next.CycleEnd = next.Start;
            var distinct = cycleEngine.Plan(cycles, "synthetic", "cycles", cycleRemote, "", frozen);
            check("reviewed_nonoverlapping_source_cycles_adopt_old_and_create_new", distinct.Items[0].Action == "채택" && distinct.Items[0].GoogleId == oldRemote.Id && distinct.Items[1].Action == "신규" && distinct.Items[1].GoogleId != oldRemote.Id);
            next.CycleStart = DateTimeOffset.Parse("2026-10-14T11:00:00+09:00");
            check("overlapping_same_title_source_cycles_remain_held", cycleEngine.Plan(cycles, "synthetic", "cycles", cycleRemote, "", frozen).Items[1].Action == "검토");
            next.CycleStart = DateTimeOffset.Parse("2026-10-26T11:00:00+09:00"); next.IdentityNeedsReview = true;
            check("nonoverlapping_cycle_still_requires_new_identity_confirmation", cycleEngine.Plan(cycles, "synthetic", "cycles", cycleRemote, "", frozen).Items[1].Action == "검토");
        }
    }

    internal static class SortedFixture
    {
        internal static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string Title = "합성 이벤트 (10/ 5 11:00 ~ 10/8 10:59), result (10/8 11:00 ~ 10/9 10:59)";
        internal static XElement Cell(XElement sheet, string reference) { return sheet.Descendants(Ns + "c").Single(c => (string)c.Attribute("r") == reference); }
        internal static void Set(XElement sheet, string reference, string value, bool numeric = false)
        {
            var cell = Cell(sheet, reference); cell.RemoveNodes(); cell.SetAttributeValue("t", numeric ? null : "inlineStr");
            cell.Add(numeric ? new XElement(Ns + "v", value) : new XElement(Ns + "is", new XElement(Ns + "t", value)));
        }
        private static void Row(XElement sheet, int number, Dictionary<string, string> values, params string[] numeric)
        {
            var row = new XElement(Ns + "row", new XAttribute("r", number));
            foreach (var value in values) {
                bool n = numeric.Contains(value.Key); var cell = new XElement(Ns + "c", new XAttribute("r", value.Key + number));
                if (!n) cell.Add(new XAttribute("t", "inlineStr"));
                cell.Add(n ? new XElement(Ns + "v", value.Value) : new XElement(Ns + "is", new XElement(Ns + "t", value.Value))); row.Add(cell);
            }
            sheet.Element(Ns + "sheetData").Add(row);
        }
        private static XElement Sheet() { return new XElement(Ns + "worksheet", new XElement(Ns + "sheetData")); }
        private static void SortedRow(XElement sheet, int number, int day, int hour, int minute, string label, string raw, string reference, string suffix, int keyDay, bool memo = false)
        {
            Row(sheet, number, new Dictionary<string, string> {
                { "A", new DateTime(2026, 10, day).ToOADate().ToString(CultureInfo.InvariantCulture) },
                { "B", memo ? "미정" : ((hour * 60 + minute) / 1440.0).ToString("R", CultureInfo.InvariantCulture) },
                { "C", label }, { "D", raw }, { "L", reference }, { "M", "202610" + keyDay.ToString("00") + "|" + raw + "|" + suffix }
            }, memo ? new[] { "A" } : new[] { "A", "B" });
        }
        internal static void Write(string path, Action<Dictionary<string, XElement>> mutate)
        {
            var sheets = new Dictionary<string, XElement> { { "이벤트 정렬", Sheet() }, { "원본 달력", Sheet() }, { "이벤트확인", Sheet() } };
            var sorted = sheets["이벤트 정렬"]; var grid = sheets["원본 달력"]; var confirm = sheets["이벤트확인"];
            Row(sorted, 4, new Dictionary<string, string> { { "A", "기준 2026년 / 원본 원본 달력 B1:H20 / 합성 자료" } });
            Row(sorted, 5, new Dictionary<string, string> { { "A", "날짜" }, { "B", "시간" }, { "C", "구분" }, { "L", "원본 셀" }, { "M", "행 키" } });
            SortedRow(sorted, 6, 5, 11, 0, "Open", Title, "C3", "OPEN", 5);
            SortedRow(sorted, 7, 8, 10, 59, "Close", Title, "C3", "CLOSE", 5);
            SortedRow(sorted, 8, 8, 11, 0, "결과 Open", Title, "C3", "RESULT_OPEN", 5);
            SortedRow(sorted, 9, 9, 10, 59, "결과 Close", Title, "C3", "RESULT_CLOSE", 5);
            const string maintenance = "합성 점검 (11시~16시)";
            SortedRow(sorted, 10, 8, 11, 0, "점검 시작", maintenance, "F10", "OPEN", 8);
            SortedRow(sorted, 11, 8, 16, 0, "점검 종료", maintenance, "F10", "CLOSE", 8);
            SortedRow(sorted, 12, 7, 0, 0, "메모", "합성 시각 미정", "E11", "MEMO", 7, true);
            Cell(sorted, "A6").Add(new XElement(Ns + "f", "SORT(synthetic)"));
            Row(grid, 1, new Dictionary<string, string> { { "B", "10월" } });
            Row(grid, 2, new Dictionary<string, string> { { "B", "27" }, { "C", "28" }, { "D", "29" }, { "E", "30" }, { "F", "1" }, { "G", "2" }, { "H", "3" } });
            Row(grid, 3, new Dictionary<string, string> { { "C", Title } });
            Row(grid, 9, new Dictionary<string, string> { { "B", "4" }, { "C", "5" }, { "D", "6" }, { "E", "7" }, { "F", "8" }, { "G", "9" }, { "H", "10" } });
            Row(grid, 10, new Dictionary<string, string> { { "F", maintenance } });
            Row(grid, 11, new Dictionary<string, string> { { "E", "합성 시각 미정" } });
            Row(confirm, 1, new Dictionary<string, string> { { "B", "10/5(月) Open" }, { "C", Title }, { "D", "합성 세팅" }, { "E", "합성 확인" }, { "F", "합성 테스터" }, { "I", "10/5(月) Open" }, { "J", Title }, { "K", "합성 리얼 확인" }, { "L", "합성 리얼 테스터" } });
            Row(confirm, 2, new Dictionary<string, string> { { "B", "10/8(木) Close" }, { "C", Title }, { "I", "10/8(木) Close" }, { "J", Title } });
            Row(confirm, 3, new Dictionary<string, string> { { "C", "합성 별도 항목" } });
            Row(confirm, 4, new Dictionary<string, string> { { "B", "10/9(金) Close" }, { "C", Title } });
            Row(confirm, 5, new Dictionary<string, string> { { "C", Title } });
            confirm.Add(new XElement(Ns + "mergeCells", new XElement(Ns + "mergeCell", new XAttribute("ref", "B2:B3"))));
            if (mutate != null) mutate(sheets);
            using (var stream = File.Create(path)) using (var zip = new ZipArchive(stream, ZipArchiveMode.Create)) {
                Action<string, string> add = (name, content) => { using (var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false))) writer.Write(content); };
                XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships", pkg = "http://schemas.openxmlformats.org/package/2006/relationships";
                var names = new XElement(Ns + "sheets"); var links = new XElement(pkg + "Relationships"); int index = 0;
                foreach (var sheet in sheets) {
                    index++; string id = "rId" + index; names.Add(new XElement(Ns + "sheet", new XAttribute("name", sheet.Key), new XAttribute("sheetId", index), new XAttribute(rel + "id", id)));
                    links.Add(new XElement(pkg + "Relationship", new XAttribute("Id", id), new XAttribute("Target", "worksheets/sheet" + index + ".xml"), new XAttribute("Type", rel.NamespaceName + "/worksheet")));
                    add("xl/worksheets/sheet" + index + ".xml", sheet.Value.ToString());
                }
                add("xl/workbook.xml", new XElement(Ns + "workbook", names).ToString()); add("xl/_rels/workbook.xml.rels", links.ToString());
            }
        }
    }
}
