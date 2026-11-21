param([Parameter(Mandatory)][string]$Manifest,[Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
[Windows.Media.Ocr.OcrEngine,Windows.Foundation,ContentType=WindowsRuntime] | Out-Null
[Windows.Storage.StorageFile,Windows.Storage,ContentType=WindowsRuntime] | Out-Null
[Windows.Storage.Streams.IRandomAccessStream,Windows.Storage.Streams,ContentType=WindowsRuntime] | Out-Null
[Windows.Graphics.Imaging.BitmapDecoder,Windows.Graphics.Imaging,ContentType=WindowsRuntime] | Out-Null
[Windows.Graphics.Imaging.SoftwareBitmap,Windows.Graphics.Imaging,ContentType=WindowsRuntime] | Out-Null
[Windows.Media.Ocr.OcrResult,Windows.Foundation,ContentType=WindowsRuntime] | Out-Null
$taskMethod=[System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {$_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetGenericArguments().Count -eq 1 -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'} | Select-Object -First 1
function Await-Operation($Operation,[Type]$ResultType){
 $netTask=$taskMethod.MakeGenericMethod($ResultType).Invoke($null,@($Operation))
 $netTask.Wait()
 $netTask.Result
}
$engine=[Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
if(-not $engine){throw 'Classic Windows.Media.Ocr engine is unavailable.'}
$data=Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
$root=Split-Path -Parent ([IO.Path]::GetFullPath($Manifest))
$frames=@()
foreach($film in $data.films){
 foreach($sample in $film.samples){
  $image=Join-Path $root $sample.frame
  $actual=(Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash.ToLowerInvariant()
  if($actual -ne $sample.sha256){throw ('Input no longer matches Yumlog receipt: '+$sample.frame)}
  $stream=$null;$bitmap=$null
  try{
   $file=Await-Operation ([Windows.Storage.StorageFile]::GetFileFromPathAsync($image)) ([Windows.Storage.StorageFile])
   $stream=Await-Operation ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
   $decoder=Await-Operation ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
   $bitmap=Await-Operation ($decoder.GetSoftwareBitmapAsync([Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8,[Windows.Graphics.Imaging.BitmapAlphaMode]::Premultiplied)) ([Windows.Graphics.Imaging.SoftwareBitmap])
   $recognized=Await-Operation ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
   $lines=@($recognized.Lines | ForEach-Object {
    [ordered]@{text=$_.Text;words=@($_.Words | ForEach-Object {[ordered]@{text=$_.Text;bounds=[ordered]@{x=$_.BoundingRect.X;y=$_.BoundingRect.Y;width=$_.BoundingRect.Width;height=$_.BoundingRect.Height}}})}
   })
   $frames += [ordered]@{film=$film.id;frame=$sample.frame;timestampSeconds=$sample.timestampSeconds;sourceFrameIndex=$sample.sourceFrameIndex;sha256=$actual;provider='Windows.Media.Ocr';isAvailable=$true;text=$recognized.Text;lines=$lines}
  }catch{
   $frames += [ordered]@{film=$film.id;frame=$sample.frame;timestampSeconds=$sample.timestampSeconds;sourceFrameIndex=$sample.sourceFrameIndex;sha256=$actual;provider='Windows.Media.Ocr';isAvailable=$false;error=$_.Exception.Message;text='';lines=@()}
  }finally{
   if($bitmap){$bitmap.Dispose()};if($stream){$stream.Dispose()}
  }
 }
 Write-Output ('Read local OCR frames for '+$film.id)
}
$dll=Get-Item -LiteralPath (Join-Path $env:WINDIR 'System32/Windows.Media.Ocr.dll')
$result=[ordered]@{schemaVersion='1.0.0';attribution='Classic OS Windows.Media.Ocr invoked downstream of verified Yumlog image receipts; not Yumlog Windows AI OCR and not a speech transcription.';engine=[ordered]@{provider='Windows.Media.Ocr';language=$engine.RecognizerLanguage.LanguageTag;osVersion=[Environment]::OSVersion.VersionString;fileVersion=$dll.VersionInfo.FileVersion;sha256=(Get-FileHash -LiteralPath $dll.FullName -Algorithm SHA256).Hash.ToLowerInvariant()};frames=$frames;successfulFrames=@($frames | Where-Object {$_.isAvailable}).Count;failedFrames=@($frames | Where-Object {-not $_.isAvailable}).Count}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($Output),($result | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
Write-Output ('OCR complete: '+$result.successfulFrames+' succeeded; '+$result.failedFrames+' failed')
if($result.failedFrames -gt 0){exit 2}
