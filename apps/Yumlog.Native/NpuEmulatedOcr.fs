namespace Yumlog.Native

open System
open System.IO
open Windows.Graphics.Imaging
open Windows.Storage

/// CPU target of the Yumlog NPU emulator (apps\Yumlog.NpuEmulator, mode "npu-emulated-cpu").
/// When Windows AI TextRecognizer is not Ready (no NPU, or the model is not provisioned),
/// OCR still runs on any Windows box through the in-box Windows.Media.Ocr engine on the CPU.
/// This engine reports no per-word confidence; Confidence is 0.0 meaning unmeasured, not rejected.
module NpuEmulatedOcr =
    [<Literal>]
    let Provider = "npu-emulated-cpu"

    let private engine () =
        match Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages() with
        | null ->
            match Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages |> Seq.tryHead with
            | Some language -> Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(language) |> Option.ofObj
            | None -> None
        | created -> Some created

    let readiness () : OcrReadiness =
        try
            match engine () with
            | Some created ->
                { Ready = true
                  Code = "ready"
                  Message = $"NPU-emulated CPU OCR ready (Windows.Media.Ocr, {created.RecognizerLanguage.LanguageTag})." }
            | None ->
                { Ready = false
                  Code = "language-missing"
                  Message = "NPU-emulated CPU OCR needs an installed OCR language (Settings > Time & language > Language)." }
        with ex ->
            { Ready = false; Code = "probe-failed"; Message = ex.Message }

    let private point (x: float) (y: float) : TextPoint = { X = x; Y = y }

    let recognize (imagePath: string) : OcrResult =
        let fullPath = Path.GetFullPath(imagePath)
        if not (File.Exists(fullPath)) then
            invalidArg "imagePath" $"Image file does not exist: {imagePath}"
        match engine () with
        | None ->
            { Provider = Provider
              IsAvailable = false
              Message = "NPU-emulated CPU OCR has no installed OCR language."
              Text = ""
              Lines = Array.empty }
        | Some ocr ->
            let file = StorageFile.GetFileFromPathAsync(fullPath).AsTask().GetAwaiter().GetResult()
            use stream = file.OpenAsync(FileAccessMode.Read).AsTask().GetAwaiter().GetResult()
            let decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult()
            use bitmap =
                decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
                    .AsTask().GetAwaiter().GetResult()
            let result = ocr.RecognizeAsync(bitmap).AsTask().WaitAsync(TimeSpan.FromSeconds(30.)).GetAwaiter().GetResult()
            let lines =
                result.Lines
                |> Seq.map (fun line ->
                    { Text = line.Text
                      Words =
                        line.Words
                        |> Seq.map (fun word ->
                            let r = word.BoundingRect
                            let x, y, w, h = float r.X, float r.Y, float r.Width, float r.Height
                            { Text = word.Text
                              Confidence = 0.0
                              BoundingBox =
                                { TopLeft = point x y
                                  TopRight = point (x + w) y
                                  BottomRight = point (x + w) (y + h)
                                  BottomLeft = point x (y + h) } })
                        |> Seq.toArray } : OcrLine)
                |> Seq.toArray
            { Provider = Provider
              IsAvailable = true
              Message = $"NPU-emulated CPU OCR (Windows.Media.Ocr, {ocr.RecognizerLanguage.LanguageTag}): {lines.Length} lines."
              Text = result.Text
              Lines = lines }