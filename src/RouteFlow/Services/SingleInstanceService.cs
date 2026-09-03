using System.IO.Pipes;

namespace RouteFlow.Services;

public sealed class SingleInstanceService : IDisposable
{
    public const string MutexName = "RouteFlow.Desktop.1f5875cc-78ea-4d18-8d3a-2602ca8398ef";
    private const string PipeName = "RouteFlow.Desktop.Activation.1f5875cc-78ea-4d18-8d3a-2602ca8398ef";

    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private Task? _listenerTask;

    public void Start(Action activate)
    {
        _listenerTask = ListenAsync(activate, _cancellationTokenSource.Token);
    }

    public static async Task ActivateExistingInstanceAsync()
    {
        // The first process may still be initializing after it has acquired the mutex.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                await using var client = new NamedPipeClientStream(
                    ".",
                    PipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);
                await client.ConnectAsync(150);
                await using var writer = new StreamWriter(client) { AutoFlush = true };
                await writer.WriteLineAsync("activate");
                return;
            }
            catch (TimeoutException)
            {
            }
            catch (IOException)
            {
            }

            await Task.Delay(100);
        }
    }

    private static async Task ListenAsync(Action activate, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server);
                var command = await reader.ReadLineAsync(cancellationToken);
                if (string.Equals(command, "activate", StringComparison.Ordinal))
                    activate();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
    }
}
