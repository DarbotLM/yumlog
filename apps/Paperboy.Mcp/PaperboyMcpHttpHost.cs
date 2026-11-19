using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Paperboy.Desktop.Mcp;

public static class PaperboyMcpHttpHost
{
    public const string DefaultUrl = "http://127.0.0.1:5117";
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
        app.Use(async (context, next) =>
        {
            if (!IsLoopbackHost(context.Request.Host.Host))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "loopback-only" }, cancellationToken);
                return;
            }

            await next(context);
        });

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
        app.MapPost("/api/tag", async (HttpContext context) =>
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
            var arguments = JsonObject.Create(document.RootElement)
                ?? throw new ArgumentException("Tagging arguments must be a JSON object.");
            var request = ToolRequest("paperboy.foundryLocal.analyze", arguments);
            var response = await PaperboyMcpServer.HandleJsonRpcAsync(request, cancellationToken);
            return response is null
                ? Results.StatusCode(StatusCodes.Status202Accepted)
                : Results.Json(response);
        });

        Console.Error.WriteLine($"[Paperboy.Mcp] HTTP transports listening on {url}");
        Console.Error.WriteLine($"[Paperboy.Mcp] Streamable: {url}/mcp");
        Console.Error.WriteLine($"[Paperboy.Mcp] SSE: {url}/sse");
        await app.RunAsync(cancellationToken);
    }

    private static async Task<IResult> HandleStreamablePostAsync(HttpContext context)
    {
        if (!context.Request.HasJsonContentType())
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        if (context.Request.ContentLength is > 10 * 1024 * 1024)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var accept = context.Request.Headers.Accept.ToString();
        if (!accept.Contains("application/json", StringComparison.OrdinalIgnoreCase)
            && !accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)
            && !accept.Contains("*/*", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(accept))
        {
            return Results.StatusCode(StatusCodes.Status406NotAcceptable);
        }

        await using var requestStream = context.Request.Body;
        using var document = await JsonDocument.ParseAsync(requestStream, cancellationToken: context.RequestAborted);
        var responses = await HandlePayloadAsync(document.RootElement, context.RequestAborted);
        if (responses.Count == 0)
        {
            return Results.StatusCode(StatusCodes.Status202Accepted);
        }

        var payload = document.RootElement.ValueKind == JsonValueKind.Array
            ? JsonSerializer.SerializeToNode(responses, JsonOptions)
            : responses[0];

        if (accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            return new SseResult(responses);
        }

        return Results.Json(payload);
    }

    private static async Task HandleStreamableSseAsync(HttpContext context)
    {
        var response = context.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache, no-transform";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";
        await response.Body.FlushAsync(context.RequestAborted);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(context.RequestAborted))
        {
            await response.WriteAsync(": keepalive\n\n", context.RequestAborted);
            await response.Body.FlushAsync(context.RequestAborted);
        }
    }

    private static async Task HandleLegacySseAsync(HttpContext context)
    {
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
            await PumpSseAsync(context, stream, endpoint);
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

        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        var responses = await HandlePayloadAsync(document.RootElement, context.RequestAborted);
        await WriteMessagesAsync(stream, responses, context.RequestAborted);
        return Results.Accepted();
    }

    private static async Task<List<JsonObject>> HandlePayloadAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var messages = payload.ValueKind == JsonValueKind.Array
            ? payload.EnumerateArray().Select(element => JsonObject.Create(element))
            : [JsonObject.Create(payload)];
        var responses = new List<JsonObject>();
        foreach (var message in messages)
        {
            if (message is null)
            {
                throw new ArgumentException("Each JSON-RPC message must be an object.");
            }

            var response = await PaperboyMcpServer.HandleJsonRpcAsync(message, cancellationToken);
            if (response is not null)
            {
                responses.Add(response);
            }
        }

        return responses;
    }

    private static async Task WriteMessagesAsync(
        Channel<JsonObject> stream,
        IReadOnlyCollection<JsonObject> responses,
        CancellationToken cancellationToken)
    {
        foreach (var response in responses)
        {
            await stream.Writer.WriteAsync(response, cancellationToken);
        }
    }

    private static async Task PumpSseAsync(
        HttpContext context,
        Channel<JsonObject> stream,
        string? endpoint)
    {
        var response = context.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache, no-transform";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";
        await response.Body.FlushAsync(context.RequestAborted);

        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            await WriteSseAsync(response, "endpoint", endpoint, context.RequestAborted);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (!context.RequestAborted.IsCancellationRequested)
        {
            var readTask = stream.Reader.ReadAsync(context.RequestAborted).AsTask();
            var timerTask = timer.WaitForNextTickAsync(context.RequestAborted).AsTask();
            var completed = await Task.WhenAny(readTask, timerTask);
            if (completed == readTask)
            {
                var message = await readTask;
                await WriteSseAsync(response, "message", message.ToJsonString(JsonOptions), context.RequestAborted);
            }
            else
            {
                await response.WriteAsync(": keepalive\n\n", context.RequestAborted);
                await response.Body.FlushAsync(context.RequestAborted);
            }
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
            description_for_human = "Local Yumlog capture, tagging, Agent Cards, and Paperboy evidence tools.",
            description_for_model = "Use these local-only tools to analyze selected Yumlog media with Foundry Local, inspect Agent Cards, and package verified evidence. Do not claim a tool succeeded unless its response reports success.",
            auth = new { type = "none" },
            api = new { type = "openapi", url = $"{origin.GetLeftPart(UriPartial.Authority)}/openapi.json" },
            capabilities = new
            {
                mcp = new[] { "streamable-http", "sse", "stdio", "stateless" },
                pipeline = new[] { "file-hash", "media-classification", "tagging", "sidecar", "paperboy-bundle" }
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
                description = "Loopback Agent Plugin 1.0 and MCP transport for Yumlog Paperboy."
            },
            servers = new[] { new { url = origin.GetLeftPart(UriPartial.Authority) } },
            paths = new Dictionary<string, object>
            {
                ["/api/tag"] = new
                {
                    post = new
                    {
                        operationId = "tagYumlogMedia",
                        summary = "Run the complete local Foundry tagging pipeline",
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
                        responses = new Dictionary<string, object>
                        {
                            ["200"] = new { description = "MCP tool result with tags, classifications, summaries, and hashes." },
                            ["400"] = new { description = "Invalid request." }
                        }
                    }
                },
                ["/mcp"] = new
                {
                    post = new
                    {
                        operationId = "mcpJsonRpc",
                        summary = "Stateless or streamable MCP JSON-RPC endpoint",
                        responses = new Dictionary<string, object>
                        {
                            ["200"] = new { description = "JSON-RPC response." },
                            ["202"] = new { description = "Notification accepted." }
                        }
                    },
                    get = new
                    {
                        operationId = "mcpEventStream",
                        summary = "MCP server-sent event stream",
                        responses = new Dictionary<string, object>
                        {
                            ["200"] = new { description = "text/event-stream responses." }
                        }
                    }
                },
                ["/sse"] = new
                {
                    get = new
                    {
                        operationId = "legacyMcpSse",
                        summary = "Legacy MCP SSE endpoint with POST /message",
                        responses = new Dictionary<string, object>
                        {
                            ["200"] = new { description = "SSE stream." }
                        }
                    }
                },
                ["/health"] = new
                {
                    get = new
                    {
                        operationId = "paperboyHealth",
                        summary = "Paperboy MCP transport health",
                        responses = new Dictionary<string, object>
                        {
                            ["200"] = new { description = "Healthy." }
                        }
                    }
                }
            }
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

    private sealed class SseResult(IReadOnlyCollection<JsonObject> messages) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.ContentType = "text/event-stream";
            httpContext.Response.Headers.CacheControl = "no-cache, no-transform";
            foreach (var message in messages)
            {
                await WriteSseAsync(httpContext.Response, "message", message.ToJsonString(JsonOptions), httpContext.RequestAborted);
            }
        }
    }
}
