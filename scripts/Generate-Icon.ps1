Add-Type -AssemblyName System.Drawing
$assetRoot=Join-Path (Split-Path $PSScriptRoot) 'Scout.Desktop/Assets'
[IO.Directory]::CreateDirectory($assetRoot)|Out-Null
$bitmap=[Drawing.Bitmap]::new(1024,1024)
$graphics=[Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([Drawing.Color]::Transparent)
$graphics.ScaleTransform(4,4)
$navy=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#142338'))
$blue=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#78B8FF'))
$light=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#E3F1FF'))
$ring=[Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#78B8FF'),12)
$graphics.FillEllipse($navy,8,8,240,240)
$graphics.DrawEllipse($ring,34,34,188,188)
$points=[Drawing.PointF[]]@([Drawing.PointF]::new(182,68),[Drawing.PointF]::new(147,147),[Drawing.PointF]::new(74,188),[Drawing.PointF]::new(109,109))
$graphics.FillPolygon($blue,$points)
$tip=[Drawing.PointF[]]@([Drawing.PointF]::new(182,68),[Drawing.PointF]::new(147,147),[Drawing.PointF]::new(109,109))
$graphics.FillPolygon($light,$tip)
$graphics.Dispose()
$frames=[Collections.Generic.List[byte[]]]::new()
$sizes=@(16,20,24,32,40,48,64,128,256)
foreach($size in $sizes) {
 $scaled=[Drawing.Bitmap]::new($size,$size)
 $g=[Drawing.Graphics]::FromImage($scaled)
 $g.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
 $g.DrawImage($bitmap,0,0,$size,$size)
 $stream=[IO.MemoryStream]::new()
 $scaled.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
 $frames.Add($stream.ToArray())
 if($size -eq 256){$scaled.Save((Join-Path $assetRoot 'Scout.png'),[Drawing.Imaging.ImageFormat]::Png)}
 $stream.Dispose();$g.Dispose();$scaled.Dispose()
}
$file=[IO.File]::Create((Join-Path $assetRoot 'Scout.ico'))
$writer=[IO.BinaryWriter]::new($file)
$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
$offset=6+16*$sizes.Count
for($i=0;$i -lt $sizes.Count;$i++){
 $dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
 $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0)
 $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frames[$i].Length);$writer.Write([uint32]$offset)
 $offset+=$frames[$i].Length
}
foreach($frame in $frames){$writer.Write([byte[]]$frame)}
$writer.Dispose();$bitmap.Dispose();$navy.Dispose();$blue.Dispose();$light.Dispose();$ring.Dispose()
[IO.File]::WriteAllText((Join-Path $assetRoot 'Scout.svg'),'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256"><circle cx="128" cy="128" r="120" fill="#142338"/><circle cx="128" cy="128" r="94" fill="none" stroke="#78B8FF" stroke-width="12"/><path d="M182 68 147 147 74 188 109 109Z" fill="#78B8FF"/><path d="M182 68 147 147 109 109Z" fill="#E3F1FF"/></svg>')
