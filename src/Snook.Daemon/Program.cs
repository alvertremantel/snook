using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Snook.Application;
using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;

namespace Snook.Daemon;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        var dataRoot = Environment.GetEnvironmentVariable("SNOOK_DATA_DIR")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var snookRoot = Path.Combine(dataRoot, "Snook");
        Directory.CreateDirectory(snookRoot);
        var databasePath = Path.Combine(snookRoot, "workspace.db");
        var token = await LoadOrCreateTokenAsync(Path.Combine(snookRoot, "daemon.token"));
        var port = ReadPort();
        using var cancellationSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };
        PosixSignalRegistration? terminationRegistration = null;
        if (!OperatingSystem.IsWindows())
        {
            terminationRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                cancellationSource.Cancel();
            });
        }

        try
        {
            var store = new SqliteStore(databasePath);
            await using var backend = new SnookBackend(store);
            await backend.InitializeAsync(cancellationSource.Token);
            await using var server = new DaemonRpcServer(backend, token, port);
            server.Start();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ready = true,
                contract = $"{ContractInfo.Major}.{ContractInfo.Minor}",
                endpoint = server.Endpoint,
                tokenPath = Path.Combine(snookRoot, "daemon.token")
            }, JsonOptions));
            await server.RunAsync(cancellationSource.Token);
            return 0;
        }
        finally
        {
            terminationRegistration?.Dispose();
        }
    }

    private static int ReadPort()
    {
        var value = Environment.GetEnvironmentVariable("SNOOK_DAEMON_PORT");
        return int.TryParse(value, out var port) && port is >= 1024 and <= 65535 ? port : 43871;
    }

    private static async Task<string> LoadOrCreateTokenAsync(string path)
    {
        if (File.Exists(path))
        {
            return Guard.Required((await File.ReadAllTextAsync(path)).Trim('\uFEFF'), "daemon token", 512);
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256, FileOptions.Asynchronous);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(token);
        await writer.FlushAsync();
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return token;
    }
}

