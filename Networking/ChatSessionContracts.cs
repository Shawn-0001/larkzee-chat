using System;

namespace LarkzeeChat.Networking;

public enum ConnectFailureReason
{
    None,
    AlreadyConnected,
    InvalidAddress,
    ConnectionFailed,
    AuthenticationFailed,
    RateLimited,
    RemoteBusy,
    Cancelled
}

public enum ConnectionClosedReason
{
    None,
    LocalRequest,
    RemoteRequest,
    ConnectionLost,
    ServerDisabled,
    ApplicationClosing
}

public sealed class ServerStartResult
{
    public ServerStartResult(bool succeeded, string? connectionKey)
    {
        Succeeded = succeeded;
        ConnectionKey = connectionKey;
    }

    public bool Succeeded { get; }

    public string? ConnectionKey { get; }
}

public sealed class ConnectResult
{
    public ConnectResult(bool succeeded, ConnectFailureReason failureReason)
    {
        Succeeded = succeeded;
        FailureReason = failureReason;
    }

    public bool Succeeded { get; }

    public ConnectFailureReason FailureReason { get; }
}

public sealed class ConnectionStateChangedEventArgs : EventArgs
{
    public ConnectionStateChangedEventArgs(bool isConnected, ConnectionClosedReason reason)
    {
        IsConnected = isConnected;
        Reason = reason;
    }

    public bool IsConnected { get; }

    public ConnectionClosedReason Reason { get; }
}

public sealed class ChatMessageReceivedEventArgs : EventArgs
{
    public ChatMessageReceivedEventArgs(string text, DateTimeOffset timestamp)
    {
        Text = text;
        Timestamp = timestamp;
    }

    public string Text { get; }

    public DateTimeOffset Timestamp { get; }
}
