namespace Yumlog.Native

open System
open System.Collections.Generic
open System.Drawing
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Windows.Graphics.Imaging
open Windows.Storage
open Windows.Media.Ocr
open Yumlog.Native.Mdkg

/// Local, evidence-preserving sparse indexing. This is not a vision-language model:
/// unmeasured identity, typography and acoustic interpretation remain explicitly unknown.
module StudioSemantic =
    type Result =
        { Artifacts: (string * string * Nullable<float> * Nullable<float>) array
          LibraryPath: string
          ReportPath: string
          CardCount: int }

    type Cue = { Start: float; End: float; Text: string }
    type Source = { Path: string; Sha256: string }
    type Evidence = { Path: string; Sha256: string; StartSeconds: float; EndSeconds: float }
    type Provenance = { Method: string; Status: string; Limitations: string array }
    type Flipcard = { Front: JsonElement; Back: JsonElement }
    type Card =
        { Id: string; Modality: string; Kind: string; Title: string
          StartSeconds: float; EndSeconds: float; Source: Source; Evidence: Evidence
          Attributes: JsonElement; Tags: string array; Provenance: Provenance; Flipcard: Flipcard }

    let private json value = JsonSerializer.SerializeToElement(value, NativeJson.options)
    let private stamp (seconds: float) = TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
    let private unknown reason = {| status = "unknown"; value = ""; reason = reason |}
    let private measured value = {| status = "measured"; value = value |}
    let private stopWords =
        set [ "that"; "this"; "with"; "have"; "from"; "there"; "they"; "what"; "your"; "just"; "like"; "about"; "when"; "then"
              "because"; "actually"; "going"; "right"; "also"; "which"; "would"; "will"; "these"; "those"; "more"; "some"; "into"
              "them"; "very"; "want"; "think"; "been"; "does"; "their"; "were"; "where"; "thing"; "things"; "know"; "really"
              "different"; "here"; "still"; "yeah"; "okay"; "something"; "basically"; "every"; "each"; "only"; "than"; "well" ]

    let private tags (text: string) =
        Regex.Matches(text.ToLowerInvariant(), @"\b[a-z][a-z0-9-]{3,}\b")
        |> Seq.map (fun m -> m.Value)
        |> Seq.filter (fun word -> not (stopWords.Contains(word)))
        |> Seq.countBy id
        |> Seq.sortBy (fun (word, count) -> -count, word)
        |> Seq.truncate 8
        |> Seq.map fst
        |> Seq.toArray

    let private time (value: string) =
        let parts = value.Replace(',', '.').Split(':')
        if parts.Length < 2 || parts.Length > 3 then invalidOp "Invalid subtitle timestamp."
        let numbers = parts |> Array.map (fun x -> Double.Parse(x, CultureInfo.InvariantCulture))
        if numbers |> Array.exists (Double.IsFinite >> not) then invalidOp "Subtitle timestamps must be finite."
        if parts.Length = 3 then numbers[0] * 3600. + numbers[1] * 60. + numbers[2]
        else numbers[0] * 60. + numbers[1]

    /// Keep transcript source bytes intact. Whisper endpoints may round up by <=250 ms;
    /// clamp that bounded drift in the index only, and reject larger speech misalignment.
    let readTranscript (path: string) duration =
        let content = File.ReadAllText(path)
        let blocks = Regex.Split(content.Replace("\r\n", "\n"), @"\n\s*\n")
        blocks
        |> Array.choose (fun block ->
            let lines = block.Split('\n')
            let timing = lines |> Array.tryFindIndex (fun line -> line.Contains("-->"))
            match timing with
            | None -> None
            | Some index ->
                let m = Regex.Match(lines[index], @"(?<start>(?:\d+:)?\d{2}:\d{2}[.,]\d+)\s*-->\s*(?<end>(?:\d+:)?\d{2}:\d{2}[.,]\d+)")
                if not m.Success then invalidOp "A subtitle cue has an invalid time range."
                let start = time m.Groups["start"].Value
                let finish = time m.Groups["end"].Value
                if start < 0. || finish < start then invalidOp "A subtitle cue has reversed or negative timestamps."
                let text = lines |> Array.skip (index + 1) |> String.concat " " |> fun x -> Regex.Replace(x, "<[^>]+>", "").Trim()
                if String.IsNullOrWhiteSpace(text) || text = "[BLANK_AUDIO]" then None
                elif start >= duration then invalidOp "A speech cue starts beyond the recording; fix alignment before decimation."
                elif finish > duration + 0.250 then invalidOp "A speech cue extends more than 250 ms beyond the recording; fix alignment before decimation."
                else Some { Start = start; End = min duration finish; Text = text })
        |> Array.sortBy _.Start

    let private palette (path: string) =
        use image = new Bitmap(path)
        let colors = Dictionary<int, int>()
        for gy in 0 .. 35 do
            for gx in 0 .. 63 do
                let pixel = image.GetPixel(min (image.Width - 1) (gx * image.Width / 64), min (image.Height - 1) (gy * image.Height / 36))
                let key = (int pixel.R / 32 <<< 6) ||| (int pixel.G / 32 <<< 3) ||| (int pixel.B / 32)
                colors[key] <- (match colors.TryGetValue(key) with true, n -> n + 1 | _ -> 1)
        let values =
            colors |> Seq.sortBy (fun pair -> -pair.Value, pair.Key) |> Seq.truncate 6
            |> Seq.map (fun pair ->
                let r = ((pair.Key >>> 6) &&& 7) * 32 + 16
                let g = ((pair.Key >>> 3) &&& 7) * 32 + 16
                let b = (pair.Key &&& 7) * 32 + 16
                {| hex = sprintf "#%02X%02X%02X" r g b; fraction = float pair.Value / 2304. |}) |> Seq.toArray
        image.Width, image.Height, values

    let private classicOcr (path: string) =
        try
            match OcrEngine.TryCreateFromUserProfileLanguages() with
            | null -> "", "No installed classic Windows OCR language."
            | engine ->
                let file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult()
                use stream = file.OpenAsync(FileAccessMode.Read).AsTask().GetAwaiter().GetResult()
                let decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult()
                use bitmap = decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask().GetAwaiter().GetResult()
                let result = engine.RecognizeAsync(bitmap).AsTask().WaitAsync(TimeSpan.FromSeconds(15.)).GetAwaiter().GetResult()
                result.Text, "Classic Windows OCR; automatic text may be inaccurate."
        with error -> "", "Classic Windows OCR unavailable: " + error.Message

    let private schema = """{
      "$schema":"https://json-schema.org/draft/2020-12/schema",
      "$id":"urn:yumlog:atomic-flipcard:v1",
      "title":"Evidence-linked atomic flipcard","type":"object",
      "required":["id","modality","kind","title","startSeconds","endSeconds","source","evidence","attributes","tags","provenance","flipcard"],
      "properties":{
        "id":{"type":"string","minLength":1},"modality":{"enum":["video","audio","document"]},
        "kind":{"type":"string"},"title":{"type":"string"},
        "startSeconds":{"type":"number","minimum":0},"endSeconds":{"type":"number","minimum":0},
        "source":{"type":"object","required":["path","sha256"],"properties":{"path":{"type":"string"},"sha256":{"type":"string","pattern":"^[0-9a-fA-F]{64}$"}}},
        "evidence":{"type":"object","required":["path","sha256","startSeconds","endSeconds"],"properties":{"path":{"type":"string"},"sha256":{"type":"string","pattern":"^[0-9a-fA-F]{64}$"},"startSeconds":{"type":"number","minimum":0},"endSeconds":{"type":"number","minimum":0}}},
        "attributes":{"type":"object","description":"Each interpretation includes measured, inferred, or unknown status; absence of measurement is not zero confidence."},
        "tags":{"type":"array","items":{"type":"string"}},
        "provenance":{"type":"object","required":["method","status","limitations"],"properties":{"method":{"type":"string"},"status":{"type":"string"},"limitations":{"type":"array","items":{"type":"string"}}}},
        "flipcard":{"type":"object","required":["front","back"],"properties":{"front":{"type":"object"},"back":{"type":"object"}}}
      }}"""

    let run (ffmpeg: string) (whisper: string) (model: string) (videoPath: string) (transcriptPath: string) (outputDirectory: string) : Result =
        let videoPath = Path.GetFullPath(videoPath)
        if not (File.Exists(videoPath)) then invalidArg "videoPath" "Recording is missing."
        let output = Path.GetFullPath(outputDirectory)
        Directory.CreateDirectory(output) |> ignore
        let libraryPath = Path.Combine(output, "atomic-library.json")
        if File.Exists(libraryPath) then invalidOp "Use a new output directory for semantic decimation."
        let media = DesktopMedia.probe ffmpeg videoPath
        if media.Duration <= 0. || media.Duration > 604800. then invalidOp "A finite recording duration of at most seven days is required."
        let source: Source = { Path = videoPath; Sha256 = FileHash.sha256 videoPath }
        let transcript = Path.Combine(output, "transcript.srt")
        let reused = not (String.IsNullOrWhiteSpace(transcriptPath))
        if reused then
            if not (File.Exists(transcriptPath)) then invalidArg "transcriptPath" "Attached transcript is missing."
            let extension = Path.GetExtension(transcriptPath).ToLowerInvariant()
            if extension <> ".srt" && extension <> ".vtt" then invalidOp "Full decimation requires a timestamped SRT or VTT transcript; plain text cannot be aligned honestly."
            File.Copy(transcriptPath, transcript, false)
        elif media.AudioCodec <> "" then DesktopMedia.transcribe ffmpeg whisper model videoPath transcript
        else File.WriteAllText(transcript, "")
        let cues = readTranscript transcript media.Duration
        if media.AudioCodec <> "" && cues.Length = 0 then invalidOp "No timestamped speech was found. Check the transcript before running full decimation."
        let transcriptHash = FileHash.sha256 transcript
        let frameDirectory = Directory.CreateDirectory(Path.Combine(output, "frames")).FullName
        let frames = DesktopMedia.scenes ffmpeg videoPath 0.12 10000 frameDirectory
        if frames.Length = 10000 then invalidOp "Visual extraction reached its safety limit; full duration coverage cannot be asserted."
        let cards = ResizeArray<Card>()
        let mutable ocrFrames = 0
        let mutable ocrStatus = "Not attempted."
        let artifacts = ResizeArray<string * string * Nullable<float> * Nullable<float>>()
        let addArtifact kind path start duration = artifacts.Add(kind, path, start, duration)
        let addCard modality kind title start finish evidence attributes cardTags method limitations =
            let id = sprintf "atom-%s-%06d" modality (cards.Count + 1)
            let provenance = { Method = method; Status = "evidence-linked"; Limitations = limitations }
            cards.Add({ Id = id; Modality = modality; Kind = kind; Title = title; StartSeconds = start; EndSeconds = finish
                        Source = source; Evidence = evidence; Attributes = json attributes; Tags = cardTags; Provenance = provenance
                        Flipcard = { Front = json {| title = title; modality = modality; tags = cardTags; startSeconds = start |}
                                     Back = json {| schema = "urn:yumlog:atomic-flipcard:v1"; evidence = evidence; attributes = attributes; provenance = provenance |} } })
        for index in 0 .. frames.Length - 1 do
            let path, start = frames[index]
            let finish = if index + 1 < frames.Length then snd frames[index + 1] else media.Duration
            let width, height, colors = palette path
            let ocrText, ocrMessage = classicOcr path
            ocrStatus <- ocrMessage
            if ocrText <> "" then ocrFrames <- ocrFrames + 1
            let speechContext = cues |> Array.filter (fun cue -> cue.Start >= start && cue.Start < min finish (start + 20.)) |> Array.map _.Text |> String.concat " "
            let context = ocrText + " " + speechContext
            let attributes =
                {| ocrText = {| status = (if ocrText = "" then "unavailable-or-empty" else "inferred"); value = ocrText; method = ocrMessage |}
                   width = measured width; height = measured height; colors = measured colors
                   tags = {| status = "inferred"; value = tags context; method = "OCR and nearby transcript lexical frequency, not object recognition" |}
                   agentAvatars = unknown "Face/avatar identity recognition is not configured."
                   dataflowDiagrams = unknown "Diagram structure recognition is not configured."
                   slides = unknown "This is a retained visual change, not a confirmed slide boundary."
                   icons = unknown "Icon recognition is not configured."
                   font = unknown "Exact typeface cannot be determined from these pixels by this engine." |}
            let evidence = { Path = path; Sha256 = FileHash.sha256 path; StartSeconds = start; EndSeconds = min media.Duration (start + 0.5) }
            addCard "video" "sceneFrame" ("Visual change · " + stamp start) start finish evidence attributes (tags context) "FFmpeg 2 fps scene detector (0.12); 64x36 quantized palette; best-effort classic Windows OCR" [| "Sub-half-second changes and subtle transitions can be missed."; "Display interval is an index span, not evidence that the image stayed unchanged." |]
            addArtifact "frame" path (Nullable start) (Nullable())
        let groups = cues |> Array.groupBy (fun cue -> int (cue.Start / 30.)) |> Array.sortBy fst
        for _, entries in groups do
            let start = entries[0].Start
            let finish = entries |> Array.map _.End |> Array.max
            let text = entries |> Array.map _.Text |> String.concat " "
            let words = Regex.Matches(text, @"\b[\p{L}\p{N}]+\b").Count
            let speechTags = tags text
            let attributes =
                {| text = measured text; cueCount = measured entries.Length; wordCount = measured words
                   wordsPerMinute = measured (float words * 60. / max 0.001 (finish - start))
                   voice = unknown "No speaker diarization or voice identification was performed."
                   tonality = unknown "No acoustic emotion classifier was run."
                   confidence = unknown "Whisper SRT does not contain calibrated confidence."
                   quality = unknown "No acoustic signal-quality assessment was run."
                   perspective = unknown "Speaker identity and viewpoint require review."
                   agentName = unknown "No speaker-to-agent identity mapping supplied."
                   intent = {| status = "inferred"; value = (if text.Contains('?') then "question-containing speech" else "statement-containing speech"); method = "Punctuation heuristic; not psychological intent" |} |}
            let title = if speechTags.Length = 0 then "Speech · " + stamp start else String.concat " · " (speechTags |> Array.truncate 3)
            addCard "audio" "speech-segment" title start finish { Path = transcript; Sha256 = transcriptHash; StartSeconds = start; EndSeconds = finish } attributes speechTags
                (if reused then "Attached timestamped transcript; lexical grouping into 30-second bins" else "Local Whisper; lexical grouping into 30-second bins")
                [| "Automatic text may misrecognize names and product terms."; "Attached transcripts are caller-supplied; identity with source speech is not automatically verified."; "Words per minute includes pauses; not an acoustic measure." |]
        let attributes =
            {| font = unknown "SRT/VTT carries no reliable source document font."; header = measured (Path.GetFileName(videoPath))
               schema = measured "urn:yumlog:timestamped-transcript:v1"
               fields = measured [| "startSeconds"; "endSeconds"; "text" |]
               format = measured (if reused then Path.GetExtension(transcriptPath).TrimStart('.') else "srt")
               entityName = measured "TranscriptCue"; friendlyName = measured "Timestamped speech cue"
               customerFields = unknown "No customer-specific document fields were supplied."
               cueCount = measured cues.Length |}
        addCard "document" "transcript-schema" "Transcript cue schema" 0. media.Duration { Path = transcript; Sha256 = transcriptHash; StartSeconds = 0.; EndSeconds = media.Duration }
            attributes [| "transcript"; "schema"; "fields" |] "Schema distilled from actual parsed subtitle cues" [| "This describes the transcript document, not an unseen source document." |]
        let metadataFile = Path.Combine(output, "source-media.json")
        NativeJson.write metadataFile media
        addCard "document" "media-schema" "Recording metadata schema" 0. media.Duration
            { Path = metadataFile; Sha256 = FileHash.sha256 metadataFile; StartSeconds = 0.; EndSeconds = media.Duration }
            {| schema = measured "urn:yumlog:media-info:v1"; fields = measured [| "duration"; "width"; "height"; "videoCodec"; "audioCodec"; "subtitleStreams"; "bytes" |]
               format = measured "json"; entityName = measured "MediaInfo"; friendlyName = measured "Recording metadata"
               font = unknown "Not applicable to JSON data."; header = measured "Recording metadata"; customerFields = unknown "No custom fields supplied." |}
            [| "media"; "schema" |] "FFprobe media properties" [||]
        let documentDirectory = Path.Combine(output, "documents")
        let mutable documentCount = 0
        if Directory.Exists(documentDirectory) then
            for path in Directory.GetFiles(documentDirectory) |> Array.sort do
                let ext = Path.GetExtension(path).ToLowerInvariant()
                if [| ".json"; ".csv"; ".txt"; ".md" |] |> Array.contains ext then
                    if FileInfo(path).Length > 8_000_000L then invalidOp "Attached text documents must be at most 8 MB."
                    let text = File.ReadAllText(path)
                    let rows = text.Replace("\r\n", "\n").Split('\n')
                    let headings = rows |> Array.filter (fun row -> row.TrimStart().StartsWith("#")) |> Array.map (fun row -> row.Trim().TrimStart('#').Trim()) |> Array.truncate 50
                    let fields, fieldTypes =
                        if ext = ".json" then
                            use document = JsonDocument.Parse(text)
                            let sample =
                                if document.RootElement.ValueKind = JsonValueKind.Array && document.RootElement.GetArrayLength() > 0 then document.RootElement[0]
                                else document.RootElement
                            if sample.ValueKind = JsonValueKind.Object then
                                let properties = sample.EnumerateObject() |> Seq.toArray
                                properties |> Array.map _.Name, properties |> Array.map (fun prop -> {| name = prop.Name; jsonKind = string prop.Value.ValueKind |})
                            else [||], [||]
                        elif ext = ".csv" && rows.Length > 0 then
                            let header = Regex.Matches(rows[0], "(?:^|,)(\"(?:[^\"]|\"\")*\"|[^,]*)") |> Seq.map (fun item -> item.Groups[1].Value.Trim().Trim('\"').Replace("\"\"", "\"")) |> Seq.toArray
                            header, header |> Array.map (fun name -> {| name = name; jsonKind = "string (CSV header only)" |})
                        else [| "text"; "headings" |], [| {| name = "text"; jsonKind = "string" |}; {| name = "headings"; jsonKind = "array" |} |]
                    let attributes =
                        {| font = unknown "Plain-text files do not specify reliable font/rendering."
                           header = measured (if headings.Length > 0 then headings[0] else rows |> Array.tryFind (String.IsNullOrWhiteSpace >> not) |> Option.defaultValue "")
                           schema = measured {| fields = fieldTypes; inferenceScope = "First JSON object or CSV header; not validation of every row." |}
                           fields = measured fields; format = measured (ext.TrimStart('.')); entityName = measured (Path.GetFileNameWithoutExtension(path))
                           friendlyName = measured (Path.GetFileName(path)); customerFields = measured fields; headings = measured headings |}
                    let id = "document-" + string documentCount
                    addCard "document" "attached-document-schema" (Path.GetFileName(path)) 0. media.Duration
                        { Path = path; Sha256 = FileHash.sha256 path; StartSeconds = 0.; EndSeconds = media.Duration }
                        attributes (tags (text.Substring(0, min 50000 text.Length))) "Structure distilled from staged attached document bytes"
                        [| "Document attached to recording; no claim its contents appeared on screen."; "Schema describes sampled structure and must be reviewed before reuse." |]
                    addArtifact "document" path (Nullable()) (Nullable())
                    documentCount <- documentCount + 1
        let schemaPath = Path.Combine(output, "atomic-flipcard.schema.json")
        File.WriteAllText(schemaPath, schema)
        let library =
            {| schema = "urn:yumlog:atomic-library:v1"; version = "1.0"; source = source; durationSeconds = media.Duration
               generatedUtc = DateTimeOffset.UtcNow; cards = cards.ToArray(); cardSchema = "atomic-flipcard.schema.json" |}
        NativeJson.write libraryPath library
        let binCount = int (Math.Ceiling(media.Duration))
        let bins = Array.zeroCreate<int> binCount
        let mark mask (start: float) (finish: float) =
            for bin in max 0 (int start) .. min (binCount - 1) (max (int start) (int (Math.Ceiling(finish)) - 1)) do bins[bin] <- bins[bin] ||| mask
        for _, at in frames do mark 1 at (at + 0.5)
        for cue in cues do mark 2 cue.Start cue.End
        for card in cards do
            if card.Modality = "audio" && card.Tags.Length > 0 then mark 4 card.StartSeconds card.EndSeconds
        mark 8 0. (min 1. media.Duration)
        let runs = ResizeArray<_>()
        let mutable first = 0
        while first < bins.Length do
            let mutable last = first + 1
            while last < bins.Length && bins[last] = bins[first] do last <- last + 1
            runs.Add({| startBin = first; length = last - first; mask = bins[first] |})
            first <- last
        let layersPath = Path.Combine(output, "bitdot-layers.json")
        NativeJson.write layersPath
            {| schema = "urn:yumlog:bitdot-layers:v1"; version = "1.0"; definition = "Yumlog local sparse semantic layer index; one-second occupancy bitmask, run-length encoded. No external bitdot format is implied."
               source = source; durationSeconds = media.Duration; binSeconds = 1; binCount = binCount
               layers = [| {| bit = 1; name = "visual-change" |}; {| bit = 2; name = "speech" |}; {| bit = 4; name = "lexical-tags" |}; {| bit = 8; name = "document-schema" |} |]
               runs = runs.ToArray(); references = cards |> Seq.map (fun card -> {| id = card.Id; modality = card.Modality; startSeconds = card.StartSeconds; endSeconds = card.EndSeconds |}) |> Seq.toArray |}
        let mdkgPath = Path.Combine(output, "semantic.mdkg")
        let p: TextPoint = { X = 0.; Y = 0. }
        let bounds: TextBounds = { TopLeft = p; TopRight = p; BottomRight = p; BottomLeft = p }
        let tick seconds = int64 (Math.Round(seconds * float MovieTimescale))
        // Expiry is an event too: otherwise reconstruction keeps ended cards alive
        // through gaps until the next start. The source timeline is [0, duration).
        let boundaries =
            cards
            |> Seq.collect (fun card -> [ card.StartSeconds; card.EndSeconds ])
            |> Seq.filter (fun at -> at < media.Duration)
            |> Seq.distinct
            |> Seq.sort
            |> Seq.toArray
        use writer = MdkgWriter.Open(mdkgPath)
        writer.AddDefaultLayers()
        for index in 0 .. boundaries.Length - 1 do
            let at = boundaries[index]
            let finish = if index + 1 < boundaries.Length then boundaries[index + 1] else media.Duration
            let nodes: SemanticGraphNode array =
                cards |> Seq.filter (fun card -> card.StartSeconds <= at && card.EndSeconds > at)
                |> Seq.map (fun card ->
                    { Id = card.Id; Kind = card.Kind; Label = card.Title; Confidence = 1.; HasBounds = false; Bounds = bounds
                      FirstPtsTicks = tick card.StartSeconds; LastPtsTicks = tick card.EndSeconds
                      Attributes = [| { Key = "modality"; Value = card.Modality }; { Key = "evidencePath"; Value = card.Evidence.Path }
                                      { Key = "evidenceSha256"; Value = card.Evidence.Sha256 }; { Key = "attributes"; Value = card.Attributes.GetRawText() }
                                      { Key = "confidenceMeaning"; Value = "Structural inclusion weight, not model confidence" } |] }) |> Seq.toArray
            writer.WriteSemanticGraph({ Qpc = 0L; FrameIndex = index; PtsTicks = tick at; DurationTicks = tick finish - tick at
                                        GraphId = "yumlog-atomic-library"; IsKeyFrame = true; KeyFrameNodes = nodes; KeyFrameEdges = [||]
                                        Delta = Producers.emptySemanticGraphDelta() })
        writer.Close()
        let reportPath = Path.Combine(output, "report.json")
        use reader = openReader mdkgPath
        let runbook = MdkgIntel.buildRunbook reader
        let verification = MdkgIntel.verifyRunbook reader runbook
        if not verification.Verified then invalidOp "Generated MDKG structural references failed verification."
        let runbookPath = Path.Combine(output, "semantic-runbook.json")
        NativeJson.write runbookPath runbook
        if FileHash.sha256 videoPath <> source.Sha256 then invalidOp "The source recording changed during processing."
        NativeJson.write reportPath
            {| schema = "urn:yumlog:semantic-report:v1"; complete = true; source = source; durationSeconds = media.Duration
               scope = "Entire source decoded for scene changes; all timestamped speech indexed. Sparse indexing does not guarantee recognition of every semantic object."
               transcriptEndpointPolicy = "Source bytes retained. Speech endpoints up to 250 ms beyond media duration are clamped in indexed cues; larger drift or starts beyond media are rejected."
               transcriptReused = reused; transcriptCues = cues.Length; visualFrames = frames.Length; audioSegments = groups.Length
               attachedDocuments = documentCount; classicOcrFrames = ocrFrames; classicOcrStatus = ocrStatus
               cardCount = cards.Count; layerRuns = runs.Count; indexedSeconds = binCount; mdkgProofs = verification.TotalProofs
               sourceUnchanged = true
               limitations = [| "No vision-language model, diarization or acoustic emotion model was used; classic Windows OCR is best-effort."
                                "Palette and media properties are measured; tags and question intent are lexical heuristics."
                                "Font, avatar identity, diagrams, icons, tone, confidence, quality and perspective remain unknown."
                                "Document schemas describe transcript/media data and explicitly attached documents; they are not inferred from unseen pixels."
                                "Attached transcript alignment is caller-supplied; hashes record provenance, not truth." |] |}
        addArtifact "transcript" transcript (Nullable()) (Nullable())
        addArtifact "atomicLibrary" libraryPath (Nullable()) (Nullable())
        addArtifact "bitdot" layersPath (Nullable()) (Nullable())
        addArtifact "semanticReport" reportPath (Nullable()) (Nullable())
        addArtifact "timedMetadata" mdkgPath (Nullable()) (Nullable())
        addArtifact "runbook" runbookPath (Nullable()) (Nullable())
        addArtifact "schema" schemaPath (Nullable()) (Nullable())
        addArtifact "semanticReport" metadataFile (Nullable()) (Nullable())
        { Artifacts = artifacts.ToArray(); LibraryPath = libraryPath; ReportPath = reportPath; CardCount = cards.Count }
