# 트레이용 미어캣 아이콘(assets/meerkat.ico) 생성 스크립트.
#
# 빌드 시점에만 쓰인다. GDI+ 로 256x256 원본을 그린 뒤 여러 크기로 축소해
# 멀티 사이즈 .ico 로 묶는다. 실행 파일에는 /win32icon 으로 포함되므로
# 런타임에는 이 스크립트도 .ico 파일도 필요 없다.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$here   = Split-Path -Parent $MyInvocation.MyCommand.Path
$icoOut = Join-Path $here 'meerkat.ico'
$pngOut = Join-Path $here 'meerkat-preview.png'

# --- 색상 (사막 모래빛 + 부드러운 대비) -------------------------------------
$cFur      = [System.Drawing.Color]::FromArgb(255, 214, 163,  99)   # 몸통
$cFurDark  = [System.Drawing.Color]::FromArgb(255, 178, 128,  70)   # 그림자/꼬리
$cBelly    = [System.Drawing.Color]::FromArgb(255, 246, 224, 188)   # 배/주둥이
$cEarIn    = [System.Drawing.Color]::FromArgb(255, 150, 100,  60)   # 귀 안쪽
$cDark     = [System.Drawing.Color]::FromArgb(255,  59,  42,  32)   # 눈두덩/코
$cOutline  = [System.Drawing.Color]::FromArgb(255, 120,  80,  44)   # 외곽선
$cWhite    = [System.Drawing.Color]::White

function New-MeerkatBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size,
           ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    # 256 기준 좌표를 실제 크기로 변환
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    $furBrush   = New-Object System.Drawing.SolidBrush $cFur
    $darkBrush  = New-Object System.Drawing.SolidBrush $cDark
    $bellyBrush = New-Object System.Drawing.SolidBrush $cBelly
    $earBrush   = New-Object System.Drawing.SolidBrush $cEarIn
    $furDBrush  = New-Object System.Drawing.SolidBrush $cFurDark
    $whiteBrush = New-Object System.Drawing.SolidBrush $cWhite
    # 작은 크기에서도 형태가 뭉개지지 않도록 외곽선을 넉넉히 준다
    $outline    = New-Object System.Drawing.Pen $cOutline, 7
    $outline.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    # 꼬리 - 몸 뒤에서 오른쪽으로 살짝 말려 올라감
    $tail = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tail.AddBezier(188, 224,  232, 214,  246, 168,  222, 132)
    $tailPen = New-Object System.Drawing.Pen $cFurDark, 20
    $tailPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $tailPen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawPath($tailPen, $tail)

    # 귀 (머리보다 먼저 그려 뒤로 보내기)
    foreach ($ex in @(44, 168)) {
        $g.FillEllipse($furBrush, $ex, 26, 46, 46)
        $g.DrawEllipse($outline,  $ex, 26, 46, 46)
        $g.FillEllipse($earBrush, ($ex + 11), 37, 24, 24)
    }

    # 몸통 - 미어캣 특유의 곧게 선 자세
    $g.FillEllipse($furBrush, 66, 108, 124, 140)
    $g.DrawEllipse($outline,  66, 108, 124, 140)

    # 앞발 (몸 앞으로 모은 짧은 팔)
    foreach ($ax in @(78, 148)) {
        $g.FillEllipse($furBrush, $ax, 152, 30, 56)
        $g.DrawEllipse($outline,  $ax, 152, 30, 56)
    }

    # 밝은 배
    $g.FillEllipse($bellyBrush, 96, 140, 64, 96)

    # 머리
    $g.FillEllipse($furBrush, 54, 24, 148, 136)
    $g.DrawEllipse($outline,  54, 24, 148, 136)

    # 눈두덩 - 미어캣의 가장 뚜렷한 특징이라 작은 크기에서도 남도록 크게
    $g.FillEllipse($darkBrush, 72, 62, 58, 50)
    $g.FillEllipse($darkBrush, 126, 62, 58, 50)

    # 눈 (흰자 + 눈동자 + 반짝임)
    $g.FillEllipse($whiteBrush, 86, 74, 28, 28)
    $g.FillEllipse($whiteBrush, 142, 74, 28, 28)
    $g.FillEllipse($darkBrush, 93, 80, 17, 17)
    $g.FillEllipse($darkBrush, 149, 80, 17, 17)
    $g.FillEllipse($whiteBrush, 97, 83, 6, 6)
    $g.FillEllipse($whiteBrush, 153, 83, 6, 6)

    # 주둥이와 코
    $g.FillEllipse($bellyBrush, 98, 104, 60, 46)
    $g.FillEllipse($darkBrush, 116, 110, 24, 18)

    # 입 - 작은 미소
    $mouthPen = New-Object System.Drawing.Pen $cDark, 5
    $mouthPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $mouthPen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($mouthPen, 108, 118, 20, 20, 20, 120)
    $g.DrawArc($mouthPen, 128, 118, 20, 20, 40, 120)

    # 발
    foreach ($fx in @(84, 132)) {
        $g.FillEllipse($furDBrush, $fx, 230, 42, 22)
        $g.DrawEllipse($outline,   $fx, 230, 42, 22)
    }

    foreach ($d in @($furBrush,$darkBrush,$bellyBrush,$earBrush,$furDBrush,$whiteBrush,
                     $outline,$tailPen,$mouthPen,$tail,$g)) { $d.Dispose() }
    return $bmp
}

