---
layout: default
title: Yumlog SDK and framework guide
---

# Yumlog SDK and framework guide

Choose the integration boundary before choosing a library. This repository
ships Windows executables, file formats, schemas, and a JSON-RPC host. No
separate `Yumlog.SDK` package is required for these examples. The standalone
Paperboy application's SDK and widget lifecycle are separate from the
components maintained here.

| Boundary | Use it for | What it does not promise |
|---|---|---|
| `Yumlog.Native.Cli` | Capture, record, analyze existing media, inspect extension declarations, produce cited runbooks | A model is not installed or ready merely because it is listed |
| `.mdkg` and versioned Agent Cards | Exchange evidence and provenance without sharing a working directory | A catalog or generated summary is not a proof of an event |
| `Paperboy.Mcp` | Discover/call tools, read resources and retrieve prompts | HTTP exposes only a read-only subset of the local stdio tools |
| `Yumlog.APIM.Service` | Inspect ACP catalogs, exchange envelopes, exercise route contracts | The local scaffold is not Azure API Management or an execution backend for every route |
| `Yumlog.WidgetStudio` | Validate, preview and package widget declarations | Previewing or packaging does not install a widget or invoke its capabilities |

## 1. Discover capabilities without recording anything

From the repository root in PowerShell on Windows, build the CLI with a
compatible .NET SDK and Windows targeting packs. The output helper derives
the target framework and runtime directory from the project:

```powershell
dotnet build .\apps\Yumlog.Native.Cli\Yumlog.Native.Cli.fsproj -c Release
$bin = .\launchers\Get-YumlogBuildOutput.ps1 -Project Yumlog.Native.Cli
$cli = Join-Path $bin 'Yumlog.Native.Cli.exe'
& $cli extensions validate --dir .\apps\Yumlog.Native\Samples
& $cli extensions list --dir .\apps\Yumlog.Native\Samples
& $cli extensions mcp --no-external
```

An extension has a versioned semantic ID and capability descriptors with a
kind, provider, status and invocation target. Start with the
[sample manifest](https://github.com/DarbotLM/yumlog/blob/main/apps/Yumlog.Native/Samples/contoso-vision-pack.extension.json).
Validate your own directory before distribution. Discovery does not load a
DLL, execute the declared command, or turn the projected MCP descriptor
into a listening server. See the [native extension contract](https://github.com/DarbotLM/yumlog/blob/main/apps/Yumlog.Native/EXTENSIBILITY.md)
for configuration, filtering, kinds, and compatibility.

## 2. Read evidence from an existing recording

Use a recording and sidecar you are authorized to read. Given an existing
`recording.mdkg`, this command produces JSON and Markdown; it does not start
a capture:

```powershell
& $cli runbook --input .\recording.mdkg --json-out .\runbook.json --markdown-out .\runbook.md
```

Keep the original sidecar with the runbook. A citation such as
`mdkg://ocr-text@333333#/ocr/text` names a layer, timestamp and JSON
pointer. Resolve it against that sidecar, not regenerated OCR. A consumer
should report an absent layer/sample/pointer as missing evidence rather
than substituting nearby text. See the
[container format](https://github.com/DarbotLM/yumlog/blob/main/apps/Yumlog.Native/hevc-swarm-out/MDKG-SPEC.md)
and [CLI reference](NATIVE-CLI.md).

## 3. Integrate an agent

Use [Agent Plugin 1.0 and MCP](AGENT-PLUGIN.md) for initialization, tool
discovery, stateless HTTP, SSE framing, path restrictions, and error handling.
For Agent Card and extension schemas, prefer `resources/list` followed by
`resources/read` on the URI returned by the running host. Treat schema IDs
as identifiers; changing a GitHub owner does not change a versioned contract.

When embedding the host in a compatible Windows .NET application, reference
`apps\Paperboy.Mcp\Paperboy.Mcp.csproj` and use the shared dispatcher. For
example, this reads schema data without opening a socket:

```csharp
using System.Text.Json.Nodes;
using Paperboy.Desktop.Mcp;

var call = new JsonObject {
    ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "resources/read",
    ["params"] = new JsonObject { ["uri"] = "paperboy://cards/schema" }
};
var reply = await PaperboyMcpServer.HandleJsonRpcAsync(call, CancellationToken.None);
if (reply?["error"] is JsonNode error)
    throw new InvalidOperationException(error["message"]?.GetValue<string>());
Console.WriteLine(reply?["result"]?["contents"]?.ToJsonString());
```

Keep the default read-only dispatch policy. A full stdio subprocess is a
separate trust decision: it can expose bundle writes, model management, and
opt-in extension invocation that HTTP does not. For a non-.NET SDK, the same
JSON objects are the wire contract; no shared assembly is needed.

## 4. Author a widget or consume an ACP contract

```powershell
$widget = '.\apps\Yumlog.WidgetStudio\Samples\paperboy-capability-mesh.widget.json'
dotnet run --project .\apps\Yumlog.WidgetStudio -c Release -- validate $widget --json
dotnet run --project .\apps\Yumlog.WidgetStudio -c Release -- capabilities $widget --json
```

Check the validation exit code before consuming the index. A `1.0` project
can still be read; `1.1` adds capability declarations. Package format,
project schema, and capability contract have independent versions. Use
the [Widget Studio guide](https://github.com/DarbotLM/yumlog/blob/main/apps/Yumlog.WidgetStudio/README.md)
for preview, ZIP export, and compatibility rules.

ACP is a separate surface, not a synonym for MCP. Its default service port
is `5118`, not the MCP port `5117`. The
[ACP/APIM contract](https://github.com/DarbotLM/yumlog/blob/main/apps/Yumlog.APIM.Service/README.md)
documents the resources, tools, prompts, route envelopes, and optional
key checks. A successful health probe does not establish that a backend is
available. The [Paperboy component guide](https://github.com/DarbotLM/yumlog/blob/main/apps/Paperboy.Desktop/README.md)
describes the separate local bundle and capability invocation contracts.

## Distribution boundaries

Run captures only with operator consent. Keep recordings, model files,
signing material, local paths, and configuration secrets out of published
artifacts. Windows AI OCR has separate OS, identity, and model requirements;
model provisioning and configured HTTP connectors can use the network.
Yumlog calls [Gen-Audio](https://github.com/DarbotLM/gen-audio) through its
CLI boundary. A verified audio receipt is evidence of that operation, not
a promise that every model runs natively in Yumlog.
