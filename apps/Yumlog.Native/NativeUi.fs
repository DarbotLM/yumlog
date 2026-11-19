namespace Yumlog.Native

open System
open System.Collections.Generic
open System.Diagnostics
open System.Drawing
open System.IO
open System.Linq
open System.Security.Cryptography
open System.Text.Json
open System.Threading.Tasks
open System.Windows.Forms

[<CLIMutable>]
type YumlogAgentCard =
    { Id: string
      Title: string
      Role: string
      Description: string
      Inputs: string array
      Outputs: string array
      Actions: string array
      LiveTileState: string }

module NativeUi =
    let private background = Color.FromArgb(4, 8, 12)
    let private surface = Color.FromArgb(10, 18, 24)
    let private raised = Color.FromArgb(16, 28, 35)
    let private border = Color.FromArgb(52, 75, 79)
    let private accent = Color.FromArgb(163, 228, 206)
    let private warm = Color.FromArgb(231, 197, 153)
    let private textColor = Color.FromArgb(237, 243, 247)
    let private muted = Color.FromArgb(157, 174, 184)
    let private danger = Color.FromArgb(239, 165, 165)

    let private baseDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Yumlog Native")

    let private dataDirectory name =
        let path = Path.Combine(baseDirectory, name)
        Directory.CreateDirectory(path) |> ignore
        path

    let private styleLabel (label: Label) size bold color =
        label.Font <- new Font("Cascadia Mono", size, if bold then FontStyle.Bold else FontStyle.Regular)
        label.ForeColor <- color
        label

    let private flatButton (caption: string) =
        let button = new Button()
        button.Text <- caption
        button.AutoSize <- true
        button.FlatStyle <- FlatStyle.Flat
        button.FlatAppearance.BorderColor <- border
        button.FlatAppearance.BorderSize <- 1
        button.BackColor <- raised
        button.ForeColor <- textColor
        button.Padding <- Padding(10, 5, 10, 5)
        button.Margin <- Padding(4, 4, 4, 4)
        button

    let private makeOutput () =
        let output = new RichTextBox()
        output.Dock <- DockStyle.Fill
        output.ReadOnly <- true
        output.BorderStyle <- BorderStyle.None
        output.BackColor <- Color.FromArgb(6, 11, 16)
        output.ForeColor <- muted
        output.Font <- new Font("Cascadia Mono", 9.5f)
        output.DetectUrls <- false
        output

    let private appendLine (output: RichTextBox) (message: string) =
        output.AppendText(message + Environment.NewLine)
        output.SelectionStart <- output.TextLength
        output.ScrollToCaret()

    let private invokeUi (control: Control) (action: unit -> unit) =
        if control.IsDisposed then
            ()
        elif control.InvokeRequired then
            control.BeginInvoke(Action(action)) |> ignore
        else
            action()

    let private jsonOptions =
        JsonSerializerOptions(PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true)

    let private loadAgentCards () =
        let fallback =
            [| { Id = "yumlog.video"; Title = "Video"; Role = "capture-and-record"; Description = "Record and inspect local video."; Inputs = Array.empty; Outputs = Array.empty; Actions = [| "record-video"; "open-video-folder" |]; LiveTileState = "ready|error" }
               { Id = "yumlog.screenshot"; Title = "Screenshot"; Role = "screen-evidence"; Description = "Capture local screen evidence."; Inputs = Array.empty; Outputs = Array.empty; Actions = [| "capture-screen" |]; LiveTileState = "ready|error" }
               { Id = "yumlog.settings"; Title = "Settings"; Role = "runtime-settings"; Description = "Review local runtime settings."; Inputs = Array.empty; Outputs = Array.empty; Actions = [| "check-ocr"; "open-config" |]; LiveTileState = "ready|error" } |]

        let path = Path.Combine(AppContext.BaseDirectory, "Assets", "yumlog_agent_cards.json")
        if File.Exists(path) then
            try
                match JsonSerializer.Deserialize<YumlogAgentCard array>(File.ReadAllText(path), jsonOptions) with
                | null -> fallback
                | cards when cards.Length = 0 -> fallback
                | cards -> cards
            with _ ->
                fallback
        else
            fallback

    let private latestFile pattern directory =
        if Directory.Exists(directory) then
            Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
            |> Seq.map FileInfo
            |> Seq.sortByDescending _.LastWriteTimeUtc
            |> Seq.tryHead
        else
            None

    let private sha256 path =
        use stream = File.OpenRead(path)
        use hash = SHA256.Create()
        Convert.ToHexString(hash.ComputeHash(stream)).ToLowerInvariant()

    let private resolveFfmpeg () =
        let configured = Environment.GetEnvironmentVariable("FFMPEG_PATH")

        let local =
            let componentLocal = Path.Combine(AppContext.BaseDirectory, ".tools", "ffmpeg", "bin", "ffmpeg.exe")
            let direct = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".tools", "ffmpeg", "bin", "ffmpeg.exe"))
            let rootDirect = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".tools", "ffmpeg", "bin", "ffmpeg.exe"))
            if File.Exists(componentLocal) then componentLocal
            elif File.Exists(direct) then direct
            elif File.Exists(rootDirect) then rootDirect
            else ""

        if not (String.IsNullOrWhiteSpace(configured)) && File.Exists(configured) then configured |> orEmptyIfNull
        elif not (String.IsNullOrWhiteSpace(local)) then local
        else "ffmpeg"

    let private runProcess executable (arguments: string seq) =
        let info = ProcessStartInfo()
        info.FileName <- executable
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.CreateNoWindow <- true
        for argument in arguments do
            info.ArgumentList.Add(argument)

        use child = new Process()
        child.StartInfo <- info
        if not (child.Start()) then
            failwith $"Could not start {executable}."

        let output = child.StandardOutput.ReadToEndAsync()
        let error = child.StandardError.ReadToEndAsync()
        child.WaitForExit()
        let standardOutput = output.GetAwaiter().GetResult().Trim()
        let standardError = error.GetAwaiter().GetResult().Trim()
        if child.ExitCode <> 0 then
            failwith $"{Path.GetFileName(executable)} exited {child.ExitCode}: {standardError}"

        if String.IsNullOrWhiteSpace(standardOutput) then standardError else standardOutput

    let private openPath path =
        if File.Exists(path) || Directory.Exists(path) then
            Process.Start(ProcessStartInfo(path, UseShellExecute = true)) |> ignore
        else
            failwith $"Path does not exist: {path}"

    let private cardState (card: YumlogAgentCard) state =
        { card with LiveTileState = state }

    let private saveVideoAgentCard (video: FileInfo) portableRoot =
        let mdkgPath = Path.ChangeExtension(video.FullName, ".mdkg") |> orEmptyIfNull
        let baseName = Path.GetFileNameWithoutExtension(video.Name) |> orEmptyIfNull
        let id =
            baseName.ToLowerInvariant()
            |> Seq.map (fun ch -> if Char.IsLetterOrDigit(ch) then ch else '-')
            |> Seq.toArray
            |> fun characters -> String(characters)
        let cardPath = Path.ChangeExtension(video.FullName, ".agent-card.json") |> orEmptyIfNull
        let serializePath (path: string) =
            match portableRoot with
            | Some root when not (String.IsNullOrWhiteSpace(path)) ->
                Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')
            | _ -> path
        let card =
            {| ``$schema`` = "https://darbotlabs.github.io/yumlog/paperboy.video-agent-card.schema.json"
               SchemaVersion = "1.0"
               Id = $"paperboy.video.{id}"
               Title = video.Name
               Role = "video-evidence"
               Description = "Adaptive Yumlog video evidence card with a front preview shell and inspectable metadata back."
               Inputs = [| "mp4"; "mdkg"; "durationSec"; "fps"; "monitor" |]
               Outputs = [| "videoCard"; "preview"; "citations"; "sha256"; "harvestManifest" |]
               Actions = [| "open"; "flip"; "buildTranscript"; "extractAudio"; "harvest"; "inspectMdkg" |]
               LiveTileState = "ready"
               Shell =
                   {| AspectRatio = "4:3"
                      BaseWidth = 720
                      BaseHeight = 540
                      Radius = 20
                      BorderColor = "#344b4f"
                      Accent = "#a3e4ce"
                      Background = "#080c10"
                      Flip = "rotateY(180deg)"
                      Front = [| "preview"; "title"; "status"; "duration"; "actions" |]
                      Back = [| "description"; "inputs"; "outputs"; "actions"; "provenance" |] |}
               AdaptiveSizes =
                   [| {| Name = "small"; Width = 220; Height = 165; CardsPerViewport = 8 |}
                      {| Name = "medium"; Width = 360; Height = 270; CardsPerViewport = 4 |}
                      {| Name = "large"; Width = 720; Height = 540; CardsPerViewport = 2 |}
                      {| Name = "presentation"; Width = 1080; Height = 810; CardsPerViewport = 1 |} |]
               Media =
                   {| Kind = "video"
                      Path = serializePath video.FullName
                      FileName = video.Name
                      Bytes = video.Length
                      LastWriteTimeUtc = video.LastWriteTimeUtc
                      Sha256 = sha256 video.FullName
                      MdkgPath = if File.Exists(mdkgPath) then serializePath mdkgPath else ""
                      MdkgSha256 = if File.Exists(mdkgPath) then sha256 mdkgPath else "" |}
               Provenance =
                   {| Producer = "Yumlog.Native"
                      Version = "0.5.0"
                      GeneratedUtc = DateTimeOffset.UtcNow
                      LocalOnly = true
                      Verification = "sha256 and MDKG proof verification on request" |} |}

        NativeJson.write cardPath card
        cardPath

    let private flipCard (front: Panel) (back: Panel) (button: Button) =
        let showingFront = front.Visible
        front.Visible <- not showingFront
        back.Visible <- showingFront
        button.Text <- if showingFront then "Front" else "Flip"

    let private makeVideoCard (video: FileInfo) width height =
        let card = new Panel()
        card.Width <- width
        card.Height <- height
        card.Margin <- Padding(8)
        card.BackColor <- surface
        card.BorderStyle <- BorderStyle.FixedSingle

        let top = new Panel()
        top.Dock <- DockStyle.Top
        top.Height <- 38
        top.BackColor <- raised
        let title = styleLabel (new Label()) 9.5f true accent
        title.Text <- video.Name
        title.AutoEllipsis <- true
        title.Dock <- DockStyle.Fill
        title.Padding <- Padding(10, 10, 52, 0)
        top.Controls.Add(title)

        let flip = flatButton "Flip"
        flip.Dock <- DockStyle.Right
        flip.Width <- 50
        flip.Margin <- Padding(4)
        top.Controls.Add(flip)

        let front = new Panel()
        front.Dock <- DockStyle.Fill
        front.BackColor <- Color.FromArgb(7, 13, 18)
        front.Padding <- Padding(14)
        let frontSummary =
            styleLabel
                (new Label())
                15.0f
                true
                textColor
        frontSummary.Text <- "YUMLOG VIDEO EVIDENCE"
        frontSummary.AutoSize <- true
        front.Controls.Add(frontSummary)
        let frontPath = styleLabel (new Label()) 9.0f false muted
        frontPath.Text <- $"{video.Length / 1024L:N0} KB • {video.LastWriteTimeUtc:u}"
        frontPath.Top <- 72
        frontPath.Left <- 14
        frontPath.Width <- max 160 (width - 40)
        frontPath.Height <- 44
        front.Controls.Add(frontPath)
        let openButton = flatButton "Open video"
        openButton.Left <- 14
        openButton.Top <- max 58 (height - 70)
        openButton.Click.Add(fun _ -> openPath video.FullName)
        front.Controls.Add(openButton)
        let cardButton = flatButton "Write agent card"
        cardButton.Left <- 122
        cardButton.Top <- openButton.Top
        cardButton.Visible <- width >= 300
        cardButton.Click.Add(fun _ ->
            let path = saveVideoAgentCard video None
            MessageBox.Show($"Agent card written:{Environment.NewLine}{path}", "Yumlog Agent Card", MessageBoxButtons.OK, MessageBoxIcon.Information) |> ignore)
        front.Controls.Add(cardButton)

        let back = new Panel()
        back.Dock <- DockStyle.Fill
        back.BackColor <- Color.FromArgb(5, 10, 14)
        back.Visible <- false
        back.Padding <- Padding(12)
        let backText = makeOutput()
        backText.ReadOnly <- true
        backText.Dock <- DockStyle.Fill
        backText.Text <-
            $"ROLE  video-evidence{Environment.NewLine}"
            + $"PATH  {video.FullName}{Environment.NewLine}"
            + $"SHA256  {sha256 video.FullName}{Environment.NewLine}"
            + $"INPUTS  mp4, mdkg, fps, duration, monitor{Environment.NewLine}"
            + $"OUTPUTS  videoCard, citations, sha256, harvestManifest{Environment.NewLine}"
            + $"ACTIONS  open, flip, transcript, audio, harvest, inspect MDKG{Environment.NewLine}"
            + $"SIZES  220x165 / 360x270 / 720x540 / 1080x810"
        back.Controls.Add(backText)

        flip.Click.Add(fun _ -> flipCard front back flip)
        card.Controls.AddRange([| front :> Control; back :> Control; top :> Control |])
        card

    let private refreshVideoCards (host: FlowLayoutPanel) =
        host.SuspendLayout()
        host.Controls.Clear()
        let directory = dataDirectory "Video"
        let width, height =
            match host.Tag with
            | :? string as value when value = "small" -> 220, 165
            | :? string as value when value = "large" -> 720, 540
            | :? string as value when value = "presentation" -> 1080, 810
            | _ -> 360, 270
        match
            Directory.EnumerateFiles(directory, "*.mp4", SearchOption.AllDirectories)
            |> Seq.sortByDescending (fun path -> File.GetLastWriteTimeUtc(path))
            |> Seq.toArray
        with
        | [||] ->
            let empty = styleLabel (new Label()) 10.0f false muted
            empty.Text <- "No local Yumlog videos yet. Record the desktop to create an adaptive 4:3 Agent Card."
            empty.AutoSize <- true
            host.Controls.Add(empty)
        | files ->
            files
            |> Array.iter (fun path ->
                let video = FileInfo(path)
                let cardPath = Path.ChangeExtension(video.FullName, ".agent-card.json") |> orEmptyIfNull
                if not (File.Exists(cardPath)) || File.GetLastWriteTimeUtc(cardPath) < video.LastWriteTimeUtc then
                    saveVideoAgentCard video None |> ignore
                host.Controls.Add(makeVideoCard video width height))
        host.ResumeLayout()

    let private runOnBackground (owner: Control) (button: Button) (output: RichTextBox) caption (work: unit -> string) =
        button.Enabled <- false
        appendLine output $"{caption}..."
        Task.Run(fun () ->
            let result =
                try
                    work()
                with ex ->
                    $"ERROR: {ex.GetType().Name}: {ex.Message}"

            invokeUi owner (fun () ->
                button.Enabled <- true
                appendLine output result))
        |> ignore

    let private recordVideo button output onSaved =
        runOnBackground output button output "Recording local screen video" (fun () ->
            let directory = dataDirectory "Video"
            let stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
            let config =
                { NativeConfig.defaults.Record with
                    OutFile = Path.Combine(directory, $"yumlog-{stamp}.mp4")
                    TimedMetadataOut = Path.Combine(directory, $"yumlog-{stamp}.mdkg")
                    Fps = 12
                    DurationSec = 5
                    Backend = "ffmpeg"
                    Encoder = "libx264"
                    Preset = "ultrafast"
                    Lossless = false }
            let result = NativeRecording.record config
            onSaved result.Path
            $"Saved {result.Path}{Environment.NewLine}MDKG {result.MetadataPath}")

    let private captureScreen button output =
        runOnBackground output button output "Capturing screenshot" (fun () ->
            let directory = dataDirectory "Screenshot"
            let frames =
                NativeCapture.capture
                    { NativeConfig.defaults.Capture with
                        OutDir = directory
                        Count = 1
                        DurationSec = 1
                        Fps = 1 }
            $"Captured {frames[0].Path}{Environment.NewLine}SHA256 {sha256 frames[0].Path}")

    let private analyzeLatestImage button output =
        runOnBackground output button output "Analyzing latest screenshot" (fun () ->
            match latestFile "*.png" (dataDirectory "Screenshot") with
            | None -> "No screenshot is available."
            | Some image ->
                let config = { NativeConfig.defaults.Analyze with Input = image.FullName; OcrMode = "none" }
                let result = NativeAnalysis.analyzeImage config image.FullName
                let name = Path.GetFileNameWithoutExtension(image.Name) |> orEmptyIfNull
                let path = Path.Combine(dataDirectory "Screenshot", name + ".analysis.json")
                NativeJson.write path result
                $"Analyzed {image.FullName}{Environment.NewLine}Wrote {path}")

    let private extractAudio button output =
        runOnBackground output button output "Extracting audio from latest video" (fun () ->
            match latestFile "*.mp4" (dataDirectory "Video") with
            | None -> "No local video is available."
            | Some video ->
                let name = Path.GetFileNameWithoutExtension(video.Name) |> orEmptyIfNull
                let outputPath = Path.Combine(dataDirectory "Audio", name + ".m4a")
                let arguments: string list = [ "-y"; "-i"; video.FullName; "-vn"; "-c:a"; "aac"; outputPath ]
                runProcess (resolveFfmpeg()) arguments |> ignore
                $"Wrote {outputPath}{Environment.NewLine}SHA256 {sha256 outputPath}")

    let private buildTranscript button output =
        runOnBackground output button output "Building screen transcript" (fun () ->
            match latestFile "*.mdkg" (dataDirectory "Video") with
            | None -> "No Yumlog MDKG recording is available. Speech transcription is intentionally not inferred from video."
            | Some mdkg ->
                use reader = Mdkg.openReader mdkg.FullName
                let runbook = MdkgIntel.buildRunbook reader
                let verification = MdkgIntel.verifyRunbook reader runbook
                if not verification.Verified then
                    failwith $"Runbook proof verification failed ({verification.ResolvedProofs}/{verification.TotalProofs})."
                let name = Path.GetFileNameWithoutExtension(mdkg.Name) |> orEmptyIfNull
                let directory = dataDirectory "Transcript"
                let markdownPath = Path.Combine(directory, name + ".md")
                let jsonPath = Path.Combine(directory, name + ".json")
                File.WriteAllText(markdownPath, MdkgIntel.toMarkdown runbook)
                NativeJson.write jsonPath runbook
                $"Verified {verification.ResolvedProofs}/{verification.TotalProofs} citations.{Environment.NewLine}{markdownPath}{Environment.NewLine}{jsonPath}")

    let private refreshAgents button output =
        runOnBackground output button output "Discovering agent cards and extensions" (fun () ->
            let catalog = NativeExtensibility.catalog NativeConfig.defaults.Extensions
            let report = NativeExtensibility.validate NativeConfig.defaults.Extensions
            let outputPath = Path.Combine(dataDirectory "Agents", "native-agent-catalog.json")
            NativeJson.write outputPath catalog
            $"Discovered {catalog.Manifests.Length} manifests and {catalog.Capabilities.Length} capabilities.{Environment.NewLine}Valid: {report.IsValid}{Environment.NewLine}{outputPath}")

    let private exportAgentCards button output =
        runOnBackground output button output "Exporting Yumlog Agent Cards" (fun () ->
            let outputPath = Path.Combine(dataDirectory "Agents", "yumlog.agent-cards.json")
            NativeJson.write outputPath (loadAgentCards())
            $"Wrote {outputPath}")

    let private exportMcp button output =
        runOnBackground output button output "Exporting MCP manifest" (fun () ->
            let catalog = NativeExtensibility.catalog NativeConfig.defaults.Extensions
            let outputPath = Path.Combine(dataDirectory "Agents", "yumlog.native.mcp.json")
            NativeJson.write outputPath (NativeExtensibility.mcpManifest catalog)
            $"Wrote {outputPath}")

    let private discoverVoices button output =
        runOnBackground output button output "Discovering voice and audio capabilities" (fun () ->
            let catalog = NativeExtensibility.catalog NativeConfig.defaults.Extensions
            let voice =
                catalog.Capabilities
                |> Array.filter (fun capability ->
                    capability.Kind.Contains("model", StringComparison.OrdinalIgnoreCase)
                    || capability.Tags.Any(fun tag -> tag.Contains("voice") || tag.Contains("audio")))
            if voice.Length = 0 then
                "No voice model is declared. Yumlog keeps speech generation/transcription unavailable until a capability manifest and local model are installed."
            else
                voice
                |> Array.map (fun capability -> $"{capability.DisplayName} [{capability.Status}] :: {capability.Provider}")
                |> String.concat Environment.NewLine)

    let private audioRuntimeStatus button output =
        runOnBackground output button output "Checking audio runtime" (fun () ->
            try
                let ffmpeg = runProcess (resolveFfmpeg()) [ "-version" ]
                let firstLine =
                    ffmpeg.Split([| Environment.NewLine |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.tryHead
                    |> Option.defaultValue ffmpeg
                "FFmpeg audio runtime available." + Environment.NewLine + firstLine
            with ex ->
                "FFmpeg audio runtime unavailable: " + ex.Message)

    let private buildRunbook button output =
        runOnBackground output button output "Building verified runbook" (fun () ->
            match latestFile "*.mdkg" (dataDirectory "Video") with
            | None -> "No MDKG recording is available."
            | Some mdkg ->
                use reader = Mdkg.openReader mdkg.FullName
                let runbook = MdkgIntel.buildRunbook reader
                let verification = MdkgIntel.verifyRunbook reader runbook
                let name = Path.GetFileNameWithoutExtension(mdkg.Name) |> orEmptyIfNull
                let path = Path.Combine(dataDirectory "Insights", name + ".runbook.json")
                NativeJson.write path runbook
                $"Proof verification: {verification.ResolvedProofs}/{verification.TotalProofs}; drift={verification.MismatchedProofs}{Environment.NewLine}{path}")

    let private localInsight button output =
        runOnBackground output button output "Running local insight" (fun () ->
            let models = OnDeviceLlm.discoverAll()
            if models.Length = 0 then
                "No on-device model is installed. Install a supported local model or use runbook evidence without generation."
            else
                match latestFile "*.mdkg" (dataDirectory "Video") with
                | None -> "No MDKG recording is available."
                | Some mdkg ->
                    use reader = Mdkg.openReader mdkg.FullName
                    let runbook = MdkgIntel.buildRunbook reader
                    let options =
                        { OnDeviceLlm.defaultOptions with
                            Input = mdkg.FullName
                            Question = "Summarize this recording. Cite only grounded Yumlog evidence and state any uncertainty."
                            Provider = "cpu"
                            JsonOut =
                                let name = Path.GetFileNameWithoutExtension(mdkg.Name) |> orEmptyIfNull
                                Path.Combine(dataDirectory "Insights", name + ".insight.json") }
                    let result = OnDeviceLlm.reasonOverRunbook options runbook ignore
                    NativeJson.write options.JsonOut result
                    result.Answer)

    let private selfTest button output =
        runOnBackground output button output "Running Yumlog self-test" (fun () ->
            let report = NativeSelfTest.run ""
            report.Cases
            |> Array.map (fun case ->
                let outcome = if case.Passed then "PASS" else "FAIL"
                $"{outcome} {case.Name} :: {case.Detail}")
            |> String.concat Environment.NewLine
            |> fun details -> $"{details}{Environment.NewLine}{report.PassedCount}/{report.Cases.Length} passed")

    let private harvestSession button output =
        runOnBackground output button output "Harvesting latest Yumlog evidence" (fun () ->
            let stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
            let sessionDirectory = Path.Combine(dataDirectory "Harvest", $"session-{stamp}")
            Directory.CreateDirectory(sessionDirectory) |> ignore
            let latestFor category pattern =
                latestFile pattern (dataDirectory category) |> Option.toList
            let latestSources =
                [ latestFor "Video" "*.mp4"
                  latestFor "Video" "*.mdkg"
                  latestFor "Screenshot" "*.png"
                  latestFor "Audio" "*.*"
                  latestFor "Transcript" "*.*"
                  latestFor "Insights" "*.json" ]
                |> List.concat
                |> List.map _.FullName
                |> List.distinct
            if latestSources.IsEmpty then
                failwith "No local evidence is available to harvest. Capture or generate evidence first."
            let files =
                latestSources
                |> List.map (fun (source: string) ->
                    let fileName = Path.GetFileName(source) |> orEmptyIfNull
                    let target = Path.Combine(sessionDirectory, fileName)
                    File.Copy(source, target, true)
                    target)
            files
            |> List.filter (fun path ->
                String.Equals(Path.GetExtension(path) |> orEmptyIfNull, ".mp4", StringComparison.OrdinalIgnoreCase))
            |> List.iter (fun path -> saveVideoAgentCard (FileInfo(path)) (Some sessionDirectory) |> ignore)
            let entries =
                files
                |> List.map (fun path ->
                    let info = FileInfo(path)
                    {| Path = info.Name; Bytes = info.Length; Sha256 = sha256 path; LastWriteTimeUtc = info.LastWriteTimeUtc |})
            let manifest =
                {| Schema = "urn:yumlog:harvest:v1"
                   SchemaVersion = "1.0"
                   SessionId = Guid.NewGuid().ToString()
                   CreatedUtc = DateTimeOffset.UtcNow
                   Files = entries |}
            let manifestPath = Path.Combine(sessionDirectory, "harvest.json")
            NativeJson.write manifestPath manifest
            $"Harvested {files.Length} file(s).{Environment.NewLine}{manifestPath}")

    let private checkOcr button output =
        runOnBackground output button output "Checking OCR readiness without provisioning" (fun () ->
            let readiness = NativeOcr.readiness "auto"
            $"{readiness.Code}: {readiness.Message}")

    let private loadOcr button output =
        runOnBackground output button output "Loading Windows AI OCR model" (fun () ->
            match WinAppRuntime.tryInitialize() with
            | Error hr -> "Windows App Runtime bootstrap failed: " + WinAppRuntime.hresultText hr
            | Ok () ->
                match WindowsAiOcr.ensureReady() with
                | Ok () -> "OCR model is ready."
                | Error error -> "OCR model load failed: " + error)

    let private actionFor card (action: string) button output (refreshVideos: unit -> unit) =
        match action with
        | "record-video" -> recordVideo button output (fun path -> invokeUi output refreshVideos)
        | "open-video-folder" -> openPath (dataDirectory "Video"); appendLine output "Opened Video folder."
        | "latest-video" ->
            match latestFile "*.mp4" (dataDirectory "Video") with
            | Some file -> openPath file.FullName
            | None -> appendLine output "No video is available."
        | "capture-screen" -> captureScreen button output
        | "analyze-latest-image" -> analyzeLatestImage button output
        | "open-screenshot-folder" -> openPath (dataDirectory "Screenshot"); appendLine output "Opened Screenshot folder."
        | "extract-audio" -> extractAudio button output
        | "open-audio-folder" -> openPath (dataDirectory "Audio"); appendLine output "Opened Audio folder."
        | "build-transcript" -> buildTranscript button output
        | "open-transcript-folder" -> openPath (dataDirectory "Transcript"); appendLine output "Opened Transcript folder."
        | "refresh-agents" -> refreshAgents button output
        | "export-agent-cards" -> exportAgentCards button output
        | "export-mcp" -> exportMcp button output
        | "discover-voices" -> discoverVoices button output
        | "check-audio-runtime" -> audioRuntimeStatus button output
        | "build-runbook" -> buildRunbook button output
        | "run-local-insight" -> localInsight button output
        | "run-selftest" -> selfTest button output
        | "harvest-session" -> harvestSession button output
        | "open-harvest-folder" -> openPath (dataDirectory "Harvest"); appendLine output "Opened Harvest folder."
        | "check-ocr" -> checkOcr button output
        | "load-ocr-model" -> loadOcr button output
        | "open-config" ->
            let candidates =
                [ Path.Combine(AppContext.BaseDirectory, "config", "yumlog.native.json")
                  Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "config", "yumlog.native.json"))
                  Path.GetFullPath(Path.Combine("config", "yumlog.native.json")) ]
            match candidates |> List.tryFind File.Exists with
            | Some path ->
                openPath path
                appendLine output path
            | None -> appendLine output "No config/yumlog.native.json was found in the installed package or source root."
        | other -> appendLine output $"Action '{other}' is declared but not implemented in this build."

    let private makeCardShell (card: YumlogAgentCard) outputPath (videoHost: FlowLayoutPanel option) =
        let panel = new Panel()
        panel.Dock <- DockStyle.Fill
        panel.BackColor <- background
        panel.Padding <- Padding(16)

        let shell = new TableLayoutPanel()
        shell.Dock <- DockStyle.Fill
        shell.ColumnCount <- 2
        shell.RowCount <- 4
        shell.ColumnStyles.Add(ColumnStyle(SizeType.Percent, 66.0f)) |> ignore
        shell.ColumnStyles.Add(ColumnStyle(SizeType.Percent, 34.0f)) |> ignore
        shell.RowStyles.Add(RowStyle(SizeType.AutoSize)) |> ignore
        shell.RowStyles.Add(RowStyle(SizeType.AutoSize)) |> ignore
        shell.RowStyles.Add(RowStyle(SizeType.Percent, 100.0f)) |> ignore
        shell.RowStyles.Add(RowStyle(SizeType.Absolute, 42.0f)) |> ignore
        shell.Padding <- Padding(2)

        let title = styleLabel (new Label()) 21.0f true textColor
        title.Text <- card.Title
        title.AutoSize <- true
        title.Margin <- Padding(0, 0, 0, 2)
        let subtitle = styleLabel (new Label()) 9.5f false muted
        subtitle.Text <- card.Description
        subtitle.AutoSize <- true
        subtitle.MaximumSize <- Size(760, 0)

        let meta = new Panel()
        meta.Dock <- DockStyle.Fill
        meta.BackColor <- raised
        meta.Padding <- Padding(12)
        let id = styleLabel (new Label()) 9.0f true accent
        id.Text <- card.Id
        id.AutoSize <- true
        id.Dock <- DockStyle.Top
        let role = styleLabel (new Label()) 8.5f false muted
        role.Text <- $"ROLE / {card.Role}{Environment.NewLine}STATE / {card.LiveTileState}"
        role.AutoSize <- true
        role.Dock <- DockStyle.Top
        role.Padding <- Padding(0, 10, 0, 0)
        meta.Controls.AddRange([| role :> Control; id :> Control |])

        let content = new Panel()
        content.Dock <- DockStyle.Fill
        content.BackColor <- Color.FromArgb(8, 14, 19)
        content.Padding <- Padding(10)
        content.BorderStyle <- BorderStyle.FixedSingle

        match videoHost with
        | Some host ->
            host.Dock <- DockStyle.Fill
            host.AutoScroll <- true
            host.WrapContents <- true
            host.BackColor <- content.BackColor
            host.Tag <- "medium"
            content.Controls.Add(host)
            refreshVideoCards host
        | None ->
            let contract = makeOutput()
            contract.Text <-
                "INPUTS" + Environment.NewLine
                + (card.Inputs |> Array.map (sprintf "  • %s") |> String.concat Environment.NewLine)
                + Environment.NewLine + Environment.NewLine
                + "OUTPUTS" + Environment.NewLine
                + (card.Outputs |> Array.map (sprintf "  • %s") |> String.concat Environment.NewLine)
                + Environment.NewLine + Environment.NewLine
                + "ACTIONS" + Environment.NewLine
                + (card.Actions |> Array.map (sprintf "  • %s") |> String.concat Environment.NewLine)
            content.Controls.Add(contract)

        let output = makeOutput()
        output.Text <- $"Ready: {card.Title}{Environment.NewLine}Data root: {outputPath}{Environment.NewLine}"
        output.Dock <- DockStyle.Fill
        output.BorderStyle <- BorderStyle.FixedSingle

        let actionPanel = new FlowLayoutPanel()
        actionPanel.Dock <- DockStyle.Fill
        actionPanel.FlowDirection <- FlowDirection.LeftToRight
        actionPanel.WrapContents <- true
        actionPanel.BackColor <- background
        let refreshVideos () =
            videoHost |> Option.iter refreshVideoCards

        match videoHost with
        | Some host ->
            let sizeLabel = styleLabel (new Label()) 8.5f true warm
            sizeLabel.Text <- "Card size"
            sizeLabel.AutoSize <- true
            sizeLabel.Padding <- Padding(4, 10, 4, 4)
            let sizePicker = new ComboBox()
            sizePicker.DropDownStyle <- ComboBoxStyle.DropDownList
            sizePicker.Items.AddRange([| "small"; "medium"; "large"; "presentation" |] |> Array.map box)
            sizePicker.SelectedIndex <- 1
            sizePicker.SelectedIndexChanged.Add(fun _ ->
                host.Tag <-
                    match sizePicker.SelectedItem with
                    | :? string as selected -> selected
                    | _ -> "medium"
                refreshVideoCards host)
            actionPanel.Controls.AddRange([| sizeLabel :> Control; sizePicker :> Control |])
        | None -> ()

        for action in card.Actions do
            let actionButton = flatButton (action.Replace('-', ' '))
            actionButton.Click.Add(fun _ -> actionFor card action actionButton output refreshVideos)
            actionPanel.Controls.Add(actionButton)

        shell.Controls.Add(title, 0, 0)
        shell.Controls.Add(meta, 1, 0)
        shell.SetRowSpan(meta, 2)
        shell.Controls.Add(subtitle, 0, 1)
        shell.Controls.Add(content, 0, 2)
        shell.Controls.Add(output, 1, 2)
        shell.Controls.Add(actionPanel, 0, 3)
        shell.SetColumnSpan(actionPanel, 2)
        panel.Controls.Add(shell)
        panel

    let private makeSidebar (cards: YumlogAgentCard array) (tabs: TabControl) =
        let side = new Panel()
        side.Dock <- DockStyle.Fill
        side.BackColor <- Color.FromArgb(5, 11, 15)
        side.Padding <- Padding(12)

        let eyebrow = styleLabel (new Label()) 9.0f true warm
        eyebrow.Text <- "YUMLOG MODES"
        eyebrow.Dock <- DockStyle.Top
        eyebrow.AutoSize <- true
        eyebrow.Padding <- Padding(2, 6, 2, 12)

        let modeList = new ListBox()
        modeList.Dock <- DockStyle.Fill
        modeList.BorderStyle <- BorderStyle.None
        modeList.BackColor <- side.BackColor
        modeList.ForeColor <- muted
        modeList.Font <- new Font("Cascadia Mono", 10.0f)
        modeList.ItemHeight <- 38
        modeList.Items.AddRange(cards |> Array.map (fun card -> box $"{card.Title.ToUpperInvariant()}  /  {card.Role}"))
        modeList.SelectedIndex <- 0
        let mutable syncingSelection = false
        modeList.SelectedIndexChanged.Add(fun _ ->
            if not syncingSelection && modeList.SelectedIndex >= 0 then
                syncingSelection <- true
                tabs.SelectedIndex <- modeList.SelectedIndex
                syncingSelection <- false)

        let rootInfo = styleLabel (new Label()) 8.5f false muted
        rootInfo.Dock <- DockStyle.Bottom
        rootInfo.AutoSize <- true
        rootInfo.Text <- $"LOCAL ROOT{Environment.NewLine}{baseDirectory}"

        side.Controls.AddRange([| modeList :> Control; eyebrow :> Control; rootInfo :> Control |])
        tabs.SelectedIndexChanged.Add(fun _ ->
            if not syncingSelection && tabs.SelectedIndex >= 0 && tabs.SelectedIndex < modeList.Items.Count then
                syncingSelection <- true
                modeList.SelectedIndex <- tabs.SelectedIndex
                syncingSelection <- false)
        side

    let run () =
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(false)
        Directory.CreateDirectory(baseDirectory) |> ignore
        let cards = loadAgentCards()

        use form = new Form()
        form.Text <- "Yumlog 0.5.0 • Agent Card Studio"
        form.StartPosition <- FormStartPosition.CenterScreen
        form.MinimumSize <- Size(1180, 760)
        form.Size <- Size(1460, 920)
        form.BackColor <- background
        form.ForeColor <- textColor

        let root = new TableLayoutPanel()
        root.Dock <- DockStyle.Fill
        root.ColumnCount <- 1
        root.RowCount <- 3
        root.BackColor <- background
        root.RowStyles.Add(RowStyle(SizeType.Absolute, 82.0f)) |> ignore
        root.RowStyles.Add(RowStyle(SizeType.Percent, 100.0f)) |> ignore
        root.RowStyles.Add(RowStyle(SizeType.Absolute, 34.0f)) |> ignore

        let header = new Panel()
        header.Dock <- DockStyle.Fill
        header.BackColor <- Color.FromArgb(7, 13, 18)
        header.Padding <- Padding(20, 12, 20, 8)
        let brand = styleLabel (new Label()) 21.0f true textColor
        brand.Text <- "DARBOTLABS / YUMLOG"
        brand.AutoSize <- true
        brand.Dock <- DockStyle.Left
        let kicker = styleLabel (new Label()) 9.0f false accent
        kicker.Text <- "LOCAL CAPTURE  •  EVIDENCE CARDS  •  A2SWE HARVEST"
        kicker.AutoSize <- true
        kicker.Dock <- DockStyle.Bottom
        kicker.TextAlign <- ContentAlignment.TopLeft
        let identity =
            RuntimeIdentity.currentPackageFullName()
            |> Option.defaultValue "UNPACKAGED"
        let identityLabel = styleLabel (new Label()) 8.5f false muted
        identityLabel.Text <- $"v0.5.0  /  {identity}"
        identityLabel.AutoSize <- true
        identityLabel.Dock <- DockStyle.Right
        header.Controls.AddRange([| kicker :> Control; brand :> Control; identityLabel :> Control |])

        let workspace = new TableLayoutPanel()
        workspace.Dock <- DockStyle.Fill
        workspace.ColumnCount <- 2
        workspace.ColumnStyles.Add(ColumnStyle(SizeType.Absolute, 280.0f)) |> ignore
        workspace.ColumnStyles.Add(ColumnStyle(SizeType.Percent, 100.0f)) |> ignore
        workspace.Padding <- Padding(0)

        let tabs = new TabControl()
        tabs.Dock <- DockStyle.Fill
        tabs.Alignment <- TabAlignment.Bottom
        tabs.SizeMode <- TabSizeMode.Fixed
        tabs.ItemSize <- Size(112, 34)
        tabs.Font <- new Font("Cascadia Mono", 8.5f)
        tabs.BackColor <- background

        for card in cards do
            let page = new TabPage(card.Title)
            page.BackColor <- background
            page.ForeColor <- textColor
            let outputPath = dataDirectory (card.Title.Replace(' ', '_'))
            let videoHost =
                if card.Id = "yumlog.video" then Some(new FlowLayoutPanel()) else None
            page.Controls.Add(makeCardShell card outputPath videoHost)
            tabs.TabPages.Add(page)

        workspace.Controls.Add(makeSidebar cards tabs, 0, 0)
        workspace.Controls.Add(tabs, 1, 0)

        let status = new Panel()
        status.Dock <- DockStyle.Fill
        status.BackColor <- raised
        status.Padding <- Padding(14, 8, 14, 0)
        let statusText = styleLabel (new Label()) 8.5f false muted
        statusText.Text <- "Ready • Agent Card schema loaded • Video, screenshot, audio, transcript, agents, voices, insights, harvest, and settings are local-first."
        statusText.AutoSize <- true
        status.Controls.Add(statusText)

        root.Controls.Add(header, 0, 0)
        root.Controls.Add(workspace, 0, 1)
        root.Controls.Add(status, 0, 2)
        form.Controls.Add(root)
        Application.Run(form)
