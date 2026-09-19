using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
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
        try
        {
            if (args is ["--help"] or ["-h"])
            {
                Console.WriteLine("snookd: --data-dir PATH --port PORT. Environment: SNOOK_DATA_DIR, SNOOK_DAEMON_PORT. Ctrl-C/SIGTERM stops gracefully.");
                return 0;
            }
            string? dataRoot = null, port = null;
            for (var index = 0; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length || args[index] is not ("--data-dir" or "--port"))
                    throw new SnookException(SnookErrorCode.ValidationFailed, "Use snookd --help for options.");
                if (args[index] == "--data-dir") dataRoot = args[index + 1];
                else port = args[index + 1];
            }
            return await RunAsync(dataRoot, port);
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception exception)
        {
            // Startup diagnostics must not expose database paths, SQL, content or tokens.
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                ready = false,
                error = new
                {
                    code = exception is SnookException snook ? snook.Code.ToString() : "StartupFailed",
                    message = "Daemon startup failed. Check configuration, private file permissions, workspace ownership and loopback port availability."
                }
            }, JsonOptions));
            return 1;
        }
    }

    private static async Task<int> RunAsync(string? selectedDataRoot, string? selectedPort)
    {
        var dataRoot = selectedDataRoot ?? Environment.GetEnvironmentVariable("SNOOK_DATA_DIR")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var snookRoot = Path.Combine(dataRoot, "Snook");
        if (new DirectoryInfo(snookRoot).LinkTarget is not null)
            throw new SnookException(SnookErrorCode.ValidationFailed, "Daemon profile directory cannot be a symbolic link.");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(snookRoot);
        else
        {
            Directory.CreateDirectory(snookRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(snookRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var databasePath = Path.Combine(snookRoot, "workspace.db");
        var port = ReadPort(selectedPort);
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
            var token = await LoadOrCreateTokenAsync(Path.Combine(snookRoot, "daemon.token"));
            await using var server = new DaemonRpcServer(backend, token, port);
            server.Start();
            var descriptorPath = Path.Combine(snookRoot, "daemon.endpoint.json");
            var descriptor = new DaemonEndpointDescriptor(1, Guid.NewGuid(), server.Endpoint.AbsoluteUri, ContractInfo.Major, ContractInfo.Minor);
            await WriteDescriptorAsync(descriptorPath, descriptor, cancellationSource.Token);
            try
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    ready = true,
                    contract = $"{ContractInfo.Major}.{ContractInfo.Minor}",
                    endpoint = server.Endpoint,
                    instanceId = descriptor.InstanceId,
                    tokenFile = "daemon.token"
                }, JsonOptions));
                await server.RunAsync(cancellationSource.Token);
                return 0;
            }
            finally { File.Delete(descriptorPath); }
        }
        finally
        {
            terminationRegistration?.Dispose();
        }
    }

    private static async Task WriteDescriptorAsync(string path, DaemonEndpointDescriptor descriptor, CancellationToken cancellationToken)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporaryPath, options))
            {
                await JsonSerializer.SerializeAsync(stream, descriptor, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { File.Delete(temporaryPath); }
    }

    private static int ReadPort(string? selectedPort)
    {
        var value = selectedPort ?? Environment.GetEnvironmentVariable("SNOOK_DAEMON_PORT");
        if (value is null) return 43871;
        return int.TryParse(value, out var port) && port is >= 1024 and <= 65535
            ? port
            : throw new SnookException(SnookErrorCode.ValidationFailed, "SNOOK_DAEMON_PORT must be between 1024 and 65535.");
    }

    private static async Task<string> LoadOrCreateTokenAsync(string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
            throw new SnookException(SnookErrorCode.ValidationFailed, "Daemon token cannot be a symbolic link.");
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 1024 || (!OperatingSystem.IsWindows()
                && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0))
                throw new SnookException(SnookErrorCode.ValidationFailed, "Daemon token requires private file permissions and bounded contents.");
            var existing = (await File.ReadAllTextAsync(path)).Trim('\uFEFF', '\r', '\n', ' ');
            if (existing.Length != 64 || !existing.All(char.IsAsciiHexDigit))
                throw new SnookException(SnookErrorCode.ValidationFailed, "Daemon token must contain a 256-bit hexadecimal credential.");
            return existing;
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        await using var stream = new FileStream(path, options);
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
    private const int MaxRequests = 64;
    private const int MaxSubscribers = 16;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IBackendClient _backend;
    private readonly string _token;
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, System.Reflection.MethodInfo> _contractMethods = typeof(IBackendClient).GetMethods()
        .Where(method => typeof(Task).IsAssignableFrom(method.ReturnType))
        .ToDictionary(method => method.Name, StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Channel<string>> _changeSubscribers = new();
    private readonly SemaphoreSlim _dispatch = new(1, 1);
    private readonly SemaphoreSlim _subscriptions = new(MaxSubscribers, MaxSubscribers);

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
        var handlers = new HashSet<Task>();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
                handlers.RemoveWhere(task => task.IsCompleted);
                if (handlers.Count >= MaxRequests)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    context.Response.Close();
                    continue;
                }

                handlers.Add(HandleAsync(context, stopping.Token));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await stopping.CancelAsync();
            _listener.Stop();
            await Task.WhenAll(handlers);
            _listener.Close();
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
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
                await HandleCallAsync(context, deadline.Token);
                return;
            }

            await WriteErrorAsync(context.Response, HttpStatusCode.NotFound, "Daemon route was not found.", SnookErrorCode.NotFound, cancellationToken);
        }
        catch (Exception exception)
        {
            var actual = exception is System.Reflection.TargetInvocationException { InnerException: not null } invocation
                ? invocation.InnerException
                : exception;
            var code = actual is SnookException snook ? snook.Code
                : actual is JsonException ? SnookErrorCode.ValidationFailed : SnookErrorCode.InternalError;
            var status = actual is SnookException { Code: not SnookErrorCode.InternalError } or JsonException ? HttpStatusCode.BadRequest
                : actual is OperationCanceledException ? HttpStatusCode.RequestTimeout : HttpStatusCode.InternalServerError;
            var message = actual is SnookException { Code: not SnookErrorCode.InternalError } ? actual.Message
                : actual is JsonException ? "Daemon call contains invalid JSON or argument types."
                : "Daemon request failed.";
            try
            {
                using var errorDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await WriteErrorAsync(context.Response, status, message, code, errorDeadline.Token);
            }
            catch (Exception writeException) when (writeException is IOException or HttpListenerException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
            {
                // The peer may have disconnected, or an SSE response already started.
            }
        }
        finally
        {
            try { context.Response.Close(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task HandleChangesAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        if (!await _subscriptions.WaitAsync(0, cancellationToken))
        {
            await WriteErrorAsync(context.Response, HttpStatusCode.ServiceUnavailable,
                "Daemon change subscriber limit reached.", SnookErrorCode.StoreUnavailable, cancellationToken);
            return;
        }
        var queue = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        var id = Guid.NewGuid();
        try
        {
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.SendChunked = true;
            context.Response.KeepAlive = true;
            await using var writer = new StreamWriter(context.Response.OutputStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
            {
                AutoFlush = true
            };
            _changeSubscribers[id] = queue;
            await WriteFrameAsync(writer, ": connected\n\n", cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    if (!await queue.Reader.WaitToReadAsync(heartbeat.Token)) break;
                    while (queue.Reader.TryRead(out var payload))
                    {
                        await WriteFrameAsync(writer, payload, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (heartbeat.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    await WriteFrameAsync(writer, ": heartbeat\n\n", cancellationToken);
                }
            }
        }
        finally
        {
            _changeSubscribers.TryRemove(id, out _);
            _subscriptions.Release();
        }
    }

    private static async Task WriteFrameAsync(StreamWriter writer, string payload, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(payload.AsMemory(), deadline.Token);
        await writer.FlushAsync(deadline.Token);
    }

    private void OnBackendChanged(object? sender, ChangeNotification notification)
    {
        var payload = $"data: {JsonSerializer.Serialize(notification, JsonOptions)}\n\n";
        foreach (var subscriber in _changeSubscribers.ToArray())
        {
            if (!subscriber.Value.Writer.TryWrite(payload))
            {
                // Disconnect on overflow; reconnecting clients obtain a fresh snapshot.
                subscriber.Value.Writer.TryComplete();
                _changeSubscribers.TryRemove(subscriber.Key, out _);
            }
        }
    }

    private async Task HandleCallAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var body = await ReadRequestBodyAsync(context.Request, cancellationToken);
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("method", out var methodElement)
            || methodElement.ValueKind != JsonValueKind.String
            || !document.RootElement.TryGetProperty("args", out var argsElement)
            || argsElement.ValueKind != JsonValueKind.Array)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "Daemon calls require a method and args array.");
        }

        var methodName = methodElement.GetString()!;
        if (!_contractMethods.TryGetValue(methodName, out var method))
        {
            throw new SnookException(SnookErrorCode.NotFound, "The requested backend method is not in the public contract.");
        }

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

            if (args[supplied].ValueKind == JsonValueKind.Null
                && (parameter.ParameterType.IsValueType
                    ? Nullable.GetUnderlyingType(parameter.ParameterType) is null
                    : new System.Reflection.NullabilityInfoContext().Create(parameter).ReadState == System.Reflection.NullabilityState.NotNull))
            {
                throw new SnookException(SnookErrorCode.ValidationFailed, $"Argument '{parameter.Name}' cannot be null.");
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

        object? result;
        await _dispatch.WaitAsync(cancellationToken);
        try
        {
            var task = (Task)(method.Invoke(_backend, values) ?? throw new SnookException(SnookErrorCode.InternalError, "The backend method returned no task."));
            await task;
            result = method.ReturnType.IsGenericType ? method.ReturnType.GetProperty("Result")?.GetValue(task) : null;
        }
        finally
        {
            _dispatch.Release();
        }
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
        foreach (var subscriber in _changeSubscribers.Values) subscriber.Writer.TryComplete();
        _changeSubscribers.Clear();
        _dispatch.Dispose();
        _subscriptions.Dispose();
        return ValueTask.CompletedTask;
    }
}
