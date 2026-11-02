<#
.SYNOPSIS
  Draws TurboDL's mark and writes assets/TurboDL.ico plus the Chrome extension's PNG icons.

.DESCRIPTION
  The mark is a teal (#0E7C86) rounded square with a white arrow landing on a line, drawn on a
  32-unit grid. Sizes 32 and up are that drawing scaled. 16, 20 and 24 have their own geometry,
  tuned by hand so the straight strokes sit on whole pixels and stay crisp.

  The .ico holds 16, 20, 24, 32, 40, 48, 64, 128 (32-bit BMP) and 256 (PNG). The extension gets
  icon16/32/48/128.png. Rerun this after changing the drawing and commit what it writes.

  Needs Windows and an STA thread for WPF (pwsh 7 and Windows PowerShell 5.1 both are by default).

.EXAMPLE
  pwsh scripts/make-icons.ps1
#>
[CmdletBinding()]
param(
    # Where TurboDL.ico goes. Defaults to <repo>/assets.
    [string]$AssetsDir = (Join-Path $PSScriptRoot '..\assets'),
    # Where the extension's icon PNGs go. Defaults to <repo>/extension/icons.
    [string]$ExtensionIconsDir = (Join-Path $PSScriptRoot '..\extension\icons')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw 'WPF rendering needs an STA thread. Run this with pwsh or powershell.exe (not -MTA).'
}
Add-Type -AssemblyName PresentationCore, WindowsBase

$Teal = [Windows.Media.Color]::FromRgb(0x0E, 0x7C, 0x86)

# One drawing per size, in that size's pixels. Rect = x, y, side, corner radius.
# Shaft = x, top, bottom. Head = left arm end, tip, right arm end. Base = y, left, right.
function Get-Geometry([int]$size) {
    switch ($size) {
        # Hand-tuned: 2 px strokes on whole-pixel edges, flat ends, and at least 1 px between the tip and the line.
        16 { return @{ Rect = 0, 0, 16, 3.5; Stroke = 2; Cap = 'Flat'
                       Shaft = 8, 3, 9; Head = (5, 6), (8, 9), (11, 6); Base = 12, 4, 12 } }
        20 { return @{ Rect = 1, 1, 18, 4.5; Stroke = 2; Cap = 'Flat'
                       Shaft = 10, 4, 12; Head = (7, 9), (10, 12), (13, 9); Base = 15, 5, 15 } }
        24 { return @{ Rect = 1, 1, 22, 5; Stroke = 2; Cap = 'Flat'
                       Shaft = 12, 4, 15; Head = (8, 11), (12, 15), (16, 11); Base = 18, 6, 18 } }
        default {
            # The 32-unit master drawing, scaled.
            $s = $size / 32.0
            return @{ Rect = (1 * $s), (1 * $s), (30 * $s), (7 * $s); Stroke = 3 * $s; Cap = 'Round'
                      Shaft = (16 * $s), (7 * $s), (19 * $s)
                      Head = ((10.5 * $s), (14 * $s)), ((16 * $s), (19.5 * $s)), ((21.5 * $s), (14 * $s))
                      Base = (24.5 * $s), (9.5 * $s), (22.5 * $s) }
        }
    }
}

function New-IconBitmap([int]$size) {
    $g = Get-Geometry $size
    $pt = { param($x, $y) [Windows.Point]::new($x, $y) }

    $dv = [Windows.Media.DrawingVisual]::new()
    $dc = $dv.RenderOpen()
    try {
        $r = $g.Rect
        $dc.DrawRoundedRectangle([Windows.Media.SolidColorBrush]::new($Teal), $null, [Windows.Rect]::new($r[0], $r[1], $r[2], $r[2]), $r[3], $r[3])

        $pen = [Windows.Media.Pen]::new([Windows.Media.Brushes]::White, $g.Stroke)
        $pen.StartLineCap = $g.Cap
        $pen.EndLineCap = $g.Cap
        $pen.LineJoin = 'Round'

        $dc.DrawLine($pen, (& $pt $g.Shaft[0] $g.Shaft[1]), (& $pt $g.Shaft[0] $g.Shaft[2]))

        $head = [Windows.Media.StreamGeometry]::new()
        $ctx = $head.Open()
        try {
            $ctx.BeginFigure((& $pt $g.Head[0][0] $g.Head[0][1]), $false, $false)
            $ctx.LineTo((& $pt $g.Head[1][0] $g.Head[1][1]), $true, $true)
            $ctx.LineTo((& $pt $g.Head[2][0] $g.Head[2][1]), $true, $true)
        } finally { $ctx.Close() }
        $dc.DrawGeometry($null, $pen, $head)

        $dc.DrawLine($pen, (& $pt $g.Base[1] $g.Base[0]), (& $pt $g.Base[2] $g.Base[0]))
    } finally { $dc.Close() }

    $bmp = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    # Straight (not premultiplied) BGRA, which is what both PNG and an icon's DIB want.
    $straight = [Windows.Media.Imaging.FormatConvertedBitmap]::new($bmp, [Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $straight.Freeze()
    return $straight
}

function ConvertTo-Png([Windows.Media.Imaging.BitmapSource]$bitmap) {
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $ms = [IO.MemoryStream]::new()
    $encoder.Save($ms)
    return $ms.ToArray()
}

# An icon image as a 32-bit DIB: BITMAPINFOHEADER (height doubled), bottom-up BGRA rows, then the 1-bit AND mask.
function ConvertTo-IconDib([Windows.Media.Imaging.BitmapSource]$bitmap) {
    $size = $bitmap.PixelWidth
    $stride = $size * 4
    $pixels = [byte[]]::new($stride * $size)
    $bitmap.CopyPixels($pixels, $stride, 0)

    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $mask = [byte[]]::new($maskStride * $size)

    $ms = [IO.MemoryStream]::new()
    $w = [IO.BinaryWriter]::new($ms)
    $w.Write([uint32]40); $w.Write([int32]$size); $w.Write([int32]($size * 2))
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]0)
    $w.Write([uint32]($pixels.Length + $mask.Length))
    $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        $w.Write($pixels, $y * $stride, $stride)
        for ($x = 0; $x -lt $size; $x++) {
            if ($pixels[$y * $stride + $x * 4 + 3] -eq 0) {
                $row = ($size - 1 - $y) * $maskStride
                $mask[$row + ($x -shr 3)] = $mask[$row + ($x -shr 3)] -bor (0x80 -shr ($x -band 7))
            }
        }
    }
    $w.Write($mask)
    $w.Flush()
    return $ms.ToArray()
}

