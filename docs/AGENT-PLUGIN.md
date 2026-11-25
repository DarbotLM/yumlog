---
layout: default
title: Agent Plugin 1.0 and MCP
---

# Agent Plugin 1.0 and MCP

One Windows executable, `Paperboy.Mcp`, exposes several interfaces. Use
stdio for a trusted local subprocess, stateless Streamable HTTP for a new
HTTP client, and legacy SSE only for a client that needs it. The public
GitHub Pages URL is **not** a host for any of these interfaces.

| Interface | Discovery / endpoint | Lifetime |
|---|---|---|
| Agent Plugin 1.0 | `GET /.well-known/ai-plugin.json` or `GET /agent-plugin/v1`, then follow `api.url` to `/openapi.json` | Local v1 manifest and OpenAPI 3.1; not a remote plugin marketplace |
| Streamable HTTP | `POST /mcp`, `Content-Type: application/json` | Independent requests; no session creation, cookies, or `Mcp-Session-Id` required |
| SSE over Streamable HTTP | `POST /mcp` accepting `text/event-stream`; optional `GET /mcp` for a keepalive stream | POST responses belong to the POST; the separate GET is not a mailbox or resumable event log |
| Legacy HTTP + SSE | `GET /sse`, then POST JSON-RPC to the URL in its `endpoint` event | A temporary channel per connection; responses arrive as `message` events on that connection |
| stdio | Launch `Paperboy.Mcp.exe --ndjson` | One JSON-RPC object per UTF-8 line; no HTTP listener |

Plugin version `1.0`, OpenAPI version `3.1.0`, and MCP protocol dates are
different version numbers. This host negotiates `2025-06-18`, `2025-03-26`,
or `2024-11-05`; inspect `initialize.result.protocolVersion` and decline
an unsupported fallback before calling a tool.

## Start with stdio

The following discovery-only example runs from the repository root. It does
not bind a port, call Foundry, or read a recording:

```powershell
dotnet build .\apps\Paperboy.Mcp\Paperboy.Mcp.csproj -c Release
$bin = .\launchers\Get-YumlogBuildOutput.ps1 -Project Paperboy.Mcp
$mcp = Join-Path $bin 'Paperboy.Mcp.exe'
@(
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"docs-client","version":"1.0"}}}'
    '{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}'
    '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
) | & $mcp --ndjson
```

Expect responses with IDs `1` and `2`, not a response to the notification.
In an interactive client, wait for initialization before sending the
notification. Route logs to stderr, not into the JSON-RPC input. The host
also has `--content-length` compatibility for clients using the older
framing convention.

## HTTP client sequence

An operator who chooses HTTP starts the same executable with `--http`. Its
default base is `http://127.0.0.1:5117`. It must remain local; no API key
is advertised by the plugin manifest. Do not expose it through a reverse
proxy or point a cloud plugin installer at it.

1. Fetch the plugin manifest and follow its OpenAPI URL, or connect directly
   to `/mcp`. `GET /health` describes the host, not model readiness.
2. POST the `initialize` message above to `/mcp`. A Streamable HTTP client
   should accept both `application/json` and `text/event-stream` and parse
   the returned `Content-Type`. For a JSON-only integration, explicitly
   request `application/json`.
3. POST `notifications/initialized` without an `id`; expect HTTP `202` and
   no body. Use distinct IDs for later requests and match replies by ID.
4. Discover the HTTP tool names with `tools/list`. `resources/list` and
   `prompts/list` provide the names/URIs available from this build.
   For example:

   ```json
   {"jsonrpc":"2.0","id":3,"method":"resources/read","params":{"uri":"paperboy://cards/schema"}}
   ```

5. To exercise SSE rather than JSON, POST a `ping` request with `Accept:
   text/event-stream`. An event has `event: message` and one or more `data:`
   lines followed by a blank line. Join its data lines with a newline,
   then decode the JSON-RPC object. Ignore `:` comments. Do not try to
   parse the entire response as one JSON document.

For legacy SSE, keep the original `GET /sse` open, resolve the announced
relative `/message?sessionId=...` against the **same origin**, and POST
requests there. HTTP `202` acknowledges the POST; read the reply from
the original stream. Reconnect and use the newly announced endpoint after
disconnect. Do not send the legacy session ID to stateless `/mcp`.

## Tools, tagging and error handling

HTTP supports bundle inspection, Foundry model cards and analysis, Agent Card
listing and schemas, and capability/extension discovery. It does **not**
offer bundle pack/unpack, model installation/load, or extension invocation.
Use `tools/list` rather than assuming the broader stdio catalog applies.

`POST /api/tag` is the plugin convenience operation for
`paperboy.foundryLocal.analyze`. Pass `sourcePaths` and `model`; the response
is a JSON-RPC tool result, not a raw tag array. The HTTP form has no
`outputDirectory` and does not write sidecars. `endpoint`, if supplied,
must be a loopback HTTP Foundry endpoint. The default roots are `Yumlog
Native` and `Yumlog Projects` under Documents and `DarbotLabs\Yumlog\Studio`
under LocalAppData. `YUMLOG_AGENT_ALLOWED_ROOTS` adds semicolon-separated
operator-approved paths. Grant only directories/files appropriate for the
calling agent, not a drive root. Media is never posted to the Pages site.

Check three layers independently: HTTP status and media type, JSON-RPC
`error`, then `result.isError` and the returned content/receipts. `202` for
a notification is not a tool result. Read `result.content` for tagging
details and hashes; do not report success from HTTP `200` alone. Clients
must handle rejected paths, unavailable Foundry models, unsupported methods,
and interrupted streams without retrying a different transport's mutations.

[SDK and framework guide](SDK-FRAMEWORK.md) ·
[HTTP implementation](https://github.com/DarbotLM/yumlog/blob/main/apps/Paperboy.Mcp/PaperboyMcpHttpHost.cs) ·
[Shared dispatcher and tool schemas](https://github.com/DarbotLM/yumlog/blob/main/apps/Paperboy.Desktop/Mcp/PaperboyMcpServer.cs)
