namespace Yumlog.Native

open System
open System.Collections.Concurrent
open System.Globalization
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Paperboy.Desktop.Services
open Yumlog.Native.Mdkg

module DesktopLibrary =
    type Settings =
        { OutputDirectory: string
          CardSize: string
          FfmpegPath: string
          WhisperPath: string
          WhisperModel: string }

    type Artifact =
        { Id: string
          Kind: string
          Path: string
          Start: Nullable<float>
          Duration: Nullable<float> }

    type Video =
        { Id: string
          Path: string
          AddedAt: DateTimeOffset
          Media: DesktopMedia.MediaInfo option
          Poster: string
          Artifacts: Artifact array }

    type Library =
        { Settings: Settings
          SelectedId: string
          Videos: Video array
          Captures: Artifact array }

    type ToolTab =
        { Key: string
          Label: string
          Role: string
          Inputs: string array
          Outputs: string array
          Actions: string array }

    let tabs =
        [| { Key = "video"; Label = "Video"; Role = "recording-and-clips"
             Inputs = [| "recording"; "startSeconds"; "endSeconds"; "captureDuration" |]
             Outputs = [| "video"; "clip" |]; Actions = [| "import"; "record"; "clip"; "openSource" |] }
           { Key = "screenshot"; Label = "Screenshot"; Role = "screen-and-scene-capture"
             Inputs = [| "recording"; "timestamp"; "sceneThreshold"; "frameLimit" |]
             Outputs = [| "frame"; "screenshot" |]; Actions = [| "capture"; "frame"; "scenes" |] }
           { Key = "audio"; Label = "Audio"; Role = "audio-extraction"
             Inputs = [| "audioTrack" |]; Outputs = [| "audio"; "spectrogram" |]; Actions = [| "wave"; "spectrogram" |] }
           { Key = "transcript"; Label = "Transcript"; Role = "caption-and-speech-transcription"
             Inputs = [| "captionFile"; "subtitleTrack"; "audioTrack"; "whisperModel" |]; Outputs = [| "transcript" |]
             Actions = [| "attachTranscript"; "subtitles"; "transcribe" |] }
           { Key = "agents"; Label = "Agents"; Role = "native-capability-catalog"
             Inputs = [| "extensionManifests"; "windowsAiRuntime" |]; Outputs = [| "catalog"; "ocrReadiness" |]
             Actions = [| "catalog"; "ocrStatus"; "loadOcr" |] }
           { Key = "voices"; Label = "Voices"; Role = "local-speech-synthesis"
             Inputs = [| "installedVoice"; "script" |]; Outputs = [| "voice" |]; Actions = [| "voices"; "speak" |] }
           { Key = "insights"; Label = "Insights"; Role = "recording-and-frame-analysis"
             Inputs = [| "recording"; "frame"; "timedMetadata" |]; Outputs = [| "mediaInfo"; "ocr"; "runbook" |]
             Actions = [| "probe"; "ocr"; "runbook" |] }
           { Key = "harvest"; Label = "Harvest"; Role = "paperboy-delivery"
             Inputs = [| "recording"; "derivedArtifacts"; "paperboyCard" |]; Outputs = [| "cardJson"; "paperboyBundle" |]
             Actions = [| "exportCard"; "copyCard"; "copySchema"; "bundle"; "openArtifact"; "exportArtifact" |] }
           { Key = "settings"; Label = "Settings"; Role = "local-workspace-configuration"
             Inputs = [| "outputDirectory"; "cardSize"; "ffmpeg"; "whisper" |]; Outputs = [| "workspaceSettings" |]
             Actions = [| "saveSettings"; "setSize"; "openOutput" |] } |]

    let cardSizes = [| "compact"; "studio"; "wide" |]
    let cardSchema = "https://darbotlabs.github.io/yumlog/paperboy-video-card.schema.json"

    let private key (path: string) =
        path.ToUpperInvariant() |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexStringLower

    let private artifact kind path start duration : Artifact =
        { Id = key path; Kind = kind; Path = path; Start = start; Duration = duration }

    let private plain kind path = artifact kind path (Nullable()) (Nullable())

    let private fileName (path: string) = Path.GetFileName(path) |> nonNull
    let private title (path: string) = Path.GetFileNameWithoutExtension(path) |> nonNull

    let private descriptor (item: Artifact) =
        {| id = item.Id; kind = item.Kind; name = fileName item.Path; start = item.Start; duration = item.Duration
           exists = File.Exists(item.Path) |}

    let private metadataPath (video: Video) =
        [ yield! video.Artifacts |> Array.filter (fun a -> a.Kind = "timedMetadata") |> Array.map (fun a -> a.Path)
          yield Path.ChangeExtension(video.Path, ".mdkg") |> nonNull ]
        |> List.tryFind File.Exists

    let private artifacts (video: Video) =
        match metadataPath video with
        | Some path when not (video.Artifacts |> Array.exists (fun item -> item.Id = key path)) ->
            Array.append video.Artifacts [| plain "timedMetadata" path |]
        | _ -> video.Artifacts

    let private manifest (size: string) (video: Video) =
        {| schema = cardSchema
           version = "1.0"
           id = "paperboy.video." + video.Id
           title = title video.Path
           role = "video-workspace"
           description = "Yumlog recording and derived media"
           inputs = [| "video"; "captureSettings"; "optionalSidecars" |]
           outputs = tabs |> Array.collect (fun tab -> tab.Outputs) |> Array.distinct
           actions = tabs |> Array.collect (fun tab -> tab.Actions) |> Array.distinct
           liveTileState = if File.Exists(video.Path) then "ready" else "missing"
           asset = {| name = fileName video.Path; addedAt = video.AddedAt; media = video.Media |}
           artifacts = artifacts video |> Array.map descriptor
           flipcard =
               {| size = size
                  sizes = cardSizes
                  front = {| title = title video.Path; defaultTab = "video" |}
                  back = {| title = "Paperboy"; schema = cardSchema |}
                  tabs = tabs |} |}

    let private writeAtomic (path: string) value =
        let staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
        try
            NativeJson.write staging value
            File.Move(staging, path, true)
        finally
            File.Delete(staging)

    let field (name: string) (payload: JsonElement) =
        if payload.ValueKind = JsonValueKind.Object then
            match payload.TryGetProperty(name) with
            | true, value when value.ValueKind = JsonValueKind.String -> value.GetString() |> orEmptyIfNull
            | _ -> ""
        else ""

    let private bounded name min max payload =
        match Double.TryParse(field name payload, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, value when Double.IsFinite(value) && value >= min && value <= max -> value
        | _ -> invalidArg name $"Enter {name} between {DesktopMedia.number min} and {DesktopMedia.number max}."

    let private readPreview (path: string) =
        use reader = new StreamReader(path)
        let buffer = Array.zeroCreate<char> 160000
        let read = reader.ReadBlock(buffer)
        let suffix = if reader.EndOfStream then "" else "\n\n[Preview truncated. Open the file to read the rest.]"
        String(buffer, 0, read) + suffix

    let private ocrStatus () =
        match WinAppRuntime.tryInitialize() with
        | Error hr -> $"Windows App Runtime bootstrap failed: {WinAppRuntime.hresultText hr}"
        | Ok () ->
            let state = WindowsAiOcr.getReadyState()
            $"Windows AI OCR: {WindowsAiOcr.readyStateName state}."

    type Workspace(root: string) =
        let settings =
            { OutputDirectory = Path.Combine(root, "Exports"); CardSize = "studio"
              FfmpegPath = ""; WhisperPath = ""; WhisperModel = "" }
        let statePath = Path.Combine(root, "studio.json")
        let defaults = { Settings = settings; SelectedId = ""; Videos = [||]; Captures = [||] }
        let mutable state = if File.Exists(statePath) then NativeJson.readOver defaults statePath else defaults
        let mediaPaths = ConcurrentDictionary<string, string>(StringComparer.Ordinal)
        let config () = NativeConfig.load NativeConfig.defaultConfigPath
        let recordConfig () =
            let original = (config ()).Record
            if String.IsNullOrWhiteSpace(state.Settings.FfmpegPath) then original
            else { original with FFmpegPath = state.Settings.FfmpegPath }
        let ffmpeg () = NativeRecording.resolveFfmpeg (recordConfig ())

        let save updated =
            writeAtomic statePath updated
            state <- updated

        let selected () = state.Videos |> Array.tryFind (fun video -> video.Id = state.SelectedId)
        let current () = selected () |> Option.defaultWith (fun () -> invalidOp "Open a recording first.")
        let source () =
            let video = current ()
            if not (File.Exists(video.Path)) then invalidOp "The source file was moved or is unavailable."
            video

        let update video =
            save { state with Videos = state.Videos |> Array.map (fun original -> if original.Id = video.Id then video else original) }

        let newDirectory root =
            let name = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..7]
            Directory.CreateDirectory(Path.Combine(root, name)).FullName

        let job produce =
            let directory = newDirectory state.Settings.OutputDirectory
            try produce directory
            with _ ->
                try Directory.Delete(directory, true) with _ -> ()
                reraise ()

        let attach video files =
            update { video with Artifacts = Array.append files video.Artifacts }

        let createArtifact video kind name create =
            job (fun directory ->
                let file = Path.Combine(directory, name)
                create file
                if not (File.Exists(file)) then invalidOp "The tool did not produce an output file."
                attach video [| plain kind file |])

        let makeVideo path =
            if not (File.Exists(path)) || not (DesktopMedia.videoExtensions.Contains(DesktopMedia.extension path)) then
                invalidOp "Choose a supported local video file."
            let id = key path
            let info =
                try Some(DesktopMedia.probe (ffmpeg ()) path)
                with
                | :? ComponentModel.Win32Exception -> None
                | :? UnauthorizedAccessException -> None
                | :? InvalidOperationException as error when not (error.Message.EndsWith("does not contain a video track.", StringComparison.Ordinal)) -> None
            let poster =
                let directory = Directory.CreateDirectory(Path.Combine(root, "Posters")).FullName
                let destination = Path.Combine(directory, id + ".jpg")
                if not (File.Exists(destination)) then
                    let temporary = Path.Combine(directory, id + ".new.jpg")
                    try
                        File.Delete(temporary)
                        DesktopMedia.poster (ffmpeg ()) path temporary
                        if File.Exists(temporary) then File.Move(temporary, destination)
                    with _ -> File.Delete(temporary)
                if File.Exists(destination) then destination else ""
            { Id = id; Path = path; AddedAt = DateTimeOffset.UtcNow; Media = info; Poster = poster; Artifacts = [||] }

        let import (paths: string array) =
            let ids = paths |> Array.map key
            let previous = state.Videos |> Array.map (fun video -> video.Id) |> Set.ofArray
            let additions =
                paths |> Array.distinctBy key |> Array.filter (fun path -> not (previous.Contains(key path))) |> Array.map makeVideo
            if ids.Length > 0 then
                save { state with Videos = Array.append additions state.Videos; SelectedId = ids[0] }
            match additions.Length with
            | 0 -> "Recording already in the library."
            | 1 -> "Recording added."
            | count -> $"Added {count} recordings."

        let findArtifact id =
            let files = selected () |> Option.map artifacts |> Option.defaultValue [||]
            Array.append files state.Captures
            |> Array.tryFind (fun item -> item.Id = id && File.Exists(item.Path))
            |> Option.defaultWith (fun () -> invalidOp "The selected output is unavailable.")

        let register (id: string) (path: string) =
            if File.Exists(path) then mediaPaths[id] <- path

        member _.TryGetMedia(id: string) =
            match mediaPaths.TryGetValue(id) with
            | true, path -> Some path
            | _ -> None

        member _.Snapshot() =
            let active = selected ()
            let rows =
                state.Videos |> Array.map (fun video ->
                    register video.Id video.Path
                    register (video.Id + "-poster") video.Poster
                    {| id = video.Id; title = title video.Path; name = fileName video.Path; exists = File.Exists(video.Path)
                       addedAt = video.AddedAt; media = video.Media; hasPoster = File.Exists(video.Poster) |})
            let outputs =
                match active with
                | Some video -> artifacts video
                | None -> state.Captures
            for item in outputs do
                register item.Id item.Path
            {| videos = rows
               selectedId = state.SelectedId
               settings = state.Settings
               tabs = tabs
               artifacts = Array.map descriptor outputs
               hasTimedMetadata = active |> Option.bind metadataPath |> Option.isSome
               paperboy = active |> Option.map (manifest state.Settings.CardSize) |}

        member _.SuggestedName(command: string, id: string) =
            match command with
            | "exportCard" -> title (current ()).Path + ".paperboy-card.json"
            | "bundle" -> title (current ()).Path + ".paperboy.zip"
            | "exportArtifact" -> fileName (findArtifact id).Path
            | _ -> ""

        member _.Run(command: string, payload: JsonElement, paths: string array) =
            let get name = field name payload
            let result message = {| message = message; data = None |}
            let data message value = {| message = message; data = Some(box value) |}
            let path () = paths |> Array.exactlyOne
            let openFile (file: string) =
                Diagnostics.Process.Start(Diagnostics.ProcessStartInfo(file, UseShellExecute = true)) |> ignore
            let videoTime video name =
                let limit =
                    match video.Media with
                    | Some info when info.Duration > 0.0 -> info.Duration
                    | _ -> 604800.0
                bounded name 0.0 limit payload
            let writeText (file: string) (value: string) = File.WriteAllText(file, value)

            match command with
            | "snapshot" -> result ""
            | "select" ->
                let id = get "videoId"
                if not (state.Videos |> Array.exists (fun video -> video.Id = id)) then invalidOp "Recording not found."
                save { state with SelectedId = id }
                result ""
            | "import" -> result (import paths)
            | "openSource" -> openFile (source ()).Path; result ""
            | "openOutput" -> openFile (Directory.CreateDirectory(state.Settings.OutputDirectory).FullName); result ""
            | "openArtifact" -> openFile (findArtifact (get "artifactId")).Path; result ""
            | "exportArtifact" -> File.Copy((findArtifact (get "artifactId")).Path, path (), true); result "File exported."
            | "readArtifact" ->
                let item = findArtifact (get "artifactId")
                if not (Array.contains item.Kind [| "transcript"; "runbook"; "ocr"; "catalog" |]) then
                    invalidOp "This output cannot be displayed as text."
                data "" (readPreview item.Path)
            | "setSize" ->
                let size = get "cardSize"
                if not (Array.contains size cardSizes) then invalidArg "cardSize" "Unknown card size."
                save { state with Settings = { state.Settings with CardSize = size } }
                result ""
            | "saveSettings" ->
                let output = get "outputDirectory"
                if not (Path.IsPathFullyQualified(output)) then invalidArg "outputDirectory" "Enter an absolute output directory."
                let file name =
                    let value = (get name).Trim()
                    if value <> "" && (not (Path.IsPathFullyQualified(value)) || not (File.Exists(value))) then
                        invalidOp $"The configured file for {name} is unavailable."
                    value
                let updated =
                    { state.Settings with OutputDirectory = Path.GetFullPath(output); FfmpegPath = file "ffmpegPath"
                                          WhisperPath = file "whisperPath"; WhisperModel = file "whisperModel" }
                Directory.CreateDirectory(updated.OutputDirectory) |> ignore
                save { state with Settings = updated }
                result "Settings saved."
            | "record" ->
                let duration = bounded "duration" 1.0 600.0 payload |> int
                job (fun directory ->
                    let config = recordConfig ()
                    let isFfmpeg = config.Backend = "ffmpeg"
                    let format = if isFfmpeg then "mp4" else config.Container
                    let output = Path.Combine(directory, "Desktop-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "." + format)
                    let mdkg = Path.ChangeExtension(output, ".mdkg") |> nonNull
                    NativeRecording.record
                        { config with OutFile = output; Container = format; DurationSec = duration
                                      TimedMetadataOut = if isFfmpeg then "" else mdkg }
                    |> ignore
                    import [| output |] |> ignore)
                result "Desktop recording saved."
            | "capture" ->
                job (fun directory ->
                    let config = (config ()).Capture
                    let files =
                        NativeCapture.capture { config with OutDir = directory; Count = 1; DurationSec = 1; ImageFormat = "png" }
                        |> Array.map (fun frame -> plain "screenshot" frame.Path)
                    match selected () with
                    | Some video -> attach video files
                    | None -> save { state with Captures = Array.append files state.Captures })
                result "Desktop screenshot saved."
            | "clip" ->
                let video = source ()
                let start, finish = videoTime video "start", videoTime video "end"
                if finish - start < 0.05 then invalidOp "Clip end must be at least 0.05 seconds after the start."
                job (fun directory ->
                    let output = Path.Combine(directory, "clip.mp4")
                    DesktopMedia.clip (ffmpeg ()) video.Path start (finish - start) output
                    attach video [| artifact "clip" output (Nullable(start)) (Nullable(finish - start)) |])
                result "Clip exported."
            | "frame" ->
                let video = source ()
                let seconds = videoTime video "timestamp"
                job (fun directory ->
                    let output = Path.Combine(directory, "frame-" + DesktopMedia.number seconds + "s.png")
                    DesktopMedia.still (ffmpeg ()) video.Path seconds output
                    attach video [| artifact "frame" output (Nullable(seconds)) (Nullable()) |])
                result "Video frame exported."
            | "scenes" ->
                let video = source ()
                let threshold = bounded "threshold" 0.01 0.95 payload
                let count = bounded "limit" 1.0 300.0 payload |> int
                let frames =
                    job (fun directory ->
                        let frames = DesktopMedia.scenes (ffmpeg ()) video.Path threshold count directory
                        attach video (frames |> Array.map (fun (file, seconds) -> artifact "frame" file (Nullable(seconds)) (Nullable())))
                        frames.Length)
                let detail = if frames = count then " Frame limit reached." else ""
                result $"Exported {frames} scene frames.{detail}"
            | "wave" ->
                let video = source ()
                createArtifact video "audio" "audio.wav" (DesktopMedia.wave (ffmpeg ()) video.Path)
                result "WAV extracted."
            | "spectrogram" ->
                let video = source ()
                createArtifact video "spectrogram" "spectrogram.png" (DesktopMedia.spectrogram (ffmpeg ()) video.Path)
                result "Spectrogram generated."
            | "attachTranscript" ->
                let input = path ()
                let extension = DesktopMedia.extension input
                if not (DesktopMedia.transcriptExtensions.Contains(extension)) then invalidOp "Choose an SRT, WebVTT, or text file."
                createArtifact (current ()) "transcript" ("transcript" + extension) (fun output -> File.Copy(input, output))
                result "Transcript attached."
            | "subtitles" | "transcribe" ->
                let video = source ()
                let extract =
                    if command = "subtitles" then DesktopMedia.subtitles (ffmpeg ()) video.Path
                    else DesktopMedia.transcribe (ffmpeg ()) state.Settings.WhisperPath state.Settings.WhisperModel video.Path
                createArtifact video "transcript" "transcript.srt" extract
                result "Transcript saved."
            | "catalog" ->
                let catalog = NativeExtensibility.catalog (config ()).Extensions
                data "Native capabilities loaded." catalog.Capabilities
            | "ocrStatus" -> result (ocrStatus ())
            | "loadOcr" ->
                match WindowsAiOcr.ensureReady() with
                | Ok () -> result (ocrStatus ())
                | Error detail -> invalidOp detail
            | "voices" -> data "Installed voices loaded." (DesktopMedia.voices ())
            | "speak" ->
                let script = get "script"
                if String.IsNullOrWhiteSpace(script) || script.Length > 10000 then invalidOp "Enter between 1 and 10,000 characters."
                createArtifact (current ()) "voice" "narration.wav" (DesktopMedia.speak (get "voice") script)
                result "Narration rendered."
            | "probe" ->
                let video = source ()
                update { video with Media = Some(DesktopMedia.probe (ffmpeg ()) video.Path) }
                result "Media metadata refreshed."
            | "ocr" ->
                let video = current ()
                let frame = findArtifact (get "artifactId")
                if not (NativeAnalysis.isImage frame.Path) then invalidOp "Select a screenshot or video frame."
                let results = NativeAnalysis.analyzeWithoutProvisioning { (config ()).Analyze with Input = frame.Path }
                createArtifact video "ocr" "frame-analysis.json" (fun output -> NativeJson.write output results)
                result "Frame analysis saved."
            | "runbook" ->
                let video = current ()
                let metadata = metadataPath video |> Option.defaultWith (fun () -> invalidOp "A matching MDKG sidecar is required.")
                use reader = openReader metadata
                let runbook = MdkgIntel.buildRunbook reader
                createArtifact video "runbook" "runbook.md" (fun output -> writeText output (MdkgIntel.toMarkdown runbook))
                result "MDKG runbook exported."
            | "exportCard" ->
                let destination = path ()
                if DesktopMedia.extension destination <> ".json" then invalidOp "Use a .json file for the Paperboy card."
                writeAtomic destination (manifest state.Settings.CardSize (current ()))
                result "Paperboy card exported."
            | "bundle" ->
                let video = source ()
                let destination = path ()
                if DesktopMedia.extension destination <> ".zip" then invalidOp "Use a .zip file for the Paperboy bundle."
                let staging = destination + "." + Guid.NewGuid().ToString("N") + ".tmp"
                let scratch = newDirectory (Path.Combine(root, "Staging"))
                try
                    let cardPath = Path.Combine(scratch, "paperboy-video-card.json")
                    let card = manifest state.Settings.CardSize video
                    NativeJson.write cardPath card
                    let inputs =
                        [ yield video.Path
                          yield cardPath
                          yield! artifacts video |> Array.map (fun item -> item.Path) |> Array.filter File.Exists ]
                    let bundler = PaperboyBundleService()
                    bundler.CreateBundleAsync(inputs, staging, CompressionLevel.Fastest, true, false, null).GetAwaiter().GetResult() |> ignore
                    File.Move(staging, destination, true)
                    result "Paperboy bundle exported."
                finally
                    File.Delete(staging)
                    Directory.Delete(scratch, true)
            | _ -> invalidOp "Unknown Yumlog action."
