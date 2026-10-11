# 从 Assets\icon-source.png 生成 Assets\app.ico（多尺寸）+ Assets\icon-preview.png（预览）
#   powershell -ExecutionPolicy Bypass -File Tools\make-icon.ps1
#
# 流程：
#   1. 把右下角「AI 生成」水印用背景色盖掉
#   2. 自动找出主体（萝卜）的包围盒，居中裁成正方形
#   3. 切成圆角、透明背景
#   4. 输出 16/24/32/48/64/128/256 七个尺寸的 ico（PNG 压缩帧，Win10+ 通用）

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$srcPath = Join-Path $root 'Assets\icon-source.png'
$icoPath = Join-Path $root 'Assets\app.ico'
$pngPath = Join-Path $root 'Assets\icon.png'
$prevPath = Join-Path $root 'Assets\icon-preview.png'

if (-not (Test-Path $srcPath)) { throw "找不到底图：$srcPath" }

$SIZE   = 1024                      # 输出画布
$RADIUS = 180                       # 圆角半径
$BRIGHT = 140                       # 亮度阈值：比这个亮就算「主体」（背景是暗绿，主体是奶白/亮绿）

$src = [System.Drawing.Bitmap]::FromFile($srcPath)
$W = $src.Width; $H = $src.Height

# ---------- 1. 盖掉右下角水印 ----------
# 水印下方那块背景是一整片纯色，所以直接拿水印上一行的颜色「逐列向下延展」，
# 比用平均色填矩形更自然（不会有肉眼可见的色差接缝）。
$maskTop = 900      # 从这一行开始往下盖
$maskLeft = 770     # 只盖右侧，左边是主体
for ($y = $maskTop; $y -lt $H; $y++) {
    for ($x = $maskLeft; $x -lt $W; $x++) {
        $src.SetPixel($x, $y, $src.GetPixel($x, $maskTop - 1))
    }
}
Write-Host ("水印区域已用上一行背景延展覆盖（x>=$maskLeft, y>=$maskTop）") -ForegroundColor DarkGray

# ---------- 2. 找主体包围盒 ----------
$minX = $W; $minY = $H; $maxX = 0; $maxY = 0
for ($y = 0; $y -lt $H; $y += 2) {
    for ($x = 0; $x -lt $W; $x += 2) {
        $c = $src.GetPixel($x, $y)
        $v = [Math]::Max($c.R, [Math]::Max($c.G, $c.B))
        if ($v -ge $BRIGHT) {
            if ($x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
}
if ($maxX -le $minX) { throw "没识别出主体，检查一下底图" }

$cx = ($minX + $maxX) / 2
$cy = ($minY + $maxY) / 2
# 主体占地要留白，取「主体最长边 × 1.2」作为裁剪边长（留白太多会让小尺寸图标看不清）
$side = [Math]::Max($maxX - $minX, $maxY - $minY) * 1.2
if ($side -gt $W) { $side = $W }
if ($side -gt $H) { $side = $H }
$side = [int]$side

$cropX = [int][Math]::Max(0, [Math]::Min($W - $side, $cx - $side / 2))
$cropY = [int][Math]::Max(0, [Math]::Min($H - $side, $cy - $side / 2))

Write-Host ("主体包围盒 x[$minX..$maxX] y[$minY..$maxY]，裁剪 ${side}x${side} @ ($cropX,$cropY)") -ForegroundColor DarkGray

# ---------- 3. 圆角 + 缩放铺满 ----------
$bmp = New-Object System.Drawing.Bitmap($SIZE, $SIZE, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

$d = $RADIUS * 2
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc(0, 0, $d, $d, 180, 90)
$path.AddArc($SIZE - $d, 0, $d, $d, 270, 90)
$path.AddArc($SIZE - $d, $SIZE - $d, $d, $d, 0, 90)
$path.AddArc(0, $SIZE - $d, $d, $d, 90, 90)
$path.CloseFigure()

$g.SetClip($path)
$g.DrawImage($src,
    (New-Object System.Drawing.Rectangle(0, 0, $SIZE, $SIZE)),
    (New-Object System.Drawing.Rectangle($cropX, $cropY, $side, $side)),
    [System.Drawing.GraphicsUnit]::Pixel)
$g.ResetClip()
$bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

# ---------- 4. 多尺寸 + 组装 ico ----------
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @()
$tmpDir = Join-Path ([IO.Path]::GetTempPath()) ("smm-ico-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null

foreach ($s in $sizes) {
    $small = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $sg = [System.Drawing.Graphics]::FromImage($small)
    $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $sg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $sg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $sg.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $sg.Clear([System.Drawing.Color]::Transparent)
    $sg.DrawImage($bmp, (New-Object System.Drawing.Rectangle(0, 0, $s, $s)))
    $sg.Dispose()

    $f = Join-Path $tmpDir "$s.png"
    $small.Save($f, [System.Drawing.Imaging.ImageFormat]::Png)
    $small.Dispose()
    $frames += , @{ Size = $s; File = $f }
}

Copy-Item $frames[-1].File $prevPath -Force

# 小尺寸对比图：16/24/32/48 放大 6 倍排一行，方便肉眼确认小图标认不认得出
$zoom = 6
$picked = @(16, 24, 32, 48)
$cellW = 48 * $zoom + 12
$sheet = New-Object System.Drawing.Bitmap(($cellW * $picked.Count + 12), (48 * $zoom + 24), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$shg = [System.Drawing.Graphics]::FromImage($sheet)
$shg.Clear([System.Drawing.Color]::FromArgb(255, 70, 70, 70))
$shg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$shg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$px = 12
foreach ($fr in $frames) {
    if ($picked -notcontains $fr.Size) { continue }
    $img = [System.Drawing.Image]::FromFile($fr.File)
    $z = [int]$fr.Size * $zoom
    $rect = [System.Drawing.Rectangle]::new([int]$px, 12, $z, $z)
    $shg.DrawImage($img, $rect)
    $img.Dispose()
    $px = [int]$px + $cellW
}
$shg.Dispose()
$sheet.Save((Join-Path $root 'Assets\icon-sizes.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$sheet.Dispose()

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$frames.Count)

$offset = 6 + 16 * $frames.Count
foreach ($fr in $frames) {
    $bytes = [IO.File]::ReadAllBytes($fr.File)
    $dim = if ($fr.Size -ge 256) { 0 } else { $fr.Size }
    $bw.Write([Byte]$dim); $bw.Write([Byte]$dim)
    $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$bytes.Length); $bw.Write([UInt32]$offset)
    $offset += $bytes.Length
    $fr.Bytes = $bytes
}
foreach ($fr in $frames) { $bw.Write($fr.Bytes) }
$bw.Flush()
[IO.File]::WriteAllBytes($icoPath, $ms.ToArray())

$bw.Dispose(); $ms.Dispose(); $g.Dispose(); $bmp.Dispose(); $src.Dispose()
Remove-Item $tmpDir -Recurse -Force

Write-Host ("生成完成：" + $icoPath + "  (" + [math]::Round((Get-Item $icoPath).Length / 1KB, 1) + " KB)") -ForegroundColor Green
Write-Host ("预览图：" + $prevPath) -ForegroundColor Green
