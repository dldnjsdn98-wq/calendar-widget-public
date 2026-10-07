using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace CalendarWidget
{
    // The source is a downloaded local copy. A verified backup remains beside it after every edit.
    public sealed class LocalWorkbookStatusEndpoint : IStatusEndpoint
    {
        private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private readonly string path; private readonly ExcelReadOptions options;
        public LocalWorkbookStatusEndpoint(string path, ExcelReadOptions options) { this.path = path; this.options = options; }
        public bool Ready { get { return options != null && !String.IsNullOrWhiteSpace(path) && String.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase) && File.Exists(path); } }
        private StatusRemoteState Inspect(StatusDraft draft, string sourcePath)
        {
            var workbook = ExcelEventParser.Read(sourcePath, options);
            var matches = workbook.Events.Where(e => e.StatusBinding != null && e.StatusBinding.Exact && e.StatusBinding.Signature == draft.Binding.Signature).ToList();
            var table = XlsxReader.Read(sourcePath, draft.Binding.Worksheet);
            var state = new StatusRemoteState { TargetKey = draft.DocumentKey, BindingSignature = draft.Binding.Signature, Version = table.Hash, Exact = matches.Count == 1 && workbook.FileHash == table.Hash && options.DocumentId == draft.Binding.DocumentId && options.Worksheet == draft.Binding.Worksheet };
            foreach (var pair in draft.Binding.Cells) {
                var cell = pair.Value; var parsed = Regex.Match(cell.Address ?? "", @"^([A-Z]+)([1-9][0-9]*)$");
                if (!parsed.Success) { state.Exact = false; continue; }
                int rowNumber = Int32.Parse(parsed.Groups[2].Value); var row = table.Rows.SingleOrDefault(x => x.Number == rowNumber);
                if (row == null) { state.Exact = false; continue; }
                var actual = row.Cell(parsed.Groups[1].Value);
                if (actual.HasFormula || !String.IsNullOrEmpty(actual.MergedRange) || !cell.Editable) state.Exact = false;
                state.Values[pair.Key] = actual.Text ?? "";
            }
            return state;
        }
        public Task<StatusRemoteState> Read(StatusDraft draft, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            return Task.FromResult(Inspect(draft, path));
        }
        public Task Apply(StatusDraft draft, Dictionary<string, string> changedValues, string expectedVersion, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); draft.Validate();
            if (!Ready || changedValues.Keys.Any(key => !draft.Binding.Cells.ContainsKey(key) || !draft.Binding.Cells[key].Editable)) throw new InvalidDataException("확인된 로컬 셀만 수정할 수 있습니다.");
            string temp = path + ".status-" + Guid.NewGuid().ToString("N") + ".xlsx";
            string backup = path + ".status-backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N") + ".xlsx";
            bool replaced = false; string candidateHash = null;
            try {
                // Sharing only deletion keeps Excel writers out while File.Replace swaps the name.
                using (var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) {
                    var source = Inspect(draft, path);
                    if (!source.Exact || source.Version != expectedVersion || source.Version != draft.Binding.SourceHash) throw new StatusConflictException("원본 파일 또는 행이 바뀌었습니다. 새로 읽은 뒤 확인하세요.");
                    foreach (var pair in changedValues) if (source.Values[pair.Key] != (draft.Binding.Cells[pair.Key].OriginalValue ?? "")) throw new StatusConflictException("상태 셀이 다른 값으로 바뀌었습니다.");
                    File.Copy(path, temp, false);
                    if (changedValues.Count > 0) Rewrite(temp, draft.Binding.Worksheet, changedValues.ToDictionary(p => draft.Binding.Cells[p.Key].Address, p => p.Value));
                    var candidate = Inspect(draft, temp);
                    if (!candidate.Exact || changedValues.Any(p => candidate.Values[p.Key] != p.Value) || draft.Values.Any(p => candidate.Values[p.Key] != p.Value)) throw new InvalidDataException("임시 파일 상태를 확인하지 못했습니다.");
                    candidateHash = candidate.Version;
                    cancellation.ThrowIfCancellationRequested();
                    if (SyncHash.Bytes(File.ReadAllBytes(path)) != expectedVersion) throw new StatusConflictException("저장 직전 원본이 바뀌었습니다.");
                    File.Replace(temp, path, backup); replaced = true;
                }
                var saved = Inspect(draft, path);
                if (!saved.Exact || saved.Version != candidateHash || draft.Values.Any(p => saved.Values[p.Key] != p.Value)) throw new IOException("저장 후 다시 읽은 값이 다릅니다. 백업을 보존했습니다.");
                return Task.FromResult(0);
            } catch {
                if (replaced && candidateHash != null) {
                    try {
                        if (SyncHash.Bytes(File.ReadAllBytes(path)) == candidateHash && File.Exists(backup)) {
                            string restore = path + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
                            File.Copy(backup, restore); File.Replace(restore, path, null);
                        }
                    } catch { /* Keep both current file and backup for manual recovery. */ }
                }
                throw;
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private static XDocument Xml(ZipArchive zip, string entryName)
        {
            var entry = zip.GetEntry(entryName); if (entry == null || entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("Excel XML 항목이 없습니다.");
            using (var stream = entry.Open()) using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16000000 })) return XDocument.Load(reader);
        }
        private static void Rewrite(string file, string sheetName, Dictionary<string, string> values)
        {
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) using (var zip = new ZipArchive(stream, ZipArchiveMode.Update)) {
                var sheet = Xml(zip, "xl/workbook.xml").Descendants(S + "sheet").SingleOrDefault(x => (string)x.Attribute("name") == sheetName);
                if (sheet == null) throw new InvalidDataException("대상 시트가 없습니다.");
                var relation = Xml(zip, "xl/_rels/workbook.xml.rels").Root.Elements().SingleOrDefault(x => (string)x.Attribute("Id") == (string)sheet.Attribute(R + "id"));
                if (relation == null || (string)relation.Attribute("TargetMode") == "External") throw new InvalidDataException("시트 경로를 확인할 수 없습니다.");
                var target = new Uri(new Uri("http://package.local/xl/workbook.xml"), (string)relation.Attribute("Target"));
                if (target.Host != "package.local") throw new InvalidDataException("시트 경로가 잘못되었습니다.");
                var sheetPath = target.AbsolutePath.TrimStart('/'); var xml = Xml(zip, sheetPath);
                foreach (var pair in values) {
                    var match = Regex.Match(pair.Key, @"^([A-Z]+)([1-9][0-9]*)$"); if (!match.Success) throw new InvalidDataException("셀 주소가 잘못되었습니다.");
                    int rowNumber = Int32.Parse(match.Groups[2].Value);
                    var rows = xml.Descendants(S + "row").Where(x => (int?)x.Attribute("r") == rowNumber).ToList();
                    if (rows.Count != 1) throw new InvalidDataException("대상 행이 하나가 아닙니다.");
                    var cells = rows[0].Elements(S + "c").Where(x => (string)x.Attribute("r") == pair.Key).ToList();
                    if (cells.Count > 1 || cells.Any(x => x.Element(S + "f") != null)) throw new InvalidDataException("수식 또는 중복 셀은 수정할 수 없습니다.");
                    var cell = cells.SingleOrDefault(); if (cell == null) { cell = new XElement(S + "c", new XAttribute("r", pair.Key)); rows[0].Add(cell); }
                    cell.RemoveNodes(); cell.SetAttributeValue("t", "inlineStr");
                    cell.Add(new XElement(S + "is", new XElement(S + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), pair.Value)));
                }
                var old = zip.GetEntry(sheetPath); old.Delete();
                using (var output = zip.CreateEntry(sheetPath, CompressionLevel.Optimal).Open()) xml.Save(output);
            }
        }
    }
}
