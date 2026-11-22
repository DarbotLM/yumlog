namespace Yumlog.Native

open System
open System.Diagnostics
open System.Drawing
open System.Drawing.Imaging
open System.Globalization
open System.IO
open System.Management
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open Microsoft.Win32

/// Opens Windows Settings > System > About, captures that window with Yumlog's own GDI
/// capture path, and writes a tag sidecar with the machine specs read from WMI/CIM and the
/// registry. Every knob comes from the "specs" section of config\yumlog.native.json, the
/// per-user yumlog.settings.json and YUMLOG_SPECS_* environment variables.
module SystemSpecs =
    [<Literal>]
    let SidecarSchema = "yumlog.system-specs/1.0"

    type SpecsSettings =
        { /// Shell URI that opens the page to capture.
          SettingsUri: string
          /// Top-level window title and class that host the page.
          WindowTitle: string
          WindowClass: string
          /// How long to wait for the window, and how long to let it render after it appears.
          WaitMs: int
          SettleMs: int
          Maximize: bool
          /// auto (PrintWindow, then screen copy), printwindow, or screen.
          CaptureMethod: string
          /// Folder name under the Studio output directory; ignored when an explicit folder is passed.
          LibraryFolder: string
          FileStem: string
          CloseAfter: bool }

    let defaults =
        { SettingsUri = "ms-settings:about"
          WindowTitle = "Settings"
          WindowClass = "ApplicationFrameWindow"
          WaitMs = 20000
          SettleMs = 2500
          Maximize = true
          CaptureMethod = "auto"
          LibraryFolder = "SystemSpecs"
          FileStem = "system-specs"
          CloseAfter = false }

    let private clamp low high value = max low (min high value)

    let applyJson (settings: SpecsSettings) (json: string) =
        try
            use document = JsonDocument.Parse(json, JsonDocumentOptions(CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true))
            let mutable section = Unchecked.defaultof<JsonElement>
            let found =
                document.RootElement.ValueKind = JsonValueKind.Object
                && (document.RootElement.TryGetProperty("specs", &section) || document.RootElement.TryGetProperty("Specs", &section))
            if not found || section.ValueKind <> JsonValueKind.Object then settings
            else
                let mutable result = settings
                for property in section.EnumerateObject() do
                    let value = property.Value
                    let text () = value.GetString() |> nonNull
                    match property.Name.ToLowerInvariant(), value.ValueKind with
                    | "settingsuri", JsonValueKind.String -> result <- { result with SettingsUri = text () }
                    | "windowtitle", JsonValueKind.String -> result <- { result with WindowTitle = text () }
                    | "windowclass", JsonValueKind.String -> result <- { result with WindowClass = text () }
                    | "waitms", JsonValueKind.Number -> result <- { result with WaitMs = clamp 1000 120000 (value.GetInt32()) }
                    | "settlems", JsonValueKind.Number -> result <- { result with SettleMs = clamp 0 30000 (value.GetInt32()) }
                    | "maximize", (JsonValueKind.True | JsonValueKind.False) -> result <- { result with Maximize = value.GetBoolean() }
                    | "capturemethod", JsonValueKind.String -> result <- { result with CaptureMethod = text () }
                    | "libraryfolder", JsonValueKind.String -> result <- { result with LibraryFolder = text () }
                    | "filestem", JsonValueKind.String -> result <- { result with FileStem = text () }
                    | "closeafter", (JsonValueKind.True | JsonValueKind.False) -> result <- { result with CloseAfter = value.GetBoolean() }
                    | _ -> ()
                result
        with _ -> settings

    let private applyFile (settings: SpecsSettings) (path: string) =
        if String.IsNullOrWhiteSpace path || not (File.Exists path) then settings
        else try applyJson settings (File.ReadAllText path) with _ -> settings

    let private environment name =
        match Environment.GetEnvironmentVariable(name) with
        | null -> None
        | value when String.IsNullOrWhiteSpace value -> None
        | value -> Some(value.Trim())

    /// YUMLOG_SPECS_URI, YUMLOG_SPECS_WINDOW_TITLE, YUMLOG_SPECS_WINDOW_CLASS, YUMLOG_SPECS_WAIT_MS,
    /// YUMLOG_SPECS_SETTLE_MS and YUMLOG_SPECS_CAPTURE_METHOD override the files.
    let applyEnvironment (settings: SpecsSettings) =
        let number name = environment name |> Option.bind (fun v -> match Int32.TryParse(v) with | true, n -> Some n | _ -> None)
        { settings with
            SettingsUri = environment "YUMLOG_SPECS_URI" |> Option.defaultValue settings.SettingsUri
            WindowTitle = environment "YUMLOG_SPECS_WINDOW_TITLE" |> Option.defaultValue settings.WindowTitle
            WindowClass = environment "YUMLOG_SPECS_WINDOW_CLASS" |> Option.defaultValue settings.WindowClass
            WaitMs = number "YUMLOG_SPECS_WAIT_MS" |> Option.map (clamp 1000 120000) |> Option.defaultValue settings.WaitMs
            SettleMs = number "YUMLOG_SPECS_SETTLE_MS" |> Option.map (clamp 0 30000) |> Option.defaultValue settings.SettleMs
            CaptureMethod = environment "YUMLOG_SPECS_CAPTURE_METHOD" |> Option.defaultValue settings.CaptureMethod }

    let load () =
        let fromPackage =
            match UpdateCheck.packageConfigPath () with
            | Some path -> applyFile defaults path
            | None -> defaults
        applyFile fromPackage (UpdateCheck.userSettingsPath ()) |> applyEnvironment

    // ----------------------------------------------------------------------------------
    // Specs from WMI/CIM and the registry.

    type GpuSpec = { Name: string; DriverVersion: string; MemoryGb: float }
    type DiskSpec = { Model: string; SizeGb: float; MediaType: string; BusType: string }
    type VolumeSpec = { Drive: string; Label: string; FileSystem: string; SizeGb: float; FreeGb: float }

    type MachineSpecs =
        { DeviceName: string
          Manufacturer: string
          Model: string
          Processor: string
          Cores: int
          LogicalProcessors: int
          MaxClockMHz: int
          InstalledRamGb: float
          UsableRamGb: float
          SystemType: string
          OsEdition: string
          OsVersion: string
          OsBuild: string
          OsArchitecture: string
          Gpus: GpuSpec array
          Disks: DiskSpec array
          Volumes: VolumeSpec array }

    let private gb (bytes: float) = Math.Round(bytes / 1073741824.0, 1)

    let private rows (cimClass: string) (properties: string list) (where: string) =
        try
            let wql = $"""SELECT {String.Join(",", properties)} FROM {cimClass}{(if where = "" then "" else " WHERE " + where)}"""
            use searcher = new ManagementObjectSearcher(wql)
            use results = searcher.Get()
            [| for item in results do
                 use item = item
                 yield
                     properties
                     |> List.map (fun name ->
                         let value =
                             match item[name] with
                             | null -> ""
                             | value -> Convert.ToString(value, CultureInfo.InvariantCulture) |> Option.ofObj |> Option.defaultValue ""
                         name, value.Trim())
                     |> Map.ofList |]
        with _ -> [||]

    let private field name (row: Map<string, string>) = row |> Map.tryFind name |> Option.defaultValue ""
    let private number name row = match Double.TryParse(field name row, NumberStyles.Float, CultureInfo.InvariantCulture) with | true, v -> v | _ -> 0.0
    let private first (table: Map<string, string> array) = table |> Array.tryHead |> Option.defaultValue Map.empty

    let private registryText (key: string) (name: string) =
        try
            match Registry.GetValue(key, name, null) with
            | null -> ""
            | value -> Convert.ToString(value, CultureInfo.InvariantCulture) |> Option.ofObj |> Option.defaultValue ""
        with _ -> ""

    /// AdapterRAM is a 32-bit value capped at 4 GB; the display class key holds the real 64-bit size.
    let private gpuMemoryGb (name: string) (fallback: float) =
        try
            use classKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}")
            match classKey with
            | null -> fallback
            | classKey ->
                classKey.GetSubKeyNames()
                |> Array.tryPick (fun sub ->
                    try
                        use key = classKey.OpenSubKey(sub)
                        match key with
                        | null -> None
                        | key ->
                            let description = key.GetValue("DriverDesc") |> Option.ofObj |> Option.map string |> Option.defaultValue ""
                            if not (String.Equals(description, name, StringComparison.OrdinalIgnoreCase)) then None
                            else
                                match key.GetValue("HardwareInformation.qwMemorySize") with
                                | :? int64 as size when size > 0L -> Some(gb (float size))
                                | :? (byte array) as bytes when bytes.Length >= 8 -> Some(gb (float (BitConverter.ToInt64(bytes, 0))))
                                | _ -> None
                    with _ -> None)
                |> Option.defaultValue fallback
        with _ -> fallback

    let collect () =
        let system = rows "Win32_ComputerSystem" [ "Name"; "Manufacturer"; "Model"; "SystemType"; "TotalPhysicalMemory" ] "" |> first
        let cpu = rows "Win32_Processor" [ "Name"; "NumberOfCores"; "NumberOfLogicalProcessors"; "MaxClockSpeed" ] ""
        let os = rows "Win32_OperatingSystem" [ "Caption"; "Version"; "BuildNumber"; "OSArchitecture" ] "" |> first
        let memory = rows "Win32_PhysicalMemory" [ "Capacity" ] ""
        let video = rows "Win32_VideoController" [ "Name"; "DriverVersion"; "AdapterRAM" ] ""
        let drives = rows "Win32_DiskDrive" [ "Model"; "Size"; "MediaType"; "InterfaceType" ] ""
        let volumes = rows "Win32_LogicalDisk" [ "DeviceID"; "VolumeName"; "FileSystem"; "Size"; "FreeSpace" ] "DriveType=3"
        let currentVersion = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
        let displayVersion = registryText currentVersion "DisplayVersion"
        let build =
            let current = registryText currentVersion "CurrentBuild"
            let ubr = registryText currentVersion "UBR"
            let core = if current = "" then field "BuildNumber" os else current
            if ubr = "" then core else $"{core}.{ubr}"
        let installed = memory |> Array.sumBy (number "Capacity")
        let usable = number "TotalPhysicalMemory" system
        let architecture = field "OSArchitecture" os
        let platform = (field "SystemType" system).Replace(" PC", "", StringComparison.OrdinalIgnoreCase)
        let deviceName = match field "Name" system with "" -> Environment.MachineName | name -> name
        { DeviceName = deviceName
          Manufacturer = field "Manufacturer" system
          Model = field "Model" system
          Processor = cpu |> Array.map (field "Name") |> Array.distinct |> String.concat "; "
          Cores = cpu |> Array.sumBy (number "NumberOfCores") |> int
          LogicalProcessors = match cpu |> Array.sumBy (number "NumberOfLogicalProcessors") |> int with 0 -> Environment.ProcessorCount | n -> n
          MaxClockMHz = cpu |> Array.map (number "MaxClockSpeed") |> Array.fold max 0.0 |> int
          InstalledRamGb = gb (if installed > 0.0 then installed else usable)
          UsableRamGb = gb usable
          SystemType = (if architecture = "" || platform = "" then field "SystemType" system else $"{architecture} operating system, {platform} processor")
          OsEdition = (field "Caption" os).Replace("Microsoft ", "", StringComparison.Ordinal)
          OsVersion = displayVersion
          OsBuild = build
          OsArchitecture = architecture
          Gpus =
            video |> Array.map (fun row ->
                let name = field "Name" row
                { Name = name; DriverVersion = field "DriverVersion" row; MemoryGb = gpuMemoryGb name (gb (number "AdapterRAM" row)) })
          Disks = drives |> Array.map (fun row -> { Model = field "Model" row; SizeGb = gb (number "Size" row); MediaType = field "MediaType" row; BusType = field "InterfaceType" row })
          Volumes =
            volumes |> Array.map (fun row ->
                { Drive = field "DeviceID" row; Label = field "VolumeName" row; FileSystem = field "FileSystem" row
                  SizeGb = gb (number "Size" row); FreeGb = gb (number "FreeSpace" row) }) }

    /// Flat, searchable tags for the Library (lower-case key:value pairs).
    let tags (specs: MachineSpecs) =
        let tag (key: string) (value: string) = if String.IsNullOrWhiteSpace value then None else Some($"{key}:{value.Trim()}")
        [| Some "system-specs"; Some "windows-settings"; Some "settings-about"
           tag "device" specs.DeviceName
           tag "manufacturer" specs.Manufacturer
           tag "model" specs.Model
           tag "cpu" specs.Processor
           tag "ram-gb" (string specs.InstalledRamGb)
           tag "os" specs.OsEdition
           tag "os-version" specs.OsVersion
           tag "os-build" specs.OsBuild
           tag "arch" specs.OsArchitecture |]
        |> Array.append (specs.Gpus |> Array.map (fun gpu -> tag "gpu" gpu.Name))
        |> Array.choose id
        |> Array.distinct

    // ----------------------------------------------------------------------------------
    // Window discovery and capture.

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private RECT =
        val mutable Left: int
        val mutable Top: int
        val mutable Right: int
        val mutable Bottom: int

    type private EnumWindowsProc = delegate of nativeint * nativeint -> bool

    module private Win32 =
        [<DllImport("user32.dll")>]
        extern bool EnumWindows(EnumWindowsProc callback, nativeint lParam)
        [<DllImport("user32.dll", CharSet = CharSet.Unicode)>]
        extern int GetWindowText(nativeint hWnd, StringBuilder text, int count)
        [<DllImport("user32.dll", CharSet = CharSet.Unicode)>]
        extern int GetClassName(nativeint hWnd, StringBuilder text, int count)
        [<DllImport("user32.dll")>]
        extern bool IsWindowVisible(nativeint hWnd)
        [<DllImport("user32.dll")>]
        extern bool ShowWindow(nativeint hWnd, int command)
        [<DllImport("user32.dll")>]
        extern bool SetForegroundWindow(nativeint hWnd)
        [<DllImport("user32.dll")>]
        extern bool BringWindowToTop(nativeint hWnd)
        [<DllImport("user32.dll")>]
        extern bool GetWindowRect(nativeint hWnd, RECT& rect)
        [<DllImport("user32.dll")>]
        extern bool PrintWindow(nativeint hWnd, nativeint hdc, uint32 flags)
        [<DllImport("user32.dll")>]
        extern bool PostMessage(nativeint hWnd, uint32 msg, nativeint wParam, nativeint lParam)
        [<DllImport("dwmapi.dll")>]
        extern int DwmGetWindowAttribute(nativeint hWnd, int attribute, RECT& value, int size)

    let private text (getter: nativeint * StringBuilder * int -> int) hwnd =
        let builder = StringBuilder(512)
        getter (hwnd, builder, builder.Capacity) |> ignore
        builder.ToString()

    let private findWindow (settings: SpecsSettings) =
        let mutable found = 0n
        let callback =
            EnumWindowsProc(fun hwnd _ ->
                if Win32.IsWindowVisible(hwnd)
                   && String.Equals(text (fun (h, b, c) -> Win32.GetWindowText(h, b, c)) hwnd, settings.WindowTitle, StringComparison.OrdinalIgnoreCase)
                   && (String.IsNullOrWhiteSpace settings.WindowClass
                       || String.Equals(text (fun (h, b, c) -> Win32.GetClassName(h, b, c)) hwnd, settings.WindowClass, StringComparison.Ordinal)) then
                    found <- hwnd
                    false
                else true)
        Win32.EnumWindows(callback, 0n) |> ignore
        GC.KeepAlive(callback)
        if found = 0n then None else Some found

    let private bounds hwnd =
        // DWMWA_EXTENDED_FRAME_BOUNDS excludes the invisible resize border; GetWindowRect is the fallback.
        let mutable rect = RECT()
        let ok = Win32.DwmGetWindowAttribute(hwnd, 9, &rect, Marshal.SizeOf<RECT>()) = 0
        if not ok then Win32.GetWindowRect(hwnd, &rect) |> ignore
        Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)

    /// A capture is blank when a sparse grid of samples has (almost) one colour.
    let private looksBlank (bitmap: Bitmap) =
        let colours = Collections.Generic.HashSet<int>()
        for y in 0 .. max 1 (bitmap.Height / 24) .. bitmap.Height - 1 do
            for x in 0 .. max 1 (bitmap.Width / 24) .. bitmap.Width - 1 do
                colours.Add(bitmap.GetPixel(x, y).ToArgb()) |> ignore
        colours.Count < 8

    let private printWindow hwnd (area: Rectangle) =
        let mutable window = RECT()
        Win32.GetWindowRect(hwnd, &window) |> ignore
        let full = new Bitmap(max 1 (window.Right - window.Left), max 1 (window.Bottom - window.Top), PixelFormat.Format32bppArgb)
        let ok =
            use graphics = Graphics.FromImage(full)
            let hdc = graphics.GetHdc()
            try Win32.PrintWindow(hwnd, hdc, 2u) // PW_RENDERFULLCONTENT
            finally graphics.ReleaseHdc(hdc)
        if not ok then
            full.Dispose()
            None
        else
            // Crop the invisible border so both capture methods frame the same pixels.
            let crop = Rectangle(area.Left - window.Left, area.Top - window.Top, area.Width, area.Height)
            let crop = Rectangle.Intersect(crop, Rectangle(0, 0, full.Width, full.Height))
            let cropped = full.Clone(crop, PixelFormat.Format32bppArgb)
            full.Dispose()
            Some cropped

    let private screenCopy (area: Rectangle) =
        let bitmap = new Bitmap(max 1 area.Width, max 1 area.Height, PixelFormat.Format32bppArgb)
        use graphics = Graphics.FromImage(bitmap)
        graphics.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy)
        bitmap

    type CaptureReceipt =
        { Path: string
          FileName: string
          Width: int
          Height: int
          Bytes: int64
          Sha256: string
          Method: string }

    type Sidecar =
        { Schema: string
          CapturedUtc: DateTimeOffset
          YumlogVersion: string
          Source: {| Uri: string; WindowTitle: string; WindowClass: string |}
          Screenshot: CaptureReceipt
          Tags: string array
          Specs: MachineSpecs }

    let private sha256 (path: string) =
        use stream = File.OpenRead(path)
        Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()

    let private openPage (settings: SpecsSettings) =
        if not (settings.SettingsUri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)) then
            invalidArg "specs.settingsUri" "specs.settingsUri must be an ms-settings: URI."
        use launched = Process.Start(ProcessStartInfo(settings.SettingsUri, UseShellExecute = true))
        ()

    /// Opens the page, waits for its window, frames it and saves <stem>-<stamp>.png in outDir.
    let captureWindow (settings: SpecsSettings) (outDir: string) =
        openPage settings
        let deadline = DateTime.UtcNow.AddMilliseconds(float settings.WaitMs)
        let rec wait () =
            match findWindow settings with
            | Some hwnd -> hwnd
            | None when DateTime.UtcNow < deadline ->
                Thread.Sleep(250)
                wait ()
            | None -> raise (TimeoutException($"No visible '{settings.WindowTitle}' window ({settings.WindowClass}) within {settings.WaitMs} ms."))
        let hwnd = wait ()
        Win32.ShowWindow(hwnd, (if settings.Maximize then 3 else 9)) |> ignore // SW_MAXIMIZE or SW_RESTORE
        Win32.BringWindowToTop(hwnd) |> ignore
        Win32.SetForegroundWindow(hwnd) |> ignore
        Thread.Sleep(settings.SettleMs)
        let area = bounds hwnd
        if area.Width < 64 || area.Height < 64 then invalidOp $"The {settings.WindowTitle} window is too small to capture ({area.Width}x{area.Height})."
        let method = settings.CaptureMethod.Trim().ToLowerInvariant()
        let bitmap, used =
            let viaPrint () = printWindow hwnd area |> Option.filter (fun image -> if looksBlank image then (image.Dispose(); false) else true)
            match method with
            | "screen" -> screenCopy area, "screen"
            | "printwindow" ->
                match printWindow hwnd area with
                | Some image -> image, "printwindow"
                | None -> invalidOp "PrintWindow failed."
            | "auto" ->
                match viaPrint () with
                | Some image -> image, "printwindow"
                | None -> screenCopy area, "screen"
            | other -> invalidArg "specs.captureMethod" $"Unknown capture method '{other}' (auto, printwindow, screen)."
        use bitmap = bitmap
        if looksBlank bitmap then invalidOp "The captured Settings window is blank; is the desktop session locked?"
        Directory.CreateDirectory(outDir) |> ignore
        let stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
        let path = Path.GetFullPath(Path.Combine(outDir, $"{settings.FileStem}-{stamp}.png"))
        bitmap.Save(path, ImageFormat.Png)
        if settings.CloseAfter then Win32.PostMessage(hwnd, 0x0010u, 0n, 0n) |> ignore // WM_CLOSE
        { Path = path; FileName = orEmptyIfNull (Path.GetFileName(path)); Width = bitmap.Width; Height = bitmap.Height
          Bytes = FileInfo(path).Length; Sha256 = sha256 path; Method = used }

    /// Full flow: capture, collect specs, write <png>.specs.json beside the image. Returns (sidecar, sidecarPath).
    let captureAndTag (settings: SpecsSettings) (outDir: string) =
        let screenshot = captureWindow settings outDir
        let specs = collect ()
        let sidecar =
            { Schema = SidecarSchema
              CapturedUtc = DateTimeOffset.UtcNow
              YumlogVersion = UpdateCheck.currentVersion ()
              Source = {| Uri = settings.SettingsUri; WindowTitle = settings.WindowTitle; WindowClass = settings.WindowClass |}
              Screenshot = screenshot
              Tags = tags specs
              Specs = specs }
        let sidecarPath = orEmptyIfNull (Path.ChangeExtension(screenshot.Path, ".specs.json"))
        NativeJson.write sidecarPath sidecar
        sidecar, sidecarPath