function Write-Ico([string]$path, [int[]]$sizes) {
    $images = foreach ($size in $sizes) {
        $bitmap = New-IconBitmap $size
        $data = if ($size -ge 256) { ConvertTo-Png $bitmap } else { ConvertTo-IconDib $bitmap }
        , @($size, $data)
    }
    $ms = [IO.MemoryStream]::new()
    $w = [IO.BinaryWriter]::new($ms)
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)   # ICONDIR: reserved, type 1 = icon, count
    $offset = 6 + 16 * $images.Count
    foreach ($img in $images) {
        $dim = if ($img[0] -ge 256) { 0 } else { $img[0] }                         # 0 means 256
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)                                    # planes, bits per pixel
        $w.Write([uint32]$img[1].Length); $w.Write([uint32]$offset)
        $offset += $img[1].Length
    }
    foreach ($img in $images) { $w.Write([byte[]]$img[1]) }
    $w.Flush()
    [IO.File]::WriteAllBytes($path, $ms.ToArray())
}

$AssetsDir = [IO.Path]::GetFullPath($AssetsDir)
$ExtensionIconsDir = [IO.Path]::GetFullPath($ExtensionIconsDir)
New-Item -ItemType Directory -Force $AssetsDir, $ExtensionIconsDir | Out-Null

$ico = Join-Path $AssetsDir 'TurboDL.ico'
Write-Ico $ico 16, 20, 24, 32, 40, 48, 64, 128, 256
Write-Host "Wrote $ico"

foreach ($size in 16, 32, 48, 128) {
    $png = Join-Path $ExtensionIconsDir "icon$size.png"
    [IO.File]::WriteAllBytes($png, (ConvertTo-Png (New-IconBitmap $size)))
    Write-Host "Wrote $png"
}