# --- .ico 컨테이너를 직접 조립 ----------------------------------------------
# Bitmap.GetHicon() + Icon.Save() 경로는 이미지를 16색으로 떨어뜨려 색이 뭉개진다.
# 그래서 32비트 BGRA DIB 를 직접 써서 알파와 색을 그대로 보존한다.

function Get-IconImageBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width
    $h = $bmp.Height

    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $lock = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                          [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $lock.Stride
    $pixels = New-Object byte[] ($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($lock.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($lock)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms

    # BITMAPINFOHEADER - 높이는 XOR + AND 마스크를 합쳐 2배로 적는다
    $bw.Write([uint32]40)
    $bw.Write([int32]$w)
    $bw.Write([int32]($h * 2))
    $bw.Write([uint16]1)              # planes
    $bw.Write([uint16]32)             # bpp
    $bw.Write([uint32]0)              # BI_RGB
    $bw.Write([uint32]($w * $h * 4))  # 이미지 크기
    $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)

    # XOR 픽셀 - DIB 는 아래에서 위로 저장한다
    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($pixels, $y * $stride, $w * 4) }

    # AND 마스크 - 32비트 알파를 쓰므로 전부 0(불투명)으로 두고 행은 4바이트 정렬
    $maskStride = [int]([math]::Floor(($w + 31) / 32) * 4)
    $zeroRow = New-Object byte[] $maskStride
    for ($y = 0; $y -lt $h; $y++) { $bw.Write($zeroRow, 0, $maskStride) }

    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose()
    return $bytes
}

$sizes  = @(16, 20, 24, 32, 48, 64, 128, 256)
$images = @()

foreach ($s in $sizes) {
    $bmp = New-MeerkatBitmap $s
    if ($s -eq 256) { $bmp.Save($pngOut, [System.Drawing.Imaging.ImageFormat]::Png) }

    if ($s -ge 128) {
        # 큰 크기는 무압축 DIB 로 담으면 수백 KB가 된다. Vista 이후로는 .ico 안에
        # PNG 를 그대로 넣을 수 있으므로 그렇게 해서 파일을 작게 유지한다.
        $pms = New-Object System.IO.MemoryStream
        $bmp.Save($pms, [System.Drawing.Imaging.ImageFormat]::Png)
        $data = $pms.ToArray()
        $pms.Dispose()
    } else {
        $data = Get-IconImageBytes $bmp
    }

    $images += [pscustomobject]@{ Size = $s; Data = $data }
    $bmp.Dispose()
}

$out = New-Object System.IO.MemoryStream
$bw  = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0)                    # reserved
$bw.Write([uint16]1)                    # type = icon
$bw.Write([uint16]$images.Count)

$offset = 6 + (16 * $images.Count)
foreach ($img in $images) {
    # ICONDIRENTRY - 256 은 0 으로 표기한다
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $bw.Write([byte]$dim)               # width
    $bw.Write([byte]$dim)               # height
    $bw.Write([byte]0)                  # colorCount (32비트라 0)
    $bw.Write([byte]0)                  # reserved
    $bw.Write([uint16]1)                # planes
    $bw.Write([uint16]32)               # bitCount
    $bw.Write([uint32]$img.Data.Length)
    $bw.Write([uint32]$offset)
    $offset += $img.Data.Length
}
foreach ($img in $images) { $bw.Write($img.Data, 0, $img.Data.Length) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($icoOut, $out.ToArray())
$bw.Dispose()

Write-Output ("아이콘 생성 완료: {0} ({1:N0} bytes, {2}개 크기)" -f $icoOut, (Get-Item $icoOut).Length, $images.Count)
Write-Output ("미리보기: {0}" -f $pngOut)
