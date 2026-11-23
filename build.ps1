$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

Add-Type -AssemblyName System.Drawing

function New-SafeSweepIcon {
    param([string]$OutPath)

    $sizes = @(16, 32, 48, 256)
    $pngList = New-Object System.Collections.Generic.List[byte[]]

    foreach ($sz in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($sz, $sz, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        $pad = [float]($sz * 0.06)
        $rect = New-Object System.Drawing.RectangleF($pad, $pad, ($sz - $pad * 2), ($sz - $pad * 2))
        $rad = [float]($sz * 0.22)
        $d = $rad * 2.0

        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
        $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
        $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
        $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
        $path.CloseFigure()

        $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $rect,
            [System.Drawing.Color]::FromArgb(255, 20, 32, 44),
            [System.Drawing.Color]::FromArgb(255, 11, 16, 23),
            45.0
        )
        $g.FillPath($bgBrush, $path)

        $borderPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 45, 66, 86), [Math]::Max(1.0, $sz * 0.035))
        $g.DrawPath($borderPen, $path)

        # Crisp Emerald Shield
        $sx = [float]($sz * 0.22)
        $sy = [float]($sz * 0.19)
        $sw = [float]($sz * 0.56)
        $sh = [float]($sz * 0.62)

        $shieldPts = [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF(($sx + $sw * 0.50), $sy)),
            (New-Object System.Drawing.PointF(($sx + $sw), ($sy + $sh * 0.20))),
            (New-Object System.Drawing.PointF(($sx + $sw * 0.90), ($sy + $sh * 0.64))),
            (New-Object System.Drawing.PointF(($sx + $sw * 0.50), ($sy + $sh))),
            (New-Object System.Drawing.PointF(($sx + $sw * 0.10), ($sy + $sh * 0.64))),
            (New-Object System.Drawing.PointF($sx, ($sy + $sh * 0.20)))
        )

        $shieldFill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(60, 52, 211, 153))
        $g.FillPolygon($shieldFill, $shieldPts)

        $shieldPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 52, 211, 153), [Math]::Max(1.4, $sz * 0.068))
        $shieldPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.DrawPolygon($shieldPen, $shieldPts)

        # Crisp Cyan Checkmark inside shield
        $chkPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 94, 234, 212), [Math]::Max(1.5, $sz * 0.085))
        $chkPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $chkPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $chkPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $chkPts = [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF(($sx + $sw * 0.27), ($sy + $sh * 0.50))),
            (New-Object System.Drawing.PointF(($sx + $sw * 0.45), ($sy + $sh * 0.68))),
            (New-Object System.Drawing.PointF(($sx + $sw * 0.75), ($sy + $sh * 0.34)))
        )
        $g.DrawLines($chkPen, $chkPts)

        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngList.Add($ms.ToArray())

        $ms.Dispose()
        $chkPen.Dispose()
        $shieldPen.Dispose()
        $shieldFill.Dispose()
        $borderPen.Dispose()
        $bgBrush.Dispose()
        $path.Dispose()
        $g.Dispose()
        $bmp.Dispose()
    }

    $fs = [System.IO.File]::Create($OutPath)
    $bwOut = New-Object System.IO.BinaryWriter($fs)
    try {
        $bwOut.Write([uint16]0)
        $bwOut.Write([uint16]1)
        $bwOut.Write([uint16]$sizes.Count)

        $offset = 6 + (16 * $sizes.Count)
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $sz = $sizes[$i]
            $bytes = $pngList[$i]
            $dimByte = if ($sz -ge 256) { [byte]0 } else { [byte]$sz }
            $bwOut.Write($dimByte)
            $bwOut.Write($dimByte)
            $bwOut.Write([byte]0)
            $bwOut.Write([byte]0)
            $bwOut.Write([uint16]1)
            $bwOut.Write([uint16]32)
            $bwOut.Write([uint32]$bytes.Length)
            $bwOut.Write([uint32]$offset)
            $offset += $bytes.Length
        }

        foreach ($bytes in $pngList) {
            $bwOut.Write($bytes)
        }
    }
    finally {
        $bwOut.Dispose()
        $fs.Dispose()
    }
}

Write-Host "Generating app.ico..."
New-SafeSweepIcon -OutPath (Join-Path $scriptDir "app.ico")

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    throw "csc.exe not found: $csc"
}

Write-Host "Compiling SafeSweep.exe..."
& $csc `
    /nologo `
    /target:winexe `
    /optimize+ `
    /platform:anycpu `
    /codepage:65001 `
    /utf8output `
    /win32icon:"$scriptDir\app.ico" `
    /win32manifest:"$scriptDir\app.manifest" `
    /reference:System.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /out:"$scriptDir\SafeSweep.exe" `
    "$scriptDir\CleanupEngine.cs" `
    "$scriptDir\Dialogs.cs" `
    "$scriptDir\Program.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed (ExitCode=$LASTEXITCODE)"
}

$exeInfo = Get-Item (Join-Path $scriptDir "SafeSweep.exe")
Write-Host ("Successfully built: {0} ({1:N1} KB)" -f $exeInfo.FullName, ($exeInfo.Length / 1024.0))
