using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LarkzeeChat.Models;
using LarkzeeChat.Services;

namespace LarkzeeChat.Networking;

/// <summary>
/// Owns the optional IPv4 listener and the single authenticated chat session.
/// </summary>
public sealed class ChatSessionManager : IAsyncDisposable
{
    public const int Port = 45678;

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan InboundActivityTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan AuthBlockDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ConnectionOperationTimeout = TimeSpan.FromSeconds(15);

    private readonly object _stateGate = new();
    private readonly object _failureGate = new();
    private readonly SemaphoreSlim _listenerGate = new(1, 1);
    private readonly SemaphoreSlim _connectionSetupGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Dictionary<string, FailureRecord> _authenticationFailures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _pendingInboundTasks = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private Task? _acceptLoopTask;
    private ConnectionContext? _activeConnection;
    private string? _configuredConnectionPassword;
    private string? _localConnectionCode;
    private bool _serverEnabled;
    private int _disposed;

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    public event EventHandler<ChatMessageReceivedEventArgs>? MessageReceived;

    public bool IsServerEnabled
    {
        get
        {
            lock (_stateGate)
            {
                return _serverEnabled;
            }
        }
    }

    public string? LocalConnectionKey
    {
        get
        {
            lock (_stateGate)
            {
                return _serverEnabled ? _configuredConnectionPassword : null;
            }
        }
    }

    /// <summary>
    /// The active listener's eight-character connection code. It is hidden
    /// while the listener is disabled so a stale code cannot be mistaken for
    /// a currently reachable endpoint.
    /// </summary>
    public string? LocalConnectionCode
    {
        get
        {
            lock (_stateGate)
            {
                return _serverEnabled ? _localConnectionCode : null;
            }
        }
    }

    /// <summary>
    /// The configured local password exposed under the future-facing name.
    /// It remains available while the listener is disabled so the UI can
    /// configure it before enabling the service.
    /// </summary>
    public string? LocalPassword
    {
        get
        {
            lock (_stateGate)
            {
                return _configuredConnectionPassword;
            }
        }
    }

    public bool SetConnectionPassword(string? password)
    {
        if (Volatile.Read(ref _disposed) != 0
            || !AuthenticationService.TryValidateManualPassword(password, out string validatedPassword))
        {
            return false;
        }

        lock (_stateGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            _configuredConnectionPassword = validatedPassword;
            _localConnectionCode = null;
            return true;
        }
    }

    /// <summary>
    /// Atomically changes the inbound password and its user-facing connection
    /// code. Existing authenticated sessions keep their negotiated keys;
    /// future handshakes read the new password under the same state lock.
    /// </summary>
    public bool SetConnectionCode(string? code)
    {
        if (Volatile.Read(ref _disposed) != 0
            || !ConnectionCodeService.TryDecode(
                code,
                out ConnectionCodeInfo connectionCode,
                out _))
        {
            return false;
        }

        lock (_stateGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            // Both values are assigned while holding the same gate used by
            // listener setup and inbound authentication.
            _configuredConnectionPassword = connectionCode.AuthenticationPassword;
            _localConnectionCode = connectionCode.Code;
            return true;
        }
    }

