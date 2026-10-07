using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace CalendarWidget
{
    public sealed class ExcelCell { public string Text = ""; public string Reference, MergedRange; public bool HasFormula; public DateTime? Date; }
    public sealed class ExcelRow
    {
        public int Number; public readonly Dictionary<string, ExcelCell> Cells = new Dictionary<string, ExcelCell>(StringComparer.OrdinalIgnoreCase);
        public ExcelCell Cell(string column) { ExcelCell value; return column != null && Cells.TryGetValue(column, out value) ? value : new ExcelCell(); }
    }
    public sealed class ExcelTable { public string Hash; public List<ExcelRow> Rows = new List<ExcelRow>(); public List<string> Warnings = new List<string>(); }
    public static class XlsxReader
    {
        private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        public static bool HasSheet(string path, string name)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                return Xml(zip, "xl/workbook.xml").Descendants(S + "sheet").Any(e => (string)e.Attribute("name") == name);
        }
        public static ExcelTable Read(string path, string sheetName)
        {
            if (!String.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(".xlsx 파일을 선택하세요.");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("64MB 이하 파일만 지원합니다.");
                byte[] bytes; using (var memory = new MemoryStream()) { stream.CopyTo(memory); bytes = memory.ToArray(); }
                var table = new ExcelTable { Hash = SyncHash.Bytes(bytes) };
                using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
                {
                    if (zip.Entries.Sum(e => e.Length) > 128 * 1024 * 1024) throw new InvalidDataException("압축 해제 크기가 제한을 넘었습니다.");
                    var workbook = Xml(zip, "xl/workbook.xml");
                    var sheet = workbook.Descendants(S + "sheet").FirstOrDefault(e => (string)e.Attribute("name") == sheetName);
                    if (sheet == null) throw new InvalidDataException("선택한 시트를 찾을 수 없습니다: " + sheetName);
                    var relationId = (string)sheet.Attribute(R + "id");
                    var relations = Xml(zip, "xl/_rels/workbook.xml.rels");
                    var relation = relations.Root.Elements().FirstOrDefault(e => (string)e.Attribute("Id") == relationId);
                    if (relation == null || (string)relation.Attribute("TargetMode") == "External") throw new InvalidDataException("시트 연결 형식이 지원되지 않습니다.");
                    string target = (string)relation.Attribute("Target");
                    var sheetPath = new Uri(new Uri("http://package.local/xl/workbook.xml"), target).AbsolutePath.TrimStart('/');
                    var shared = new List<string>();
                    if (zip.GetEntry("xl/sharedStrings.xml") != null)
                        shared = Xml(zip, "xl/sharedStrings.xml").Descendants(S + "si").Select(si => String.Concat(si.Descendants(S + "t").Select(t => t.Value))).ToList();
                    var dateStyles = new HashSet<int>();
                    if (zip.GetEntry("xl/styles.xml") != null)
                    {
                        var styles = Xml(zip, "xl/styles.xml");
                        var custom = styles.Descendants(S + "numFmt").ToDictionary(e => (int)e.Attribute("numFmtId"), e => (string)e.Attribute("formatCode"));
                        var xfs = styles.Root.Element(S + "cellXfs");
                        if (xfs != null)
                        {
                            int index = 0;
                            foreach (var xf in xfs.Elements())
                            {
                                int fmt = (int?)xf.Attribute("numFmtId") ?? 0; string code;
                                if ((fmt >= 14 && fmt <= 22) || (custom.TryGetValue(fmt, out code) && Regex.IsMatch(Regex.Replace(code, "\"[^\"]*\"|\\\\.", ""), "[yd]", RegexOptions.IgnoreCase))) dateStyles.Add(index);
                                index++;
                            }
                        }
                    }
                    bool date1904 = workbook.Descendants(S + "workbookPr").Any(e => (string)e.Attribute("date1904") == "1" || (string)e.Attribute("date1904") == "true");
                    var sheetXml = Xml(zip, sheetPath);
                    foreach (var rowElement in sheetXml.Descendants(S + "row"))
                    {
                        if (table.Rows.Count >= 20000) throw new InvalidDataException("시트 행이 20,000개 제한을 넘었습니다.");
                        var row = new ExcelRow { Number = (int?)rowElement.Attribute("r") ?? table.Rows.Count + 1 };
                        foreach (var cellElement in rowElement.Elements(S + "c"))
                        {
                            string reference = (string)cellElement.Attribute("r");
                            if (reference == null || !Regex.IsMatch(reference, "^[A-Z]+[0-9]+$")) continue;
                            string column = Regex.Match(reference, "^[A-Z]+").Value;
                            string type = (string)cellElement.Attribute("t"); string raw = (string)cellElement.Element(S + "v") ?? "";
                            var cell = new ExcelCell { Text = raw, Reference = reference, HasFormula = cellElement.Element(S + "f") != null };
                            if (type == "s") { int index; if (!Int32.TryParse(raw, out index) || index < 0 || index >= shared.Count) throw new InvalidDataException("공유 문자열 인덱스 오류"); cell.Text = shared[index]; }
                            else if (type == "inlineStr") cell.Text = String.Concat(cellElement.Descendants(S + "t").Select(t => t.Value));
                            else if (type == "b") cell.Text = raw == "1" ? "TRUE" : "FALSE";
                            else if (type == "e") cell.Text = "";
                            int style = (int?)cellElement.Attribute("s") ?? 0; double serial;
                            if (dateStyles.Contains(style) && Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out serial))
                            { cell.Date = date1904 ? new DateTime(1904, 1, 1).AddDays(serial) : DateTime.FromOADate(serial); cell.Text = cell.Date.Value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture); }
                            row.Cells[column] = cell;
                        }
                        table.Rows.Add(row);
                    }
                    foreach (var merge in sheetXml.Descendants(S + "mergeCell"))
                    {
                        string mergedRange = (string)merge.Attribute("ref") ?? "";
                        var rectangle = Regex.Match(mergedRange, @"^(?<c1>[A-Z]+)(?<r1>\d+):(?<c2>[A-Z]+)(?<r2>\d+)$");
                        if (rectangle.Success) {
                            int fromRow = Int32.Parse(rectangle.Groups["r1"].Value), toRow = Int32.Parse(rectangle.Groups["r2"].Value);
                            int fromColumn = ColumnNumber(rectangle.Groups["c1"].Value), toColumn = ColumnNumber(rectangle.Groups["c2"].Value);
                            foreach (var row in table.Rows.Where(r => r.Number >= fromRow && r.Number <= toRow)) foreach (var protectedColumn in row.Cells.Keys.Concat(new[] { "D", "E", "F", "K", "L" }).Distinct().ToList()) {
                                int number = ColumnNumber(protectedColumn); if (number >= fromColumn && number <= toColumn) { var cell = row.Cell(protectedColumn); cell.MergedRange = mergedRange; row.Cells[protectedColumn] = cell; }
                            }
                        }
                        var match = Regex.Match((string)merge.Attribute("ref") ?? "", @"^(?<column>[A-Z]+)(?<from>\d+):\k<column>(?<to>\d+)$");
                        if (!match.Success) continue;
                        int first = Int32.Parse(match.Groups["from"].Value), last = Int32.Parse(match.Groups["to"].Value);
                        string column = match.Groups["column"].Value; var anchorRow = table.Rows.FirstOrDefault(r => r.Number == first);
                        if (anchorRow == null) continue; var anchorCell = anchorRow.Cell(column); anchorCell.MergedRange = (string)merge.Attribute("ref"); anchorRow.Cells[column] = anchorCell;
                        foreach (var row in table.Rows.Where(r => r.Number > first && r.Number <= last)) if (String.IsNullOrWhiteSpace(row.Cell(column).Text)) row.Cells[column] = anchorCell;
                    }
                    if (table.Rows.Any(row => row.Cells.Values.Any(c => c.HasFormula))) table.Warnings.Add("수식 캐시의 최신성은 확인할 수 없습니다. 원본 제목의 날짜 범위를 직접 계산하며 수식 시간값은 자동 동기화에 사용하지 않습니다.");
                }
                return table;
            }
        }
        private static int ColumnNumber(string column) { int result = 0; foreach (char value in column) result = checked(result * 26 + value - 'A' + 1); return result; }
        private static XDocument Xml(ZipArchive zip, string name)
        {
            var entry = zip.GetEntry(name); if (entry == null || entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("Excel XML 구성 오류");
            using (var stream = entry.Open()) using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16000000 })) return XDocument.Load(reader);
        }
    }
    public sealed class ExcelReadOptions
    {
        public string DocumentId { get; set; } public string Worksheet { get; set; } public int BaseYear { get; set; }
        public string DateColumn { get; set; } public string PhaseColumn { get; set; } public string TitleColumn { get; set; }
        public string RealTitleColumn { get; set; } public int PhaseDurationMinutes { get; set; } public string SummaryPrefix { get; set; }
        public string SourceFormat { get; set; }
        public bool AllowUnlinkedSortedRows { get; set; }
        public ExcelReadOptions() { Worksheet = "이벤트확인"; BaseYear = 2026; DateColumn = "A"; PhaseColumn = "B"; TitleColumn = "C"; RealTitleColumn = "J"; SummaryPrefix = ""; SourceFormat = "auto"; }
    }
    public sealed class SourceEvent
    {
        public string StableId; public string Alias; public string ExplicitId; public string Title; public string BaseTitle; public string Phase;
        public DateTimeOffset Start; public DateTimeOffset? End; public bool AllDay; public bool UnsafeFormula; public bool UnlinkedSource;
        public DateTimeOffset? CycleStart, CycleEnd;
        public bool IdentityNeedsReview; public string MappingStatus;
        public StatusBinding StatusBinding;
        public Dictionary<string, string> Fields = new Dictionary<string, string>();
    }
    public sealed class WorkbookEvents
    {
        public string DocumentId; public string FileHash; public string Path; public DateTimeOffset ObservedAt; public DateTimeOffset? FileModifiedAt;
        public List<SourceEvent> Events = new List<SourceEvent>(); public List<string> Warnings = new List<string>();
    }
    public static class ExcelEventParser
    {
        private static readonly Regex Range = new Regex(@"(?:(?<sy>\d{4})[./-])?(?<sm>\d{1,2})[./-](?<sd>\d{1,2})\s+(?<sh>\d{1,2}):(?<sn>\d{2})\s*[~～–]\s*(?:(?<ey>\d{4})[./-])?(?<em>\d{1,2})[./-](?<ed>\d{1,2})\s+(?<eh>\d{1,2}):(?<en>\d{2})");
        public static WorkbookEvents Read(string path, ExcelReadOptions options)
        {
            return Read(path, options, DateTimeOffset.UtcNow);
        }
        public static WorkbookEvents Read(string path, ExcelReadOptions options, DateTimeOffset now)
        {
            if (options == null || String.IsNullOrWhiteSpace(options.DocumentId) || options.BaseYear < 2000 || options.BaseYear > 2100) throw new InvalidDataException("원본 ID와 기준 연도를 설정하세요.");
            if (options.SourceFormat == "sorted" || ((String.IsNullOrEmpty(options.SourceFormat) || options.SourceFormat == "auto") && XlsxReader.HasSheet(path, "이벤트 정렬"))) return SortedWorkbookParser.Read(path, options, now);
            var table = XlsxReader.Read(path, options.Worksheet);
            var result = new WorkbookEvents { DocumentId = options.DocumentId, FileHash = table.Hash, Path = path, ObservedAt = now, FileModifiedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path)), Warnings = new List<string>(table.Warnings) };
            result.Warnings.Add("로컬 파일 수정 시각: " + CalendarRules.InSeoul(result.FileModifiedAt.Value).ToString("yyyy.MM.dd HH:mm") + " · 불러온 시각: " + CalendarRules.InSeoul(result.ObservedAt).ToString("yyyy.MM.dd HH:mm") + " · Asia/Seoul. 수정 시각만으로 서버 최신성을 보장할 수 없습니다. 공식 페이지에서 최신 파일을 다운로드하세요.");
            if (result.ObservedAt - result.FileModifiedAt.Value > TimeSpan.FromHours(24)) result.Warnings.Add("파일 수정 후 24시간 이상 지났습니다. 최신 다운로드인지 확인한 뒤 동기화하세요.");
            var header = table.Rows.Take(30).FirstOrDefault(row => row.Cells.Values.Any(c => c.Text == "이벤트명" || c.Text == "Title") && row.Cells.Values.Any(c => c.Text == "시작" || c.Text == "Start"));
            if (header != null) ReadNormalized(table, header, result);
            else ReadBrownfarm(table, options, result);
            if (result.Events.Count == 0) result.Warnings.Add("해석 가능한 일정이 없습니다. 시트와 열 설정, 제목의 날짜 범위를 확인하세요.");
            return result;
        }
        private static void ReadNormalized(ExcelTable table, ExcelRow header, WorkbookEvents result)
        {
            Func<ExcelRow, string[], ExcelCell> get = (row, names) => { var column = header.Cells.FirstOrDefault(c => names.Contains(c.Value.Text)).Key; return row.Cell(column); };
            foreach (var row in table.Rows.Where(row => row.Number > header.Number))
            {
                var title = get(row, new[] { "이벤트명", "Title" }); if (String.IsNullOrWhiteSpace(title.Text)) continue;
                var start = get(row, new[] { "시작", "Start" }); var end = get(row, new[] { "종료", "End" });
                try
                {
                    bool allDay = new[] { "TRUE", "1", "종일" }.Contains(get(row, new[] { "종일", "AllDay" }).Text.ToUpperInvariant());
                    var ev = new SourceEvent { Title = title.Text.Trim(), ExplicitId = get(row, new[] { "이벤트ID", "EventId" }).Text.Trim(), Phase = "EVENT", AllDay = allDay,
                        Start = Date(start, allDay), End = String.IsNullOrWhiteSpace(end.Text) ? (DateTimeOffset?)null : Date(end, allDay), UnsafeFormula = title.HasFormula || start.HasFormula || end.HasFormula };
                    if (ev.End.HasValue && ev.End <= ev.Start) throw new InvalidDataException("종료가 시작보다 이르거나 같습니다.");
                    ev.MappingStatus = "직접 일정 행";
                    ev.Fields["setup"] = Blank(get(row, new[] { "세팅", "Setup" }).Text);
                    ev.Fields["check"] = Blank(get(row, new[] { "확인", "Confirmed" }).Text);
                    ev.Fields["alphaTester"] = Blank(get(row, new[] { "알파 테스터", "AlphaTester" }).Text);
                    ev.Fields["realTester"] = Blank(get(row, new[] { "리얼 테스터", "RealTester" }).Text);
                    ev.Alias = SyncHash.Text(ev.Title + "|" + ev.Start.ToString("o") + "|EVENT"); result.Events.Add(ev);
                }
                catch { result.Warnings.Add("행 " + row.Number + ": 시작/종료 형식을 확인하세요. 이 행은 제외했습니다."); }
            }
        }
        private static DateTimeOffset Date(ExcelCell cell, bool allDay)
        {
            if (cell.Date.HasValue) return new DateTimeOffset(DateTime.SpecifyKind(cell.Date.Value, DateTimeKind.Unspecified), TimeSpan.FromHours(9));
            if (allDay) return CalendarRules.ParseDate(cell.Text, true);
            return CalendarRules.ParseDate(cell.Text, false);
        }
        private sealed class Group
        {
            public string Title; public MatchCollection Ranges; public int Year;
            public Dictionary<string, ExcelRow> Phases = new Dictionary<string, ExcelRow>(); public HashSet<string> DuplicatePhases = new HashSet<string>();
        }
        private static void ReadBrownfarm(ExcelTable table, ExcelReadOptions options, WorkbookEvents result)
        {
            var groups = new Dictionary<string, Group>(); bool usedBaseYear = false;
            foreach (var row in table.Rows)
            {
                var titleCell = row.Cell(options.TitleColumn); string text = titleCell.Text.Trim(); var ranges = Range.Matches(text);
                if (ranges.Count == 0) continue;
                if (titleCell.HasFormula) { result.Warnings.Add("행 " + row.Number + ": 수식으로 만든 제목은 최신성을 알 수 없어 제외했습니다."); continue; }
                int year = options.BaseYear; var anchor = row.Cell(options.DateColumn);
                if (!anchor.HasFormula && anchor.Date.HasValue) year = anchor.Date.Value.Year;
                else if (!anchor.HasFormula && Regex.IsMatch(anchor.Text, @"^\d{8}$")) year = Int32.Parse(anchor.Text.Substring(0, 4));
                else if (!ranges[0].Groups["sy"].Success) usedBaseYear = true;
                if (ranges[0].Groups["sy"].Success) year = Int32.Parse(ranges[0].Groups["sy"].Value);
                string title = text.Split('(')[0].Trim(); string key = SyncHash.Text(Regex.Replace(text, @"\s+", "") + "|" + year);
                Group group; if (!groups.TryGetValue(key, out group)) { group = new Group { Title = title, Ranges = ranges, Year = year }; groups.Add(key, group); }
                string phase = NormalizePhase(row.Cell(options.PhaseColumn).Text);
                if (phase != null) { if (group.Phases.ContainsKey(phase)) group.DuplicatePhases.Add(phase); else group.Phases[phase] = row; }
            }
            if (usedBaseYear) result.Warnings.Add("연도 없는 제목은 설정의 기준 연도 " + options.BaseYear + "을 사용했습니다. 동기화 전 연도를 확인하세요.");
            foreach (var group in groups.Values)
            {
                DateTimeOffset? previousEnd = null;
                for (int index = 0; index < Math.Min(2, group.Ranges.Count); index++)
                {
                    var range = group.Ranges[index];
                    try
                    {
                        var start = RangeDate(range, "s", group.Year);
                        if (previousEnd.HasValue && !range.Groups["sy"].Success && start < previousEnd.Value) start = RangeDate(range, "s", previousEnd.Value.Year);
                        int endYear = range.Groups["ey"].Success ? Int32.Parse(range.Groups["ey"].Value) : start.Year;
                        var end = RangeDate(range, "e", endYear); if (!range.Groups["ey"].Success && end < start) end = RangeDate(range, "e", endYear + 1);
                        if (end <= start) throw new InvalidDataException();
                        previousEnd = end;
                        foreach (string kind in new[] { "OPEN", "CLOSE" })
                        {
                            string phase = index == 0 ? kind : "RESULT_" + kind;
                            var point = kind == "OPEN" ? start : end;
                            var ev = new SourceEvent { Title = (options.SummaryPrefix ?? "") + "[" + Label(phase) + "] " + group.Title, BaseTitle = group.Title, Phase = phase, Start = point,
                                End = options.PhaseDurationMinutes > 0 ? point.AddMinutes(options.PhaseDurationMinutes) : (DateTimeOffset?)null, CycleStart = start, CycleEnd = end };
                            ev.Alias = SyncHash.Text(Regex.Replace(group.Title, @"\s+", "") + "|" + start.ToString("o") + "|" + phase);
                            ExcelRow row;
                            bool matched = group.Phases.TryGetValue(phase, out row) && !group.DuplicatePhases.Contains(phase);
                            ev.MappingStatus = matched ? "원본 단계 행 연결" : group.DuplicatePhases.Contains(phase) ? "미연결 · 단계 행 중복" : "미연결 · 해당 단계 행 없음";
                            if (matched)
                            {
                                bool realMatched = Regex.Replace(row.Cell(options.RealTitleColumn).Text, @"\s+", "") == Regex.Replace(row.Cell(options.TitleColumn).Text, @"\s+", "");
                                ev.Fields["setup"] = Value(row.Cell("D"));
                                ev.Fields["check"] = "알파: " + Value(row.Cell("E")) + " · 리얼: " + (realMatched ? Value(row.Cell("K")) : "미연결");
                                ev.Fields["alphaTester"] = "확인: " + Value(row.Cell("E")) + " · 테스터: " + Value(row.Cell("F"));
                                ev.Fields["realTester"] = realMatched ? "확인: " + Value(row.Cell("K")) + " · 테스터: " + Value(row.Cell("L")) : "미연결 · 리얼 제목 불일치";
                                if (row.Cells.Values.Any(c => c.HasFormula)) result.Warnings.Add("행 " + row.Number + ": 수식 상태는 캐시값으로 표시합니다.");
                            }
                            else foreach (var field in new[] { "setup", "check", "alphaTester", "realTester" }) ev.Fields[field] = ev.MappingStatus;
                            result.Events.Add(ev);
                        }
                    }
                    catch { result.Warnings.Add(group.Title + ": 날짜 범위를 해석하지 못했습니다. 해당 범위는 제외했습니다."); }
                }
            }
        }
        public static string NormalizePhase(string text)
        {
            text = Regex.Replace(text ?? "", @"^\s*\d{1,2}\s*/\s*\d{1,2}\s*(?:\([^)]*\))?\s*", "");
            string value = Regex.Replace(text ?? "", @"[\s_-]+", "").ToUpperInvariant();
            if (value == "OPEN") return "OPEN"; if (value == "CLOSE") return "CLOSE";
            if (value == "결과OPEN" || value == "RESULTOPEN") return "RESULT_OPEN";
            if (value == "결과CLOSE" || value == "RESULTCLOSE") return "RESULT_CLOSE";
            return null;
        }
        private static DateTimeOffset RangeDate(Match match, string prefix, int year)
        {
            if (match.Groups[prefix + "y"].Success) year = Int32.Parse(match.Groups[prefix + "y"].Value);
            return new DateTimeOffset(year, Int32.Parse(match.Groups[prefix + "m"].Value), Int32.Parse(match.Groups[prefix + "d"].Value), Int32.Parse(match.Groups[prefix + "h"].Value), Int32.Parse(match.Groups[prefix + "n"].Value), 0, TimeSpan.FromHours(9));
        }
        private static string Label(string phase) { return phase.Replace("RESULT_", "결과 ").Replace("OPEN", "Open").Replace("CLOSE", "Close"); }
        private static string Blank(string value) { return String.IsNullOrWhiteSpace(value) ? "빈칸" : value; }
        private static string Value(ExcelCell cell) { return Blank(cell.Text) + (cell.HasFormula ? " (수식 캐시)" : ""); }
    }
}
