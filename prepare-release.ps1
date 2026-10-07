param(
  [Parameter(Mandatory=$true)][string]$OutputDirectory,
  [Parameter(Mandatory=$true)][string]$Version
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$files = @('CalendarWidget.exe','CalendarWidget.exe.config','CalendarWidgetUpdater.exe','widget.xaml','settings.xaml','day-popup.xaml','manual-event.xaml','preview.xaml','sample-data.json')
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a three-part release version.' }
if ((Get-Item (Join-Path $root 'CalendarWidget.exe')).VersionInfo.FileVersion -ne "$Version.0") { throw 'Executable version differs from release version.' }
if ((Get-Item (Join-Path $root 'CalendarWidgetUpdater.exe')).VersionInfo.FileVersion -ne "$Version.0") { throw 'Updater version differs from release version.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$temporary = Join-Path $output ('.staging-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary -Force | Out-Null
try {
  foreach ($file in $files) {
    $source = Join-Path $root $file
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing runtime file: $file" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $temporary $file)
  }
  $name = "CalendarWidget-v$Version.zip"
  $zip = Join-Path $output $name
  $zipTemporary = Join-Path $output ("CalendarWidget-v$Version.tmp.zip")
  if (Test-Path -LiteralPath $zipTemporary) { Remove-Item -LiteralPath $zipTemporary }
  Compress-Archive -Path (Join-Path $temporary '*') -DestinationPath $zipTemporary -CompressionLevel Optimal
  if (Test-Path -LiteralPath $zip) { Move-Item -LiteralPath $zip -Destination ($zip + '.previous') -Force }
  Move-Item -LiteralPath $zipTemporary -Destination $zip
  $record = [ordered]@{ Application='CalendarWidget'; Version=$Version; Package=$name; Sha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() }
  $manifest = Join-Path $output 'release.json'
  $manifestTemporary = $manifest + '.tmp'
  [IO.File]::WriteAllText($manifestTemporary, ($record | ConvertTo-Json -Compress), (New-Object System.Text.UTF8Encoding($false)))
  Move-Item -LiteralPath $manifestTemporary -Destination $manifest -Force
  [pscustomobject]@{ Version=$Version; Package=$zip; Manifest=$manifest; FileCount=$files.Count; ContainsPersonalFiles=$false }
} finally {
  $resolved = [IO.Path]::GetFullPath($temporary)
  $outputRoot = [IO.Path]::GetFullPath($output).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
  if ($resolved.StartsWith($outputRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
