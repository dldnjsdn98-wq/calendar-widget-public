param([switch]$BreakNewXaml)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$test = Join-Path $root ('evidence\helper-smoke-' + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $test '.updates\staged'
$runnerFolder = Join-Path $test '.updates\runner'
New-Item -ItemType Directory -Path $stage,$runnerFolder -Force | Out-Null
$releaseFolder = Join-Path $root 'evidence\release-1.1.0'
$record = Get-Content -LiteralPath (Join-Path $releaseFolder 'release.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$file = Join-Path $stage $record.Package
if ($BreakNewXaml) {
  $unpacked = Join-Path $test 'broken-package'
  Expand-Archive -LiteralPath (Join-Path $releaseFolder $record.Package) -DestinationPath $unpacked
  [IO.File]::WriteAllText((Join-Path $unpacked 'widget.xaml'), '<broken-xaml')
  Compress-Archive -Path (Join-Path $unpacked '*') -DestinationPath $file
  $record.Sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
} else { Copy-Item -LiteralPath (Join-Path $releaseFolder $record.Package) -Destination $file }
$names = @('CalendarWidget.exe','CalendarWidget.exe.config','CalendarWidgetUpdater.exe','widget.xaml','settings.xaml','day-popup.xaml','manual-event.xaml','preview.xaml','sample-data.json')
foreach ($name in $names) {
  if ($BreakNewXaml) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $test $name) }
  else { [IO.File]::WriteAllText((Join-Path $test $name), ('old-' + $name)) }
}
[IO.File]::WriteAllText((Join-Path $test 'settings.json'), '{"SourceDocumentId":"synthetic-document","UseConnectedCalendar":false,"UseWorkbook":false,"ReleaseFeed":""}')
New-Item -ItemType Directory -Path (Join-Path $test '.credentials'),(Join-Path $test '.state') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $test '.credentials\token.dat'), 'synthetic-credential-sentinel')
[IO.File]::WriteAllText((Join-Path $test '.state\status-drafts.json'), '[]')
$runner = Join-Path $runnerFolder 'helper.exe'
Copy-Item -LiteralPath (Join-Path $root 'CalendarWidgetUpdater.exe') -Destination $runner
$request = Join-Path $runnerFolder 'request.json'
@{ Root=$test; PackagePath=$file; Release=$record; OldProcessId=2147483647; EnableOAuth=$false } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $request -Encoding UTF8
$process = Start-Process -FilePath $runner -ArgumentList ('"' + $request + '"') -WorkingDirectory $test -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(35000)) { throw 'Synthetic updater did not exit.' }
$report = Get-Content -LiteralPath (Join-Path $test '.updates\last-result.json') -Raw -Encoding UTF8 | ConvertFrom-Json
Start-Sleep -Seconds 3
$app = @(Get-Process CalendarWidget -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $test 'CalendarWidget.exe') })
$preserved = (Get-Content -LiteralPath (Join-Path $test 'settings.json') -Raw | ConvertFrom-Json).SourceDocumentId -eq 'synthetic-document' -and
  (Get-Content -LiteralPath (Join-Path $test '.credentials\token.dat') -Raw) -eq 'synthetic-credential-sentinel' -and
  (Get-Content -LiteralPath (Join-Path $test '.state\status-drafts.json') -Raw) -eq '[]'
$restored = !$BreakNewXaml -or (Get-FileHash -LiteralPath (Join-Path $test 'CalendarWidget.exe')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $root 'CalendarWidget.exe')).Hash
foreach ($item in $app) { if ($item.Path -eq (Join-Path $test 'CalendarWidget.exe')) { Stop-Process -Id $item.Id -Force } }
[pscustomobject]@{ helperExit=$process.ExitCode; updated=$report.updated; rolledBack=$report.rolledBack;
  testWindowCount=$app.Count; preserved=$preserved; restored=$restored; oldAppUntouched=(Get-Process CalendarWidget -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $root 'CalendarWidget.exe') } | Measure-Object).Count -eq 0 }
if ($BreakNewXaml) {
  if ($process.ExitCode -eq 0 -or $report.updated -or !$report.rolledBack -or $app.Count -ne 1 -or !$preserved -or !$restored) { throw 'Isolated rollback smoke test failed.' }
} elseif ($process.ExitCode -ne 0 -or !$report.updated -or $app.Count -ne 1 -or !$preserved) { throw 'Isolated updater smoke test failed.' }
