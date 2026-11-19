using Paperboy.Desktop.Mcp;

var http = args.Any(arg => string.Equals(arg, "--http", StringComparison.OrdinalIgnoreCase));
var verbose = args.Any(arg => string.Equals(arg, "--verbose", StringComparison.OrdinalIgnoreCase));
using var cancellation = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    if (http)
    {
        await PaperboyMcpHttpHost.RunAsync(args, cancellation.Token);
        return 0;
    }

    Log(verbose, "Starting Paperboy MCP server.");
    await PaperboyMcpServer.RunAsync(
        Console.OpenStandardInput(),
        Console.OpenStandardOutput(),
        args,
        cancellation.Token);
    Log(verbose, "Paperboy MCP server stopped.");
    return 0;
}
catch (OperationCanceledException)
{
    Log(verbose, "Paperboy MCP server canceled.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[Paperboy.Mcp] Fatal error: {ex}");
    return 1;
}

static void Log(bool enabled, string message)
{
    if (enabled || Environment.GetEnvironmentVariable("PAPERBOY_DEBUG") == "1")
    {
        Console.Error.WriteLine($"[{DateTime.UtcNow:O}] [Paperboy.Mcp] {message}");
    }
}
