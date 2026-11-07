# Export the Scout mascot artwork into Windows icon sizes without redrawing it.
Add-Type -AssemblyName System.Drawing
$assetRoot=Join-Path (Split-Path $PSScriptRoot) 'Scout.Desktop/Assets'
$source=Join-Path $assetRoot 'Scout-mascot-source.png'
if (!(Test-Path -LiteralPath $source)) { throw "Missing mascot source: $source" }
$bitmap=[Drawing.Bitmap]::new($source)
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
$writer.Dispose();$bitmap.Dispose()
