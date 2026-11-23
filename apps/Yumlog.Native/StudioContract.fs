namespace Yumlog.Native

open System

/// Shared Studio contracts used by Native Studio, self-test and the bitcube tab.
/// Tab order is part of the UI contract: bitcube sits between Insights and Library.
module StudioContract =
    let tabKeys =
        [| "video"; "screenshot"; "audio"; "transcript"; "agents"; "voices"
           "insights"; "bitcube"; "library"; "harvest"; "settings" |]

    let tabLabels =
        [| "Video"; "Screenshot"; "Audio"; "Transcript"; "Agents"; "Voices"
           "Insights"; "bitcube"; "Library"; "Harvest"; "Settings" |]

    let bitcubeLayerNames = [| "signal"; "tonality"; "confidence"; "quality" |]

    let cubeIhdrRequiredFields =
        [| "source_sha256"; "sample_rate_hz"; "bin_frames"; "time_bins"; "freq_bins"
           "duration_ms"; "covers_ms"; "inv_hdr_ppm"; "cube_revision"; "n_points" |]

    let layerRequiredFields = [| "name"; "index" |]

    let layerBodyFields = [| "mean"; "std"; "p50"; "p90"; "active_frac"; "shape" |]

    let libraryCubeDocumentFields =
        [| "source_sha256"; "engine"; "title"; "cube_revision"; "sample_rate"; "duration_s"
           "n_fft"; "hop"; "inv_hdr"; "layer_score"; "n_points"; "n_points_source"
           "n_points_source_per_layer"; "cube_shape_f_t"; "downsample_sf_st"; "bin_seconds"
           "cube_covers_s"; "stft_frames"; "axes"; "layers"; "points_preview"; "legend"
           "provenance" |]

    let schemaId = "https://github.com/darbotlabs/gen-audio/schemas/asset-object.schema.json"
    let uidScheme = "ga1"
    let layerMethod = "library_r3"
    let cubeRevision = 3
    let schemaRelativePath = "Studio/schemas/asset-object.schema.json"
    let builderRelativePath = "Studio/bitcube/build_cube.py"

    let bitcubeBetweenInsightsAndLibrary () =
        let insights = tabKeys |> Array.tryFindIndex (fun key -> key = "insights") |> Option.defaultValue -1
        let bitcube = tabKeys |> Array.tryFindIndex (fun key -> key = "bitcube") |> Option.defaultValue -1
        let library = tabKeys |> Array.tryFindIndex (fun key -> key = "library") |> Option.defaultValue -1
        insights >= 0 && bitcube = insights + 1 && library = bitcube + 1
