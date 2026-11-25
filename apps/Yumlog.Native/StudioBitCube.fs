namespace Yumlog.Native

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json

/// Builds and reads Inverse-HDR bitcubes for a recording's audio using the
/// gen-audio library_r3 schema (signal, tonality, confidence, quality).
module StudioBitCube =
    type BuildResult =
        { AssetPath: string
          CubeJsonPath: string
          PngPath: string
          Uid: string }

    let private assemblyDirectory () =
        let location = typeof<BuildResult>.Assembly.Location
        if String.IsNullOrWhiteSpace location then AppContext.BaseDirectory
        else orEmptyIfNull (Path.GetDirectoryName(location))

    let private scriptCandidates (appRoot: string) =
        let names = StudioContract.builderRelativePath
        let bases = [| assemblyDirectory (); AppContext.BaseDirectory; appRoot |]
        bases
        |> Array.collect (fun root ->
            [| Path.Combine(root, names)
               Path.Combine(root, "Studio", "bitcube", "build_cube.py") |])
        |> Array.distinct

    let scriptPath appRoot =
        scriptCandidates appRoot
        |> Array.tryFind File.Exists
        |> Option.defaultWith (fun () ->
            invalidOp "The bitcube builder (Studio/bitcube/build_cube.py) is missing from this install.")

    let schemaPath appRoot =
        [| Path.Combine(AppContext.BaseDirectory, StudioContract.schemaRelativePath)
           Path.Combine(appRoot, StudioContract.schemaRelativePath)
           Path.Combine(AppContext.BaseDirectory, "Studio", "schemas", "asset-object.schema.json") |]
        |> Array.tryFind File.Exists

    let private pythonExecutable (settings: GenAudio.Settings) =
        let resolved = GenAudio.resolve settings
        if String.IsNullOrWhiteSpace resolved.Python then "python" else resolved.Python

    let private tail (text: string) =
        let trimmed = text.Trim()
        if trimmed.Length <= 800 then trimmed else trimmed.Substring(trimmed.Length - 800)

    let requiredCubeFieldsPresent (root: JsonElement) =
        let cube =
            match root.TryGetProperty("cube") with
            | true, value -> value
            | _ -> root
        let missingDoc =
            StudioContract.libraryCubeDocumentFields
            |> Array.filter (fun name ->
                match cube.TryGetProperty(name) with
                | true, _ -> false
                | _ -> true)
        let fields =
            match root.TryGetProperty("fields") with
            | true, value -> value
            | _ -> root
        let missingIdentity =
            StudioContract.cubeIhdrRequiredFields
            |> Array.filter (fun name ->
                match fields.TryGetProperty(name) with
                | true, _ -> false
                | _ -> true)
        let layersOk =
            match root.TryGetProperty("layers") with
            | true, value when value.ValueKind = JsonValueKind.Array && value.GetArrayLength() = StudioContract.bitcubeLayerNames.Length ->
                value.EnumerateArray()
                |> Seq.mapi (fun index layer ->
                    let name =
                        match layer.TryGetProperty("fields") with
                        | true, fields ->
                            match fields.TryGetProperty("name") with
                            | true, n -> n.GetString()
                            | _ -> ""
                        | _ -> ""
                    name = StudioContract.bitcubeLayerNames[index])
                |> Seq.forall id
            | _ ->
                match cube.TryGetProperty("layers") with
                | true, value ->
                    StudioContract.bitcubeLayerNames
                    |> Array.forall (fun name ->
                        match value.TryGetProperty(name) with
                        | true, _ -> true
                        | _ -> false)
                | _ -> false
        missingDoc.Length = 0 && missingIdentity.Length = 0 && layersOk, missingDoc, missingIdentity
    let readAsset (path: string) =
        if not (File.Exists(path)) then invalidOp "The bitcube file is missing."
        use document = JsonDocument.Parse(File.ReadAllText(path))
        let ok, missingDoc, missingIdentity = requiredCubeFieldsPresent document.RootElement
        if not ok then
            let missing = String.Join(", ", Array.append missingDoc missingIdentity)
            invalidOp $"Bitcube is missing required schema fields: {missing}"
        document.RootElement.Clone()

    let build (appRoot: string) (settings: GenAudio.Settings) (wav: string) (outputDirectory: string) (stem: string) (label: string) =
        if not (File.Exists(wav)) then invalidOp "Extract the audio track before building a bitcube."
        Directory.CreateDirectory(outputDirectory) |> ignore
        let script = scriptPath appRoot
        let assetPath = Path.Combine(outputDirectory, "bitcube.json")
        let cubeJson = Path.Combine(outputDirectory, "bitcube.cube.json")
        let png = Path.Combine(outputDirectory, "bitcube.png")
        let resolved = GenAudio.resolve settings
        let info =
            ProcessStartInfo(pythonExecutable settings, UseShellExecute = false, CreateNoWindow = true,
                             RedirectStandardOutput = true, RedirectStandardError = true,
                             StandardOutputEncoding = UTF8Encoding(false), StandardErrorEncoding = UTF8Encoding(false))
        info.ArgumentList.Add(script)
        info.ArgumentList.Add(wav)
        info.ArgumentList.Add("--out")
        info.ArgumentList.Add(assetPath)
        info.ArgumentList.Add("--cube-json")
        info.ArgumentList.Add(cubeJson)
        info.ArgumentList.Add("--png")
        info.ArgumentList.Add(png)
        info.ArgumentList.Add("--stem")
        let cubeStem =
            if String.IsNullOrWhiteSpace(stem) then
                let fileStem: string | null = Path.GetFileNameWithoutExtension(wav)
                fileStem |> Option.ofObj |> Option.defaultValue "cube"
            else stem
        info.ArgumentList.Add(cubeStem)
        info.ArgumentList.Add("--engine")
        info.ArgumentList.Add(if String.IsNullOrWhiteSpace(resolved.Engine) then "yumlog" else resolved.Engine)
        if not (String.IsNullOrWhiteSpace(label)) then
            info.ArgumentList.Add("--label")
            info.ArgumentList.Add(label)
        info.Environment["PYTHONUTF8"] <- "1"
        info.Environment["PYTHONIOENCODING"] <- "utf-8"
        if not (String.IsNullOrWhiteSpace(resolved.Root)) then
            let source = Path.Combine(Path.GetFullPath(resolved.Root), "src")
            let existing = (orEmptyIfNull (Environment.GetEnvironmentVariable("PYTHONPATH"))).Trim()
            info.Environment["PYTHONPATH"] <- if existing = "" then source else source + string Path.PathSeparator + existing
        use child = new Process(StartInfo = info)
        let started =
            try child.Start()
            with :? ComponentModel.Win32Exception as error ->
                invalidOp $"Python '{info.FileName}' could not start to build the bitcube: {error.Message}"
        if not started then invalidOp $"Python '{info.FileName}' did not start."
        use _tracked = ChildProcesses.track child
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()
        if not (child.WaitForExit(180000)) then
            (try child.Kill(true) with _ -> ())
            invalidOp "Bitcube generation timed out after 180 seconds."
        child.WaitForExit()
        let output = stdout.GetAwaiter().GetResult()
        let errors = stderr.GetAwaiter().GetResult()
        if child.ExitCode <> 0 then
            invalidOp $"Bitcube generation failed (exit {child.ExitCode}): {tail errors}"
        if not (File.Exists(assetPath)) then invalidOp "Bitcube generation did not write bitcube.json."
        ignore (readAsset assetPath)
        let uid =
            use document = JsonDocument.Parse(File.ReadAllText(assetPath))
            match document.RootElement.TryGetProperty("uid") with
            | true, value -> value.GetString()
            | _ -> ""
        { AssetPath = assetPath
          CubeJsonPath = if File.Exists(cubeJson) then cubeJson else ""
          PngPath = if File.Exists(png) then png else ""
          Uid = orEmptyIfNull uid }
