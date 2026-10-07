using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CalendarWidget
{
    public static class SortedWorkbookParser
    {
        private static readonly Regex Period = new Regex(@"(?:(?<sy>\d{4})[./-])?(?<sm>\d{1,2})[./-]\s*(?<sd>\d{1,2})\s+(?<sh>\d{1,2}):(?<sn>\d{2})\s*[~～–]\s*(?:(?<ey>\d{4})[./-])?(?<em>\d{1,2})[./-]\s*(?<ed>\d{1,2})\s+(?<eh>\d{1,2}):(?<en>\d{2})");
        private static readonly Regex Hours = new Regex(@"(?<sh>\d{1,2})시\s*[~～–]\s*(?<eh>\d{1,2})시");
        private static string Clean(string value) { return Regex.Replace(value ?? "", @"\s+", ""); }
        private static string Display(string value) { return Regex.Replace(value ?? "", @"\s+", " ").Trim(); }
        private static int Column(string value) { int n = 0; foreach (char c in value) n = n * 26 + c - 'A' + 1; return n; }
        private static ExcelCell At(ExcelTable table, string reference)
        {
            var match = Regex.Match(reference, @"^(?<c>[A-Z]+)(?<r>\d+)$"); if (!match.Success) throw new InvalidDataException("정렬표 원본 셀 형식 오류");
            var row = table.Rows.FirstOrDefault(r => r.Number == Int32.Parse(match.Groups["r"].Value));
            if (row == null) throw new InvalidDataException("Sorted source row absent: " + match.Groups["r"].Value + "; rows=" + table.Rows.Count);
            if (row == null) throw new InvalidDataException("정렬표 원본 행이 없습니다."); return row.Cell(match.Groups["c"].Value);
        }
        private static List<DateTimeOffset> Points(string text, int year)
        {
            var output = new List<DateTimeOffset>(); DateTimeOffset? previousEnd = null;
            foreach (Match range in Period.Matches(text)) {
                int sy = range.Groups["sy"].Success ? Int32.Parse(range.Groups["sy"].Value) : year;
                var start = Date(range, "s", sy);
                if (previousEnd.HasValue && !range.Groups["sy"].Success && start < previousEnd.Value) {
                    start = Date(range, "s", previousEnd.Value.Year);
                    if (start < previousEnd.Value) start = start.AddYears(1);
                }
                int ey = range.Groups["ey"].Success ? Int32.Parse(range.Groups["ey"].Value) : start.Year;
                var end = Date(range, "e", ey); if (!range.Groups["ey"].Success && end < start) end = Date(range, "e", ey + 1);
                if (end <= start) throw new InvalidDataException("원본 본문 기간이 올바르지 않습니다.");
                output.Add(start); output.Add(end); previousEnd = end;
            }
            if (output.Count > 4) throw new InvalidDataException("본문의 기간이 여러 개여서 단계를 확정할 수 없습니다."); return output;
        }
        private static DateTimeOffset Date(Match m, string p, int year) { return new DateTimeOffset(year, Int32.Parse(m.Groups[p + "m"].Value), Int32.Parse(m.Groups[p + "d"].Value), Int32.Parse(m.Groups[p + "h"].Value), Int32.Parse(m.Groups[p + "n"].Value), 0, TimeSpan.FromHours(9)); }
        private static string BaseTitle(string text) { return Display(text.Split('(')[0]); }
        private static string Signature(string text, int year) { return Clean(BaseTitle(text)) + "|" + String.Join("|", Points(text, year).Select(p => p.ToString("o"))); }
        private static DateTime Day(ExcelCell cell)
        {
            if (cell.Date.HasValue) return cell.Date.Value.Date;
            double value; if (!Double.TryParse(cell.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) throw new InvalidDataException("정렬표 날짜 형식 오류"); return DateTime.FromOADate(value).Date;
        }
        private static int Minutes(ExcelCell cell)
        {
            if (cell.Date.HasValue) return (int)Math.Round(cell.Date.Value.TimeOfDay.TotalMinutes);
            double value; if (!Double.TryParse(cell.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || value < 0 || value >= 1) throw new InvalidDataException("정렬표 시각 형식 오류"); return (int)Math.Round(value * 1440);
        }
        private static DateTime GridDay(ExcelTable grid, string reference, int year)
        {
            var refMatch = Regex.Match(reference, @"^(?<c>[A-Z]+)(?<r>\d+)$"); string column = refMatch.Groups["c"].Value; int rowNumber = Int32.Parse(refMatch.Groups["r"].Value);
            int month = 0, headerRow = 0; ExcelRow band = null; int bandCount = 0;
            foreach (var row in grid.Rows.Where(r => r.Number < rowNumber)) {
                var monthCell = row.Cells.Values.Select(c => Regex.Match(c.Text.Trim(), @"^(?<m>\d{1,2})월$")).FirstOrDefault(m => m.Success);
                if (monthCell != null) { month = Int32.Parse(monthCell.Groups["m"].Value); headerRow = row.Number; band = null; bandCount = 0; }
                if (month == 0) continue;
                int count = row.Cells.Where(c => Column(c.Key) >= 2 && Column(c.Key) <= 8).Count(c => IsDay(c.Value.Text));
                if (count >= 5 && row.Number > headerRow) { band = row; bandCount++; }
            }
            if (band == null || !IsDay(band.Cell(column).Text)) throw new InvalidDataException("시각만 적힌 원본의 달력 날짜를 확정할 수 없습니다.");
            int day = Int32.Parse(band.Cell(column).Text); var numbers = band.Cells.Where(c => Column(c.Key) >= 2 && Column(c.Key) <= 8 && IsDay(c.Value.Text)).OrderBy(c => Column(c.Key)).ToList();
            int first = Int32.Parse(numbers[0].Value.Text); var date = new DateTime(year, month, 1);
            if (first > 20 && numbers.Any(c => c.Value.Text == "1")) { if (bandCount == 1 && day >= first) date = date.AddMonths(-1); else if (bandCount > 1 && day < first) date = date.AddMonths(1); }
            return new DateTime(date.Year, date.Month, day);
        }
        private static bool IsDay(string text) { int day; return Int32.TryParse(text, out day) && day >= 1 && day <= 31; }
        public static WorkbookEvents Read(string path, ExcelReadOptions options)
        {
            return Read(path, options, DateTimeOffset.UtcNow);
        }
        public static WorkbookEvents Read(string path, ExcelReadOptions options, DateTimeOffset now)
        {
            var sorted = XlsxReader.Read(path, "이벤트 정렬"); var caption = sorted.Rows.FirstOrDefault(r => r.Number == 4);
            if (caption == null) throw new InvalidDataException("정렬표 기준/원본 안내가 없습니다.");
            var source = Regex.Match(caption.Cell("A").Text, @"기준\s*(?<y>\d{4})년\s*/\s*원본\s+(?<sheet>.+?)\s+(?<first>[A-Z]+\d+):(?<last>[A-Z]+\d+)\s*/");
            if (!source.Success || Int32.Parse(source.Groups["y"].Value) != options.BaseYear) throw new InvalidDataException("정렬표 기준 연도와 원본 참조를 확인하세요.");
            string sheet = source.Groups["sheet"].Value.Trim(); var grid = XlsxReader.Read(path, sheet); var confirmation = XlsxReader.Read(path, options.Worksheet);
            if (grid.Hash != sorted.Hash || confirmation.Hash != sorted.Hash) throw new InvalidDataException("읽는 중 원본이 변경됐습니다.");
            var header = sorted.Rows.FirstOrDefault(r => r.Number == 5);
            if (header == null || header.Cell("A").Text != "날짜" || header.Cell("B").Text != "시간" || header.Cell("C").Text != "구분" || header.Cell("L").Text != "원본 셀" || header.Cell("M").Text != "행 키") throw new InvalidDataException("지원하는 이벤트 정렬 형식이 아닙니다.");
            var first = Regex.Match(source.Groups["first"].Value, @"^(?<c>[A-Z]+)(?<r>\d+)$"); var last = Regex.Match(source.Groups["last"].Value, @"^(?<c>[A-Z]+)(?<r>\d+)$");
            var result = new WorkbookEvents { DocumentId = options.DocumentId, Path = path, FileHash = sorted.Hash, ObservedAt = now, FileModifiedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path)) };
            var cutoff = CalendarRules.Cutoff(now); int excluded = 0; var keys = new HashSet<string>();
            foreach (var row in sorted.Rows.Where(r => r.Number > 5 && !String.IsNullOrWhiteSpace(r.Cell("A").Text))) {
                var day = Day(row.Cell("A"));
                string[] references = row.Cell("L").Text.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                if (references.Length == 0 && options.AllowUnlinkedSortedRows) {
                    result.Warnings.Add("원본 셀 연결이 없는 정렬 행 " + row.Number + "은(는) Google 동기화와 상태 편집에서 제외했습니다. 원본 XLSX에서 연결을 확인하세요.");
                    var dayCell = row.Cell("A"); var rawCell = row.Cell("D"); var labelCell = row.Cell("C");
                    DateTime unlinkedDay; int unlinkedMinute;
                    try { unlinkedDay = Day(dayCell); unlinkedMinute = Minutes(row.Cell("B")); } catch (InvalidDataException) { continue; }
                    if (!String.IsNullOrWhiteSpace(rawCell.Text) && !String.IsNullOrWhiteSpace(labelCell.Text)) {
                        var start = new DateTimeOffset(unlinkedDay.AddMinutes(unlinkedMinute), TimeSpan.FromHours(9));
                        if (start.Date >= cutoff) {
                            var pendingTitle = String.IsNullOrEmpty(options.SummaryPrefix) ? "[" + labelCell.Text.Trim() + "] " + BaseTitle(rawCell.Text) : options.SummaryPrefix + labelCell.Text.Trim() + " | " + BaseTitle(rawCell.Text);
                            result.Events.Add(new SourceEvent { Alias = SyncHash.Text("unlinked|" + row.Number + "|" + row.Cell("M").Text),
                                Title = pendingTitle, BaseTitle = BaseTitle(rawCell.Text), Phase = ExcelEventParser.NormalizePhase(labelCell.Text),
                                Start = start, End = options.PhaseDurationMinutes > 0 ? start.AddMinutes(options.PhaseDurationMinutes) : (DateTimeOffset?)null,
                                UnlinkedSource = true, MappingStatus = "원본 셀 연결 없음: Google 동기화·상태 편집 보류" });
                        }
                    }
                    continue;
                }
                if (references.Length == 0) throw new InvalidDataException("정렬표 원본 셀이 없습니다.");
                string raw = null;
                foreach (var reference in references) {
                    var address = Regex.Match(reference, @"^(?<c>[A-Z]+)(?<r>\d+)$");
                    if (!address.Success || Column(address.Groups["c"].Value) < Column(first.Groups["c"].Value) || Column(address.Groups["c"].Value) > Column(last.Groups["c"].Value) || Int32.Parse(address.Groups["r"].Value) < Int32.Parse(first.Groups["r"].Value) || Int32.Parse(address.Groups["r"].Value) > Int32.Parse(last.Groups["r"].Value)) throw new InvalidDataException("정렬표 원본 주소가 선언 범위를 벗어났습니다.");
                    var cell = At(grid, reference); if (cell.HasFormula || Clean(cell.Text) != Clean(row.Cell("D").Text)) throw new InvalidDataException("정렬표 캐시와 원본 본문이 다릅니다. 등록을 보류합니다."); raw = cell.Text;
                }
                string label = row.Cell("C").Text.Trim(); string phase = ExcelEventParser.NormalizePhase(label); var points = Points(raw, options.BaseYear); DateTimeOffset point; DateTime keyDate;
                string suffix;
                if (points.Count > 0) {
                    int index = phase == "OPEN" ? 0 : phase == "CLOSE" ? 1 : phase == "RESULT_OPEN" ? 2 : phase == "RESULT_CLOSE" ? 3 : -1;
                    if (index < 0 || index >= points.Count) throw new InvalidDataException("본문 기간과 정렬표 단계가 다릅니다."); point = points[index]; keyDate = points[0].Date; suffix = phase;
                } else {
                    var calendarDay = GridDay(grid, references[0], options.BaseYear); var hours = Hours.Match(raw);
                    if (hours.Success && raw.Contains("점검")) { phase = label == "점검 시작" ? "MAINTENANCE_START" : label == "점검 종료" ? "MAINTENANCE_END" : null; if (phase == null) throw new InvalidDataException("점검 단계가 다릅니다."); int hour = Int32.Parse(hours.Groups[phase == "MAINTENANCE_START" ? "sh" : "eh"].Value); point = new DateTimeOffset(calendarDay.AddHours(hour), TimeSpan.FromHours(9)); suffix = phase == "MAINTENANCE_START" ? "OPEN" : "CLOSE"; }
                    else if (label == "메모" && row.Cell("B").Text == "미정") { if (day != calendarDay) throw new InvalidDataException("미정 메모의 달력 날짜가 다릅니다."); if (day >= cutoff) excluded++; continue; }
                    else { if (day < cutoff) continue; throw new InvalidDataException("본문에서 시각을 확인할 수 없습니다."); }
                    keyDate = calendarDay;
                }
                string expectedKey = keyDate.ToString("yyyyMMdd") + "|" + Display(raw) + "|" + suffix;
                if (Clean(row.Cell("M").Text) != Clean(expectedKey) || point.Date != day || (int)point.TimeOfDay.TotalMinutes != Minutes(row.Cell("B"))) throw new InvalidDataException("정렬표 행 키·날짜·시각과 원본 본문이 다릅니다. 등록을 보류합니다.");
                if (point.Date < cutoff) continue;
                string semantic = Clean(BaseTitle(raw)) + "|" + phase + "|" + point.ToString("o"); if (!keys.Add(semantic)) throw new InvalidDataException("같은 제목·단계·시각의 정렬표 행이 중복됐습니다.");
                string title = String.IsNullOrEmpty(options.SummaryPrefix) ? "[" + label + "] " + BaseTitle(raw) : options.SummaryPrefix + label + " | " + BaseTitle(raw);
                var ev = new SourceEvent { BaseTitle = BaseTitle(raw), Title = title, Phase = phase, Start = point, End = options.PhaseDurationMinutes > 0 ? point.AddMinutes(options.PhaseDurationMinutes) : (DateTimeOffset?)null,
                    Alias = SyncHash.Text(Clean(expectedKey)), MappingStatus = "미연결 · 해당 단계 행 없음" };
                if (points.Count > 0) { int cycle = phase.StartsWith("RESULT_") ? 2 : 0; ev.CycleStart = points[cycle]; ev.CycleEnd = points[cycle + 1]; }
                MapFields(ev, raw, points, confirmation, options);
                result.Events.Add(ev);
            }
            result.Warnings.Add("이벤트 정렬 캐시는 원본 셀 본문·행 키·날짜·시각과 대조했습니다. 원본 불일치 시 등록을 보류합니다.");
            result.Warnings.Add("시각 미정 메모 " + excluded + "건은 제외했습니다.");
            result.Warnings.Add("표시 기준일: " + cutoff.ToString("yyyy.MM.dd") + " 00:00 · Asia/Seoul.");
            result.Warnings.Add("로컬 파일 수정: " + CalendarRules.InSeoul(result.FileModifiedAt.Value).ToString("yyyy.MM.dd HH:mm") + " · 불러온 시각: " + CalendarRules.InSeoul(result.ObservedAt).ToString("yyyy.MM.dd HH:mm") + " · Asia/Seoul. 서버 최신성은 보장하지 않습니다.");
            if (result.ObservedAt - result.FileModifiedAt.Value > TimeSpan.FromHours(24)) result.Warnings.Add("파일 수정 후 24시간 이상 지났습니다. 최신 다운로드인지 확인하세요.");
            return result;
        }
        private static bool PhaseMatches(string label, SourceEvent ev)
        {
            string normalized = ExcelEventParser.NormalizePhase(label); string kind = ev.Phase.StartsWith("RESULT_") ? ev.Phase.Substring(7) : ev.Phase;
            if (normalized != ev.Phase && normalized != kind) return false;
            var date = Regex.Match(label ?? "", @"(?<m>\d{1,2})\s*/\s*(?<d>\d{1,2})");
            return date.Success ? Int32.Parse(date.Groups["m"].Value) == ev.Start.Month && Int32.Parse(date.Groups["d"].Value) == ev.Start.Day : normalized == ev.Phase;
        }
        private static void MapFields(SourceEvent ev, string raw, List<DateTimeOffset> points, ExcelTable confirmation, ExcelReadOptions options)
        {
            var candidates = confirmation.Rows.Where(r => !r.Cell(options.TitleColumn).HasFormula && PhaseMatches(r.Cell(options.PhaseColumn).Text, ev) && Signature(r.Cell(options.TitleColumn).Text, options.BaseYear) == Signature(raw, options.BaseYear)).ToList();
            if (candidates.Count == 1) {
                var row = candidates[0]; ev.MappingStatus = "원본 단계 행 연결";
                bool real = !row.Cell(options.RealTitleColumn).HasFormula && Signature(row.Cell(options.RealTitleColumn).Text, options.BaseYear) == Signature(raw, options.BaseYear) && PhaseMatches(row.Cell("I").Text, ev);
                ev.StatusBinding = new StatusBinding { DocumentId = options.DocumentId, Worksheet = options.Worksheet, SourceHash = confirmation.Hash,
                    EventAlias = ev.Alias, Phase = ev.Phase, RoundSignature = Signature(raw, options.BaseYear), Exact = true };
                AddStatusCell(ev.StatusBinding, row, "setup", "D"); AddStatusCell(ev.StatusBinding, row, "alphaCheck", "E"); AddStatusCell(ev.StatusBinding, row, "alphaTester", "F");
                if (real) { AddStatusCell(ev.StatusBinding, row, "realCheck", "K"); AddStatusCell(ev.StatusBinding, row, "realTester", "L"); }
                ev.Fields["setup"] = Value(row.Cell("D")); ev.Fields["check"] = "알파: " + Value(row.Cell("E")) + " · 리얼: " + (real ? Value(row.Cell("K")) : "미연결");
                ev.Fields["alphaTester"] = "확인: " + Value(row.Cell("E")) + " · 테스터: " + Value(row.Cell("F")); ev.Fields["realTester"] = real ? "확인: " + Value(row.Cell("K")) + " · 테스터: " + Value(row.Cell("L")) : "미연결 · 리얼 단계 불일치";
            } else { if (candidates.Count > 1) ev.MappingStatus = "미연결 · 단계 행 중복"; foreach (var field in new[] { "setup", "check", "alphaTester", "realTester" }) ev.Fields[field] = ev.MappingStatus; }
        }
        private static string Value(ExcelCell cell) { return (String.IsNullOrWhiteSpace(cell.Text) ? "빈칸" : cell.Text) + (cell.HasFormula ? " (수식 캐시)" : ""); }
        private static void AddStatusCell(StatusBinding binding, ExcelRow row, string field, string column)
        {
            var cell = row.Cell(column); binding.Cells[field] = new StatusCell { Field = field, Address = column + row.Number, OriginalValue = cell.Text ?? "", Formula = cell.HasFormula, SharedMerge = !String.IsNullOrEmpty(cell.MergedRange) };
        }
    }
}