    /// <summary>
    /// Removes the configured inbound password while the listener is off.
    /// An established outbound encrypted session is unaffected.
    /// </summary>
    public bool ClearConnectionPassword()
    {
        lock (_stateGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || _serverEnabled)
            {
                return false;
            }

            _configuredConnectionPassword = null;
            _localConnectionCode = null;
            return true;
        }
    }

    public bool IsConnected
    {
        get
        {
            lock (_stateGate)
            {
                return _activeConnection is { IsAuthenticated: true } connection
                    && Volatile.Read(ref connection.CloseStarted) == 0;
            }
        }
    }

    public async Task<ServerStartResult> EnableServerAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0 || cancellationToken.IsCancellationRequested)
        {
            return new ServerStartResult(false, null);
        }

        try
        {
            await _listenerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ServerStartResult(false, null);
        }

        try
        {
            lock (_stateGate)
            {
                if (_serverEnabled)
                {
                    return new ServerStartResult(true, _configuredConnectionPassword);
                }

                if (!AuthenticationService.TryValidateManualPassword(
                        _configuredConnectionPassword,
                        out _))
                {
                    return new ServerStartResult(false, null);
                }
            }

            TcpListener listener = new(IPAddress.Any, Port);
            try
            {
                listener.Start();
            }
            catch (Exception exception) when (exception is SocketException or InvalidOperationException)
            {
                listener.Stop();
                return new ServerStartResult(false, null);
            }

            CancellationTokenSource listenerCts =
                CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            lock (_stateGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    listenerCts.Cancel();
                    listenerCts.Dispose();
                    listener.Stop();
                    return new ServerStartResult(false, null);
                }

                if (!AuthenticationService.TryValidateManualPassword(
                        _configuredConnectionPassword,
                        out string configuredPassword))
                {
                    listenerCts.Cancel();
                    listenerCts.Dispose();
                    listener.Stop();
                    return new ServerStartResult(false, null);
                }

                _listener = listener;
                _listenerCts = listenerCts;
                _serverEnabled = true;
                _acceptLoopTask = AcceptLoopAsync(listener, listenerCts.Token);
                return new ServerStartResult(true, configuredPassword);
            }
        }
        catch (Exception exception) when (exception is SocketException or InvalidOperationException)
        {
            Debug.WriteLine(exception);
            return new ServerStartResult(false, null);
        }
        finally
        {
            _listenerGate.Release();
        }
    }

    public async Task DisableServerAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            await _listenerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            // Once listenerGate is acquired, turning the listener OFF must
            // finish its setup/session cleanup even if the caller token is
            // cancelled.  Cancellation is only allowed to abort acquisition.
            await DisableServerCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _listenerGate.Release();
        }
    }

    public async Task<ConnectResult> ConnectAsync(
        string peerIp,
        string key,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new ConnectResult(false, ConnectFailureReason.Cancelled);
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return new ConnectResult(false, ConnectFailureReason.ConnectionFailed);
        }

        if (!AuthenticationService.TryValidateManualPassword(key, out string validatedPassword))
        {
            return new ConnectResult(false, ConnectFailureReason.AuthenticationFailed);
        }

        if (!IPAddress.TryParse(peerIp?.Trim(), out IPAddress? address)
            || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return new ConnectResult(false, ConnectFailureReason.InvalidAddress);
        }

        using CancellationTokenSource operationCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        operationCts.CancelAfter(ConnectionOperationTimeout);
        CancellationToken operationToken = operationCts.Token;

        try
        {
            await _connectionSetupGate.WaitAsync(operationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ConnectResult(
                false,
                cancellationToken.IsCancellationRequested
                    ? ConnectFailureReason.Cancelled
                    : ConnectFailureReason.ConnectionFailed);
        }

        TcpClient? client = null;
        ConnectionContext? context = null;
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return new ConnectResult(false, ConnectFailureReason.ConnectionFailed);
            }

            lock (_stateGate)
            {
                if (_activeConnection is not null)
                {
                    return new ConnectResult(false, ConnectFailureReason.AlreadyConnected);
                }
            }

            client = new TcpClient(AddressFamily.InterNetwork)
            {
                NoDelay = true
            };
            await client.ConnectAsync(address, Port, operationToken).ConfigureAwait(false);

            context = new ConnectionContext(client, ConnectionOrigin.Outbound);
            client = null;
            SetActiveConnection(context);

            ConnectFailureReason authenticationResult =
                await AuthenticateAsClientAsync(context, validatedPassword, operationToken).ConfigureAwait(false);
            if (authenticationResult != ConnectFailureReason.None)
            {
                await CloseConnectionAsync(context, ConnectionClosedReason.ConnectionLost, false, true)
                    .ConfigureAwait(false);
                return new ConnectResult(false, authenticationResult);
            }

            if (!StartSession(context))
            {
                await CloseConnectionAsync(context, ConnectionClosedReason.ConnectionLost, false, true)
                    .ConfigureAwait(false);
                return new ConnectResult(false, ConnectFailureReason.ConnectionFailed);
            }

            return new ConnectResult(true, ConnectFailureReason.None);
        }
        catch (OperationCanceledException)
        {
            if (context is not null)
            {
                await CloseConnectionAsync(context, ConnectionClosedReason.ConnectionLost, false, true)
                    .ConfigureAwait(false);
            }

            return new ConnectResult(
                false,
                cancellationToken.IsCancellationRequested
                    ? ConnectFailureReason.Cancelled
                    : ConnectFailureReason.ConnectionFailed);
        }
        catch (Exception exception) when (exception is SocketException
                                          or IOException
                                          or InvalidOperationException
                                          or ObjectDisposedException)
        {
            Debug.WriteLine(exception);
            if (context is not null)
            {
                await CloseConnectionAsync(context, ConnectionClosedReason.ConnectionLost, false, true)
                    .ConfigureAwait(false);
            }
            else
            {
                client?.Close();
            }

            return new ConnectResult(false, ConnectFailureReason.ConnectionFailed);
        }
        catch (Exception exception)
        {
            // Keep implementation details away from the UI while ensuring a
            // failed asynchronous operation is always observed.
            Debug.WriteLine(exception);
            if (context is not null)
            {
                await CloseConnectionAsync(context, ConnectionClosedReason.ConnectionLost, false, true)
                    .ConfigureAwait(false);
            }
            else
            {
                client?.Close();
            }

            return new ConnectResult(false, ConnectFailureReason.ConnectionFailed);
        }
        finally
        {
            _connectionSetupGate.Release();
        }
    }

    public async Task<bool> SendMessageAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text) || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        ConnectionContext? context;
        lock (_stateGate)
        {
            context = _activeConnection is { IsAuthenticated: true } connection
                && Volatile.Read(ref connection.CloseStarted) == 0
                ? connection
                : null;
        }

        if (context is null)
        {
            return false;
        }

        NetworkMessage message = new()
        {
            Type = MessageProtocol.Chat,
            Text = text,
            Timestamp = DateTimeOffset.Now
        };

        try
        {
            return await SendProtocolMessageAsync(context, message, cancellationToken, false)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (InvalidDataException exception)
        {
            // Reject an oversized local frame without affecting the current
            // authenticated session.
            Debug.WriteLine(exception);
            return false;
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            await CloseConnectionAsync(context, ConnectionClosedReason.ConnectionLost, false, true)
                .ConfigureAwait(false);
            return false;
        }
    }

    public async Task DisconnectAsync(
        ConnectionClosedReason reason = ConnectionClosedReason.LocalRequest)
    {
        ConnectionContext? context;
        lock (_stateGate)
        {
            context = _activeConnection;
        }

        if (context is null)
        {
            return;
        }

        bool sendDisconnect = reason is not ConnectionClosedReason.RemoteRequest
            and not ConnectionClosedReason.ConnectionLost;
        await CloseConnectionAsync(context, reason, sendDisconnect, true).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            // Cancel first so an in-flight outbound connect/auth operation
            // observes disposal and releases _connectionSetupGate before the
            // gate is disposed below.
            _lifetimeCts.Cancel();
            await StopServerCoreAsync(CancellationToken.None).ConfigureAwait(false);

            // StopServerCoreAsync has nothing to do when the listener was
            // already disabled.  Still wait for an outbound connect/auth
            // operation that may own the single setup gate before disposing
            // that gate.
            await _connectionSetupGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            _connectionSetupGate.Release();

            ConnectionContext? context;
            lock (_stateGate)
            {
                context = _activeConnection;
            }

            if (context is not null)
            {
                await CloseConnectionAsync(
                        context,
                        ConnectionClosedReason.ApplicationClosing,
                        true,
                        true)
                    .ConfigureAwait(false);
            }

            await AwaitTaskQuietlyAsync(_acceptLoopTask).ConfigureAwait(false);
        }
        finally
        {
            _lifetimeCts.Dispose();
            _connectionSetupGate.Dispose();
            _listenerGate.Dispose();
        }
    }

    private async Task DisableServerCoreAsync(CancellationToken cancellationToken)
    {
        TcpListener? listener;
        CancellationTokenSource? listenerCts;
        Task? acceptTask;

        lock (_stateGate)
        {
            if (!_serverEnabled)
            {
                return;
            }

            _serverEnabled = false;
            _localConnectionCode = null;
            listener = _listener;
            listenerCts = _listenerCts;
            acceptTask = _acceptLoopTask;
            _listener = null;
            _listenerCts = null;
            _acceptLoopTask = null;
        }

        listenerCts?.Cancel();
        listener?.Stop();
        await AwaitTaskQuietlyAsync(acceptTask).ConfigureAwait(false);
        await AwaitPendingInboundTasksAsync().ConfigureAwait(false);

        try
        {
            await _connectionSetupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            listenerCts?.Dispose();
            return;
        }

        try
        {
            ConnectionContext? inbound = null;
            lock (_stateGate)
            {
                if (_activeConnection is { Origin: ConnectionOrigin.Inbound } connection)
                {
                    inbound = connection;
                }
            }

            if (inbound is not null)
            {
                await CloseConnectionAsync(inbound, ConnectionClosedReason.ServerDisabled, true, true)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _connectionSetupGate.Release();
            listenerCts?.Dispose();
        }
    }

    private async Task StopServerCoreAsync(CancellationToken cancellationToken)
    {
        // DisposeAsync marks the manager disposed before arriving here, so use
        // the core method directly instead of the public guard.
        await _listenerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisableServerCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _listenerGate.Release();
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException exception) when (cancellationToken.IsCancellationRequested)
                {
                    Debug.WriteLine(exception);
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    Debug.WriteLine(exception);
                    break;
                }

                Task task = HandleInboundConnectionAsync(client, cancellationToken);
                _pendingInboundTasks.TryAdd(task, 0);
                _ = ObserveInboundTaskAsync(task);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    private async Task ObserveInboundTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
        finally
        {
            _pendingInboundTasks.TryRemove(task, out _);
        }
    }

    private async Task HandleInboundConnectionAsync(TcpClient client, CancellationToken listenerToken)
    {
        ConnectionContext? context = null;
        string sourceIp = GetSourceIp(client);
        bool authenticationSucceeded = false;
        bool authenticationFailureCounted = false;

        try
        {
            client.NoDelay = true;

            if (IsAuthenticationRateLimited(sourceIp))
            {
                await TrySendStandaloneMessageAsync(client, MessageProtocol.RateLimited)
                    .ConfigureAwait(false);
                return;
            }

            await _connectionSetupGate.WaitAsync(listenerToken).ConfigureAwait(false);
            try
            {
                if (listenerToken.IsCancellationRequested || !IsServerEnabled)
                {
                    return;
                }

                // Re-check after waiting.  A concurrent failed attempt may
                // have changed the per-IP limiter while this request waited.
                if (IsAuthenticationRateLimited(sourceIp))
                {
                    await TrySendStandaloneMessageAsync(client, MessageProtocol.RateLimited)
                        .ConfigureAwait(false);
                    return;
                }

                lock (_stateGate)
                {
                    if (_activeConnection is not null)
                    {
                        context = null;
                    }
                    else
                    {
                        context = new ConnectionContext(client, ConnectionOrigin.Inbound);
                        _activeConnection = context;
                    }
                }

                if (context is null)
                {
                    await TrySendStandaloneMessageAsync(client, MessageProtocol.Busy)
                        .ConfigureAwait(false);
                    return;
                }

                client = null!;
                using CancellationTokenSource authTimeoutCts =
                    CancellationTokenSource.CreateLinkedTokenSource(listenerToken);
                authTimeoutCts.CancelAfter(ConnectionOperationTimeout);
                ServerAuthenticationResult authenticationResult = await AuthenticateAsServerAsync(
                        context,
                        sourceIp,
                        authTimeoutCts.Token)
                    .ConfigureAwait(false);
                if (authenticationResult != ServerAuthenticationResult.Success)
                {
                    authenticationFailureCounted =
                        authenticationResult == ServerAuthenticationResult.FailedAfterCounting;
                    await CloseConnectionAsync(context, ConnectionClosedReason.ConnectionLost, false, true)
                        .ConfigureAwait(false);
                    return;
                }

                authenticationSucceeded = true;

                if (!StartSession(context))
                {
                    await CloseConnectionAsync(context, ConnectionClosedReason.ConnectionLost, false, true)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                _connectionSetupGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            if (context is not null
                && !authenticationSucceeded
                && !authenticationFailureCounted
                && ShouldCountAuthenticationFailure(listenerToken))
            {
                RegisterAuthenticationFailure(sourceIp);
            }

            if (context is not null)
            {
                await CloseConnectionAsync(context, ConnectionClosedReason.ConnectionLost, false, true)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            if (context is not null
                && !authenticationSucceeded
                && !authenticationFailureCounted
                && ShouldCountAuthenticationFailure(listenerToken))
            {
                RegisterAuthenticationFailure(sourceIp);
            }

            if (context is not null)
            {
                await CloseConnectionAsync(context, ConnectionClosedReason.ConnectionLost, false, true)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            client?.Close();
        }
    }

    private async Task<ConnectFailureReason> AuthenticateAsClientAsync(
        ConnectionContext context,
        string key,
        CancellationToken cancellationToken)
    {
        NetworkMessage? challengeMessage = await MessageProtocol.ReadMessageAsync(
                context.Stream,
                cancellationToken)
            .ConfigureAwait(false);
        if (challengeMessage is null)
        {
            return ConnectFailureReason.ConnectionFailed;
        }

        if (string.Equals(challengeMessage.Type, MessageProtocol.RateLimited, StringComparison.OrdinalIgnoreCase)
            && challengeMessage.Version == ProtocolCrypto.ProtocolVersion
            && IsPlainControlMessage(challengeMessage))
        {
            return ConnectFailureReason.RateLimited;
        }

        if (string.Equals(challengeMessage.Type, MessageProtocol.Busy, StringComparison.OrdinalIgnoreCase)
            && challengeMessage.Version == ProtocolCrypto.ProtocolVersion
            && IsPlainControlMessage(challengeMessage))
        {
            return ConnectFailureReason.RemoteBusy;
        }

        byte[] challenge = Array.Empty<byte>();
        byte[] serverPublicKeyBytes = Array.Empty<byte>();
        byte[] clientPublicKeyBytes = Array.Empty<byte>();
        byte[] transcriptHash = Array.Empty<byte>();
        byte[] clientProof = Array.Empty<byte>();
        byte[] serverProof = Array.Empty<byte>();
        ECDiffieHellman? serverPublicKeyAgreement = null;
        try
        {
            if (challengeMessage.Version != ProtocolCrypto.ProtocolVersion
                || !string.Equals(challengeMessage.Type, MessageProtocol.AuthChallenge, StringComparison.OrdinalIgnoreCase)
                || challengeMessage.Sequence is not null
                || challengeMessage.Tag is not null
                || challengeMessage.Text is not null
                || challengeMessage.Timestamp is not null
                || challengeMessage.Reason is not null
                || !AuthenticationService.TryDecodeBase64(
                    challengeMessage.Data,
                    ProtocolCrypto.ChallengeLength,
                    out challenge)
                || !ProtocolCrypto.TryImportPublicKey(
                    challengeMessage.PublicKey,
                    out serverPublicKeyAgreement,
                    out serverPublicKeyBytes))
            {
                return ConnectFailureReason.AuthenticationFailed;
            }

            ECDiffieHellman clientKeyAgreement = ProtocolCrypto.CreateEphemeralKeyAgreement();
            context.LocalKeyAgreement = clientKeyAgreement;
            clientPublicKeyBytes = ProtocolCrypto.ExportPublicKey(clientKeyAgreement);
            transcriptHash = ProtocolCrypto.CreateTranscriptHash(
                challenge,
                serverPublicKeyBytes,
                clientPublicKeyBytes);
            clientProof = ProtocolCrypto.ComputeClientProof(key ?? string.Empty, transcriptHash);
            bool responseSent = await SendProtocolMessageAsync(
                    context,
                    new NetworkMessage
                    {
                        Type = MessageProtocol.AuthResponse,
                        Version = ProtocolCrypto.ProtocolVersion,
                        Data = Convert.ToBase64String(clientProof),
                        PublicKey = Convert.ToBase64String(clientPublicKeyBytes)
                    },
                    cancellationToken,
                    true,
                    false)
                .ConfigureAwait(false);
            if (!responseSent)
            {
                return ConnectFailureReason.ConnectionFailed;
            }

            NetworkMessage? resultMessage = await MessageProtocol.ReadMessageAsync(
                    context.Stream,
                    cancellationToken)
                .ConfigureAwait(false);
            if (resultMessage is null)
            {
                return ConnectFailureReason.ConnectionFailed;
            }

            if (string.Equals(resultMessage.Type, MessageProtocol.AuthOk, StringComparison.OrdinalIgnoreCase)
                && resultMessage.Version == ProtocolCrypto.ProtocolVersion
                && resultMessage.Sequence is null
                && resultMessage.Tag is null
                && resultMessage.PublicKey is null
                && resultMessage.Text is null
                && resultMessage.Timestamp is null
                && resultMessage.Reason is null
                && AuthenticationService.TryDecodeBase64(
                    resultMessage.Data,
                    ProtocolCrypto.ProofLength,
                    out serverProof)
                && ProtocolCrypto.ProofMatches(
                    key ?? string.Empty,
                    transcriptHash,
                    serverProof,
                    serverRole: true))
            {
                ECDiffieHellman remoteKeyAgreement = serverPublicKeyAgreement
                    ?? throw new CryptographicException("The server key agreement was not available.");
                context.SessionCrypto = ProtocolCrypto.DeriveSessionCrypto(
                    context.LocalKeyAgreement,
                    remoteKeyAgreement,
                    transcriptHash,
                    isOutbound: true);
                context.DisposeLocalKeyAgreement();
                return ConnectFailureReason.None;
            }

            if (string.Equals(resultMessage.Type, MessageProtocol.AuthFailed, StringComparison.OrdinalIgnoreCase)
                && resultMessage.Version == ProtocolCrypto.ProtocolVersion
                && IsPlainControlMessage(resultMessage))
            {
                return ConnectFailureReason.AuthenticationFailed;
            }

            if (string.Equals(resultMessage.Type, MessageProtocol.RateLimited, StringComparison.OrdinalIgnoreCase)
                && resultMessage.Version == ProtocolCrypto.ProtocolVersion
                && IsPlainControlMessage(resultMessage))
            {
                return ConnectFailureReason.RateLimited;
            }

            if (string.Equals(resultMessage.Type, MessageProtocol.Busy, StringComparison.OrdinalIgnoreCase)
                && resultMessage.Version == ProtocolCrypto.ProtocolVersion
                && IsPlainControlMessage(resultMessage))
            {
                return ConnectFailureReason.RemoteBusy;
            }

            return ConnectFailureReason.AuthenticationFailed;
        }
        finally
        {
            serverPublicKeyAgreement?.Dispose();
            if (challenge.Length != 0)
            {
                CryptographicOperations.ZeroMemory(challenge);
            }

            if (serverPublicKeyBytes.Length != 0)
            {
                CryptographicOperations.ZeroMemory(serverPublicKeyBytes);
            }

            if (clientPublicKeyBytes.Length != 0)
            {
                CryptographicOperations.ZeroMemory(clientPublicKeyBytes);
            }

            if (transcriptHash.Length != 0)
            {
                CryptographicOperations.ZeroMemory(transcriptHash);
            }

            if (clientProof.Length != 0)
            {
                CryptographicOperations.ZeroMemory(clientProof);
            }

            if (serverProof.Length != 0)
            {
                CryptographicOperations.ZeroMemory(serverProof);
            }
        }
    }

    private async Task<ServerAuthenticationResult> AuthenticateAsServerAsync(
        ConnectionContext context,
        string sourceIp,
        CancellationToken listenerToken)
    {
        byte[] challenge = AuthenticationService.CreateChallenge();
        byte[] serverPublicKeyBytes = Array.Empty<byte>();
        byte[] clientPublicKeyBytes = Array.Empty<byte>();
        byte[] response = Array.Empty<byte>();
        byte[] transcriptHash = Array.Empty<byte>();
        byte[] serverProof = Array.Empty<byte>();
        ECDiffieHellman? clientPublicKeyAgreement = null;
        try
        {
            context.LocalKeyAgreement = ProtocolCrypto.CreateEphemeralKeyAgreement();
            serverPublicKeyBytes = ProtocolCrypto.ExportPublicKey(context.LocalKeyAgreement);
            bool challengeSent = await SendProtocolMessageAsync(
                    context,
                    new NetworkMessage
                    {
                        Type = MessageProtocol.AuthChallenge,
                        Version = ProtocolCrypto.ProtocolVersion,
                        Data = Convert.ToBase64String(challenge),
                        PublicKey = Convert.ToBase64String(serverPublicKeyBytes)
                    },
                    listenerToken,
                    true,
                    false)
                .ConfigureAwait(false);
            if (!challengeSent)
            {
                return ServerAuthenticationResult.FailedAfterCounting;
            }

            NetworkMessage? responseMessage = await MessageProtocol.ReadMessageAsync(
                    context.Stream,
                    listenerToken)
                .ConfigureAwait(false);

            bool validResponse = responseMessage is not null
                && responseMessage.Version == ProtocolCrypto.ProtocolVersion
                && string.Equals(responseMessage.Type, MessageProtocol.AuthResponse, StringComparison.OrdinalIgnoreCase)
                && responseMessage.Sequence is null
                && responseMessage.Tag is null
                && responseMessage.Text is null
                && responseMessage.Timestamp is null
                && responseMessage.Reason is null
                && AuthenticationService.TryDecodeBase64(
                    responseMessage.Data,
                    ProtocolCrypto.ProofLength,
                    out response)
                && ProtocolCrypto.TryImportPublicKey(
                    responseMessage.PublicKey,
                    out clientPublicKeyAgreement,
                    out clientPublicKeyBytes);

            string? currentKey = GetCurrentConnectionPassword();
            if (validResponse && currentKey is not null)
            {
                transcriptHash = ProtocolCrypto.CreateTranscriptHash(
                    challenge,
                    serverPublicKeyBytes,
                    clientPublicKeyBytes);
                validResponse = ProtocolCrypto.ProofMatches(
                    currentKey,
                    transcriptHash,
                    response,
                    serverRole: false);
            }

            if (!validResponse || currentKey is null || clientPublicKeyAgreement is null)
            {
                // The ordinary authentication-failure path owns exactly one
                // failure count.  Disable/dispose cancellation is deliberately
                // excluded so shutdown cannot consume rate-limit attempts.
                if (ShouldCountAuthenticationFailure(listenerToken))
                {
                    RegisterAuthenticationFailure(sourceIp);
                }

                await TrySendProtocolMessageAsync(
                        context,
                        MessageProtocol.CreateVersioned(MessageProtocol.AuthFailed),
                        encrypted: false)
                    .ConfigureAwait(false);
                return ServerAuthenticationResult.FailedAfterCounting;
            }

            context.SessionCrypto = ProtocolCrypto.DeriveSessionCrypto(
                context.LocalKeyAgreement,
                clientPublicKeyAgreement,
                transcriptHash,
                isOutbound: false);
            context.DisposeLocalKeyAgreement();
            clientPublicKeyAgreement.Dispose();
            clientPublicKeyAgreement = null;

            serverProof = ProtocolCrypto.ComputeServerProof(currentKey, transcriptHash);
            ResetAuthenticationFailures(sourceIp);
            await SendProtocolMessageAsync(
                    context,
                    new NetworkMessage
                    {
                        Type = MessageProtocol.AuthOk,
                        Version = ProtocolCrypto.ProtocolVersion,
                        Data = Convert.ToBase64String(serverProof)
                    },
                    listenerToken,
                    true,
                    false)
                .ConfigureAwait(false);
            return ServerAuthenticationResult.Success;
        }
        finally
        {
            clientPublicKeyAgreement?.Dispose();
            if (challenge.Length != 0)
            {
                CryptographicOperations.ZeroMemory(challenge);
            }

            if (serverPublicKeyBytes.Length != 0)
            {
                CryptographicOperations.ZeroMemory(serverPublicKeyBytes);
            }

            if (clientPublicKeyBytes.Length != 0)
            {
                CryptographicOperations.ZeroMemory(clientPublicKeyBytes);
            }

            if (response.Length != 0)
            {
                CryptographicOperations.ZeroMemory(response);
            }

            if (transcriptHash.Length != 0)
            {
                CryptographicOperations.ZeroMemory(transcriptHash);
            }

            if (serverProof.Length != 0)
            {
                CryptographicOperations.ZeroMemory(serverProof);
            }
        }
    }

    private static bool IsPlainControlMessage(NetworkMessage message)
    {
        return message.Data is null
            && message.PublicKey is null
            && message.Sequence is null
            && message.Tag is null
            && message.Text is null
            && message.Timestamp is null
            && message.Reason is null;
    }

    private bool ShouldCountAuthenticationFailure(CancellationToken listenerToken)
    {
        return !listenerToken.IsCancellationRequested && IsServerEnabled;
    }

    private bool StartSession(ConnectionContext context)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(_activeConnection, context)
                || Volatile.Read(ref context.CloseStarted) != 0)
            {
                return false;
            }

            context.IsAuthenticated = true;
            Interlocked.Exchange(ref context.LastInboundUtcTicks, DateTime.UtcNow.Ticks);
            context.ReceiveTask = ReceiveLoopAsync(context);
            context.HeartbeatTask = HeartbeatLoopAsync(context);
            context.LoopStart.TrySetResult(true);
        }

        RaiseConnectionStateChanged(true, ConnectionClosedReason.None);
        return true;
    }

    private async Task ReceiveLoopAsync(ConnectionContext context)
    {
        await context.LoopStart.Task.ConfigureAwait(false);
        try
        {
            while (!context.Cts.IsCancellationRequested)
            {
                NetworkMessage? envelope = await MessageProtocol.ReadMessageAsync(
                        context.Stream,
                        context.Cts.Token)
                    .ConfigureAwait(false);
                if (envelope is null)
                {
                    await CloseConnectionAsync(context, ConnectionClosedReason.ConnectionLost, false, false)
                        .ConfigureAwait(false);
                    return;
                }

                if (context.SessionCrypto is null
                    || !MessageProtocol.TryDecryptEncrypted(
                        context.SessionCrypto,
                        envelope,
                        context.NextReceiveSequence,
                        out NetworkMessage? message)
                    || message is null)
                {
                    await CloseConnectionAsync(context, ConnectionClosedReason.ConnectionLost, false, false)
                        .ConfigureAwait(false);
                    return;
                }

                if (context.NextReceiveSequence == long.MaxValue)
                {
                    await CloseConnectionAsync(context, ConnectionClosedReason.ConnectionLost, false, false)
                        .ConfigureAwait(false);
                    return;
                }

                context.NextReceiveSequence++;

                if (string.Equals(message.Type, MessageProtocol.Chat, StringComparison.OrdinalIgnoreCase))
                {
                    if (message.Text is null || message.Data is not null || message.Reason is not null)
                    {
                        await CloseConnectionAsync(context, ConnectionClosedReason.ConnectionLost, false, false)
                            .ConfigureAwait(false);
                        return;
                    }

                    MarkInboundActivity(context);
                    DateTimeOffset timestamp = message.Timestamp ?? DateTimeOffset.Now;
                    RaiseMessageReceived(message.Text, timestamp);
                    continue;
                }

                if (string.Equals(message.Type, MessageProtocol.Ping, StringComparison.OrdinalIgnoreCase))
                {
                    if (message.Text is not null || message.Timestamp is not null || message.Data is not null || message.Reason is not null)
                    {
                        await CloseConnectionAsync(context, ConnectionClosedReason.ConnectionLost, false, false)
                            .ConfigureAwait(false);
                        return;
                    }

                    MarkInboundActivity(context);
                    await SendProtocolMessageAsync(
                            context,
                            MessageProtocol.Create(MessageProtocol.Pong),
                            context.Cts.Token,
                            false)
                        .ConfigureAwait(false);
                    continue;
                }

                if (string.Equals(message.Type, MessageProtocol.Pong, StringComparison.OrdinalIgnoreCase))
                {
                    if (message.Text is not null || message.Timestamp is not null || message.Data is not null || message.Reason is not null)
                    {
                        await CloseConnectionAsync(context, ConnectionClosedReason.ConnectionLost, false, false)
                            .ConfigureAwait(false);
                        return;
                    }

                    MarkInboundActivity(context);
                    continue;
                }

                if (string.Equals(message.Type, MessageProtocol.Disconnect, StringComparison.OrdinalIgnoreCase))
                {
                    if (message.Text is not null || message.Timestamp is not null || message.Data is not null || message.Reason is not null)
                    {
                        await CloseConnectionAsync(context, ConnectionClosedReason.ConnectionLost, false, false)
                            .ConfigureAwait(false);
                        return;
                    }

                    MarkInboundActivity(context);
                    await CloseConnectionAsync(context, ConnectionClosedReason.RemoteRequest, false, false)
                        .ConfigureAwait(false);
                    return;
                }

                await CloseConnectionAsync(context, ConnectionClosedReason.ConnectionLost, false, false)
                    .ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the normal path for a local close.
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            if (Volatile.Read(ref context.CloseStarted) == 0)
            {
                await CloseConnectionAsync(context, ConnectionClosedReason.ConnectionLost, false, false)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task HeartbeatLoopAsync(ConnectionContext context)
    {
        await context.LoopStart.Task.ConfigureAwait(false);
        try
        {
            while (!context.Cts.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, context.Cts.Token).ConfigureAwait(false);

                long lastActivityTicks = Volatile.Read(ref context.LastInboundUtcTicks);
                TimeSpan idle = DateTime.UtcNow - new DateTime(lastActivityTicks, DateTimeKind.Utc);
                if (idle >= InboundActivityTimeout)
                {
                    await CloseConnectionAsync(context, ConnectionClosedReason.ConnectionLost, false, false)
                        .ConfigureAwait(false);
                    return;
                }

                await SendProtocolMessageAsync(
                        context,
                        MessageProtocol.Create(MessageProtocol.Ping),
                        context.Cts.Token,
                        false)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the normal path for a local close.
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            if (Volatile.Read(ref context.CloseStarted) == 0)
            {
                await CloseConnectionAsync(context, ConnectionClosedReason.ConnectionLost, false, false)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> SendProtocolMessageAsync(
        ConnectionContext context,
        NetworkMessage message,
        CancellationToken cancellationToken,
        bool allowDuringSetup,
        bool encrypted = true)
    {
        if (!allowDuringSetup && Volatile.Read(ref context.CloseStarted) != 0)
        {
            return false;
        }

        await context.SendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool sequenceReserved = false;
        Exception? reservedSendFailure = null;
        try
        {
            if (!allowDuringSetup && Volatile.Read(ref context.CloseStarted) != 0)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!encrypted)
            {
                await MessageProtocol.WriteMessageAsync(context.Stream, message, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            SessionCrypto? sessionCrypto = context.SessionCrypto;
            if (sessionCrypto is null || context.NextSendSequence == long.MaxValue)
            {
                return false;
            }

            long sequence = context.NextSendSequence;
            NetworkMessage envelope = MessageProtocol.CreateEncrypted(sessionCrypto, message, sequence);
            // Build and validate the complete envelope before reserving the
            // sequence.  A local size rejection therefore consumes no nonce.
            cancellationToken.ThrowIfCancellationRequested();
            context.NextSendSequence = sequence + 1;
            sequenceReserved = true;
            await MessageProtocol.WriteMessageAsync(context.Stream, envelope, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (sequenceReserved)
        {
            // Once a sequence has been reserved, a canceled or failed write
            // may have reached the peer.  The connection must be retired so
            // the nonce can never be reused on a live session.
            reservedSendFailure = exception;
        }
        finally
        {
            context.SendGate.Release();
        }

        if (reservedSendFailure is not null)
        {
            await CloseConnectionAsync(
                    context,
                    ConnectionClosedReason.ConnectionLost,
                    sendDisconnect: false,
                    waitForCleanup: false)
                .ConfigureAwait(false);
            return false;
        }

        return false;
    }

    private async Task TrySendProtocolMessageAsync(
        ConnectionContext context,
        NetworkMessage message,
        bool encrypted = true)
    {
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(1));
            await SendProtocolMessageAsync(context, message, timeout.Token, true, encrypted).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    private async Task TrySendStandaloneMessageAsync(TcpClient client, string type)
    {
        try
        {
            using NetworkStream stream = client.GetStream();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(1));
            await MessageProtocol.WriteMessageAsync(
                    stream,
                    MessageProtocol.CreateVersioned(type),
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
        finally
        {
            client.Close();
        }
    }

    private async Task CloseConnectionAsync(
        ConnectionContext context,
        ConnectionClosedReason reason,
        bool sendDisconnect,
        bool waitForCleanup)
    {
        bool firstClose;
        bool wasConnected = false;
        lock (_stateGate)
        {
            firstClose = context.CloseStarted == 0;
            if (firstClose)
            {
                // Reserve the close while holding the same gate used by
                // StartSession.  A concurrent disconnect can therefore not
                // be followed by a late authentication transition.
                context.CloseStarted = 1;
                wasConnected = context.IsAuthenticated;
                context.IsAuthenticated = false;
                if (ReferenceEquals(_activeConnection, context))
                {
                    _activeConnection = null;
                }
            }
        }

        if (firstClose)
        {
            if (sendDisconnect && wasConnected)
            {
                await TrySendProtocolMessageAsync(
                        context,
                        MessageProtocol.Create(MessageProtocol.Disconnect))
                    .ConfigureAwait(false);
            }

            context.Cts.Cancel();
            try
            {
                context.Client.Close();
            }
            catch (Exception exception)
            {
                Debug.WriteLine(exception);
            }

            if (wasConnected)
            {
                RaiseConnectionStateChanged(false, reason);
            }

            Task cleanup = CleanupConnectionAsync(context);
            Volatile.Write(ref context.CleanupTask, cleanup);
        }

        if (!waitForCleanup)
        {
            return;
        }

        Task? cleanupTask;
        while ((cleanupTask = Volatile.Read(ref context.CleanupTask)) is null)
        {
            await Task.Yield();
        }

        await cleanupTask.ConfigureAwait(false);
    }

    private async Task CleanupConnectionAsync(ConnectionContext context)
    {
        Task? receiveTask = context.ReceiveTask;
        Task? heartbeatTask = context.HeartbeatTask;

        try
        {
            Task[] tasks = new[] { receiveTask, heartbeatTask }
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
            if (tasks.Length > 0)
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
        finally
        {
            if (Interlocked.Exchange(ref context.Disposed, 1) == 0)
            {
                context.Dispose();
            }
        }
    }

    private void SetActiveConnection(ConnectionContext context)
    {
        lock (_stateGate)
        {
            _activeConnection = context;
        }
    }

    private void MarkInboundActivity(ConnectionContext context)
    {
        Interlocked.Exchange(ref context.LastInboundUtcTicks, DateTime.UtcNow.Ticks);
    }

    private string? GetCurrentConnectionPassword()
    {
        lock (_stateGate)
        {
            return _serverEnabled ? _configuredConnectionPassword : null;
        }
    }

    private bool IsAuthenticationRateLimited(string sourceIp)
    {
        lock (_failureGate)
        {
            if (!_authenticationFailures.TryGetValue(sourceIp, out FailureRecord? record))
            {
                return false;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (record.BlockedUntil != default && record.BlockedUntil <= now)
            {
                _authenticationFailures.Remove(sourceIp);
                return false;
            }

            return record.BlockedUntil != default && record.BlockedUntil > now;
        }
    }

    private void RegisterAuthenticationFailure(string sourceIp)
    {
        lock (_failureGate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!_authenticationFailures.TryGetValue(sourceIp, out FailureRecord? record)
                || (record.BlockedUntil != default && record.BlockedUntil <= now))
            {
                record = new FailureRecord();
                _authenticationFailures[sourceIp] = record;
            }

            record.ConsecutiveFailures++;
            if (record.ConsecutiveFailures >= 5)
            {
                record.BlockedUntil = now.Add(AuthBlockDuration);
            }
        }
    }

    private void ResetAuthenticationFailures(string sourceIp)
    {
        lock (_failureGate)
        {
            _authenticationFailures.Remove(sourceIp);
        }
    }

    private static string GetSourceIp(TcpClient client)
    {
        try
        {
            return (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? string.Empty;
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            return string.Empty;
        }
    }

    private async Task AwaitPendingInboundTasksAsync()
    {
        Task[] tasks = _pendingInboundTasks.Keys.ToArray();
        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    private static async Task AwaitTaskQuietlyAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    private void RaiseConnectionStateChanged(bool isConnected, ConnectionClosedReason reason)
    {
        EventHandler<ConnectionStateChangedEventArgs>? handler = ConnectionStateChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, new ConnectionStateChangedEventArgs(isConnected, reason));
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    private void RaiseMessageReceived(string text, DateTimeOffset timestamp)
    {
        EventHandler<ChatMessageReceivedEventArgs>? handler = MessageReceived;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, new ChatMessageReceivedEventArgs(text, timestamp));
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    private sealed class FailureRecord
    {
        internal int ConsecutiveFailures { get; set; }

        internal DateTimeOffset BlockedUntil { get; set; }
    }

    private enum ConnectionOrigin
    {
        Inbound,
        Outbound
    }

    private enum ServerAuthenticationResult
    {
        Success,
        FailedAfterCounting
    }

    private sealed class ConnectionContext : IDisposable
    {
        internal ConnectionContext(TcpClient client, ConnectionOrigin origin)
        {
            Client = client;
            Stream = client.GetStream();
            Origin = origin;
        }

        internal TcpClient Client { get; }

        internal NetworkStream Stream { get; }

        internal ConnectionOrigin Origin { get; }

        internal CancellationTokenSource Cts { get; } = new();

        internal SemaphoreSlim SendGate { get; } = new(1, 1);

        internal TaskCompletionSource<bool> LoopStart { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task? ReceiveTask;

        internal Task? HeartbeatTask;

        internal Task? CleanupTask;

        internal long LastInboundUtcTicks;

        internal long NextSendSequence;

        internal long NextReceiveSequence;

        internal ECDiffieHellman? LocalKeyAgreement;

        internal SessionCrypto? SessionCrypto;

        internal int CloseStarted;

        internal int Disposed;

        internal void DisposeLocalKeyAgreement()
        {
            ECDiffieHellman? keyAgreement = Interlocked.Exchange(ref LocalKeyAgreement, null);
            keyAgreement?.Dispose();
        }

        public void Dispose()
        {
            DisposeLocalKeyAgreement();

            SessionCrypto?.Dispose();
            SessionCrypto = null;

            try
            {
                Stream.Dispose();
            }
            catch (Exception exception)
            {
                Debug.WriteLine(exception);
            }

            try
            {
                Client.Dispose();
            }
            catch (Exception exception)
            {
                Debug.WriteLine(exception);
            }

            Cts.Dispose();
            SendGate.Dispose();
        }

        internal bool IsAuthenticated { get; set; }
    }
}
