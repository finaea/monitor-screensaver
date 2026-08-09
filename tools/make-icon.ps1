# Generates Assets/MonitorDim.ico, plus Assets/icon.png for the README
#
# The mark is the hoshinosleep artwork with the dim badge overlaid bottom-right.
# The badge is the same half-filled contrast glyph the icon has always used, still
# separated from what is behind it by a transparent cutout so the two shapes never
# merge at tray size.
Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot "..\Assets"
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$icoPath = Join-Path $outDir "MonitorDim.ico"
$pngPath = Join-Path $outDir "icon.png"

$srcPath = Join-Path $PSScriptRoot "..\hoshinosleep.png"
if (-not (Test-Path $srcPath)) { throw "source artwork not found: $srcPath" }

$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$frames = @()

# 32bpp uncompressed DIB frame. GDI+ cannot decode PNG-compressed .ico entries,
# and NotifyIcon goes through GDI+, so PNG frames would leave the tray blank.
function Encode-Dib([System.Drawing.Bitmap]$bmp, [int]$s) {
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($ms)
    $w.Write([UInt32]40); $w.Write([Int32]$s); $w.Write([Int32]($s * 2))
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]0)
    $w.Write([UInt32]($s * $s * 4))
    $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)

    $data = New-Object 'Byte[]' ($s * $s * 4)
    $rect = New-Object System.Drawing.Rectangle 0, 0, $s, $s
    $bd = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    for ($row = 0; $row -lt $s; $row++) {
        $src = [IntPtr]::Add($bd.Scan0, $bd.Stride * ($s - 1 - $row))   # DIB is bottom-up
        [System.Runtime.InteropServices.Marshal]::Copy($src, $data, $row * $s * 4, $s * 4)
    }
    $bmp.UnlockBits($bd)
    $w.Write($data)

    $maskStride = [Math]::Floor(($s + 31) / 32) * 4
    $w.Write((New-Object 'Byte[]' ($maskStride * $s)))
    $w.Flush()

    $bytes = $ms.ToArray()
    $w.Dispose(); $ms.Dispose()

    # Leading comma: without it PowerShell enumerates the array into the pipeline and
    # the caller receives ~40k loose bytes instead of one byte[].
    return , $bytes
}

