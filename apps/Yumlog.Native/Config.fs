namespace Yumlog.Native

open System
open System.IO

module NativeConfig =
    /// Shared catalog is embedded so installed CLI and desktop resolve the same IDs.
    let applyMediaPresets (options: Map<string,string>) =
        use stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Yumlog.MediaPresets.json")
        if isNull stream then failwith "Embedded media preset catalog missing."
        use document = System.Text.Json.JsonDocument.Parse(stream |> nonNull)
        let resolve (group:string) (id:string) : System.Text.Json.JsonElement =
            document.RootElement.GetProperty(group).EnumerateArray()
            |> Seq.tryFind (fun item -> item.GetProperty("id").GetString() = id)
            |> Option.defaultWith (fun () -> invalidArg "preset" $"Unknown media preset: {id}")
        let mutable result = options
        match Map.tryFind "duration-preset" options with
        | Some id ->
            if Map.containsKey "duration" options then invalidArg "duration" "Choose --duration or --duration-preset, not both."
            let seconds = (resolve "durationPresets" id).GetProperty("seconds").GetInt32()
            result <- Map.add "duration" (string seconds) result
        | None -> ()
        match Map.tryFind "frame-rate-preset" options with
        | Some id ->
            if Map.containsKey "fps" options then invalidArg "fps" "Choose --fps or --frame-rate-preset, not both."
            let rate = resolve "frameRatePresets" id
            if rate.GetProperty("denominator").GetInt32() <> 1 then
                invalidArg "frame-rate-preset" "Native capture requires an integer frame rate; use an explicit export conversion for fractional rates."
            result <- Map.add "fps" (string (rate.GetProperty("numerator").GetInt32())) result
        | None -> ()
        if Map.containsKey "resolution-preset" options then
            invalidArg "resolution-preset" "Native capture uses monitor dimensions. Resolve the export filter with launchers/media-presets.ps1."
        result
        |> Map.remove "duration-preset"
        |> Map.remove "frame-rate-preset"

    let defaultConfigPath =
        Path.Combine("config", "yumlog.native.json")

    let defaultExtensionsDir =
        Path.Combine("config", "extensions")

    let defaults =
        { Capture =
            { OutDir = "./screenshots"
              Fps = 1
              DurationSec = 1
              Count = 0
              AllScreens = true
              ImageFormat = "png" }
          Record =
            { OutFile = "./yumlogs/yumlog.mp4"
              Fps = 30
              DurationSec = 10
              FFmpegPath = ""
              Encoder = "hevc"
              Preset = "medium"
              Backend = "auto"
              Codec = "hevc"
              Lossless = true
              Container = "mp4"
              X265Path = ""
              CaptureBackend = "wgc"
              MonitorIndex = 0
              DisableCaptureBorder = true
              TimedMetadataOut = "" }
          Analyze =
            { Input = "./screenshots"
              OcrMode = "auto"
              JsonOut = ""
              MinConfidence = 0.5 }
          Follow =
            { OutDir = "./yumlogs/follow"
              Fps = 1
              DurationSec = 30
              ChangeThreshold = 0.05
              OcrMode = "auto"
              StopOnIdleSec = 0 }
          Extensions =
            { Dir = defaultExtensionsDir
              Enabled = true
              IncludeBuiltIn = true
              ManifestPattern = "*.extension.json" } }

    let private orDefault (value: 'T) (fallback: 'T) =
        if Object.ReferenceEquals(box value, null) then fallback else value

    let private textOrDefault (value: string) (fallback: string) =
        if String.IsNullOrWhiteSpace(value) then fallback else value

    /// Fills sections that are absent from older config files so newer sections stay optional.
    let normalize (config: AppConfig) =
        let extensions = orDefault config.Extensions defaults.Extensions

        { Capture = orDefault config.Capture defaults.Capture
          Record = orDefault config.Record defaults.Record
          Analyze = orDefault config.Analyze defaults.Analyze
          Follow = orDefault config.Follow defaults.Follow
          Extensions =
            { extensions with
                Dir = textOrDefault extensions.Dir defaults.Extensions.Dir
                ManifestPattern = textOrDefault extensions.ManifestPattern defaults.Extensions.ManifestPattern } }

    let load path =
        if File.Exists(path) then
            NativeJson.readOver defaults path |> normalize
        else
            defaults

    let save path config =
        NativeJson.write path config

    let mergeCapture (baseConfig: CaptureConfig) overrides =
        let get name fallback =
            overrides
            |> Map.tryFind name
            |> Option.defaultValue fallback

        { baseConfig with
            OutDir = get "out-dir" baseConfig.OutDir
            Fps = get "fps" (string baseConfig.Fps) |> Int32.Parse
            DurationSec = get "duration" (string baseConfig.DurationSec) |> Int32.Parse
            Count = get "count" (string baseConfig.Count) |> Int32.Parse
            AllScreens = not (Map.containsKey "primary" overrides)
            ImageFormat = get "format" baseConfig.ImageFormat }

    let mergeRecord (baseConfig: RecordConfig) overrides =
        let get name fallback =
            overrides
            |> Map.tryFind name
            |> Option.defaultValue fallback

        let getBool name (fallback: bool) =
            match Map.tryFind name overrides with
            | None -> fallback
            | Some (value: string) ->
                match value.Trim().ToLowerInvariant() with
                | "" | "1" | "true" | "yes" | "on" -> true
                | "0" | "false" | "no" | "off" -> false
                | other -> invalidArg name $"Expected a boolean for --{name}, got '{other}'."

        /// The record backend is a superset of the HEVC encoder backends: it also
        /// accepts the legacy FFmpeg pipeline, which is not an in-process encoder.
        let normalizeRecordBackend (value: string) =
            match (orEmptyIfNull value).Trim().ToLowerInvariant() with
            | "" -> "auto"
            | "ffmpeg" | "legacy" -> "ffmpeg"
            | _ -> value |> HevcBackend.parse |> HevcBackend.toString

        let normalizeContainer (value: string) =
            match (orEmptyIfNull value).Trim().ToLowerInvariant() with
            | "" | "mp4" -> "mp4"
            | other -> invalidArg "container" $"Unsupported container '{other}'. Only mp4 is implemented."

        /// One Preset field feeds three encoders (NVENC, x265, FFmpeg), and with
        /// backend "auto" the encoder is not known until the adapter is probed. So the
        /// vocabulary is validated here, once, against the x264/x265 names that every
        /// backend understands - NVENC translates them to its own GUIDs internally.
        /// Accepting NVENC-only names such as "p7" here would produce a config that
        /// works on an NVENC machine and hard-fails on any other.
        let normalizePreset (value: string) =
            match (orEmptyIfNull value).Trim().ToLowerInvariant() with
            | "" -> "medium"
            | "ultrafast" | "superfast" | "veryfast" | "faster" | "fast" | "medium" | "slow" | "slower"
            | "veryslow" | "placebo" as ok -> ok
            | other ->
                invalidArg
                    "preset"
                    $"Unsupported preset '{other}'. Expected one of: ultrafast, superfast, veryfast, faster, fast, medium, slow, slower, veryslow, placebo."

        /// Read by nothing yet - WgcCapture is always used. Validated so that setting
        /// it to an unimplemented value fails loudly instead of being silently ignored.
        let normalizeCaptureBackend (value: string) =
            match (orEmptyIfNull value).Trim().ToLowerInvariant() with
            | "" | "wgc" -> "wgc"
            | other ->
                invalidArg "capture-backend" $"Unsupported capture backend '{other}'. Only wgc is implemented."

        let normalizeCodec (value: string) =
            match (orEmptyIfNull value).Trim().ToLowerInvariant() with
            | "" | "hevc" | "h265" -> "hevc"
            | other -> invalidArg "codec" $"Unsupported codec '{other}'. Only hevc is implemented."

        { baseConfig with
            OutFile =
                match overrides |> Map.tryFind "out" with
                | Some (value: string) when not (String.IsNullOrWhiteSpace(value)) -> value
                | _ -> get "out-file" baseConfig.OutFile
            Fps = get "fps" (string baseConfig.Fps) |> Int32.Parse
            DurationSec = get "duration" (string baseConfig.DurationSec) |> Int32.Parse
            FFmpegPath = get "ffmpeg" baseConfig.FFmpegPath
            Encoder = get "encoder" baseConfig.Encoder
            Preset = get "preset" baseConfig.Preset |> normalizePreset
            Backend = get "backend" baseConfig.Backend |> normalizeRecordBackend
            Codec = get "codec" baseConfig.Codec |> normalizeCodec
            Lossless = getBool "lossless" baseConfig.Lossless
            Container = get "container" baseConfig.Container |> normalizeContainer
            X265Path = get "x265" baseConfig.X265Path
            CaptureBackend = get "capture-backend" baseConfig.CaptureBackend |> normalizeCaptureBackend
            MonitorIndex = get "monitor-index" (string baseConfig.MonitorIndex) |> Int32.Parse
            DisableCaptureBorder = getBool "disable-capture-border" baseConfig.DisableCaptureBorder
            TimedMetadataOut = get "timed-metadata-out" baseConfig.TimedMetadataOut }

    let mergeAnalyze (baseConfig: AnalyzeConfig) overrides =
        let get name fallback =
            overrides
            |> Map.tryFind name
            |> Option.defaultValue fallback

        { baseConfig with
            Input = get "input" baseConfig.Input
            OcrMode = get "ocr" baseConfig.OcrMode
            JsonOut = get "json-out" baseConfig.JsonOut
            MinConfidence = get "min-confidence" (string baseConfig.MinConfidence) |> Double.Parse }

    let mergeFollow (baseConfig: FollowConfig) overrides =
        let get name fallback =
            overrides
            |> Map.tryFind name
            |> Option.defaultValue fallback

        { baseConfig with
            OutDir = get "out-dir" baseConfig.OutDir
            Fps = get "fps" (string baseConfig.Fps) |> Int32.Parse
            DurationSec = get "duration" (string baseConfig.DurationSec) |> Int32.Parse
            ChangeThreshold = get "change-threshold" (string baseConfig.ChangeThreshold) |> Double.Parse
            OcrMode = get "ocr" baseConfig.OcrMode
            StopOnIdleSec = get "stop-on-idle" (string baseConfig.StopOnIdleSec) |> Int32.Parse }

    let mergeExtensions (baseConfig: ExtensionsConfig) overrides =
        let get name fallback =
            overrides
            |> Map.tryFind name
            |> Option.defaultValue fallback

        { baseConfig with
            Dir = get "dir" baseConfig.Dir
            ManifestPattern = get "pattern" baseConfig.ManifestPattern
            Enabled = baseConfig.Enabled && not (Map.containsKey "no-external" overrides)
            IncludeBuiltIn = baseConfig.IncludeBuiltIn && not (Map.containsKey "no-builtin" overrides) }

