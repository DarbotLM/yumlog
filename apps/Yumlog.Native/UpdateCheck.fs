namespace Yumlog.Native

open System
open System.IO
open System.Net.Http
open System.Reflection
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

/// Launch-time release check. Every value comes from the "update" section of
/// config\yumlog.native.json, then the per-user %LOCALAPPDATA%\DarbotLabs\Yumlog\yumlog.settings.json,
/// then YUMLOG_UPDATE_* environment variables (last one wins). The check never blocks or
/// fails a launch: offline, rate-limited or malformed answers resolve to "unavailable".
module UpdateCheck =
    [<Literal>]
    let ResultSchema = "yumlog.update-check/1.0"

    type UpdateSettings =
        { Enabled: bool
          /// owner/name of the GitHub repository whose releases are checked.
          Repo: string
          /// GitHub REST API base URL (GitHub Enterprise or a test server can replace it).
          ApiBase: string
          /// Base URL for the human-readable releases page shown when the API is unavailable.
          WebBase: string
          TimeoutMs: int }

    type UpdateResult =
        { Schema: string
          CheckedUtc: DateTimeOffset
          Repo: string
          Current: string
          Latest: string
          Url: string
          /// update-available | up-to-date | ahead | disabled | unavailable
          Status: string
          Detail: string }

    let defaults =
        { Enabled = true
          Repo = "darbotlabs/Yumlog"
          ApiBase = "https://api.github.com"
          WebBase = "https://github.com"
          TimeoutMs = 4000 }

    let private repoPattern = Regex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant)

    let isValidRepo (repo: string) = not (String.IsNullOrWhiteSpace repo) && repoPattern.IsMatch(repo)

    let private parseBool (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "1" | "true" | "on" | "yes" -> Some true
        | "0" | "false" | "off" | "no" -> Some false
        | _ -> None

    let private clampTimeout value = max 250 (min 30000 value)

    /// Applies the "update" object of one JSON document over the settings. Unknown keys are ignored.
    let applyJson (settings: UpdateSettings) (json: string) =
        try
            use document = JsonDocument.Parse(json, JsonDocumentOptions(CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true))
            let mutable section = Unchecked.defaultof<JsonElement>
            let found =
                document.RootElement.ValueKind = JsonValueKind.Object
                && (document.RootElement.TryGetProperty("update", &section) || document.RootElement.TryGetProperty("Update", &section))
            if not found || section.ValueKind <> JsonValueKind.Object then settings
            else
                let mutable result = settings
                for property in section.EnumerateObject() do
                    match property.Name.ToLowerInvariant(), property.Value.ValueKind with
                    | "enabled", (JsonValueKind.True | JsonValueKind.False) -> result <- { result with Enabled = property.Value.GetBoolean() }
                    | "repo", JsonValueKind.String -> result <- { result with Repo = property.Value.GetString() |> nonNull }
                    | "apibase", JsonValueKind.String -> result <- { result with ApiBase = property.Value.GetString() |> nonNull }
                    | "webbase", JsonValueKind.String -> result <- { result with WebBase = property.Value.GetString() |> nonNull }
                    | "timeoutms", JsonValueKind.Number -> result <- { result with TimeoutMs = clampTimeout (property.Value.GetInt32()) }
                    | _ -> ()
                result
        with _ -> settings

    let applyFile (settings: UpdateSettings) (path: string) =
        if String.IsNullOrWhiteSpace path || not (File.Exists path) then settings
        else
            try applyJson settings (File.ReadAllText path) with _ -> settings

    /// Environment overrides: YUMLOG_UPDATE_CHECK (0/1), YUMLOG_UPDATE_REPO, YUMLOG_UPDATE_API,
    /// YUMLOG_UPDATE_WEB and YUMLOG_UPDATE_TIMEOUT_MS.
    let applyEnvironment (getEnv: string -> string option) (settings: UpdateSettings) =
        let mutable result = settings
        getEnv "YUMLOG_UPDATE_CHECK" |> Option.bind parseBool |> Option.iter (fun value -> result <- { result with Enabled = value })
        getEnv "YUMLOG_UPDATE_REPO" |> Option.filter (String.IsNullOrWhiteSpace >> not) |> Option.iter (fun value -> result <- { result with Repo = value.Trim() })
        getEnv "YUMLOG_UPDATE_API" |> Option.filter (String.IsNullOrWhiteSpace >> not) |> Option.iter (fun value -> result <- { result with ApiBase = value.Trim() })
        getEnv "YUMLOG_UPDATE_WEB" |> Option.filter (String.IsNullOrWhiteSpace >> not) |> Option.iter (fun value -> result <- { result with WebBase = value.Trim() })
        getEnv "YUMLOG_UPDATE_TIMEOUT_MS"
        |> Option.bind (fun value -> match Int32.TryParse(value) with | true, number -> Some number | _ -> None)
        |> Option.iter (fun value -> result <- { result with TimeoutMs = clampTimeout value })
        result

    let private environment name =
        match Environment.GetEnvironmentVariable(name) with
        | null -> None
        | value -> Some value

    /// %LOCALAPPDATA%\DarbotLabs\Yumlog, or YUMLOG_INSTALL_ROOT when set (same root the installer uses).
    let dataRoot () =
        match environment "YUMLOG_INSTALL_ROOT" with
        | Some root when not (String.IsNullOrWhiteSpace root) -> root
        | _ -> Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DarbotLabs", "Yumlog")

    /// Package config: YUMLOG_CONFIG, else config\yumlog.native.json beside or one level above the app.
    let packageConfigPath () =
        match environment "YUMLOG_CONFIG" with
        | Some path when not (String.IsNullOrWhiteSpace path) -> Some path
        | _ ->
            let baseDir = AppContext.BaseDirectory
            [ Path.Combine(baseDir, "config", "yumlog.native.json")
              Path.Combine(baseDir, "..", "config", "yumlog.native.json")
              Path.Combine(Environment.CurrentDirectory, "config", "yumlog.native.json") ]
            |> List.tryFind File.Exists
            |> Option.map Path.GetFullPath

    /// Per-user settings that survive in-place upgrades: YUMLOG_SETTINGS or <dataRoot>\yumlog.settings.json.
    let userSettingsPath () =
        match environment "YUMLOG_SETTINGS" with
        | Some path when not (String.IsNullOrWhiteSpace path) -> path
        | _ -> Path.Combine(dataRoot (), "yumlog.settings.json")

    let load () =
        let fromPackage =
            match packageConfigPath () with
            | Some path -> applyFile defaults path
            | None -> defaults
        applyFile fromPackage (userSettingsPath ()) |> applyEnvironment environment

    /// Running version without the +build metadata (InformationalVersion, then AssemblyVersion).
    let currentVersion () =
        let assembly =
            match Assembly.GetEntryAssembly() with
            | null -> Assembly.GetExecutingAssembly()
            | entry -> entry
        let informational =
            match assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
            | null -> ""
            | attribute -> attribute.InformationalVersion
        let raw =
            if String.IsNullOrWhiteSpace informational then
                match assembly.GetName().Version with
                | null -> "0.0.0"
                | version -> $"{version.Major}.{version.Minor}.{max 0 version.Build}"
            else informational
        raw.Split('+').[0].Trim()

    /// Parses v?MAJOR.MINOR[.PATCH][-pre][+build]. Build metadata is ignored.
    let parseVersion (text: string) =
        if String.IsNullOrWhiteSpace text then None
        else
            let core = text.Trim().TrimStart('v', 'V').Split('+').[0]
            let dash = core.IndexOf('-')
            let numbers, pre = if dash >= 0 then core.Substring(0, dash), core.Substring(dash + 1) else core, ""
            let parts = numbers.Split('.')
            let parsed = parts |> Array.map (fun part -> match Int32.TryParse(part) with | true, n when n >= 0 -> Some n | _ -> None)
            if parts.Length < 2 || parts.Length > 3 || parsed |> Array.exists Option.isNone then None
            else
                let n = parsed |> Array.map Option.get
                Some((n[0], n[1], (if n.Length = 3 then n[2] else 0)), pre)

    /// Semver precedence: numeric core first, then a release outranks any prerelease of the same core.
    let compareVersions (left: string) (right: string) =
        match parseVersion left, parseVersion right with
        | Some(a, preA), Some(b, preB) ->
            let core = compare a b
            if core <> 0 then Some core
            elif preA = preB then Some 0
            elif preA = "" then Some 1
            elif preB = "" then Some(-1)
            else Some(String.CompareOrdinal(preA, preB) |> sign)
        | _ -> None

    /// Reads tag_name and html_url from a GitHub "latest release" response.
    let parseRelease (json: string) =
        try
            use document = JsonDocument.Parse(json)
            let root = document.RootElement
            let text (name: string) =
                let mutable value = Unchecked.defaultof<JsonElement>
                if root.ValueKind = JsonValueKind.Object && root.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.String then
                    value.GetString() |> Option.ofObj
                else None
            match text "tag_name" with
            | Some tag when not (String.IsNullOrWhiteSpace tag) -> Some(tag, text "html_url" |> Option.defaultValue "")
            | _ -> None
        with _ -> None

    let releasesPage (settings: UpdateSettings) = $"{settings.WebBase.TrimEnd('/')}/{settings.Repo}/releases"

    let private result (settings: UpdateSettings) current latest url status detail : UpdateResult =
        { Schema = ResultSchema; CheckedUtc = DateTimeOffset.UtcNow; Repo = settings.Repo; Current = current
          Latest = latest; Url = url; Status = status; Detail = detail }

    /// Turns a release (tag, url) into a result for the running version.
    let evaluate (settings: UpdateSettings) (current: string) (tag: string, url: string) =
        let link = if String.IsNullOrWhiteSpace url then releasesPage settings else url
        match compareVersions tag current with
        | Some order when order > 0 -> result settings current tag link "update-available" $"{tag} is newer than {current}"
        | Some 0 -> result settings current tag link "up-to-date" "running the latest release"
        | Some _ -> result settings current tag link "ahead" $"running {current}, newer than the latest release {tag}"
        | None -> result settings current tag link "unavailable" $"release tag '{tag}' is not a version"

    let check (settings: UpdateSettings) (current: string) : Task<UpdateResult> =
        task {
            if not settings.Enabled then
                return result settings current "" "" "disabled" "update.enabled is false"
            elif not (isValidRepo settings.Repo) then
                return result settings current "" "" "unavailable" "update.repo must be owner/name"
            else
                try
                    use cancel = new CancellationTokenSource(clampTimeout settings.TimeoutMs)
                    use client = new HttpClient(Timeout = TimeSpan.FromMilliseconds(float (clampTimeout settings.TimeoutMs)))
                    use request = new HttpRequestMessage(HttpMethod.Get, $"{settings.ApiBase.TrimEnd('/')}/repos/{settings.Repo}/releases/latest")
                    request.Headers.UserAgent.ParseAdd($"Yumlog/{current}")
                    request.Headers.Accept.ParseAdd("application/vnd.github+json")
                    request.Headers.Add("X-GitHub-Api-Version", "2022-11-28")
                    use! response = client.SendAsync(request, cancel.Token)
                    if not response.IsSuccessStatusCode then
                        return result settings current "" (releasesPage settings) "unavailable" $"HTTP {int response.StatusCode}"
                    else
                        let! body = response.Content.ReadAsStringAsync(cancel.Token)
                        match parseRelease body with
                        | Some release -> return evaluate settings current release
                        | None -> return result settings current "" (releasesPage settings) "unavailable" "no release tag in the response"
                with error ->
                    let reason =
                        match error with
                        | :? OperationCanceledException -> $"timed out after {settings.TimeoutMs} ms"
                        | _ -> error.GetType().Name
                    return result settings current "" (releasesPage settings) "unavailable" reason
        }

    let logLine (value: UpdateResult) =
        let show (text: string) = if String.IsNullOrWhiteSpace text then "-" else text
        let stamp = value.CheckedUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", Globalization.CultureInfo.InvariantCulture)
        $"{stamp} update-check repo={value.Repo} current={value.Current} latest={show value.Latest} result={value.Status} url={show value.Url} detail=\"{value.Detail}\""

    /// Appends the log line to <dataRoot>\logs\update-check.log and writes update-check.json. Never throws.
    let record (value: UpdateResult) =
        try
            let logs = Directory.CreateDirectory(Path.Combine(dataRoot (), "logs")).FullName
            File.AppendAllText(Path.Combine(logs, "update-check.log"), logLine value + Environment.NewLine)
            File.WriteAllText(Path.Combine(logs, "update-check.json"), NativeJson.serialize value)
        with _ -> ()

    let run () =
        task {
            let! value = check (load ()) (currentVersion ())
            record value
            return value
        }
