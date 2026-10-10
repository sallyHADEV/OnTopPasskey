# 배포용 설치 파일(OnTopPasskeySetup.exe)을 만든다.
#
# 본체를 빌드한 뒤, 그 실행 파일을 리소스로 품은 설치 관리자를 컴파일한다.
# 결과물은 단일 파일 하나뿐이며 외부 설치 도구가 필요 없다.
#
# 사용: powershell -ExecutionPolicy Bypass -File .\package.ps1

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$version = '1.0.5'
$dist    = Join-Path $root 'dist'
$setup   = Join-Path $dist "OnTopPasskeySetup-$version.exe"

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw "csc.exe 를 찾을 수 없습니다." }

# 1) 본체 빌드
Write-Output "[1/3] 본체 빌드"
& (Join-Path $root 'build.ps1')

$payload = Join-Path $root 'bin\OnTopPasskey.exe'
if (-not (Test-Path -LiteralPath $payload)) { throw "빌드 결과물이 없습니다: $payload" }

# 실행 파일에 박힌 버전과 패키지 버전이 어긋나면 배포 후 추적이 어려워진다.
$fileVer = (Get-Item -LiteralPath $payload).VersionInfo.FileVersion
if ($fileVer -notlike "$version*") {
    throw "버전 불일치: 본체 $fileVer / 패키지 $version (src\OnTopPasskey.cs 의 AssemblyVersion 확인)"
}

# 2) 설치 관리자 빌드 - 본체를 'payload' 라는 이름의 리소스로 포함시킨다
Write-Output "[2/3] 설치 관리자 빌드"
if (-not (Test-Path $dist)) { New-Item -ItemType Directory $dist | Out-Null }

$ico      = Join-Path $root 'assets\icon.ico'
$manifest = Join-Path $root 'src\setup.manifest'
$src      = Join-Path $root 'src\Setup.cs'

& $csc /nologo /target:winexe /platform:anycpu /optimize+ /langversion:5 `
  /win32icon:$ico `
  /win32manifest:$manifest `
  /resource:"$payload,payload" `
  /out:$setup `
  /reference:System.dll `
  /reference:System.Drawing.dll `
  /reference:System.Windows.Forms.dll `
  $src

if ($LASTEXITCODE -ne 0) { throw "설치 관리자 빌드 실패 (exit $LASTEXITCODE)" }

# 3) 요약
Write-Output "[3/3] 완료"
Write-Output ""
$f = Get-Item -LiteralPath $setup
Write-Output "설치 파일 : $setup"
Write-Output ("크기      : {0:N0} bytes (본체 {1:N0} bytes 포함)" -f $f.Length, (Get-Item $payload).Length)
Write-Output ("버전      : {0}" -f $f.VersionInfo.FileVersion)
Write-Output ("SHA256    : {0}" -f (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash)
Write-Output ""
Write-Output "실행하면 UAC 동의 창이 뜬 뒤 설치 마법사가 열립니다."
Write-Output "제거는 '설정 > 앱 > 설치된 앱' 또는 설치 폴더의 uninstall.exe 로 합니다."