internal sealed class DaemonRpcServer : IAsyncDisposable
{
    private const int MaxRequestBytes = 1_048_576;
    private const int MaxArgumentCount = 64;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IBackendClient _backend;
    private readonly string _token;
    private readonly HttpListener _listener = new();
    private readonly HashSet<string> _contractMethods = typeof(IBackendClient).GetMethods().Select(method => method.Name).ToHashSet(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, StreamWriter> _changeSubscribers = new();

    public DaemonRpcServer(IBackendClient backend, string token, int port)
    {
        _backend = backend;
        _token = token;
        Endpoint = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(Endpoint.AbsoluteUri);
        _backend.Changed += OnBackendChanged;
    }

    public Uri Endpoint { get; }

    public void Start() => _listener.Start();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!_listener.IsListening)
        {
            _listener.Start();
        }
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var contextTask = _listener.GetContextAsync();
                var completed = await Task.WhenAny(contextTask, Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
                if (completed != contextTask)
                {
                    break;
                }

                _ = HandleAsync(await contextTask, cancellationToken);
            }
        }
        finally
        {
            _listener.Stop();
            _listener.Close();
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsAuthorized(context.Request))
            {
                await WriteErrorAsync(context.Response, HttpStatusCode.Unauthorized, "Unauthorized daemon client.", SnookErrorCode.StoreUnavailable, cancellationToken);
                return;
            }

            if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/v1/health")
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { ready = true, contract = $"{ContractInfo.Major}.{ContractInfo.Minor}" }, cancellationToken);
                return;
            }

            if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/v1/changes")
            {
                await HandleChangesAsync(context, cancellationToken);
                return;
            }

            if (context.Request.HttpMethod == "POST" && context.Request.Url?.AbsolutePath == "/v1/call")
            {
                await HandleCallAsync(context, cancellationToken);
                return;
            }

            await WriteErrorAsync(context.Response, HttpStatusCode.NotFound, "Daemon route was not found.", SnookErrorCode.NotFound, cancellationToken);
        }
        catch (Exception exception)
        {
            var actual = exception is System.Reflection.TargetInvocationException { InnerException: not null } invocation
                ? invocation.InnerException
                : exception;
            var code = actual is SnookException snook ? snook.Code : SnookErrorCode.InternalError;
            var status = actual is SnookException ? HttpStatusCode.BadRequest : HttpStatusCode.InternalServerError;
            await WriteErrorAsync(context.Response, status, actual?.Message ?? "Daemon request failed.", code, CancellationToken.None);
        }
        finally
        {
            context.Response.Close();
        }
    }

    private async Task HandleChangesAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.SendChunked = true;
        context.Response.KeepAlive = true;
        await using var writer = new StreamWriter(context.Response.OutputStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
        {
            AutoFlush = true
        };
        var id = Guid.NewGuid();
        _changeSubscribers[id] = writer;
        try
        {
            await writer.WriteAsync(": connected\n\n");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _changeSubscribers.TryRemove(id, out _);
        }
    }

    private void OnBackendChanged(object? sender, ChangeNotification notification)
        => _ = BroadcastChangeAsync(notification);

    private async Task BroadcastChangeAsync(ChangeNotification notification)
    {
        var payload = $"data: {JsonSerializer.Serialize(notification, JsonOptions)}\n\n";
        foreach (var subscriber in _changeSubscribers.ToArray())
        {
            try
            {
                await subscriber.Value.WriteAsync(payload);
            }
            catch (IOException)
            {
                _changeSubscribers.TryRemove(subscriber.Key, out _);
            }
            catch (ObjectDisposedException)
            {
                _changeSubscribers.TryRemove(subscriber.Key, out _);
            }
        }
    }

    private async Task HandleCallAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var body = await ReadRequestBodyAsync(context.Request, cancellationToken);
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("method", out var methodElement)
            || methodElement.ValueKind != JsonValueKind.String
            || !document.RootElement.TryGetProperty("args", out var argsElement)
            || argsElement.ValueKind != JsonValueKind.Array)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "Daemon calls require a method and args array.");
        }

        var methodName = methodElement.GetString()!;
        if (!_contractMethods.Contains(methodName))
        {
            throw new SnookException(SnookErrorCode.NotFound, "The requested backend method is not in the public contract.");
        }

        var method = typeof(SnookBackend).GetMethods()
            .SingleOrDefault(candidate => candidate.Name == methodName && typeof(Task).IsAssignableFrom(candidate.ReturnType))
            ?? throw new SnookException(SnookErrorCode.NotFound, "The requested backend method is unavailable.");
        var parameters = method.GetParameters();
        var args = argsElement.EnumerateArray().ToArray();
        if (args.Length > MaxArgumentCount)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, $"Daemon calls accept at most {MaxArgumentCount} arguments.");
        }
        var values = new object?[parameters.Length];
        var supplied = 0;
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            if (parameter.ParameterType == typeof(CancellationToken))
            {
                values[index] = cancellationToken;
                continue;
            }

            if (supplied >= args.Length)
            {
                if (!parameter.IsOptional)
                {
                    throw new SnookException(SnookErrorCode.ValidationFailed, $"Missing argument '{parameter.Name}'.");
                }

                values[index] = parameter.DefaultValue;
                continue;
            }

            values[index] = args[supplied].ValueKind == JsonValueKind.Null
                ? null
                : args[supplied].Deserialize(parameter.ParameterType, JsonOptions);
            supplied++;
        }

        if (supplied != args.Length)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "Too many arguments for the requested backend method.");
        }

        var task = (Task)(method.Invoke(_backend, values) ?? throw new SnookException(SnookErrorCode.InternalError, "The backend method returned no task."));
        await task;
        var result = task.GetType().GetProperty("Result")?.GetValue(task);
        await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { result }, cancellationToken);
    }

    private static async Task<byte[]> ReadRequestBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength64 > MaxRequestBytes)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, $"Daemon request bodies are limited to {MaxRequestBytes} bytes.");
        }

        await using var input = request.InputStream;
        await using var body = new MemoryStream(capacity: request.ContentLength64 is > 0 and <= MaxRequestBytes
            ? (int)request.ContentLength64
            : 4096);
        var buffer = new byte[81920];
        var total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaxRequestBytes)
            {
                throw new SnookException(SnookErrorCode.ValidationFailed, $"Daemon request bodies are limited to {MaxRequestBytes} bytes.");
            }

            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return body.ToArray();
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var supplied = request.Headers["X-Snook-Token"] ?? string.Empty;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(_token));
    }

    private static async Task WriteErrorAsync(HttpListenerResponse response, HttpStatusCode status, string message, SnookErrorCode code, CancellationToken cancellationToken)
        => await WriteJsonAsync(response, status, new { error = new { code = code.ToString(), message } }, cancellationToken);

    private static async Task WriteJsonAsync(HttpListenerResponse response, HttpStatusCode status, object payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
        response.StatusCode = (int)status;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
        await response.OutputStream.FlushAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _backend.Changed -= OnBackendChanged;
        _listener.Close();
        foreach (var subscriber in _changeSubscribers.Values)
        {
            subscriber.Dispose();
        }
        _changeSubscribers.Clear();
        return ValueTask.CompletedTask;
    }
}
