namespace Yumlog.Native

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

/// Boundary to darbotlabs/gen-audio speech synthesis and VibeVoice transcription.
/// Yumlog never imports gen-audio code. It runs gen-audio's own command-line entry
/// points (python -m gen_audio.cli.render_speech and gen_audio.cli.transcribe_vibevoice),
/// reads their JSON receipts, and verifies the files they write. Every child is
/// tracked so closing Studio or the CLI never leaves Python behind.
module GenAudio =
    type Settings =
        { /// Python interpreter with gen-audio installed (GEN_AUDIO_PYTHON, else "python").
          Python: string
          /// Optional gen-audio checkout; its src folder is put first on PYTHONPATH (GEN_AUDIO_ROOT).
          Root: string
          /// gen-audio synthesis engine id, e.g. kokoro_onnx or vibevoice_realtime (YUMLOG_GEN_AUDIO_ENGINE).
          Engine: string
          /// Voice id for that engine (YUMLOG_GEN_AUDIO_VOICE).
          Voice: string
          Language: string
          /// Local VibeVoice ASR checkpoint directory (GEN_AUDIO_VIBEVOICE_ASR_MODEL).
          VibeVoiceModel: string
          /// cpu or cuda.
          Device: string }

    type SpeechReceipt =
        { Engine: string
          Voice: string
          SampleRate: int
          DurationSeconds: float
          AudioSha256: string
          Path: string }

    type TranscriptSegment =
        { Start: float
          End: float
          Speaker: string
          Text: string }

    type TranscriptReceipt =
        { Text: string
          Model: string
          Device: string
          AudioSeconds: float
          Segments: TranscriptSegment array }

    let defaults =
        { Python = ""; Root = ""; Engine = ""; Voice = ""; Language = "en-us"; VibeVoiceModel = ""; Device = "cpu" }

    let private environment (name: string) = (orEmptyIfNull (Environment.GetEnvironmentVariable(name))).Trim()

    /// Fills blank settings from the environment, then from built-in defaults.
    let resolve (settings: Settings) =
        let pick (value: string) (variable: string) (fallback: string) =
            if not (String.IsNullOrWhiteSpace(value)) then value.Trim()
            else
                match environment variable with
                | "" -> fallback
                | configured -> configured
        { settings with
            Python = pick settings.Python "GEN_AUDIO_PYTHON" "python"
            Root = pick settings.Root "GEN_AUDIO_ROOT" ""
            Engine = pick settings.Engine "YUMLOG_GEN_AUDIO_ENGINE" "kokoro_onnx"
            Voice = pick settings.Voice "YUMLOG_GEN_AUDIO_VOICE" ""
            Language = pick settings.Language "YUMLOG_GEN_AUDIO_LANGUAGE" "en-us"
            VibeVoiceModel = pick settings.VibeVoiceModel "GEN_AUDIO_VIBEVOICE_ASR_MODEL" ""
            Device = pick settings.Device "YUMLOG_GEN_AUDIO_DEVICE" "cpu" }

    let sha256 (path: string) =
        use stream = File.OpenRead(path)
        Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()

    let private tail (text: string) =
        let trimmed = text.Trim()
        if trimmed.Length <= 600 then trimmed else trimmed.Substring(trimmed.Length - 600)

    /// Runs `python -m <module> <args>` and returns stdout. A non-zero exit raises with gen-audio's stderr.
    let private invoke (settings: Settings) (moduleName: string) (arguments: string list) (input: string option) (timeoutMs: int) =
        let info =
            ProcessStartInfo(settings.Python, UseShellExecute = false, CreateNoWindow = true,
                             RedirectStandardOutput = true, RedirectStandardError = true,
                             RedirectStandardInput = input.IsSome,
                             StandardOutputEncoding = UTF8Encoding(false), StandardErrorEncoding = UTF8Encoding(false))
        if input.IsSome then info.StandardInputEncoding <- UTF8Encoding(false)
        info.ArgumentList.Add("-m")
        info.ArgumentList.Add(moduleName)
        for argument in arguments do
            info.ArgumentList.Add(argument)
        info.Environment["PYTHONUTF8"] <- "1"
        info.Environment["PYTHONIOENCODING"] <- "utf-8"
        if not (String.IsNullOrWhiteSpace(settings.Root)) then
            let source = Path.Combine(Path.GetFullPath(settings.Root), "src")
            let existing = environment "PYTHONPATH"
            info.Environment["PYTHONPATH"] <- if existing = "" then source else source + string Path.PathSeparator + existing
        use child = new Process(StartInfo = info)
        let started =
            try child.Start()
            with :? ComponentModel.Win32Exception as error ->
                invalidOp $"gen-audio Python '{settings.Python}' could not start: {error.Message}"
        if not started then invalidOp $"gen-audio Python '{settings.Python}' did not start."
        use _tracked = ChildProcesses.track child
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()
        match input with
        | Some text ->
            child.StandardInput.Write(text)
            child.StandardInput.Close()
        | None -> ()
        if not (child.WaitForExit(timeoutMs)) then
            (try child.Kill(true) with _ -> ())
            invalidOp $"gen-audio {moduleName} timed out after {timeoutMs / 1000} seconds."
        child.WaitForExit()
        let output = stdout.GetAwaiter().GetResult()
        let errors = stderr.GetAwaiter().GetResult()
        if child.ExitCode <> 0 then
            invalidOp $"gen-audio {moduleName} failed (exit {child.ExitCode}): {tail errors}"
        output

    /// gen-audio CLIs print one JSON receipt as their last JSON line.
    let private receipt (moduleName: string) (stdout: string) =
        stdout.Split('\n')
        |> Array.map (fun line -> line.Trim())
        |> Array.filter (fun line -> line.StartsWith("{", StringComparison.Ordinal))
        |> Array.tryLast
        |> Option.defaultWith (fun () -> invalidOp $"gen-audio {moduleName} returned no JSON receipt.")

    let private text (element: JsonElement) (name: string) =
        match element.TryGetProperty(name) with
        | true, value when value.ValueKind = JsonValueKind.String -> orEmptyIfNull (value.GetString())
        | true, value when value.ValueKind = JsonValueKind.Number -> value.GetRawText()
        | _ -> ""

    let private number (element: JsonElement) (names: string list) =
        names
        |> List.tryPick (fun name ->
            match element.TryGetProperty(name) with
            | true, value when value.ValueKind = JsonValueKind.Number -> Some(value.GetDouble())
            | true, value when value.ValueKind = JsonValueKind.String ->
                match Double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture) with
                | true, parsed -> Some parsed
                | _ -> None
            | _ -> None)

    /// Renders one utterance with gen-audio and verifies the WAV against gen-audio's receipt.
    let renderSpeech (settings: Settings) (script: string) (output: string) =
        let settings = resolve settings
        if String.IsNullOrWhiteSpace(script) then invalidArg "script" "Enter the narration text."
        if String.IsNullOrWhiteSpace(settings.Voice) then
            invalidOp "Set a gen-audio voice in Settings, with --voice, or in YUMLOG_GEN_AUDIO_VOICE."
        let target = Path.GetFullPath(output)
        let moduleName = "gen_audio.cli.render_speech"
        let stdout =
            invoke settings moduleName
                [ "--engine"; settings.Engine; "--voice"; settings.Voice; "--language"; settings.Language; "--output"; target ]
                (Some(script.Trim())) 900000
        use document = JsonDocument.Parse(receipt moduleName stdout)
        let root = document.RootElement
        let result =
            { Engine = text root "engine"
              Voice = text root "voice"
              SampleRate = number root [ "sampleRate" ] |> Option.map int |> Option.defaultValue 0
              DurationSeconds = number root [ "durationSeconds" ] |> Option.defaultValue 0.0
              AudioSha256 = text root "audioSha256"
              Path = target }
        if not (File.Exists(target)) then invalidOp "gen-audio reported success but wrote no WAV."
        if result.SampleRate <= 0 || result.DurationSeconds <= 0.0 then invalidOp "gen-audio receipt describes no audio."
        if not (String.Equals(sha256 target, result.AudioSha256, StringComparison.OrdinalIgnoreCase)) then
            invalidOp "gen-audio WAV does not match the SHA-256 in its receipt."
        result

    let private segments (root: JsonElement) =
        match root.TryGetProperty("segments") with
        | true, list when list.ValueKind = JsonValueKind.Array ->
            list.EnumerateArray()
            |> Seq.filter (fun item -> item.ValueKind = JsonValueKind.Object)
            |> Seq.choose (fun item ->
                let content = (text item "Content" + text item "content" + text item "text").Trim()
                match number item [ "Start"; "start"; "startSeconds" ], number item [ "End"; "end"; "endSeconds" ] with
                | Some start, Some finish when finish > start && content <> "" ->
                    Some { Start = start; End = finish; Speaker = text item "Speaker" + text item "speaker"; Text = content }
                | _ -> None)
            |> Seq.toArray
        | _ -> [||]

    /// Transcribes a WAV with gen-audio's local VibeVoice ASR checkpoint.
    let transcribe (settings: Settings) (wav: string) =
        let settings = resolve settings
        if String.IsNullOrWhiteSpace(settings.VibeVoiceModel) || not (Directory.Exists(settings.VibeVoiceModel)) then
            invalidOp "Set a local VibeVoice ASR checkpoint folder in Settings, with --model, or in GEN_AUDIO_VIBEVOICE_ASR_MODEL."
        if not (File.Exists(wav)) then invalidArg "wav" $"Audio file does not exist: {wav}"
        let moduleName = "gen_audio.cli.transcribe_vibevoice"
        let stdout =
            invoke settings moduleName
                [ Path.GetFullPath(wav); "--model"; Path.GetFullPath(settings.VibeVoiceModel); "--device"; settings.Device ]
                None 3600000
        use document = JsonDocument.Parse(receipt moduleName stdout)
        let root = document.RootElement
        let result =
            { Text = (text root "text").Trim()
              Model = text root "model"
              Device = text root "device"
              AudioSeconds = number root [ "audioSeconds" ] |> Option.defaultValue 0.0
              Segments = segments root }
        if result.Text = "" && result.Segments.Length = 0 then invalidOp "gen-audio VibeVoice returned an empty transcript."
        result

    let private clock (seconds: float) =
        let span = TimeSpan.FromSeconds(Math.Max(0.0, seconds))
        $"{(int span.TotalHours):D2}:{span.Minutes:D2}:{span.Seconds:D2},{span.Milliseconds:D3}"

    /// SubRip text: one cue per timed segment, or one cue over the whole audio when the
    /// checkpoint returned untimed text. Speaker ids are kept as labels, never as identities.
    let toSrt (transcript: TranscriptReceipt) =
        let cues =
            if transcript.Segments.Length > 0 then transcript.Segments
            else [| { Start = 0.0; End = Math.Max(transcript.AudioSeconds, 0.001); Speaker = ""; Text = transcript.Text } |]
        let builder = StringBuilder()
        cues
        |> Array.iteri (fun index cue ->
            let label = if cue.Speaker = "" then cue.Text else $"Speaker {cue.Speaker}: {cue.Text}"
            builder.Append(index + 1).Append('\n')
                .Append(clock cue.Start).Append(" --> ").Append(clock cue.End).Append('\n')
                .Append(label).Append("\n\n") |> ignore)
        builder.ToString()
