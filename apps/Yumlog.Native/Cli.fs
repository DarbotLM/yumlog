namespace Yumlog.Native

open System

module Cli =
    let private usage =
        """
Yumlog.Native.Cli

Usage:
  Yumlog.Native.Cli capture [--config path] [--out-dir path] [--fps n] [--duration n] [--count n] [--primary]
  Yumlog.Native.Cli record [--config path] [--out-file path] [--fps n] [--duration n]
                           [--backend auto|nvenc|x265|ffmpeg] [--lossless true|false] [--preset name]
                           [--container mp4] [--x265 path] [--capture-backend wgc]
                           [--monitor-index n] [--disable-capture-border true|false]
                           [--timed-metadata-out path]
  Yumlog.Native.Cli analyze [--config path] [--input path] [--ocr none|auto|windows-ai|raw-com] [--json-out path]
  Yumlog.Native.Cli follow [--config path] [--out-dir path] [--fps n] [--duration n] [--change-threshold n] [--ocr mode]
  Yumlog.Native.Cli handoff --input path --out-dir path [--ocr none|auto|windows-ai|raw-com] [--resume]
  Yumlog.Native.Cli readiness --ocr none|auto|windows-ai|raw-com
  Yumlog.Native.Cli orchestrate --plan path
  Yumlog.Native.Cli config show [--config path]
  Yumlog.Native.Cli config init [--config path] [--force]
  Yumlog.Native.Cli extensions list [--config path] [--dir path] [--kind kind] [--name text] [--no-builtin] [--no-external]
  Yumlog.Native.Cli extensions mcp [--config path] [--dir path] [--no-builtin] [--no-external]
  Yumlog.Native.Cli extensions validate [--config path] [--dir path] [--pattern glob]
  Yumlog.Native.Cli selftest [--config path] [--sample-out path.mdkg]
  Yumlog.Native.Cli runbook --input path.mdkg [--json-out path] [--markdown-out path]
  Yumlog.Native.Cli reason --input path.mdkg [--question text] [--model dir]
                           [--provider cpu|auto|webgpu|dml|cuda] [--high-water 0..1]
                           [--max-tokens n] [--max-segments n] [--json-out path]
  Yumlog.Native.Cli watch [--duration n] [--fps n] [--change-threshold n] [--ocr none|auto]
                          [--narrate-every seconds] [--question text] [--out dir]
                          [--model dir] [--provider cpu|auto] [--high-water 0..1]
                          [--max-tokens n] [--json-out path]
  Yumlog.Native.Cli models

Extension kinds:
  model, agent, tool, skill, workflow, connector, plugin, extension, mcp-server, api, continuous-ai
"""

    let helpText () = usage.Trim()

    let private parseOptions args =
        let rec loop remaining configPath options =
            match remaining with
            | [] -> configPath, options
            | "--config" :: value :: rest -> loop rest value options
            | "--force" :: rest -> loop rest configPath (Map.add "force" "true" options)
            | "--primary" :: rest -> loop rest configPath (Map.add "primary" "true" options)
            | "--no-builtin" :: rest -> loop rest configPath (Map.add "no-builtin" "true" options)
            | "--no-external" :: rest -> loop rest configPath (Map.add "no-external" "true" options)
            | "--plan" :: value :: rest -> loop rest configPath (Map.add "plan" value options)
            | option :: value :: rest when option.StartsWith("--") ->
                loop rest configPath (Map.add (option.Substring(2)) value options)
            | token :: _ -> invalidArg "args" $"Unexpected argument '{token}'."

        loop args NativeConfig.defaultConfigPath Map.empty

    let private ensureOptions command allowed options =
        let allowed = set allowed
        let unknown =
            options
            |> Map.toSeq
            |> Seq.map fst
            |> Seq.filter (allowed.Contains >> not)
            |> Seq.sort
            |> Seq.toArray

        if unknown.Length > 0 then
            let formatted =
                unknown
                |> Array.map (fun name -> "--" + name)
                |> String.concat ", "

            invalidArg
                "args"
                $"Unknown option(s) for {command}: {formatted}."

    let private parseHandoff args =
        let rec loop remaining options =
            match remaining with
            | [] -> options
            | "--resume" :: rest when not (Map.containsKey "resume" options) ->
                loop rest (Map.add "resume" "true" options)
            | key :: value :: rest
                when List.contains key [ "--input"; "--out-dir"; "--ocr" ]
                     && not (value.StartsWith("--"))
                     && not (Map.containsKey key options) ->
                loop rest (Map.add key value options)
            | token :: _ -> invalidArg "args" $"Unexpected, duplicate, or incomplete handoff option '{token}'."

        let options = loop args Map.empty
        let required key =
            options
            |> Map.tryFind key
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.defaultWith (fun () -> invalidArg "args" $"Missing {key} path.")

        { Input = required "--input"
          OutDir = required "--out-dir"
          OcrMode = options |> Map.tryFind "--ocr" |> Option.defaultValue "none"
          Resume = Map.containsKey "resume" options }

    let parse argv =
        match Array.toList argv with
        | [] -> Help
        | "help" :: _ | "--help" :: _ | "-h" :: _ -> Help
        | "capture" :: args ->
            let configPath, options = parseOptions args
            ensureOptions "capture" [ "out-dir"; "fps"; "duration"; "count"; "primary"; "format" ] options
            let appConfig = NativeConfig.load configPath
            Capture(NativeConfig.mergeCapture appConfig.Capture options, configPath)
        | "record" :: args ->
            let configPath, options = parseOptions args
            ensureOptions
                "record"
                [ "out"; "out-file"; "fps"; "duration"; "ffmpeg"; "encoder"; "preset"; "backend"; "codec"
                  "lossless"; "container"; "x265"; "capture-backend"; "monitor-index"; "disable-capture-border"
                  "timed-metadata-out" ]
                options
            let appConfig = NativeConfig.load configPath
            Record(NativeConfig.mergeRecord appConfig.Record options, configPath)
        | "analyze" :: args ->
            let configPath, options = parseOptions args
            ensureOptions "analyze" [ "input"; "ocr"; "json-out"; "min-confidence" ] options
            let appConfig = NativeConfig.load configPath
            Analyze(NativeConfig.mergeAnalyze appConfig.Analyze options, configPath)
        | "handoff" :: args ->
            Handoff(parseHandoff args)
        | [ "readiness"; "--ocr"; mode ] -> Readiness mode
        | "readiness" :: _ -> invalidArg "args" "Usage: readiness --ocr none|auto|windows-ai|raw-com"
        | "follow" :: args ->
            let configPath, options = parseOptions args
            ensureOptions "follow" [ "out-dir"; "fps"; "duration"; "change-threshold"; "ocr"; "stop-on-idle" ] options
            let appConfig = NativeConfig.load configPath
            Follow(NativeConfig.mergeFollow appConfig.Follow options, configPath)
        | "orchestrate" :: args ->
            let configPath, options = parseOptions args
            ensureOptions "orchestrate" [ "plan" ] options
            let plan =
                options
                |> Map.tryFind "plan"
                |> Option.defaultWith (fun () -> invalidArg "plan" "Missing --plan path.")
            Orchestrate(plan, configPath)
        | "config" :: "show" :: args ->
            let configPath, options = parseOptions args
            ensureOptions "config show" [] options
            ConfigShow configPath
        | "config" :: "init" :: args ->
            let configPath, options = parseOptions args
            ensureOptions "config init" [ "force" ] options
            ConfigInit(configPath, Map.containsKey "force" options)
        | "extensions" :: "mcp" :: args ->
            let configPath, options = parseOptions args
            ensureOptions "extensions mcp" [ "dir"; "no-builtin"; "no-external" ] options
            let appConfig = NativeConfig.load configPath
            ExtensionsMcp(NativeConfig.mergeExtensions appConfig.Extensions options, configPath)
        | "extensions" :: "validate" :: args ->
            let configPath, options = parseOptions args
            ensureOptions "extensions validate" [ "dir"; "pattern"; "no-builtin"; "no-external" ] options
            let appConfig = NativeConfig.load configPath
            ExtensionsValidate(NativeConfig.mergeExtensions appConfig.Extensions options, configPath)
        | "extensions" :: "list" :: args
        | "extensions" :: "show" :: args
        | "extensions" :: args ->
            let configPath, options = parseOptions args
            ensureOptions "extensions list" [ "dir"; "kind"; "name"; "no-builtin"; "no-external" ] options
            let appConfig = NativeConfig.load configPath
            let get name =
                options |> Map.tryFind name |> Option.defaultValue ""
            let query: ExtensionQuery = { Kind = get "kind"; Name = get "name" }
            ExtensionsShow(NativeConfig.mergeExtensions appConfig.Extensions options, query, configPath)
        | "selftest" :: args ->
            let configPath, options = parseOptions args
            ensureOptions "selftest" [ "sample-out" ] options
            SelfTest(configPath, options |> Map.tryFind "sample-out" |> Option.defaultValue "")
        | "runbook" :: args ->
            let _, options = parseOptions args
            ensureOptions "runbook" [ "input"; "json-out"; "markdown-out" ] options
            let input =
                options
                |> Map.tryFind "input"
                |> Option.defaultWith (fun () -> invalidArg "input" "Missing --input path to a .mdkg recording.")
            let jsonOut = options |> Map.tryFind "json-out" |> Option.defaultValue ""
            let markdownOut = options |> Map.tryFind "markdown-out" |> Option.defaultValue ""
            Runbook(input, jsonOut, markdownOut)
        | "models" :: args ->
            let configPath, options = parseOptions args
            ensureOptions "models" [] options
            ListModels
        | "reason" :: args ->
            let _, options = parseOptions args
            ensureOptions
                "reason"
                [ "input"; "question"; "model"; "provider"; "high-water"; "max-tokens"; "max-segments"; "json-out" ]
                options

            let input =
                options
                |> Map.tryFind "input"
                |> Option.defaultWith (fun () -> invalidArg "input" "Missing --input path to a .mdkg recording.")

            let number key fallback =
                match options |> Map.tryFind key with
                | Some value ->
                    match Double.TryParse(value, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
                    | true, parsed -> parsed
                    | _ -> invalidArg key $"Expected a number for --{key}, got '{value}'."
                | None -> fallback

            Reason
                { OnDeviceLlm.defaultOptions with
                    Input = input
                    ModelDir = options |> Map.tryFind "model" |> Option.defaultValue ""
                    Provider =
                        options
                        |> Map.tryFind "provider"
                        |> Option.defaultValue OnDeviceLlm.defaultOptions.Provider
                    Question =
                        options
                        |> Map.tryFind "question"
                        |> Option.defaultValue OnDeviceLlm.defaultOptions.Question
                    HighWaterPct = number "high-water" OnDeviceLlm.defaultOptions.HighWaterPct
                    MaxNewTokens = int (number "max-tokens" (float OnDeviceLlm.defaultOptions.MaxNewTokens))
                    MaxSegments = int (number "max-segments" (float OnDeviceLlm.defaultOptions.MaxSegments))
                    JsonOut = options |> Map.tryFind "json-out" |> Option.defaultValue "" }
        | "watch" :: args ->
            let _, options = parseOptions args
            ensureOptions
                "watch"
                [ "duration"; "fps"; "change-threshold"; "ocr"; "narrate-every"; "question"; "out"; "model"
                  "provider"; "high-water"; "max-tokens"; "json-out" ]
                options

            let number key fallback =
                match options |> Map.tryFind key with
                | Some value ->
                    match Double.TryParse(value, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
                    | true, parsed -> parsed
                    | _ -> invalidArg key $"Expected a number for --{key}, got '{value}'."
                | None -> fallback

            Watch
                { OutDir =
                    options
                    |> Map.tryFind "out"
                    |> Option.defaultValue (IO.Path.Combine(IO.Path.GetTempPath(), "yumlog-watch"))
                  Fps = int (number "fps" 1.0)
                  DurationSec = int (number "duration" 20.0)
                  ChangeThreshold = number "change-threshold" 0.001
                  OcrMode = options |> Map.tryFind "ocr" |> Option.defaultValue "none"
                  NarrateEverySec = number "narrate-every" 10.0
                  Question =
                    options
                    |> Map.tryFind "question"
                    |> Option.defaultValue "In one or two sentences, what is happening on screen right now?"
                  ModelDir = options |> Map.tryFind "model" |> Option.defaultValue ""
                  Provider = options |> Map.tryFind "provider" |> Option.defaultValue "cpu"
                  HighWaterPct = number "high-water" 0.6
                  MaxNewTokens = int (number "max-tokens" 96.0)
                  JsonOut = options |> Map.tryFind "json-out" |> Option.defaultValue "" }
        | command :: _ ->
            invalidArg "command" $"Unknown command '{command}'."
