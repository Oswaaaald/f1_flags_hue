param([string]$Runtime='win-x64')
$ErrorActionPreference='Stop'
if($env:CI -ne 'true'){throw 'Installer lifecycle tests require a disposable CI runner.'}
$version=([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version
$installer=(Resolve-Path "artifacts/F1Hue-$version-$Runtime-Setup.exe").Path
$target=Join-Path $env:RUNNER_TEMP 'F1Hue installer test'
$profile=Join-Path $env:LOCALAPPDATA 'F1Hue'
New-Item -ItemType Directory -Force $profile | Out-Null
$sentinel=Join-Path $profile 'ci-preserve.txt'
Set-Content $sentinel 'User data must survive uninstall'
try {
  1..2 | ForEach-Object {
    $p=Start-Process $installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/DIR=`"$target`"") -Wait -PassThru
    if($p.ExitCode -ne 0){throw "Installer failed: $($p.ExitCode)"}
    if(!(Test-Path "$target/F1Hue.exe") -or !(Test-Path "$target/service/f1-hue.exe")){throw 'Incomplete installed application'}
    $actual=& "$target/service/f1-hue.exe" --version
    if($actual -ne $version){throw 'Installed service version mismatch'}
  }
  $env:F1_HUE_SIMULATE='1'
  $env:F1_HUE_LAUNCHER_PROFILE=$profile
  $tray=Start-Process "$target/F1Hue.exe" -ArgumentList '--background' -PassThru
  try {
    for($i=0;$i -lt 50;$i++) {
      try {Invoke-RestMethod 'http://127.0.0.1:8081/health' | Out-Null; break} catch {Start-Sleep -Milliseconds 200}
    }
    node tests/launcher-service.mjs
    if($LASTEXITCODE -ne 0){throw 'Packaged tray/service integration failed'}
    Set-Content (Join-Path $profile 'stop.request') 'stop'
    for($i=0;$i -lt 50 -and (Test-Path (Join-Path $profile 'desktop-launch.key'));$i++){Start-Sleep -Milliseconds 100}
    if(Test-Path (Join-Path $profile 'desktop-launch.key')){throw 'Tray service did not stop'}
    Write-Output 'PASS Actual Windows tray launches its packaged service and the service stops with its private marker'
  } finally {if(!$tray.HasExited){Stop-Process -Id $tray.Id}; Remove-Item Env:F1_HUE_SIMULATE}
  $p=Start-Process "$target/unins000.exe" -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru
  if($p.ExitCode -ne 0 -or !(Test-Path $sentinel)){throw 'Uninstaller failed or deleted user data'}
  Write-Output 'PASS Windows per-user install, update and uninstall preserve user data'
} finally {Remove-Item $sentinel -ErrorAction SilentlyContinue}