# Tightest rectangle containing anything not fully transparent, so the icon is sized
# by the drawing rather than by whatever padding the sticker was exported with.
function Get-AlphaBounds([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $bd = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $data = New-Object 'Byte[]' ($bd.Stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($bd.Scan0, $data, 0, $data.Length)
    $bmp.UnlockBits($bd)

    $minX = $w; $minY = $h; $maxX = -1; $maxY = -1
    for ($y = 0; $y -lt $h; $y++) {
        $ro = $y * $bd.Stride
        for ($x = 0; $x -lt $w; $x++) {
            if ($data[$ro + $x * 4 + 3] -gt 8) {
                if ($x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    if ($maxX -lt 0) { return $rect }
    return New-Object System.Drawing.Rectangle $minX, $minY, ($maxX - $minX + 1), ($maxY - $minY + 1)
}

# Halve repeatedly before the final resample. One 446px -> 16px bicubic step samples
# far too sparsely and the face turns to noise; halving averages the discarded detail
# in first. Premultiplied throughout, otherwise the transparent-black surround bleeds
# a dark halo into the white sticker outline.
function Resize-Pre([System.Drawing.Bitmap]$src, [int]$tw, [int]$th) {
    $cur = $src; $owned = $false
    while ($cur.Width -ge $tw * 2 -and $cur.Height -ge $th * 2) {
        $nw = [Math]::Max(1, [int]($cur.Width / 2)); $nh = [Math]::Max(1, [int]($cur.Height / 2))
        $half = New-Object System.Drawing.Bitmap($nw, $nh, [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
        $hg = [System.Drawing.Graphics]::FromImage($half)
        $hg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBilinear
        $hg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $hg.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $hg.DrawImage($cur, (New-Object System.Drawing.Rectangle 0, 0, $nw, $nh))
        $hg.Dispose()
        if ($owned) { $cur.Dispose() }
        $cur = $half; $owned = $true
    }

    $dst = New-Object System.Drawing.Bitmap($tw, $th, [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $dg = [System.Drawing.Graphics]::FromImage($dst)
    $dg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $dg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $dg.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    # TileFlipXY: the default wrap samples past the edge and leaves a transparent seam.
    $ia = New-Object System.Drawing.Imaging.ImageAttributes
    $ia.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
    $dg.DrawImage($cur, (New-Object System.Drawing.Rectangle 0, 0, $tw, $th),
        0, 0, $cur.Width, $cur.Height, [System.Drawing.GraphicsUnit]::Pixel, $ia)
    $ia.Dispose(); $dg.Dispose()
    if ($owned) { $cur.Dispose() }
    return $dst
}

$Amber = [System.Drawing.Color]::FromArgb(255, 240, 166, 60)
$Dark  = [System.Drawing.Color]::FromArgb(255, 12, 13, 18)

$srcFull = [System.Drawing.Bitmap]::FromFile($srcPath)
$bounds = Get-AlphaBounds $srcFull
$art = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
$ag = [System.Drawing.Graphics]::FromImage($art)
$ag.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
$ag.DrawImage($srcFull, (New-Object System.Drawing.Rectangle 0, 0, $bounds.Width, $bounds.Height),
    $bounds.X, $bounds.Y, $bounds.Width, $bounds.Height, [System.Drawing.GraphicsUnit]::Pixel)
$ag.Dispose()
$srcFull.Dispose()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $u = $s / 32.0    # design on a 32x32 grid

    # ---- artwork, full-bleed on its long axis --------------------------------
    # The drawing is wider than it is tall, so it is sat high in the frame rather
    # than centred: that pushes the leftover band down into the corner the badge
    # occupies, and the badge lands on whale instead of on the face and arm.
    $fit = [Math]::Min($s / [double]$art.Width, $s / [double]$art.Height)
    $aw = [Math]::Max(1, [int][Math]::Round($art.Width * $fit))
    $ah = [Math]::Max(1, [int][Math]::Round($art.Height * $fit))
    $scaled = Resize-Pre $art $aw $ah
    $g.DrawImage($scaled, [int][Math]::Round(($s - $aw) / 2.0), [int][Math]::Round(($s - $ah) * 0.28), $aw, $ah)
    $scaled.Dispose()

    # ---- transparent notch so the badge reads as a separate shape ---------
    $bcx = 24.9 * $u; $bcy = 24.9 * $u
    $badgeR = 6.0 * $u
    $notchR = $badgeR + [Math]::Max(1.1 * $u, 1.0)

    $old = $g.CompositingMode
    $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $clear = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0, 0, 0, 0))
    $g.FillEllipse($clear, ($bcx - $notchR), ($bcy - $notchR), ($notchR * 2), ($notchR * 2))
    $clear.Dispose()
    $g.CompositingMode = $old

    # ---- the dim badge: half-filled contrast glyph ------------------------
    $badge = New-Object System.Drawing.Drawing2D.GraphicsPath
    $badge.AddEllipse(($bcx - $badgeR), ($bcy - $badgeR), ($badgeR * 2), ($badgeR * 2))

    $oldClip = $g.Clip
    $g.SetClip($badge)

    $lit = New-Object System.Drawing.SolidBrush $Amber
    $dim = New-Object System.Drawing.SolidBrush $Dark
    $g.FillRectangle($dim, ($bcx - $badgeR - 1), ($bcy - $badgeR - 1), ($badgeR * 2 + 2), ($badgeR * 2 + 2))
    $g.FillRectangle($lit, ($bcx - $badgeR - 1), ($bcy - $badgeR - 1), ($badgeR + 1), ($badgeR * 2 + 2))
    $lit.Dispose(); $dim.Dispose()

    $g.Clip = $oldClip

    $bpen = New-Object System.Drawing.Pen $Amber, ([Math]::Max(1.5 * $u, 1.0))
    $g.DrawPath($bpen, $badge)
    $bpen.Dispose(); $badge.Dispose()

    $g.Dispose()

    $frames += , (Encode-Dib $bmp $s)
    # The README cannot render an .ico, so the largest frame doubles as a PNG.
    if ($s -eq 256) { $bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

$art.Dispose()

$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([Byte]$(if ($s -ge 256) { 0 } else { $s }))
    $bw.Write([Byte]$(if ($s -ge 256) { 0 } else { $s }))
    $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$frames[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Flush(); $bw.Close(); $fs.Close()

"Wrote $icoPath ($((Get-Item $icoPath).Length) bytes, $($sizes.Count) sizes)"
"Wrote $pngPath ($((Get-Item $pngPath).Length) bytes, 256px)"
