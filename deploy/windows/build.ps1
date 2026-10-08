param([ValidateSet('win-x64','win-arm64')][string]$Runtime='win-x64')
$ErrorActionPreference='Stop'
Set-Location (Join-Path $PSScriptRoot '../..')
function Run-Native { param([string]$File,[string[]]$Arguments) & $File @Arguments; if($LASTEXITCODE -ne 0){throw "${File} failed: $LASTEXITCODE"} }
Run-Native 'npm' @('ci','--prefix','apps/web')
Run-Native 'npm' @('run','build','--prefix','apps/web')
Run-Native 'node' @('scripts/check-web-build.mjs')
$out="artifacts/$Runtime/desktop"
if(Test-Path $out){Remove-Item $out -Recurse -Force}
Run-Native 'dotnet' @('restore','apps/host',"-p:RuntimeIdentifier=$Runtime",'--locked-mode')
Run-Native 'dotnet' @('publish','apps/host','--no-restore','-c','Release','-r',$Runtime,'--self-contained','true','-o',"$out/service")
Run-Native 'dotnet' @('restore','deploy/windows/F1Hue.Desktop.csproj',"-p:RuntimeIdentifier=$Runtime",'--locked-mode')
Run-Native 'dotnet' @('publish','deploy/windows/F1Hue.Desktop.csproj','--no-restore','-c','Release','-r',$Runtime,'--self-contained','true','-o',$out)
if($env:F1_HUE_RELEASES_URL){Set-Content "$out/releases-url.txt" $env:F1_HUE_RELEASES_URL}
if($env:WINDOWS_SIGNING_THUMBPRINT){
  Get-ChildItem $out -Filter *.exe -Recurse | ForEach-Object { Run-Native 'signtool' @('sign','/sha1',$env:WINDOWS_SIGNING_THUMBPRINT,'/fd','SHA256','/tr','https://timestamp.digicert.com','/td','SHA256',$_.FullName) }
}
$arch=if($Runtime -eq 'win-arm64'){'arm64'}else{'x64compatible'}
$compiler="${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
if(!(Test-Path $compiler)){throw "Inno Setup 6 est requis pour fabriquer l'installateur Windows."}
$version=([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version
Run-Native $compiler @("/DBuildRoot=$((Resolve-Path $out).Path)","/DArch=$arch","/DAppVersion=$version","/DRuntime=$Runtime",'deploy/windows/installer.iss')
if($env:WINDOWS_SIGNING_THUMBPRINT){Run-Native 'signtool' @('sign','/sha1',$env:WINDOWS_SIGNING_THUMBPRINT,'/fd','SHA256','/tr','https://timestamp.digicert.com','/td','SHA256',"artifacts/F1Hue-$version-$Runtime-Setup.exe")}
Compress-Archive -Path "$out/*" -DestinationPath "artifacts/f1-hue-$Runtime.zip" -Force
