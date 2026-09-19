using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
            using var credentials = await DaemonCredentialRegistry.OpenAsync(Path.Combine(snookRoot, "daemon-auth.json"), cancellationSource.Token);
            await using var server = new DaemonRpcServer(backend, token, credentials, port);
            server.Start();
            var descriptorPath = Path.Combine(snookRoot, "daemon.endpoint.json");
            var descriptor = new DaemonEndpointDescriptor(1, Guid.NewGuid(), server.Endpoint.AbsoluteUri,
                ContractInfo.Major, ContractInfo.Minor);
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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly IBackendClient _backend;
    private readonly string _recoveryToken;
    private readonly DaemonCredentialRegistry _credentials;
    private readonly PairingSessions _pairings;
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, System.Reflection.MethodInfo> _contractMethods = typeof(IBackendClient).GetMethods()
        .Where(method => typeof(Task).IsAssignableFrom(method.ReturnType))
        .ToDictionary(method => method.Name, StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, ChangeSubscriber> _changeSubscribers = new();
    private readonly SemaphoreSlim _dispatch = new(1, 1);
    private readonly SemaphoreSlim _subscriptions = new(MaxSubscribers, MaxSubscribers);
    private readonly SemaphoreSlim _pairingDispatch = new(4, 4);

    public DaemonRpcServer(IBackendClient backend, string recoveryToken, DaemonCredentialRegistry credentials,
        int port, TimeProvider? timeProvider = null)
    {
        _backend = backend;
        _recoveryToken = recoveryToken;
        _credentials = credentials;
        _pairings = new PairingSessions(timeProvider);
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
            var path = context.Request.Url?.AbsolutePath;
            if (context.Request.HttpMethod == "POST" && path == "/transport/v1/pair")
            {
                await HandlePairAsync(context, deadline.Token);
                return;
            }

            var authenticated = Authenticate(context.Request);
            if (authenticated is null)
            {
                await WriteErrorAsync(context.Response, HttpStatusCode.Unauthorized, "Unauthorized daemon client.", SnookErrorCode.StoreUnavailable, cancellationToken);
                return;
            }
            if (authenticated.IsPending)
            {
                var activates = context.Request.HttpMethod == "POST" && path == "/v1/call"
                    || context.Request.HttpMethod == "GET" && path == "/v1/changes";
                var allowedWithoutActivation = context.Request.HttpMethod == "GET" && path == "/v1/health"
                    || context.Request.HttpMethod == "POST" && path == "/transport/v1/pairings/cancel";
                if (activates)
                {
                    if (!await _credentials.PromoteAsync(authenticated.CredentialId,
                        _pairings.UtcNow, deadline.Token))
                    {
                        await WriteErrorAsync(context.Response, HttpStatusCode.Unauthorized,
                            "Unauthorized daemon client.", SnookErrorCode.StoreUnavailable,
                            cancellationToken);
                        return;
                    }
                    authenticated = authenticated with { IsPending = false };
                }
                else if (!allowedWithoutActivation)
                {
                    await WriteErrorAsync(context.Response, HttpStatusCode.Unauthorized,
                        "Unauthorized daemon client.", SnookErrorCode.StoreUnavailable, cancellationToken);
                    return;
                }
            }
            if (!authenticated.IsRecoveryAdministrator && !authenticated.IsPending)
                await _credentials.TouchAsync(authenticated.CredentialId, _pairings.UtcNow, deadline.Token);

            if (path?.StartsWith("/transport/v1/admin/", StringComparison.Ordinal) == true && !authenticated.IsRecoveryAdministrator)
            {
                await WriteErrorAsync(context.Response, HttpStatusCode.Unauthorized, "Unauthorized daemon client.", SnookErrorCode.StoreUnavailable, cancellationToken);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/transport/v1/pairings/cancel")
            {
                await HandleSelfCancelAsync(context, authenticated, deadline.Token);
                return;
            }

            if (path?.StartsWith("/transport/v1/admin/", StringComparison.Ordinal) == true)
            {
                await HandleAdministrationAsync(context, path, deadline.Token);
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/v1/health")
            {
                var workspace = (await _backend.GetBootstrapAsync(deadline.Token)).Workspace;
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { ready = true,
                    contract = $"{ContractInfo.Major}.{ContractInfo.Minor}", serverId = _credentials.ServerId,
                    workspaceId = workspace.Id }, cancellationToken);
                return;
            }

            if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/v1/changes")
            {
                await HandleChangesAsync(context, authenticated, cancellationToken);
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

    private async Task HandleAdministrationAsync(HttpListenerContext context, string path, CancellationToken cancellationToken)
    {
        if (context.Request.HttpMethod == "POST" && path == "/transport/v1/admin/pairings")
        {
            var request = await ReadJsonAsync<CreatePairingRequest>(context.Request, 16 * 1024, cancellationToken);
            if (!Uri.TryCreate(request.ServeOrigin, UriKind.Absolute, out var serveOrigin))
                throw new SnookException(SnookErrorCode.ValidationFailed, "A canonical Tailscale Serve origin is required.");
            var invitation = _pairings.Arm(request.DeviceName, serveOrigin, request.IntendedTailscaleLogin);
            await WriteJsonAsync(context.Response, HttpStatusCode.OK, invitation, cancellationToken);
            return;
        }
        if (context.Request.HttpMethod == "POST" && path == "/transport/v1/admin/pairings/cancel")
        {
            var request = await ReadJsonAsync<CancelPairingRequest>(context.Request, 4096, cancellationToken);
            _pairings.Cancel(request.PairingCode);
            await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { cancelled = true }, cancellationToken);
            return;
        }
        if (context.Request.HttpMethod == "GET" && path == "/transport/v1/admin/devices")
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.OK, _credentials.List(), cancellationToken);
            return;
        }
        if (context.Request.HttpMethod == "POST" && path == "/transport/v1/admin/devices/revoke")
        {
            var request = await ReadJsonAsync<RevokeDeviceRequest>(context.Request, 4096, cancellationToken);
            await _credentials.RevokeAsync(request.CredentialId, cancellationToken);
            DisconnectCredential(request.CredentialId);
            await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { revoked = true }, cancellationToken);
            return;
        }
        await WriteErrorAsync(context.Response, HttpStatusCode.NotFound, "Daemon route was not found.", SnookErrorCode.NotFound, cancellationToken);
    }

    private async Task HandlePairAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        if (!await _pairingDispatch.WaitAsync(0, cancellationToken))
        {
            await PairingDeniedAsync(context, cancellationToken);
            return;
        }
        try
        {
            var request = await ReadJsonAsync<RedeemPairingRequest>(context.Request, 16 * 1024, cancellationToken);
            var login = PairingSessions.NormalizeLogin(context.Request.Headers["Tailscale-User-Login"]);
            var remote = context.Request.RemoteEndPoint?.Address.ToString() ?? "unknown";
            var session = _pairings.Redeem(request.PairingCode, $"{login ?? "anonymous"}|{remote}", login,
                request.DeviceName);
            if (session is null) { await PairingDeniedAsync(context, cancellationToken); return; }
            var deviceName = session.DeviceName;
            var issued = await _credentials.CreateAsync(deviceName, login, _pairings.UtcNow, cancellationToken);
            try
            {
                var workspace = (await _backend.GetBootstrapAsync(cancellationToken)).Workspace;
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new
                {
                    serveOrigin = session.ServeOrigin.AbsoluteUri,
                    serverId = _credentials.ServerId,
                    workspaceId = workspace.Id,
                    workspaceName = workspace.Name,
                    contract = $"{ContractInfo.Major}.{ContractInfo.Minor}",
                    credentialId = issued.Id,
                    deviceName,
                    tailscaleLogin = login,
                    credential = issued.Credential
                }, cancellationToken);
            }
            catch
            {
                // A client that never receives the one-time response cannot
                // self-cancel. Fail closed by revoking the just-issued record.
                await _credentials.RevokeAsync(issued.Id, CancellationToken.None);
                throw;
            }
        }
        finally { _pairingDispatch.Release(); }
    }

    private async Task HandleSelfCancelAsync(HttpListenerContext context, AuthenticatedDevice authenticated, CancellationToken cancellationToken)
    {
        if (authenticated.IsRecoveryAdministrator)
        {
            await PairingDeniedAsync(context, cancellationToken);
            return;
        }
        var request = await ReadJsonAsync<RevokeDeviceRequest>(context.Request, 4096, cancellationToken);
        if (request.CredentialId != authenticated.CredentialId) { await PairingDeniedAsync(context, cancellationToken); return; }
        await _credentials.RevokeAsync(request.CredentialId, cancellationToken);
        DisconnectCredential(request.CredentialId);
        await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { revoked = true }, cancellationToken);
    }

    private static Task PairingDeniedAsync(HttpListenerContext context, CancellationToken cancellationToken)
        => WriteErrorAsync(context.Response, HttpStatusCode.Unauthorized,
            "Pairing was not accepted. The invitation may be invalid, expired, already used, restricted to another login, or rate-limited.",
            SnookErrorCode.StoreUnavailable, cancellationToken);

    private async Task HandleChangesAsync(HttpListenerContext context, AuthenticatedDevice authenticated,
        CancellationToken cancellationToken)
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
            _changeSubscribers[id] = new(authenticated.CredentialId, queue);
            if (!authenticated.IsRecoveryAdministrator && !_credentials.IsActive(authenticated.CredentialId))
            {
                queue.Writer.TryComplete();
                _changeSubscribers.TryRemove(id, out _);
            }
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
            if (!subscriber.Value.Queue.Writer.TryWrite(payload))
            {
                // Disconnect on overflow; reconnecting clients obtain a fresh snapshot.
                subscriber.Value.Queue.Writer.TryComplete();
                _changeSubscribers.TryRemove(subscriber.Key, out _);
            }
        }
    }

    private void DisconnectCredential(Guid credentialId)
    {
        foreach (var subscriber in _changeSubscribers.ToArray())
        {
            if (subscriber.Value.CredentialId != credentialId) continue;
            subscriber.Value.Queue.Writer.TryComplete();
            _changeSubscribers.TryRemove(subscriber.Key, out _);
        }
    }

    private async Task HandleCallAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var body = await ReadRequestBodyAsync(context.Request, cancellationToken);
        using var document = JsonDocument.Parse(body,
            new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Disallow });
        RejectDuplicateJsonProperties(document.RootElement);
        var envelopeProperties = document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement.EnumerateObject().Select(property => property.Name).ToArray()
            : [];
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || envelopeProperties.Length != 2
            || envelopeProperties.Any(name => name is not ("method" or "args"))
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

    private static async Task<byte[]> ReadRequestBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken,
        int maximumBytes = MaxRequestBytes)
    {
        if (request.ContentLength64 > maximumBytes)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, $"Daemon request body exceeds its {maximumBytes}-byte limit.");
        }

        await using var input = request.InputStream;
        await using var body = new MemoryStream(capacity: request.ContentLength64 is > 0 && request.ContentLength64 <= maximumBytes
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
            if (total > maximumBytes)
            {
                throw new SnookException(SnookErrorCode.ValidationFailed, $"Daemon request body exceeds its {maximumBytes}-byte limit.");
            }

            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return body.ToArray();
    }

    private static async Task<T> ReadJsonAsync<T>(HttpListenerRequest request, int maximumBytes, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await ReadRequestBodyAsync(request, cancellationToken, maximumBytes),
            new JsonDocumentOptions { MaxDepth = 8, CommentHandling = JsonCommentHandling.Disallow });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new SnookException(SnookErrorCode.ValidationFailed, "Request JSON is invalid.");
        RejectDuplicateJsonProperties(document.RootElement);
        return document.RootElement.Deserialize<T>(JsonOptions)
            ?? throw new SnookException(SnookErrorCode.ValidationFailed, "Request JSON is invalid.");
    }

    private static void RejectDuplicateJsonProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new SnookException(SnookErrorCode.ValidationFailed,
                        "Request JSON contains duplicate fields.");
                RejectDuplicateJsonProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateJsonProperties(item);
    }

    private AuthenticatedDevice? Authenticate(HttpListenerRequest request)
    {
        var supplied = request.Headers["X-Snook-Token"] ?? string.Empty;
        if (supplied.Length is < 32 or > 512) return null;
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var recoveryBytes = Encoding.UTF8.GetBytes(_recoveryToken);
        var suppliedHash = SHA256.HashData(suppliedBytes);
        var recoveryHash = SHA256.HashData(recoveryBytes);
        try
        {
            if (CryptographicOperations.FixedTimeEquals(suppliedHash, recoveryHash))
                return new AuthenticatedDevice(Guid.Empty,
                    PairingSessions.NormalizeLogin(request.Headers["Tailscale-User-Login"]), true, false);
            return _credentials.Authenticate(supplied,
                PairingSessions.NormalizeLogin(request.Headers["Tailscale-User-Login"]), _pairings.UtcNow);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(suppliedBytes);
            CryptographicOperations.ZeroMemory(recoveryBytes);
            CryptographicOperations.ZeroMemory(suppliedHash);
            CryptographicOperations.ZeroMemory(recoveryHash);
        }
    }

    private static async Task WriteErrorAsync(HttpListenerResponse response, HttpStatusCode status, string message, SnookErrorCode code, CancellationToken cancellationToken)
        => await WriteJsonAsync(response, status, new { error = new { code = code.ToString(), message } }, cancellationToken);

    private static async Task WriteJsonAsync(HttpListenerResponse response, HttpStatusCode status, object payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
        response.StatusCode = (int)status;
        response.ContentType = "application/json";
        response.Headers["Cache-Control"] = "no-store";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
        await response.OutputStream.FlushAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _backend.Changed -= OnBackendChanged;
        _listener.Close();
        foreach (var subscriber in _changeSubscribers.Values) subscriber.Queue.Writer.TryComplete();
        _changeSubscribers.Clear();
        _pairings.Clear();
        _dispatch.Dispose();
        _subscriptions.Dispose();
        _pairingDispatch.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record CreatePairingRequest(string DeviceName, string ServeOrigin, string? IntendedTailscaleLogin);
    private sealed record CancelPairingRequest(string PairingCode);
    private sealed record RedeemPairingRequest(string PairingCode, string DeviceName);
    private sealed record RevokeDeviceRequest(Guid CredentialId);
    private sealed record ChangeSubscriber(Guid CredentialId, Channel<string> Queue);
}
