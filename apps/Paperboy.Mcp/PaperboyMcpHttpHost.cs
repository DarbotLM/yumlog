using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Paperboy.Desktop.Mcp;

public static class PaperboyMcpHttpHost
{
    public const string DefaultUrl = "http://127.0.0.1:5117";
    private const string Json = "application/json";
    private const string Sse = "text/event-stream";
    private const string VersionHeader = "MCP-Protocol-Version";
    private static readonly TimeSpan Keepalive = TimeSpan.FromSeconds(15);
    private static readonly MediaTypeHeaderValue JsonMedia = MediaTypeHeaderValue.Parse("application/json; charset=utf-8");
    private static readonly MediaTypeHeaderValue SseMedia = MediaTypeHeaderValue.Parse("text/event-stream; charset=utf-8");
    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowDuplicateProperties = false };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, Channel<JsonObject>> Streams = new(StringComparer.Ordinal);

    public static async Task RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        var url = ArgumentValue(args, "--url") ?? DefaultUrl;
        var origin = new Uri(url);
        if (!origin.IsLoopback || origin.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException("The MCP HTTP transport must bind to an HTTP loopback origin.");
        }

        var builder = WebApplication.CreateSlimBuilder(args);
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        builder.WebHost.UseUrls(url);
        builder.WebHost.ConfigureKestrel(options =>
            options.Limits.MaxRequestBodySize = 12 * 1024 * 1024);

        var app = builder.Build();
        app.Use(RequireLocalOriginAsync);

        app.MapGet("/health", () => Results.Ok(new
        {
            service = "Paperboy.MCP",
            status = "healthy",
            transports = new[] { "stdio", "streamable-http", "sse", "stateless" },
            agentPluginVersion = "1.0",
            utc = DateTimeOffset.UtcNow
        }));

        app.MapPost("/mcp", (Delegate)HandleStreamablePostAsync);
        app.MapGet("/mcp", HandleStreamableSseAsync);

        app.MapGet("/sse", HandleLegacySseAsync);
        app.MapPost("/message", (Delegate)HandleLegacyMessageAsync);
        app.MapGet("/.well-known/ai-plugin.json", () => Results.Json(AgentPlugin(origin)));
        app.MapGet("/agent-plugin/v1", () => Results.Json(AgentPlugin(origin)));
        app.MapGet("/openapi.json", () => Results.Json(OpenApi(origin)));
        app.MapPost("/api/tag", (Delegate)HandleTagAsync);

        Console.Error.WriteLine($"[Paperboy.Mcp] HTTP transports listening on {url}");
        Console.Error.WriteLine($"[Paperboy.Mcp] Streamable: {url}/mcp");
        Console.Error.WriteLine($"[Paperboy.Mcp] SSE: {url}/sse");
        await app.RunAsync(cancellationToken);
    }

    private static async Task RequireLocalOriginAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        if (!IsLoopbackHost(request.Host.Host))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "loopback-only" }, context.RequestAborted);
            return;
        }

        if (request.Headers.TryGetValue("Origin", out var origins))
        {
            // Compare the entire serialized origin to the request authority, not a URL prefix,
            // DNS resolution, the configured display URL, or client-supplied forwarding headers.
            if (origins.Count != 1 || !Uri.TryCreate($"{request.Scheme}://{request.Host}", UriKind.Absolute, out var target)
                || !(string.Equals(origins[0], target.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
                    || (target.IsDefaultPort && string.Equals(origins[0], $"{target.Scheme}://{target.Host}:{target.Port}", StringComparison.OrdinalIgnoreCase))))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "foreign-origin" }, context.RequestAborted);
                return;
            }
        }

        await next(context);
    }

    private static string? Negotiate(HttpRequest request, bool json, bool sse)
    {
        var accept = request.Headers.Accept;
        if (accept.Count == 0)
            return json ? Json : Sse;
        if (!MediaTypeHeaderValue.TryParseStrictList(accept, out var ranges))
            return null;

        // Each representation takes its quality from its MOST specific matching range:
        // e.g. */* cannot override an explicit text/event-stream;q=0.
        (double Quality, int Specificity) Score(MediaTypeHeaderValue offer)
        {
            var best = ranges.Where(offer.IsSubsetOf)
                .OrderByDescending(r => r.MatchesAllTypes ? 0 : r.MatchesAllSubTypes ? 1 : 2)
                .ThenByDescending(r => r.Parameters.TakeWhile(p => !p.Name.Equals("q", StringComparison.OrdinalIgnoreCase)).Count())
                .ThenByDescending(r => r.Quality ?? 1)
                .FirstOrDefault();
            if (best is null)
                return (0, 0);
            var q = best.Quality ?? (best.Parameters.Any(p => p.Name.Equals("q", StringComparison.OrdinalIgnoreCase)) ? 0 : 1);
            return (q is >= 0 and <= 1 ? q : 0, best.MatchesAllTypes ? 0 : best.MatchesAllSubTypes ? 1 : 2);
        }

        var j = json ? Score(JsonMedia) : default;
        var s = sse ? Score(SseMedia) : default;
        if (s.Quality > 0 && (s.CompareTo(j) > 0 || (s == j && s.Specificity == 2)))
            return Sse;
        return j.Quality > 0 ? Json : null;
    }

    private static bool HasSupportedVersion(HttpRequest request) =>
        !request.Headers.TryGetValue(VersionHeader, out var versions)
        || (versions.Count == 1 && PaperboyMcpServer.SupportsProtocolVersion(versions[0]));

    private static Task<IResult> HandleStreamablePostAsync(HttpContext context)
    {
        var media = Negotiate(context.Request, json: true, sse: true);
        if (media is null)
            return Task.FromResult(Results.StatusCode(StatusCodes.Status406NotAcceptable));
        if (!HasSupportedVersion(context.Request))
            return Task.FromResult(RpcError(media, -32600, "Unsupported MCP-Protocol-Version."));
        if (!context.Request.HasJsonContentType())
        {
            return Task.FromResult(Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
        }

        return ReadRpcAsync(context, media);
    }

    private static async Task HandleStreamableSseAsync(HttpContext context)
    {
        if (!HasSupportedVersion(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (!AcceptsStream(context))
            return;
        await PumpSseAsync(context, null, null);
    }

    private static bool AcceptsStream(HttpContext context)
    {
        if (Negotiate(context.Request, json: false, sse: true) is not null)
            return true;
        context.Response.StatusCode = StatusCodes.Status406NotAcceptable;
        return false;
    }

    private static async Task HandleLegacySseAsync(HttpContext context)
    {
        if (!AcceptsStream(context))
            return;
        var sessionId = Guid.NewGuid().ToString("N");
        var stream = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        Streams[sessionId] = stream;
        try
        {
            var endpoint = $"/message?sessionId={Uri.EscapeDataString(sessionId)}";
            await PumpSseAsync(context, stream.Reader, endpoint);
        }
        finally
        {
            Streams.TryRemove(sessionId, out _);
            stream.Writer.TryComplete();
        }
    }

    private static async Task<IResult> HandleLegacyMessageAsync(HttpContext context)
    {
        var sessionId = context.Request.Query["sessionId"].ToString();
        if (string.IsNullOrWhiteSpace(sessionId)
            || !Streams.TryGetValue(sessionId, out var stream))
        {
            return Results.BadRequest(new { error = "invalid-session" });
        }

        return await ReadRpcAsync(context, Json, stream.Writer);
    }

    private static IResult RpcError(string media, int code, string message)
    {
        var error = PaperboyMcpServer.Error(null, code, message);
        return media == Sse ? new SseResult([error], 400) : Results.Json(error, statusCode: 400);
    }

    private static async Task<IResult> ReadRpcAsync(HttpContext context, string media, ChannelWriter<JsonObject>? writer = null)
    {
        if (context.Request.ContentLength is > 10 * 1024 * 1024)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        var token = context.RequestAborted;
        using var document = await ReadDocumentAsync(context);
        if (document is null)
            return RpcError(media, -32700, "Parse error.");

        var root = document.RootElement;
        bool batch = root.ValueKind == JsonValueKind.Array;
        if (batch && root.GetArrayLength() == 0)
            return RpcError(media, -32600, "Empty JSON-RPC batch.");

        var responses = new List<JsonObject>();
        foreach (var element in batch ? root.EnumerateArray() : (IEnumerable<JsonElement>)[root])
        {
            token.ThrowIfCancellationRequested();
            if (!IsRpcRequest(element))
            {
                if (!batch)
                    return RpcError(media, -32600, "Invalid JSON-RPC request.");
                responses.Add(PaperboyMcpServer.Error(null, -32600, "Invalid JSON-RPC request."));
                continue;
            }
            var message = JsonObject.Create(element)!;
            var response = await PaperboyMcpServer.HandleJsonRpcAsync(message, token);
            if (message.ContainsKey("id") && response is not null)
                responses.Add(response);
        }

        if (responses.Count == 0)
            return Results.StatusCode(StatusCodes.Status202Accepted);
        if (writer is null)
            return media == Sse ? new SseResult(responses) : batch ? Results.Json(responses) : Results.Json(responses[0]);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Keepalive);
        try
        {
            foreach (var response in responses)
                await writer.WriteAsync(response, deadline.Token);
            return Results.Accepted();
        }
        catch (ChannelClosedException)
        {
            return Results.BadRequest(new { error = "invalid-session" });
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<JsonDocument?> ReadDocumentAsync(HttpContext context)
    {
        JsonDocument? document = null;
        try
        {
            document = await JsonDocument.ParseAsync(context.Request.Body, DocumentOptions, context.RequestAborted);
            // JsonDocument defers UTF-8 and escape decoding. Reject invalid strings before any
            // batch member is dispatched or an invalid id can reach the response serializer.
            ValidateStrings(document.RootElement);
            return document;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            document?.Dispose();
            return null;
        }

        static void ValidateStrings(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    _ = element.GetString();
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        ValidateStrings(item);
                    break;
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        _ = property.Name;
                        ValidateStrings(property.Value);
                    }
                    break;
            }
        }
    }

    private static bool IsRpcRequest(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("jsonrpc", out var version) && version.ValueKind == JsonValueKind.String && version.ValueEquals("2.0")
        && element.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(method.GetString())
        && (!element.TryGetProperty("id", out var id) || id.ValueKind is JsonValueKind.Number or JsonValueKind.String)
        && (!element.TryGetProperty("params", out var parameters) || parameters.ValueKind == JsonValueKind.Object);

    private static async Task<IResult> HandleTagAsync(HttpContext context)
    {
        if (Negotiate(context.Request, json: true, sse: false) is null)
            return Results.StatusCode(StatusCodes.Status406NotAcceptable);
        if (context.Request.ContentType is not null && !context.Request.HasJsonContentType())
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        using var document = await ReadDocumentAsync(context);
        if (document is null)
            return RpcError(Json, -32700, "Parse error.");
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return RpcError(Json, -32602, "Tagging arguments must be a JSON object.");
        var request = ToolRequest("paperboy.foundryLocal.analyze", JsonObject.Create(document.RootElement)!);
        return Results.Json(await PaperboyMcpServer.HandleJsonRpcAsync(request, context.RequestAborted));
    }

    private static async Task PumpSseAsync(
        HttpContext context,
        ChannelReader<JsonObject>? reader,
        string? endpoint)
    {
        var response = context.Response;
        response.ContentType = Sse;
        response.Headers.CacheControl = "no-cache, no-transform";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";
        await response.Body.FlushAsync(context.RequestAborted);

        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            await WriteSseAsync(response, "endpoint", endpoint, context.RequestAborted);
        }

        var token = context.RequestAborted;
        while (!token.IsCancellationRequested)
        {
            if (reader is null)
            {
                await Task.Delay(Keepalive, token);
            }
            else
            {
                // A timed-out wait consumes no item. Finish it before starting the next wait.
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                idle.CancelAfter(Keepalive);
                try
                {
                    if (!await reader.WaitToReadAsync(idle.Token))
                        return;
                    if (reader.TryRead(out var message))
                        await WriteSseAsync(response, "message", message.ToJsonString(JsonOptions), token);
                    continue;
                }
                catch (OperationCanceledException) when (idle.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    // Idle channel: emit one comment, then resume waiting for a message.
                }
            }
            await response.WriteAsync(": keepalive\n\n", token);
            await response.Body.FlushAsync(token);
        }
    }

    private static async Task WriteSseAsync(
        HttpResponse response,
        string eventName,
        string data,
        CancellationToken cancellationToken)
    {
        await response.WriteAsync($"event: {eventName}\n", cancellationToken);
        foreach (var line in data.Replace("\r", "").Split('\n'))
        {
            await response.WriteAsync($"data: {line}\n", cancellationToken);
        }

        await response.WriteAsync("\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static JsonObject ToolRequest(string name, JsonObject arguments) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Guid.NewGuid().ToString("N"),
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = name,
                ["arguments"] = arguments
            }
        };

    private static object AgentPlugin(Uri origin) =>
        new
        {
            schema_version = "v1",
            name_for_human = "Yumlog Paperboy",
            name_for_model = "yumlog_paperboy",
            description_for_human = "Read-only local media analysis, bundle inspection, and Agent Card discovery.",
            description_for_model = "Use HTTP to inspect bundles and Agent Cards or analyze allowlisted files with Foundry Local. HTTP does not package bundles, write sidecars, or invoke extensions. Check both JSON-RPC error and result.isError before reporting success.",
            auth = new { type = "none" },
            api = new { type = "openapi", url = $"{origin.GetLeftPart(UriPartial.Authority)}/openapi.json" },
            capabilities = new
            {
                mcp = new[] { "streamable-http", "sse", "stdio", "stateless" },
                pipeline = new[] { "file-hash", "media-classification", "tagging" },
                http = new
                {
                    readOnly = true,
                    tools = PaperboyMcpServer.HttpToolNames,
                    streamable = "/mcp", legacyStream = "/sse", legacyMessage = "/message?sessionId={sessionId}"
                },
                stdio = "Separate process transport with the full tool catalog, including bundle writes and extension invocation."
            }
        };

    private static object OpenApi(Uri origin) =>
        new
        {
            openapi = "3.1.0",
            info = new
            {
                title = "Yumlog Paperboy Agent API",
                version = "1.0.0",
                description = "Loopback Agent Plugin 1.0. HTTP exposes read-only tools; use tools/list for the allowlist. Bundle writes, sidecar output and extension invocation are not HTTP operations. Browser Origin, when present, must match the request origin."
            },
            servers = new[] { new { url = origin.GetLeftPart(UriPartial.Authority) } },
            paths = new Dictionary<string, object>
            {
                ["/api/tag"] = new
                {
                    post = new
                    {
                        operationId = "tagYumlogMedia",
                        summary = "Analyze allowlisted files with loopback Foundry Local without writing a sidecar",
                        requestBody = new
                        {
                            required = true,
                            content = new Dictionary<string, object>
                            {
                                ["application/json"] = new
                                {
                                    schema = new
                                    {
                                        type = "object",
                                        required = new[] { "sourcePaths", "model" },
                                        properties = new
                                        {
                                            sourcePaths = new { type = "array", items = new { type = "string" } },
                                            model = new { type = "string" },
                                            endpoint = new { type = "string", @default = "http://127.0.0.1:52495/v1" },
                                            includeImageContent = new { type = "boolean", @default = true },
                                            sampleVideoFrames = new { type = "boolean", @default = true },
                                            maxImageBytes = new { type = "integer", @default = 2_000_000 }
                                        }
                                    }
                                }
                            }
                        },
                        responses = HttpResponses(new()
                        {
                            ["200"] = JsonResponse("JSON-RPC envelope: result.content on success, result.isError or error on failure."),
                            ["400"] = JsonResponse("Malformed JSON or non-object body."),
                            ["406"] = new { description = "JSON is not acceptable." },
                            ["413"] = new { description = "Request body too large." },
                            ["415"] = new { description = "Expected a JSON content type." }
                        })
                    }
                },
                ["/mcp"] = new
                {
                    post = new
                    {
                        operationId = "mcpJsonRpc",
                        summary = "Send a read-only MCP request, notification or compatibility batch",
                        parameters = new[] { VersionParameter() },
                        requestBody = RpcRequestBody(),
                        responses = HttpResponses(new()
                        {
                            ["200"] = RpcResponse("JSON-RPC result or error; JSON batches return an array. SSE returns a message event per reply."),
                            ["202"] = new { description = "No reply (notification); empty body." },
                            ["400"] = RpcResponse("Parse error, invalid request, empty batch or unsupported version header."),
                            ["406"] = new { description = "Neither JSON nor SSE is acceptable." },
                            ["413"] = new { description = "Request body too large." },
                            ["415"] = new { description = "Expected a JSON content type." }
                        })
                    },
                    get = new
                    {
                        operationId = "mcpEventStream",
                        summary = "Open a stateless keepalive stream; POST replies are not routed here",
                        parameters = new[] { VersionParameter() },
                        responses = HttpResponses(new()
                        {
                            ["200"] = StreamResponse("SSE keepalive comments until disconnect."),
                            ["400"] = new { description = "Unsupported version header." },
                            ["406"] = new { description = "SSE is not acceptable." }
                        })
                    }
                },
                ["/sse"] = new
                {
                    get = new
                    {
                        operationId = "legacyMcpSse",
                        summary = "Legacy MCP SSE endpoint with POST /message",
                        responses = HttpResponses(new()
                        {
                            ["200"] = StreamResponse("First endpoint event supplies a session-specific POST URL. Subsequent message events carry JSON-RPC replies."),
                            ["406"] = new { description = "SSE is not acceptable." }
                        })
                    }
                },
                ["/message"] = new
                {
                    post = new
                    {
                        operationId = "legacyMcpMessage",
                        summary = "Send JSON-RPC to the session announced by GET /sse",
                        parameters = new[] { new { name = "sessionId", @in = "query", required = true, schema = new { type = "string" } } },
                        requestBody = RpcRequestBody(),
                        responses = HttpResponses(new()
                        {
                            ["202"] = new { description = "Accepted. Replies, if any, go to the session stream, not this body." },
                            ["400"] = JsonResponse("Invalid or expired session, malformed JSON, invalid request or empty batch."),
                            ["413"] = new { description = "Request body too large." },
                            ["503"] = new { description = "Session queue remained full; not all replies were queued." }
                        })
                    }
                },
                ["/health"] = new
                {
                    get = new
                    {
                        operationId = "paperboyHealth",
                        summary = "Paperboy MCP transport health",
                        responses = HttpResponses(new()
                        {
                            ["200"] = JsonResponse("Service name, health status, transports, plugin version and UTC time.")
                        })
                    }
                },
                ["/.well-known/ai-plugin.json"] = DiscoveryOperation("paperboyPluginManifest", "Agent Plugin 1.0 manifest"),
                ["/agent-plugin/v1"] = DiscoveryOperation("paperboyPluginManifestAlias", "Agent Plugin 1.0 manifest alias"),
                ["/openapi.json"] = DiscoveryOperation("paperboyOpenApi", "OpenAPI document")
            }
        };

    private static object VersionParameter() => new
    {
        name = VersionHeader, @in = "header", required = false,
        description = "Optional for compatibility; if supplied it must be a supported version. initialize negotiates via params.protocolVersion.",
        schema = new { type = "string", @enum = PaperboyMcpServer.ProtocolVersions }
    };

    private static object RpcRequestBody()
    {
        var request = new
        {
            type = "object", required = new[] { "jsonrpc", "method" },
            properties = new
            {
                jsonrpc = new { @const = "2.0" }, method = new { type = "string", minLength = 1 },
                id = new { type = new[] { "string", "number" } }, @params = new { type = "object" }
            }
        };
        return new
        {
            required = true,
            content = new Dictionary<string, object>
            {
                [Json] = new { schema = new { oneOf = new object[] { request, new { type = "array", minItems = 1, items = request } } } }
            }
        };
    }

    private static object JsonResponse(string description) => new
    {
        description, content = new Dictionary<string, object> { [Json] = new { schema = new { type = "object" } } }
    };

    private static object StreamResponse(string description) => new
    {
        description, content = new Dictionary<string, object> { [Sse] = new { schema = new { type = "string" } } }
    };

    private static object RpcResponse(string description) => new
    {
        description,
        content = new Dictionary<string, object>
        {
            [Json] = new { schema = new { type = new[] { "object", "array" } } },
            [Sse] = new { schema = new { type = "string" } }
        }
    };

    private static Dictionary<string, object> HttpResponses(Dictionary<string, object> responses)
    {
        responses["403"] = JsonResponse("Non-loopback Host or foreign browser Origin.");
        return responses;
    }

    private static object DiscoveryOperation(string operationId, string summary) => new
    {
        get = new { operationId, summary, responses = HttpResponses(new() { ["200"] = JsonResponse(summary) }) }
    };

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (Uri.CheckHostName(host) is UriHostNameType.IPv4 or UriHostNameType.IPv6
            && System.Net.IPAddress.TryParse(host, out var address)
            && System.Net.IPAddress.IsLoopback(address));

    private static string? ArgumentValue(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private sealed class SseResult(IReadOnlyCollection<JsonObject> messages, int status = 200) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = status;
            httpContext.Response.ContentType = Sse;
            httpContext.Response.Headers.CacheControl = "no-cache, no-transform";
            foreach (var message in messages)
            {
                await WriteSseAsync(httpContext.Response, "message", message.ToJsonString(JsonOptions), httpContext.RequestAborted);
            }
        }
    }
}
