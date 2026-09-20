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
                Console.WriteLine("snookd: --data-dir PATH --port PORT. Environment: SNOOK_DATA_DIR, SNOOK_DAEMON_PORT, SNOOK_TRUST_TAILSCALE_SERVE_HEADERS=0|1. Ctrl-C/SIGTERM stops gracefully.");
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
            var bootstrap = await backend.GetBootstrapAsync(cancellationSource.Token);
            var token = await LoadOrCreateTokenAsync(Path.Combine(snookRoot, "daemon.token"));
            var instanceId = await LoadOrCreateInstanceIdAsync(Path.Combine(snookRoot, "daemon.instance"), cancellationSource.Token);
            using var pairings = new DaemonPairingRegistry(Path.Combine(snookRoot, "android-pairings.json"));
            var trustServeHeaders = ReadServeHeaderTrust();
            await using var server = new DaemonRpcServer(backend, token, port, pairings,
                instanceId, bootstrap.Workspace.Id, trustServeHeaders, snookRoot);
            server.Start();
            var descriptorPath = Path.Combine(snookRoot, "daemon.endpoint.json");
            var descriptor = new DaemonEndpointDescriptor(1, instanceId, server.Endpoint.AbsoluteUri, ContractInfo.Major, ContractInfo.Minor);
            await WriteDescriptorAsync(descriptorPath, descriptor, cancellationSource.Token);
            try
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    ready = true,
                    contract = $"{ContractInfo.Major}.{ContractInfo.Minor}",
                    endpoint = server.Endpoint,
                    instanceId = descriptor.InstanceId,
                    tokenFile = "daemon.token",
                    tailnetPairingEnabled = trustServeHeaders
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

    private static bool ReadServeHeaderTrust()
    {
        var value = Environment.GetEnvironmentVariable("SNOOK_TRUST_TAILSCALE_SERVE_HEADERS");
        return value switch
        {
            null or "0" => false,
            "1" => true,
            _ => throw new SnookException(SnookErrorCode.ValidationFailed,
                "SNOOK_TRUST_TAILSCALE_SERVE_HEADERS must be 0 or 1.")
        };
    }

    private static async Task<Guid> LoadOrCreateInstanceIdAsync(string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "Daemon instance identity cannot be a symbolic link.");
        if (File.Exists(path))
        {
            if (info.Length is <= 0 or > 128 || !OperatingSystem.IsWindows()
                && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                    | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite
                    | UnixFileMode.OtherExecute)) != 0)
                throw new SnookException(SnookErrorCode.ValidationFailed,
                    "Daemon instance identity requires private file permissions and bounded contents.");
            var text = (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
            if (!Guid.TryParseExact(text, "D", out var existing) || existing == Guid.Empty)
                throw new SnookException(SnookErrorCode.ValidationFailed,
                    "Daemon instance identity is invalid.");
            return existing;
        }

        var instanceId = Guid.NewGuid();
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(instanceId.ToString("D").AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return instanceId;
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
    private const int MaxPendingRestoreUploads = 4;
    private static readonly TimeSpan RestoreUploadLifetime = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IBackendClient _backend;
    private readonly string _token;
    private readonly DaemonPairingRegistry _pairings;
    private readonly Guid _instanceId;
    private readonly object _workspaceIdentitySync = new();
    private Guid _workspaceId;
    private int _restoreInProgress;
    private readonly bool _trustServeHeaders;
    private readonly string _operationalRoot;
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, System.Reflection.MethodInfo> _contractMethods = typeof(IBackendClient).GetMethods()
        .Where(method => typeof(Task).IsAssignableFrom(method.ReturnType))
        .ToDictionary(method => method.Name, StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, ChangeSubscriber> _changeSubscribers = new();
    private readonly ConcurrentDictionary<Guid, RestoreUpload> _restoreUploads = new();
    private readonly SemaphoreSlim _dispatch = new(1, 1);
    private readonly SemaphoreSlim _subscriptions = new(MaxSubscribers, MaxSubscribers);

    public DaemonRpcServer(IBackendClient backend, string token, int port,
        DaemonPairingRegistry pairings, Guid instanceId, Guid workspaceId,
        bool trustServeHeaders, string operationalRoot)
    {
        _backend = backend;
        _token = token;
        _pairings = pairings;
        _instanceId = instanceId;
        _workspaceId = workspaceId;
        _trustServeHeaders = trustServeHeaders;
        _operationalRoot = Path.GetFullPath(operationalRoot);
        CleanupOrphanedTransferDirectories(_operationalRoot);
        Endpoint = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(Endpoint.AbsoluteUri);
        _backend.Changed += OnBackendChanged;
        _pairings.CredentialRevoked += OnCredentialRevoked;
    }

    public Uri Endpoint { get; }

    private Guid CurrentWorkspaceId
    {
        get { lock (_workspaceIdentitySync) return _workspaceId; }
    }

    private void SetCurrentWorkspaceId(Guid workspaceId)
    {
        if (workspaceId == Guid.Empty) throw new InvalidDataException("Restored workspace identity is missing.");
        lock (_workspaceIdentitySync) _workspaceId = workspaceId;
    }

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
            var path = context.Request.Url?.AbsolutePath ?? string.Empty;
            if (context.Request.HttpMethod == "POST" && path == "/v1/pairing/begin")
            {
                await HandlePairingBeginAsync(context, deadline.Token);
                return;
            }
            if (context.Request.HttpMethod == "POST" && path == "/v1/pairing/complete")
            {
                await HandlePairingCompleteAsync(context, deadline.Token);
                return;
            }
            var selfPairing = path is "/v1/pairing/self/rename" or "/v1/pairing/self/revoke";
            if (path.StartsWith("/v1/pairing/", StringComparison.Ordinal) && !selfPairing)
            {
                if (!IsAdminAuthorized(context.Request))
                {
                    await WriteErrorAsync(context.Response, HttpStatusCode.Unauthorized,
                        "Unauthorized pairing administrator.", SnookErrorCode.StoreUnavailable, cancellationToken);
                    return;
                }
                if (context.Request.HttpMethod == "POST" && path == "/v1/pairing/challenges")
                {
                    await HandlePairingChallengeAsync(context, deadline.Token);
                    return;
                }
                if (context.Request.HttpMethod == "GET" && path == "/v1/pairing/devices")
                {
                    await WriteJsonAsync(context.Response, HttpStatusCode.OK,
                        new { devices = await _pairings.ListAsync(deadline.Token) }, deadline.Token);
                    return;
                }
                if (context.Request.HttpMethod == "POST" && path == "/v1/pairing/revoke")
                {
                    await HandlePairingRevokeAsync(context, deadline.Token);
                    return;
                }
                if (context.Request.HttpMethod == "POST" && path == "/v1/pairing/rename")
                {
                    await HandlePairingRenameAsync(context, deadline.Token);
                    return;
                }
                await WriteErrorAsync(context.Response, HttpStatusCode.NotFound,
                    "Pairing route was not found.", SnookErrorCode.NotFound, cancellationToken);
                return;
            }

            var authorization = await AuthorizeAsync(context.Request, deadline.Token);
            if (authorization is null)
            {
                await WriteErrorAsync(context.Response, HttpStatusCode.Unauthorized, "Unauthorized daemon client.", SnookErrorCode.StoreUnavailable, cancellationToken);
                return;
            }

            if (selfPairing)
            {
                if (authorization.CredentialId is not { } credentialId)
                {
                    await WriteErrorAsync(context.Response, HttpStatusCode.Forbidden,
                        "A paired device credential is required.", SnookErrorCode.StoreUnavailable,
                        cancellationToken);
                    return;
                }
                if (context.Request.HttpMethod != "POST")
                {
                    await WriteErrorAsync(context.Response, HttpStatusCode.MethodNotAllowed,
                        "The paired-device operation requires POST.", SnookErrorCode.ValidationFailed,
                        cancellationToken);
                    return;
                }
                if (path == "/v1/pairing/self/rename")
                {
                    var request = await ReadPairingAsync<RenameSelfRequest>(context.Request,
                        deadline.Token);
                    await WriteJsonAsync(context.Response, HttpStatusCode.OK,
                        await _pairings.RenameAsync(credentialId, request.DeviceName,
                            deadline.Token), deadline.Token);
                    return;
                }
                var revoked = await _pairings.RevokeAsync(credentialId, deadline.Token);
                await WriteJsonAsync(context.Response, HttpStatusCode.OK,
                    new { credentialId, revoked }, deadline.Token);
                return;
            }

            if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/v1/health")
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { ready = true, contract = $"{ContractInfo.Major}.{ContractInfo.Minor}" }, cancellationToken);
                return;
            }

            if (path.StartsWith("/v1/transfers/", StringComparison.Ordinal))
            {
                using var transferDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                transferDeadline.CancelAfter(TimeSpan.FromMinutes(5));
                await HandleTransferAsync(context, path, authorization, transferDeadline.Token);
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/v1/changes")
            {
                await HandleChangesAsync(context, authorization, cancellationToken);
                return;
            }

            if (context.Request.HttpMethod == "POST" && context.Request.Url?.AbsolutePath == "/v1/call")
            {
                await HandleCallAsync(context, authorization, deadline.Token);
                return;
            }

            await WriteErrorAsync(context.Response, HttpStatusCode.NotFound, "Daemon route was not found.", SnookErrorCode.NotFound, cancellationToken);
        }
        catch (Exception exception)
        {
            var actual = exception is System.Reflection.TargetInvocationException { InnerException: not null } invocation
                ? invocation.InnerException
                : exception;
            var code = actual is UnauthorizedAccessException ? SnookErrorCode.StoreUnavailable
                : actual is SnookException snook ? snook.Code
                : actual is JsonException ? SnookErrorCode.ValidationFailed : SnookErrorCode.InternalError;
            var status = actual is UnauthorizedAccessException ? HttpStatusCode.Unauthorized
                : actual is SnookException { Code: not SnookErrorCode.InternalError } or JsonException ? HttpStatusCode.BadRequest
                : actual is OperationCanceledException ? HttpStatusCode.RequestTimeout : HttpStatusCode.InternalServerError;
            var message = actual is UnauthorizedAccessException ? "Unauthorized daemon client."
                : actual is SnookException { Code: not SnookErrorCode.InternalError } ? actual.Message
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

    private async Task HandlePairingChallengeAsync(HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        EnsurePairingEnabled();
        var request = await ReadPairingAsync<CreateChallengeRequest>(context.Request, cancellationToken);
        if (!Uri.TryCreate(request.ServeOrigin, UriKind.Absolute, out var origin))
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "serveOrigin must be an absolute HTTPS *.ts.net origin.");
        var challenge = await _pairings.CreateChallengeAsync(origin,
            _instanceId, CurrentWorkspaceId, cancellationToken);
        await WriteJsonAsync(context.Response, HttpStatusCode.OK, challenge, cancellationToken);
    }

    private async Task HandleTransferAsync(HttpListenerContext context, string path,
        RequestAuthorization authorization, CancellationToken cancellationToken)
    {
        CleanupRestoreUploads();
        var isBackup = context.Request.HttpMethod == "GET" && path == "/v1/transfers/backup";
        var isJson = context.Request.HttpMethod == "GET" && path == "/v1/transfers/json";
        var isCsv = context.Request.HttpMethod == "POST" && path == "/v1/transfers/csv";
        var isRestoreStage = context.Request.HttpMethod == "POST"
            && path == "/v1/transfers/restore/stage";
        var isRestoreCommit = context.Request.HttpMethod == "POST"
            && path == "/v1/transfers/restore/commit";
        var isRestoreDiscard = context.Request.HttpMethod == "POST"
            && path == "/v1/transfers/restore/discard";
        if (!isBackup && !isJson && !isCsv && !isRestoreStage && !isRestoreCommit
            && !isRestoreDiscard)
            throw new SnookException(SnookErrorCode.NotFound, "Document transfer route was not found.");
        if (isRestoreStage || isRestoreCommit || isRestoreDiscard)
        {
            if (authorization.CredentialId is not { } credentialId)
                throw new SnookException(SnookErrorCode.ValidationFailed,
                    "Remote restore requires a paired device credential.");
            if (isRestoreStage)
                await StageRemoteRestoreAsync(context, authorization, credentialId,
                    cancellationToken).ConfigureAwait(false);
            else if (isRestoreCommit)
                await CommitRemoteRestoreAsync(context, authorization, credentialId,
                    cancellationToken).ConfigureAwait(false);
            else
                await DiscardRemoteRestoreAsync(context, credentialId,
                    cancellationToken).ConfigureAwait(false);
            return;
        }

        var staging = Path.Combine(_operationalRoot, ".transfer-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(staging);
        else Directory.CreateDirectory(staging,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            string artifact;
            string contentType;
            long maximum;
            if (isBackup)
            {
                artifact = await WorkspaceBackupBundle.CreateAsync(_backend, staging,
                    cancellationToken).ConfigureAwait(false);
                contentType = "application/zip";
                maximum = WorkspaceBackupBundle.MaximumBundleBytes;
            }
            else if (isJson)
            {
                artifact = Path.Combine(staging, "workspace.json");
                var result = await _backend.ExportJsonAsync(artifact, cancellationToken).ConfigureAwait(false);
                if (result.Path != artifact) throw new InvalidDataException("Unexpected JSON export destination.");
                contentType = "application/json";
                maximum = WorkspaceBackupBundle.MaximumDatabaseBytes;
            }
            else
            {
                var request = await ReadPairingAsync<CsvTransferRequest>(context.Request,
                    cancellationToken).ConfigureAwait(false);
                artifact = Path.Combine(staging, "worklog.csv");
                var result = await _backend.ExportCsvAsync(artifact, request.RangeStartUtc,
                    request.RangeEndUtc, cancellationToken).ConfigureAwait(false);
                if (result.Path != artifact) throw new InvalidDataException("Unexpected CSV export destination.");
                contentType = "text/csv";
                maximum = WorkspaceBackupBundle.MaximumDatabaseBytes;
            }
            var length = new FileInfo(artifact).Length;
            if (length is < 1 || length > maximum)
                throw new InvalidDataException("Document transfer size is invalid.");
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = length;
            await using var source = new FileStream(artifact, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(context.Response.OutputStream, 65_536,
                cancellationToken).ConfigureAwait(false);
            await context.Response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A private stale transfer directory contains only a derived
                // artifact and is safe to clean during operator maintenance.
            }
        }
    }

    private async Task StageRemoteRestoreAsync(HttpListenerContext context,
        RequestAuthorization authorization, Guid credentialId, CancellationToken cancellationToken)
    {
        if (_restoreUploads.Count >= MaxPendingRestoreUploads)
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                "Too many restore documents are awaiting confirmation. Let one expire or finish it first.");
        if (context.Request.ContentLength64 > WorkspaceBackupBundle.MaximumBundleBytes)
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "The restore document exceeds the bounded upload size.");
        var uploadId = Guid.NewGuid();
        var staging = Path.Combine(_operationalRoot, ".restore-upload-" + uploadId.ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(staging);
        else Directory.CreateDirectory(staging,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var retained = false;
        try
        {
            var database = await WorkspaceBackupBundle.StageAsync(context.Request.InputStream,
                staging, cancellationToken).ConfigureAwait(false);
            var incoming = await _backend.InspectBackupAsync(database, cancellationToken)
                .ConfigureAwait(false);
            var current = await _backend.GetBootstrapAsync(cancellationToken).ConfigureAwait(false);
            if (current.Workspace.Id != authorization.WorkspaceId
                || current.Workspace.Id != CurrentWorkspaceId)
                throw new SnookException(SnookErrorCode.RevisionConflict,
                    "The daemon workspace changed while the backup was staged. Review it again.");
            var stage = new RemoteBackupStage(uploadId, current.Workspace.Id,
                current.CommittedCursor, incoming, DateTimeOffset.UtcNow + RestoreUploadLifetime);
            if (!_restoreUploads.TryAdd(uploadId,
                new RestoreUpload(stage, credentialId, staging, database)))
                throw new SnookException(SnookErrorCode.StoreUnavailable,
                    "The restore upload could not be retained for confirmation.");
            retained = true;
            await WriteJsonAsync(context.Response, HttpStatusCode.OK, stage,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!retained) DeleteRestoreDirectory(staging);
        }
    }

    private async Task CommitRemoteRestoreAsync(HttpListenerContext context,
        RequestAuthorization authorization, Guid credentialId, CancellationToken cancellationToken)
    {
        var reviewed = await ReadPairingAsync<RemoteBackupStage>(context.Request,
            cancellationToken).ConfigureAwait(false);
        if (reviewed.UploadId == Guid.Empty
            || !_restoreUploads.TryGetValue(reviewed.UploadId, out var upload)
            || upload.CredentialId != credentialId || upload.Stage != reviewed
            || upload.Stage.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw new SnookException(SnookErrorCode.RevisionConflict,
                "The reviewed restore upload is missing, expired, changed, or belongs to another device.");

        var dispatched = false;
        await _dispatch.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (authorization.WorkspaceId != CurrentWorkspaceId)
                throw new SnookException(SnookErrorCode.RevisionConflict,
                    "The daemon workspace changed. Review the backup again.");
            var current = await _backend.GetBootstrapAsync(cancellationToken).ConfigureAwait(false);
            if (current.Workspace.Id != reviewed.CurrentWorkspaceId
                || current.CommittedCursor != reviewed.CurrentCursor)
                throw new SnookException(SnookErrorCode.RevisionConflict,
                    "The current workspace changed. Review the backup and confirm again.");
            var incoming = await _backend.InspectBackupAsync(upload.DatabasePath,
                cancellationToken).ConfigureAwait(false);
            if (incoming != reviewed.Incoming)
                throw new SnookException(SnookErrorCode.RevisionConflict,
                    "The staged backup changed. Select and review it again.");
            cancellationToken.ThrowIfCancellationRequested();
            if (!_restoreUploads.TryRemove(reviewed.UploadId, out _))
                throw new SnookException(SnookErrorCode.RevisionConflict,
                    "The restore upload was already consumed.");
            dispatched = true;
            Interlocked.Exchange(ref _restoreInProgress, 1);
            CloseAllChangeStreams();
            var result = await _backend.RestoreBackupAsync(upload.DatabasePath,
                cancellationToken).ConfigureAwait(false);
            var newWorkspaceId = incoming.Workspace.Id;
            SetCurrentWorkspaceId(newWorkspaceId);
            var rebound = await _pairings.RebindWorkspaceAsync(credentialId,
                current.Workspace.Id, newWorkspaceId, CancellationToken.None).ConfigureAwait(false);
            CloseAllChangeStreams();
            var restored = await _backend.GetBootstrapAsync(CancellationToken.None).ConfigureAwait(false);
            if (restored.Workspace.Id != newWorkspaceId)
                throw new SnookException(SnookErrorCode.StoreUnavailable,
                    "The restored workspace identity could not be confirmed.");
            await WriteJsonAsync(context.Response, HttpStatusCode.OK,
                new RemoteRestoreResult(new RestoreResult(string.Empty, result.Bytes, result.Sha256),
                    newWorkspaceId, rebound.RevokedOtherDeviceCount),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            if (dispatched)
                await ReconcileRestoreIdentityAsync(credentialId, reviewed,
                    CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _dispatch.Release();
            if (dispatched) Interlocked.Exchange(ref _restoreInProgress, 0);
            if (dispatched) DeleteRestoreDirectory(upload.DirectoryPath);
        }
    }

    private async Task DiscardRemoteRestoreAsync(HttpListenerContext context,
        Guid credentialId, CancellationToken cancellationToken)
    {
        var request = await ReadPairingAsync<DiscardRestoreRequest>(context.Request,
            cancellationToken).ConfigureAwait(false);
        var discarded = false;
        if (request.UploadId != Guid.Empty
            && _restoreUploads.TryGetValue(request.UploadId, out var upload)
            && upload.CredentialId == credentialId
            && _restoreUploads.TryRemove(request.UploadId, out var removed))
        {
            DeleteRestoreDirectory(removed.DirectoryPath);
            discarded = true;
        }
        await WriteJsonAsync(context.Response, HttpStatusCode.OK,
            new { request.UploadId, discarded }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileRestoreIdentityAsync(Guid credentialId,
        RemoteBackupStage reviewed, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _backend.GetBootstrapAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot.Workspace.Id != reviewed.Incoming.Workspace.Id) return;
            SetCurrentWorkspaceId(snapshot.Workspace.Id);
            await _pairings.RebindWorkspaceAsync(credentialId, reviewed.CurrentWorkspaceId,
                snapshot.Workspace.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // CurrentWorkspaceId already fails old credentials closed when the
            // restored identity was observable. Operator repair may be required.
            global::System.Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                restoreRepair = false,
                error = exception.GetType().Name
            }, JsonOptions));
        }
    }

    private void CleanupRestoreUploads()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _restoreUploads.ToArray())
        {
            if (pair.Value.Stage.ExpiresAtUtc > now
                || !_restoreUploads.TryRemove(pair.Key, out var removed)) continue;
            DeleteRestoreDirectory(removed.DirectoryPath);
        }
    }

    private static void DeleteRestoreDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void CleanupOrphanedTransferDirectories(string operationalRoot)
    {
        foreach (var pattern in new[] { ".restore-upload-*", ".transfer-*" })
        {
            foreach (var path in Directory.EnumerateDirectories(operationalRoot, pattern,
                SearchOption.TopDirectoryOnly))
            {
                var info = new DirectoryInfo(path);
                var prefix = pattern[..^1];
                var suffix = info.Name.StartsWith(prefix, StringComparison.Ordinal)
                    ? info.Name[prefix.Length..] : string.Empty;
                if (info.LinkTarget is not null || suffix.Length != 32
                    || !suffix.All(char.IsAsciiHexDigit)) continue;
                try { info.Delete(recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private void CloseAllChangeStreams()
    {
        foreach (var subscriber in _changeSubscribers.ToArray())
        {
            subscriber.Value.Queue.Writer.TryComplete();
            _changeSubscribers.TryRemove(subscriber.Key, out _);
        }
    }

    private async Task HandlePairingBeginAsync(HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        var identity = RequireTailnetIdentity(context.Request);
        var request = await ReadPairingAsync<BeginPairingRequest>(context.Request, cancellationToken);
        var preview = await _pairings.BeginAsync(request.Code, request.InstallationId,
            request.DeviceName, identity, _instanceId, CurrentWorkspaceId, cancellationToken);
        await WriteJsonAsync(context.Response, HttpStatusCode.OK, preview, cancellationToken);
    }

    private async Task HandlePairingCompleteAsync(HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        var identity = RequireTailnetIdentity(context.Request);
        var request = await ReadPairingAsync<CompletePairingRequest>(context.Request, cancellationToken);
        var credential = await _pairings.CompleteAsync(request.EnrollmentId,
            identity, _instanceId, CurrentWorkspaceId, cancellationToken);
        await WriteJsonAsync(context.Response, HttpStatusCode.OK, credential, cancellationToken);
    }

    private async Task HandlePairingRevokeAsync(HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        var request = await ReadPairingAsync<RevokePairingRequest>(context.Request, cancellationToken);
        var revoked = await _pairings.RevokeAsync(request.CredentialId, cancellationToken);
        await WriteJsonAsync(context.Response, HttpStatusCode.OK,
            new { credentialId = request.CredentialId, revoked }, cancellationToken);
    }

    private async Task HandlePairingRenameAsync(HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        var request = await ReadPairingAsync<RenamePairingRequest>(context.Request, cancellationToken);
        await WriteJsonAsync(context.Response, HttpStatusCode.OK,
            await _pairings.RenameAsync(request.CredentialId, request.DeviceName,
                cancellationToken), cancellationToken);
    }

    private void EnsurePairingEnabled()
    {
        if (!_trustServeHeaders)
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                "Tailnet pairing is disabled until the operator enables the reviewed Tailscale Serve trust boundary.");
    }

    private string RequireTailnetIdentity(HttpListenerRequest request)
    {
        if (!_trustServeHeaders) throw new UnauthorizedAccessException();
        var identity = request.Headers["Tailscale-User-Login"];
        if (string.IsNullOrWhiteSpace(identity) || identity.Length > 254
            || identity.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw new UnauthorizedAccessException();
        return identity;
    }

    private static async Task<T> ReadPairingAsync<T>(HttpListenerRequest request,
        CancellationToken cancellationToken)
    {
        var body = await ReadRequestBodyAsync(request, cancellationToken, 8192);
        return JsonSerializer.Deserialize<T>(body, JsonOptions)
            ?? throw new JsonException("Pairing request is empty.");
    }

    private async Task HandleChangesAsync(HttpListenerContext context,
        RequestAuthorization authorization, CancellationToken cancellationToken)
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
            _changeSubscribers[id] = new(queue, authorization.CredentialId);
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

    private void OnCredentialRevoked(object? sender, Guid credentialId)
    {
        foreach (var subscriber in _changeSubscribers.ToArray())
        {
            if (subscriber.Value.CredentialId != credentialId) continue;
            subscriber.Value.Queue.Writer.TryComplete();
            _changeSubscribers.TryRemove(subscriber.Key, out _);
        }
    }

    private async Task HandleCallAsync(HttpListenerContext context,
        RequestAuthorization authorization, CancellationToken cancellationToken)
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
            if (authorization.CredentialId is not null
                && authorization.WorkspaceId != CurrentWorkspaceId)
                throw new SnookException(SnookErrorCode.StoreUnavailable,
                    "The daemon workspace changed. Reconnect and load a fresh snapshot.");
            if (authorization.CredentialId is not null && methodName is nameof(IBackendClient.CreateBackupAsync)
                or nameof(IBackendClient.ExportJsonAsync) or nameof(IBackendClient.ExportCsvAsync)
                or nameof(IBackendClient.InspectBackupAsync) or nameof(IBackendClient.RestoreBackupAsync))
                throw new SnookException(SnookErrorCode.ValidationFailed,
                    "Paired devices must use bounded document-transfer routes; server filesystem paths are prohibited.");
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

    private static async Task<byte[]> ReadRequestBodyAsync(HttpListenerRequest request,
        CancellationToken cancellationToken, int maximumBytes = MaxRequestBytes)
    {
        if (request.ContentLength64 > maximumBytes)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed,
                $"Daemon request body is limited to {maximumBytes} bytes.");
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
                throw new SnookException(SnookErrorCode.ValidationFailed,
                    $"Daemon request body is limited to {maximumBytes} bytes.");
            }

            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return body.ToArray();
    }

    private bool IsAdminAuthorized(HttpListenerRequest request)
    {
        var supplied = request.Headers["X-Snook-Token"] ?? string.Empty;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(_token));
    }

    private async Task<RequestAuthorization?> AuthorizeAsync(HttpListenerRequest request,
        CancellationToken cancellationToken)
    {
        var workspaceId = CurrentWorkspaceId;
        if (IsAdminAuthorized(request)) return new(null, workspaceId);
        if (Volatile.Read(ref _restoreInProgress) != 0) return null;
        if (!_trustServeHeaders) return null;
        var identity = request.Headers["Tailscale-User-Login"];
        var credential = request.Headers["X-Snook-Device-Token"];
        var credentialId = await _pairings.AuthenticateAsync(credential, identity,
            _instanceId, workspaceId, cancellationToken);
        return credentialId is null ? null : new(credentialId, workspaceId);
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
        _pairings.CredentialRevoked -= OnCredentialRevoked;
        _listener.Close();
        foreach (var subscriber in _changeSubscribers.Values) subscriber.Queue.Writer.TryComplete();
        _changeSubscribers.Clear();
        foreach (var upload in _restoreUploads.Values) DeleteRestoreDirectory(upload.DirectoryPath);
        _restoreUploads.Clear();
        _dispatch.Dispose();
        _subscriptions.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record RequestAuthorization(Guid? CredentialId, Guid WorkspaceId);
    private sealed record ChangeSubscriber(Channel<string> Queue, Guid? CredentialId);
    private sealed record RestoreUpload(RemoteBackupStage Stage, Guid CredentialId,
        string DirectoryPath, string DatabasePath);
    private sealed record CreateChallengeRequest(string ServeOrigin);
    private sealed record BeginPairingRequest(string Code, Guid InstallationId, string DeviceName);
    private sealed record CompletePairingRequest(Guid EnrollmentId);
    private sealed record RevokePairingRequest(Guid CredentialId);
    private sealed record RenamePairingRequest(Guid CredentialId, string DeviceName);
    private sealed record RenameSelfRequest(string DeviceName);
    private sealed record CsvTransferRequest(DateTimeOffset RangeStartUtc, DateTimeOffset RangeEndUtc);
    private sealed record DiscardRestoreRequest(Guid UploadId);
}
