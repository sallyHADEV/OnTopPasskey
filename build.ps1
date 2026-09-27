# OnTopPasskey 빌드 스크립트 (.NET Framework 4.x csc.exe 사용 - SDK 불필요)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src\OnTopPasskey.cs'
$out  = Join-Path $root 'bin\OnTopPasskey.exe'

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw "csc.exe 를 찾을 수 없습니다." }

if (-not (Test-Path (Join-Path $root 'bin'))) { New-Item -ItemType Directory (Join-Path $root 'bin') | Out-Null }

# 트레이 아이콘(미어캣). 없으면 생성한다.
# 생성 스크립트는 System.Drawing 이 필요해 Windows PowerShell 로 돌린다.
$ico = Join-Path $root 'assets\meerkat.ico'
if (-not (Test-Path $ico)) {
    Write-Output "아이콘이 없어 새로 생성합니다..."
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'assets\make-icon.ps1')
    if (-not (Test-Path $ico)) { throw "아이콘 생성 실패" }
}

$manifest = Join-Path $root 'src\app.manifest'
if (-not (Test-Path $manifest)) { throw "매니페스트가 없습니다: $manifest" }

# 참조는 최소한만. 평상시 로드되는 관리 어셈블리는 mscorlib 뿐이다.
# Drawing / Forms 는 마우스 도움말 패널을 처음 띄울 때만 로드된다 (src\MouseHelp.cs).
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /langversion:5 `
  /win32icon:$ico `
  /win32manifest:$manifest `
  /out:$out `
  /reference:System.dll `
  /reference:System.Drawing.dll `
  /reference:System.Windows.Forms.dll `
  /resource:"$(Join-Path $root 'assets\mouse-help.png'),mouse-help.png" `
  $src (Join-Path $root 'src\MouseHelp.cs')

if ($LASTEXITCODE -ne 0) { throw "빌드 실패 (exit $LASTEXITCODE)" }
Write-Output "빌드 완료: $out"
