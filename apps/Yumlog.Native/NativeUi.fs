namespace Yumlog.Native

open System
open System.Drawing
open System.Globalization
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks
open System.Windows.Forms
open Microsoft.Web.WebView2.Core
open Microsoft.Web.WebView2.WinForms

module NativeUi =
    let private studioHost = "yumlog-studio.local"
    let private studioPage = $"https://{studioHost}/index.html"
    let private mediaHost = "https://yumlog-media.local/"

    let private contentType path =
        match DesktopMedia.extension path with
        | ".mp4" | ".m4v" -> "video/mp4"
        | ".mov" -> "video/quicktime"
        | ".webm" -> "video/webm"
        | ".mkv" -> "video/x-matroska"
        | ".wav" -> "audio/wav"
        | ".jpg" | ".jpeg" -> "image/jpeg"
        | ".png" -> "image/png"
        | _ -> "application/octet-stream"

    // WebView2 can seek within a stream. For a partial response, expose only the
    // requested byte window and translate seeks relative to that window.
    type private ByteWindow(file: FileStream, first: int64, length: int64) =
        inherit Stream()
        do file.Position <- first
        override _.CanRead = true
        override _.CanSeek = true
        override _.CanWrite = false
        override _.Length = length
        override this.Position
            with get () = file.Position - first
            and set value = this.Seek(value, SeekOrigin.Begin) |> ignore
        override this.Read(buffer, offset, count) =
            file.Read(buffer, offset, int (min (int64 count) (max 0L (length - this.Position))))
        override this.Seek(offset, origin) =
            let target =
                match origin with
                | SeekOrigin.Begin -> offset
                | SeekOrigin.Current -> this.Position + offset
                | SeekOrigin.End -> length + offset
                | _ -> invalidArg "origin" "Unknown seek origin."
            if target < 0L || target > length then raise (IOException("Seek is outside the response."))
            file.Seek(first + target, SeekOrigin.Begin) - first
        override _.Flush() = ()
        override _.SetLength(_) = raise (NotSupportedException())
        override _.Write(_, _, _) = raise (NotSupportedException())
        override _.Dispose(disposing) =
            if disposing then file.Dispose()
            base.Dispose(disposing)

    let private byteRange length (header: string) =
        let range = Regex.Match(header, @"^bytes=(\d*)-(\d*)$")
        let parse (value: string) =
            match Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, number -> Some number
            | _ -> None
        if not range.Success || length = 0L then None
        else
            let left, right = range.Groups[1].Value, range.Groups[2].Value
            match parse left, parse right with
            | Some first, Some last when first < length && last >= first -> Some(first, min last (length - 1L))
            | Some first, None when first < length && right = "" -> Some(first, length - 1L)
            | None, Some suffix when left = "" && suffix > 0L -> Some(max 0L (length - suffix), length - 1L)
            | _ -> None

    let private serveMedia (workspace: DesktopLibrary.Workspace) (core: CoreWebView2) (event: CoreWebView2WebResourceRequestedEventArgs) =
        let request = event.Request
        let respond stream status reason headers =
            event.Response <- core.Environment.CreateWebResourceResponse(stream, status, reason, headers)
        let baseHeaders =
            $"Access-Control-Allow-Origin: https://{studioHost}\r\nAccept-Ranges: bytes\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff"
        let id = Uri(request.Uri).AbsolutePath.TrimStart('/')
        let send path =
            let file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
            let size = file.Length
            let mime = $"Content-Type: {contentType path}"
            let partial = request.Headers.Contains("Range")
            let range = if partial then byteRange size (request.Headers.GetHeader("Range")) else Some(0L, size - 1L)
            match range with
            | None ->
                file.Dispose()
                respond Stream.Null 416 "Range Not Satisfiable" $"{baseHeaders}\r\nContent-Range: bytes */{size}"
            | Some(first, last) ->
                let length = last - first + 1L
                let headers = $"{baseHeaders}\r\n{mime}\r\nContent-Length: {length}"
                let stream =
                    if request.Method = "HEAD" then
                        file.Dispose()
                        Stream.Null
                    else new ByteWindow(file, first, length) :> Stream
                try
                    if partial then
                        respond stream 206 "Partial Content" $"{headers}\r\nContent-Range: bytes {first}-{last}/{size}"
                    else respond stream 200 "OK" headers
                with _ ->
                    stream.Dispose()
                    reraise ()
        if request.Method <> "GET" && request.Method <> "HEAD" then
            respond Stream.Null 405 "Method Not Allowed" baseHeaders
        else
            match workspace.TryGetMedia(id) with
            | Some path ->
                try send path
                with
                | :? IOException
                | :? UnauthorizedAccessException -> respond Stream.Null 404 "Not Found" baseHeaders
            | None -> respond Stream.Null 404 "Not Found" baseHeaders

    let internal runWorkspace appRoot =
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2) |> ignore
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(false)
        let workspace = DesktopLibrary.Workspace(appRoot)
        use form = new Form(Text = "Yumlog Studio", StartPosition = FormStartPosition.CenterScreen,
                            MinimumSize = Size(460, 600), Size = Size(1350, 900), BackColor = Color.FromArgb(17, 20, 25))
        use browser = new WebView2(Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(17, 20, 25))
        form.Controls.Add(browser)
        let mutable busy = false

        let chooseFiles multi filter =
            use dialog = new OpenFileDialog(Multiselect = multi, Filter = filter, CheckFileExists = true)
            if dialog.ShowDialog(form) = DialogResult.OK then Some dialog.FileNames else None

        let chooseOutput name filter =
            use dialog = new SaveFileDialog(FileName = name, Filter = filter, OverwritePrompt = true, AddExtension = true)
            if dialog.ShowDialog(form) = DialogResult.OK then Some [| dialog.FileName |] else None

        let choose command payload =
            let artifactId = DesktopLibrary.field "artifactId" payload
            match command with
            | "import" ->
                let extensions = DesktopMedia.videoExtensions |> Seq.map (fun item -> "*" + item) |> String.concat ";"
                chooseFiles true $"Video recordings|{extensions}"
            | "attachDocument" -> chooseFiles false "Schema documents (*.txt;*.md;*.json;*.csv)|*.txt;*.md;*.json;*.csv"
            | "importAnalysis" -> chooseFiles false "Source verification manifest (source-sha256.json)|source-sha256.json"
            | "attachTranscript" -> chooseFiles false "Transcript files (*.srt;*.vtt;*.txt)|*.srt;*.vtt;*.txt"
            | "exportCard" -> chooseOutput (workspace.SuggestedName(command, artifactId)) "Paperboy card (*.json)|*.json"
            | "bundle" -> chooseOutput (workspace.SuggestedName(command, artifactId)) "Paperboy bundle (*.zip)|*.zip"
            | "exportArtifact" ->
                let name = workspace.SuggestedName(command, artifactId)
                let extension = DesktopMedia.extension name
                chooseOutput name $"Output file (*{extension})|*{extension}"
            | _ -> Some [||]

        let onMessage (core: CoreWebView2) (args: CoreWebView2WebMessageReceivedEventArgs) =
            if String.Equals(args.Source, studioPage, StringComparison.Ordinal) then
                task {
                    use document = JsonDocument.Parse(args.WebMessageAsJson)
                    let payload = document.RootElement
                    let command = DesktopLibrary.field "command" payload
                    let requestId = DesktopLibrary.field "requestId" payload
                    let post response = core.PostWebMessageAsJson(NativeJson.serialize response)
                    if busy then
                        post {| requestId = requestId; error = "Another tool is running." |}
                    else
                        busy <- true
                        let previous = form.WindowState
                        let capture = command = "capture" || command = "record"
                        try
                            try
                                if command = "copyCard" || command = "copySchema" then
                                    let text =
                                        if command = "copyCard" then
                                            workspace.Snapshot().paperboy
                                            |> Option.map NativeJson.serialize
                                            |> Option.defaultWith (fun () -> invalidOp "Open a recording first.")
                                        else File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Studio", "paperboy-video-card.schema.json"))
                                    Clipboard.SetText(text)
                                    post {| requestId = requestId; message = "Copied to clipboard." |}
                                else
                                    match choose command payload with
                                    | None -> post {| requestId = requestId; cancelled = true |}
                                    | Some paths ->
                                        if capture then
                                            form.WindowState <- FormWindowState.Minimized
                                            do! Task.Delay(500)
                                        let! result, state =
                                            Task.Run(fun () ->
                                                let result = workspace.Run(command, payload, paths)
                                                result, workspace.Snapshot())
                                        post {| requestId = requestId; message = result.message; data = result.data; state = state |}
                            with error ->
                                post {| requestId = requestId; error = error.Message |}
                        finally
                            if capture then form.WindowState <- previous
                            busy <- false
                } |> ignore

        form.FormClosing.Add(fun event ->
            if busy && event.CloseReason = CloseReason.UserClosing then
                event.Cancel <- true
                MessageBox.Show(form, "Wait for the current tool to finish before closing Yumlog.", "Yumlog Studio", MessageBoxButtons.OK, MessageBoxIcon.Information) |> ignore)

        form.Shown.Add(fun _ ->
            task {
                try
                    let! environment = CoreWebView2Environment.CreateAsync(null, Path.Combine(appRoot, "WebView2"))
                    do! browser.EnsureCoreWebView2Async(environment)
                    let core = browser.CoreWebView2 |> nonNull
                    core.Settings.AreDevToolsEnabled <- false
                    core.Settings.AreDefaultContextMenusEnabled <- false
                    core.Settings.IsStatusBarEnabled <- false
                    core.Settings.AreHostObjectsAllowed <- false
                    core.NavigationStarting.Add(fun event -> event.Cancel <- not (String.Equals(event.Uri, studioPage, StringComparison.Ordinal)))
                    core.NewWindowRequested.Add(fun event -> event.Handled <- true)
                    core.PermissionRequested.Add(fun event -> event.State <- CoreWebView2PermissionState.Deny)
                    core.SetVirtualHostNameToFolderMapping(studioHost, Path.Combine(AppContext.BaseDirectory, "Studio"), CoreWebView2HostResourceAccessKind.Deny)
                    core.AddWebResourceRequestedFilter(mediaHost + "*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.Document)
                    core.WebResourceRequested.Add(serveMedia workspace core)
                    core.WebMessageReceived.Add(onMessage core)
                    core.Navigate(studioPage)
                with error ->
                    MessageBox.Show(form, $"Could not open Yumlog Studio. Check that Microsoft Edge WebView2 Runtime is installed.\n\n{error.Message}",
                                    "Yumlog Studio", MessageBoxButtons.OK, MessageBoxIcon.Error) |> ignore
                    form.Close()
            } |> ignore)

        Application.Run(form)

    let run () =
        let localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        runWorkspace (Path.Combine(localAppData, "DarbotLabs", "Yumlog", "Studio"))
