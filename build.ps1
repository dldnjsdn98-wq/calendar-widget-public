param([switch]$Test)
$ErrorActionPreference = 'Stop'
$widgetRoot = $PSScriptRoot
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compilerPath = Join-Path $frameworkPath 'csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '기본 .NET Framework C# 컴파일러를 찾을 수 없습니다. 설치하지 않고 중단합니다.' }
$references = @('System.dll','System.Core.dll','System.Web.dll','System.Web.Extensions.dll','System.Net.Http.dll','System.Security.dll','System.Xml.dll','System.Xml.Linq.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.Windows.Forms.dll','System.Drawing.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkPath $_) }
$references += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path (Join-Path $frameworkPath 'WPF') $_) }
$references += '/reference:' + (Join-Path $frameworkPath 'System.Xaml.dll')
$compilerArgs = @('/nologo','/target:winexe','/platform:anycpu','/optimize+','/utf8output',('/out:' + (Join-Path $widgetRoot 'CalendarWidget.exe'))) + $references
$compilerArgs += Get-ChildItem -LiteralPath $widgetRoot -Filter '*.cs' | Where-Object Name -ne 'UpdaterHelper.cs' | ForEach-Object { $_.FullName }
& $compilerPath $compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Widget build failed.' }
$updaterArgs = @('/nologo','/target:winexe','/platform:anycpu','/optimize+','/utf8output',('/out:' + (Join-Path $widgetRoot 'CalendarWidgetUpdater.exe')))
$updaterArgs += @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Net.Http.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkPath $_) }
$updaterArgs += @((Join-Path $widgetRoot 'AssemblyInfo.cs'),(Join-Path $widgetRoot 'UpdateCore.cs'),(Join-Path $widgetRoot 'UpdaterHelper.cs'))
& $compilerPath $updaterArgs
if ($LASTEXITCODE -ne 0) { throw 'Updater build failed.' }
if ($LASTEXITCODE -ne 0) { throw '컴파일 실패' }
Get-Item -LiteralPath (Join-Path $widgetRoot 'CalendarWidget.exe') | Select-Object Name,Length
if ($Test) {
  $testRoot = Join-Path $widgetRoot 'evidence'
  $testProcess = Start-Process -FilePath (Join-Path $widgetRoot 'CalendarWidget.exe') -ArgumentList @('--verify', ('"' + $testRoot + '"')) -WindowStyle Hidden -PassThru -Wait
  if ($testProcess.ExitCode -ne 0) { throw "검증 실패: $($testProcess.ExitCode)" }
  Get-Content -LiteralPath (Join-Path $testRoot 'verification.json') -Encoding UTF8
}
