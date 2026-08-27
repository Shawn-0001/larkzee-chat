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

public enum AttachmentTransferStage
{
    WaitingForPeer,
    Preparing,
    Transferring,
    Verifying,
    Completed,
    Rejected,
    Cancelled,
    Failed
}

public sealed class AttachmentTransferStartedEventArgs : EventArgs
{
    public AttachmentTransferStartedEventArgs(
        string transferId,
        string fileName,
        string contentType,
        long fileSize,
        string localPath,
        bool isIncoming,
        bool isSticker = false,
        bool isInlineImage = false)
    {
        TransferId = transferId;
        FileName = fileName;
        ContentType = contentType;
        FileSize = fileSize;
        LocalPath = localPath;
        IsIncoming = isIncoming;
        IsSticker = isSticker;
        IsInlineImage = isInlineImage;
    }

    public string TransferId { get; }

    public string FileName { get; }

    public string ContentType { get; }

    public long FileSize { get; }

    public string LocalPath { get; }

    public bool IsIncoming { get; }

    public bool IsSticker { get; }

    public bool IsInlineImage { get; }
}

public sealed class IncomingAttachmentOfferEventArgs : EventArgs
{
    public IncomingAttachmentOfferEventArgs(
        string transferId,
        string fileName,
        string contentType,
        long fileSize,
        DateTimeOffset timestamp,
        bool isSticker = false,
        bool isInlineImage = false)
    {
        TransferId = transferId;
        FileName = fileName;
        ContentType = contentType;
        FileSize = fileSize;
        Timestamp = timestamp;
        IsSticker = isSticker;
        IsInlineImage = isInlineImage;
    }

    public string TransferId { get; }

    public string FileName { get; }

    public string ContentType { get; }

    public long FileSize { get; }

    public DateTimeOffset Timestamp { get; }

    public bool IsSticker { get; }

    public bool IsInlineImage { get; }
}

public sealed class AttachmentTransferProgressEventArgs : EventArgs
{
    public AttachmentTransferProgressEventArgs(
        string transferId,
        string fileName,
        long bytesTransferred,
        long totalBytes,
        bool isIncoming,
        AttachmentTransferStage stage,
        bool isSticker = false,
        bool isInlineImage = false)
    {
        TransferId = transferId;
        FileName = fileName;
        BytesTransferred = bytesTransferred;
        TotalBytes = totalBytes;
        IsIncoming = isIncoming;
        Stage = stage;
        IsSticker = isSticker;
        IsInlineImage = isInlineImage;
    }

    public string TransferId { get; }

    public string FileName { get; }

    public long BytesTransferred { get; }

    public long TotalBytes { get; }

    public bool IsIncoming { get; }

    public AttachmentTransferStage Stage { get; }

    public bool IsSticker { get; }

    public bool IsInlineImage { get; }
}

public sealed class AttachmentTransferCompletedEventArgs : EventArgs
{
    public AttachmentTransferCompletedEventArgs(
        string transferId,
        string fileName,
        string contentType,
        long fileSize,
        string? localPath,
        bool isIncoming,
        bool succeeded,
        AttachmentTransferStage stage,
        string message,
        bool isSticker = false,
        ReadOnlyMemory<byte> contentBytes = default,
        bool isInlineImage = false)
    {
        TransferId = transferId;
        FileName = fileName;
        ContentType = contentType;
        FileSize = fileSize;
        LocalPath = localPath;
        IsIncoming = isIncoming;
        Succeeded = succeeded;
        Stage = stage;
        Message = message;
        IsSticker = isSticker;
        IsInlineImage = isInlineImage;
        ContentBytes = contentBytes.IsEmpty
            ? ReadOnlyMemory<byte>.Empty
            : contentBytes.ToArray();
    }

    public string TransferId { get; }

    public string FileName { get; }

    public string ContentType { get; }

    public long FileSize { get; }

    public string? LocalPath { get; }

    public bool IsIncoming { get; }

    public bool Succeeded { get; }

    public AttachmentTransferStage Stage { get; }

    public string Message { get; }

    public bool IsSticker { get; }

    public bool IsInlineImage { get; }

    /// <summary>
    /// Verified incoming sticker or inline-image bytes. This is empty for
    /// ordinary files, failed transfers, and outgoing transfers. The event
    /// args own a copy so the transfer's internal receive buffer can be cleared
    /// immediately.
    /// </summary>
    public ReadOnlyMemory<byte> ContentBytes { get; }

    public bool HasContentBytes => !ContentBytes.IsEmpty;
}

public sealed class AttachmentSendResult
{
    public AttachmentSendResult(
        string? transferId,
        bool succeeded,
        AttachmentTransferStage stage,
        string message,
        bool isSticker = false,
        bool isInlineImage = false)
    {
        TransferId = transferId;
        Succeeded = succeeded;
        Stage = stage;
        Message = message;
        IsSticker = isSticker;
        IsInlineImage = isInlineImage;
    }

    public string? TransferId { get; }

    public bool Succeeded { get; }

    public AttachmentTransferStage Stage { get; }

    public string Message { get; }

    public bool IsSticker { get; }

    public bool IsInlineImage { get; }
}
