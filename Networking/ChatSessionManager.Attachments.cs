using System.Diagnostics;
using System.Security.Cryptography;
using LarkzeeChat.Models;

namespace LarkzeeChat.Networking;

public sealed partial class ChatSessionManager
{
    public const long MaximumAttachmentBytes = 256L * 1024 * 1024;
    public const long MaximumStickerBytes = 1L * 1024 * 1024;
    public const long MaximumInlineImageBytes = 25L * 1024 * 1024;
    public const int AttachmentChunkBytes = 24 * 1024;

    /// <summary>
    /// Stable authenticated attachment content type for an inline PNG/GIF
    /// sticker. The file extension still identifies whether it is PNG or GIF.
    /// </summary>
    public const string StickerContentType = "application/vnd.larkzee.sticker";

    private const string InlineImageOfferReason = "inline_image";

    private const int MaximumAttachmentFileNameCharacters = 240;
    private const int MaximumContentTypeCharacters = 128;
    private static readonly TimeSpan AttachmentDecisionTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AttachmentCompletionTimeout = TimeSpan.FromMinutes(2);

    public event EventHandler<IncomingAttachmentOfferEventArgs>? AttachmentOffered;

    public event EventHandler<AttachmentTransferStartedEventArgs>? AttachmentTransferStarted;

    public event EventHandler<AttachmentTransferProgressEventArgs>? AttachmentTransferProgressChanged;

    public event EventHandler<AttachmentTransferCompletedEventArgs>? AttachmentTransferCompleted;

    public static bool IsStickerContentType(string? contentType)
    {
        return string.Equals(contentType, StickerContentType, StringComparison.Ordinal);
    }

    public static bool IsInlineImageContentType(string? contentType)
    {
        return contentType is "image/png"
            or "image/jpeg"
            or "image/gif"
            or "image/bmp"
            or "image/webp";
    }

    public async Task<AttachmentSendResult> SendStickerAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
        {
            return new AttachmentSendResult(
                null,
                false,
                AttachmentTransferStage.Cancelled,
                "发送已取消。",
                isSticker: true);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(sourcePath ?? string.Empty);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or NotSupportedException
                                           or PathTooLongException)
        {
            return new AttachmentSendResult(
                null,
                false,
                AttachmentTransferStage.Failed,
                "表情文件路径无效。",
                isSticker: true);
        }

        if (!IsSupportedStickerFileName(Path.GetFileName(fullPath)))
        {
            return new AttachmentSendResult(
                null,
                false,
                AttachmentTransferStage.Failed,
                "表情仅支持 PNG 和 GIF。",
                isSticker: true);
        }

        try
        {
            FileInfo fileInfo = new(fullPath);
            if (!fileInfo.Exists || fileInfo.Length is <= 0 or > MaximumStickerBytes)
            {
                return new AttachmentSendResult(
                    null,
                    false,
                    AttachmentTransferStage.Failed,
                    "表情文件必须大于 0 且不超过 1 MiB。",
                    isSticker: true);
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or NotSupportedException
                                           or ArgumentException)
        {
            return new AttachmentSendResult(
                null,
                false,
                AttachmentTransferStage.Failed,
                "无法读取表情文件。",
                isSticker: true);
        }

        return await SendAttachmentAsync(fullPath, StickerContentType, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<AttachmentSendResult> SendImageAsync(
        string sourcePath,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        return SendAttachmentCoreAsync(
            sourcePath,
            contentType,
            cancellationToken,
            isInlineImage: true);
    }

    public Task<AttachmentSendResult> SendAttachmentAsync(
        string sourcePath,
        string? contentType = null,
        CancellationToken cancellationToken = default)
    {
        return SendAttachmentCoreAsync(
            sourcePath,
            contentType,
            cancellationToken,
            isInlineImage: false);
    }

    private async Task<AttachmentSendResult> SendAttachmentCoreAsync(
        string sourcePath,
        string? contentType,
        CancellationToken cancellationToken,
        bool isInlineImage)
    {
        if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
        {
            return new AttachmentSendResult(
                null,
                false,
                AttachmentTransferStage.Cancelled,
                "发送已取消。",
                isInlineImage: isInlineImage);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(sourcePath ?? string.Empty);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or NotSupportedException
                                           or PathTooLongException)
        {
            return new AttachmentSendResult(
                null,
                false,
                AttachmentTransferStage.Failed,
                "文件路径无效。",
                isInlineImage: isInlineImage);
        }

        string fileName = Path.GetFileName(fullPath);
        if (!IsSafeOfferedFileName(fileName))
        {
            return new AttachmentSendResult(
                null,
                false,
                AttachmentTransferStage.Failed,
                "文件名无效或过长。",
                isInlineImage: isInlineImage);
        }

        ConnectionContext? context = GetAuthenticatedContext();
        if (context is null)
        {
            return new AttachmentSendResult(
                null,
                false,
                AttachmentTransferStage.Failed,
                "当前未连接，无法发送附件。",
                isInlineImage: isInlineImage);
        }

        FileStream? source = null;
        OutgoingAttachmentTransfer? transfer = null;
        try
        {
            source = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                AttachmentChunkBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length > MaximumAttachmentBytes)
            {
                return new AttachmentSendResult(
                    null,
                    false,
                    AttachmentTransferStage.Failed,
                    "文件超过 256 MB，无法发送。",
                    isInlineImage: isInlineImage);
            }

            string normalizedContentType = NormalizeContentType(contentType, fileName);
            bool isSticker = IsStickerContentType(normalizedContentType);
            if (isInlineImage
                && (!IsInlineImageContentType(normalizedContentType)
                    || !IsSupportedImageFileName(fileName, normalizedContentType)
                    || source.Length is <= 0 or > MaximumInlineImageBytes))
            {
                return new AttachmentSendResult(
                    null,
                    false,
                    AttachmentTransferStage.Failed,
                    "图片必须是支持的图片格式且不超过 25 MiB。",
                    isInlineImage: true);
            }

            if (isSticker
                && (!IsSupportedStickerFileName(fileName)
                    || source.Length is <= 0 or > MaximumStickerBytes))
            {
                return new AttachmentSendResult(
                    null,
                    false,
                    AttachmentTransferStage.Failed,
                    "表情文件必须是 PNG/GIF 且不超过 1 MiB。",
                    isSticker: true,
                    isInlineImage: isInlineImage);
            }

            transfer = new OutgoingAttachmentTransfer(
                Guid.NewGuid().ToString("N"),
                fileName,
                normalizedContentType,
                source.Length,
                fullPath,
                isInlineImage);
            lock (context.AttachmentGate)
            {
                if (context.OutgoingAttachment is not null)
                {
                    return new AttachmentSendResult(
                        null,
                        false,
                        AttachmentTransferStage.Failed,
                        "已有文件正在发送，请等待完成后重试。",
                        isInlineImage: isInlineImage);
                }

                context.OutgoingAttachment = transfer;
            }

            RaiseAttachmentTransferStarted(new AttachmentTransferStartedEventArgs(
                transfer.Id,
                transfer.FileName,
                transfer.ContentType,
                transfer.FileSize,
                transfer.LocalPath,
                isIncoming: false,
                isSticker: transfer.IsSticker,
                isInlineImage: transfer.IsInlineImage));
            RaiseAttachmentProgress(transfer, 0, AttachmentTransferStage.Preparing, isIncoming: false);

            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                context.Cts.Token,
                _lifetimeCts.Token);
            CancellationToken transferToken = linkedCts.Token;
            byte[] digest = await SHA256.HashDataAsync(source, transferToken).ConfigureAwait(false);
            transfer.Sha256 = Convert.ToHexString(digest).ToLowerInvariant();
            CryptographicOperations.ZeroMemory(digest);
            source.Position = 0;

            RaiseAttachmentProgress(transfer, 0, AttachmentTransferStage.WaitingForPeer, isIncoming: false);
            bool offered = await SendProtocolMessageAsync(
                    context,
                    new NetworkMessage
                    {
                        Type = MessageProtocol.AttachmentOffer,
                        TransferId = transfer.Id,
                        FileName = transfer.FileName,
                        ContentType = transfer.ContentType,
                        FileSize = transfer.FileSize,
                        Sha256 = transfer.Sha256,
                        Reason = transfer.IsInlineImage ? InlineImageOfferReason : null,
                        Timestamp = DateTimeOffset.Now
                    },
                    transferToken,
                    false)
                .ConfigureAwait(false);
            if (!offered)
            {
                return FinishOutgoingTransfer(
                    transfer,
                    false,
                    AttachmentTransferStage.Failed,
                    "附件请求发送失败。");
            }

            AttachmentDecision decision;
            try
            {
                decision = await transfer.Decision.Task
                    .WaitAsync(AttachmentDecisionTimeout, transferToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await TrySendAttachmentCancelAsync(context, transfer.Id, "decision_timeout")
                    .ConfigureAwait(false);
                return FinishOutgoingTransfer(
                    transfer,
                    false,
                    AttachmentTransferStage.Failed,
                    "等待对方确认超时。");
            }

            if (!decision.Accepted)
            {
                return FinishOutgoingTransfer(
                    transfer,
                    false,
                    AttachmentTransferStage.Rejected,
                    string.IsNullOrWhiteSpace(decision.Reason)
                        ? "对方已拒绝接收。"
                        : decision.Reason);
            }

            byte[] buffer = new byte[AttachmentChunkBytes];
            long sentBytes = 0;
            int chunkIndex = 0;
            RaiseAttachmentProgress(transfer, 0, AttachmentTransferStage.Transferring, isIncoming: false);
            while (sentBytes < transfer.FileSize)
            {
                int read = await source.ReadAsync(buffer.AsMemory(), transferToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new EndOfStreamException("The source attachment ended before its declared size.");
                }

                bool chunkSent = await SendProtocolMessageAsync(
                        context,
                        new NetworkMessage
                        {
                            Type = MessageProtocol.AttachmentChunk,
                            TransferId = transfer.Id,
                            ChunkIndex = chunkIndex,
                            Data = Convert.ToBase64String(buffer, 0, read)
                        },
                        transferToken,
                        false)
                    .ConfigureAwait(false);
                if (!chunkSent)
                {
                    throw new IOException("The attachment chunk could not be sent.");
                }

                sentBytes += read;
                chunkIndex++;
                RaiseAttachmentProgress(
                    transfer,
                    sentBytes,
                    AttachmentTransferStage.Transferring,
                    isIncoming: false);
            }

            RaiseAttachmentProgress(
                transfer,
                transfer.FileSize,
                AttachmentTransferStage.Verifying,
                isIncoming: false);
            bool completionSent = await SendProtocolMessageAsync(
                    context,
                    new NetworkMessage
                    {
                        Type = MessageProtocol.AttachmentComplete,
                        TransferId = transfer.Id
                    },
                    transferToken,
                    false)
                .ConfigureAwait(false);
            if (!completionSent)
            {
                throw new IOException("The attachment completion message could not be sent.");
            }

            AttachmentCompletion completion;
            try
            {
                completion = await transfer.Completion.Task
                    .WaitAsync(AttachmentCompletionTimeout, transferToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return FinishOutgoingTransfer(
                    transfer,
                    false,
                    AttachmentTransferStage.Failed,
                    "等待对方校验结果超时。");
            }

            return FinishOutgoingTransfer(
                transfer,
                completion.Succeeded,
                completion.Succeeded
                    ? AttachmentTransferStage.Completed
                    : AttachmentTransferStage.Failed,
                completion.Succeeded ? "发送完成，对方校验通过。" : completion.Message);
        }
        catch (OperationCanceledException)
        {
            if (transfer is not null)
            {
                await TrySendAttachmentCancelAsync(context, transfer.Id, "cancelled").ConfigureAwait(false);
                return FinishOutgoingTransfer(
                    transfer,
                    false,
                    AttachmentTransferStage.Cancelled,
                    "发送已取消。");
            }

            return new AttachmentSendResult(
                null,
                false,
                AttachmentTransferStage.Cancelled,
                "发送已取消。",
                isInlineImage: isInlineImage);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or CryptographicException
                                           or InvalidDataException
                                           or ObjectDisposedException)
        {
            Debug.WriteLine(exception);
            if (transfer is not null)
            {
                await TrySendAttachmentCancelAsync(context, transfer.Id, "failed").ConfigureAwait(false);
                return FinishOutgoingTransfer(
                    transfer,
                    false,
                    AttachmentTransferStage.Failed,
                    "附件发送失败，请重试。");
            }

            return new AttachmentSendResult(
                null,
                false,
                AttachmentTransferStage.Failed,
                "无法读取该文件。",
                isInlineImage: isInlineImage);
        }
        finally
        {
            source?.Dispose();
            if (transfer is not null)
            {
                lock (context.AttachmentGate)
                {
                    if (ReferenceEquals(context.OutgoingAttachment, transfer))
                    {
                        context.OutgoingAttachment = null;
                    }
                }
            }
        }
    }

    public async Task<bool> AcceptIncomingAttachmentAsync(
        string transferId,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ConnectionContext? context = GetAuthenticatedContext();
        if (context is null || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        string fullDestination;
        try
        {
            fullDestination = Path.GetFullPath(destinationPath ?? string.Empty);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or NotSupportedException
                                           or PathTooLongException)
        {
            return false;
        }

        string? directory = Path.GetDirectoryName(fullDestination);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        IncomingAttachmentTransfer? transfer;
        try
        {
            lock (context.AttachmentGate)
            {
                transfer = context.IncomingAttachment;
                if (transfer is null
                    || transfer.Accepted
                    || transfer.IsSticker
                    || transfer.IsInlineImage
                    || !string.Equals(transfer.Id, transferId, StringComparison.Ordinal))
                {
                    return false;
                }

                string partialPath = Path.Combine(directory, $".larkzee-{transfer.Id}.part");
                transfer.OpenDestination(fullDestination, partialPath);
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or NotSupportedException
                                           or PathTooLongException)
        {
            Debug.WriteLine(exception);
            return false;
        }

        RaiseAttachmentTransferStarted(new AttachmentTransferStartedEventArgs(
            transfer.Id,
            transfer.FileName,
            transfer.ContentType,
            transfer.FileSize,
            transfer.DestinationPath!,
            isIncoming: true,
            isSticker: transfer.IsSticker,
            isInlineImage: transfer.IsInlineImage));
        RaiseAttachmentProgress(transfer, 0, AttachmentTransferStage.Transferring, isIncoming: true);

        bool accepted;
        try
        {
            accepted = await SendProtocolMessageAsync(
                    context,
                    new NetworkMessage
                    {
                        Type = MessageProtocol.AttachmentAccept,
                        TransferId = transfer.Id
                    },
                    cancellationToken,
                    false)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException
                                           or IOException
                                           or InvalidDataException
                                           or ObjectDisposedException
                                           or CryptographicException)
        {
            Debug.WriteLine(exception);
            accepted = false;
        }
        if (accepted)
        {
            return true;
        }

        await FailIncomingTransferAsync(
                context,
                transfer,
                AttachmentTransferStage.Failed,
                "无法开始接收文件。",
                sendResult: false)
            .ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Accepts an incoming sticker into a bounded memory stream. No destination
    /// or partial file is created; verified bytes are exposed on the
    /// <see cref="AttachmentTransferCompletedEventArgs"/> event.
    /// </summary>
    public async Task<bool> AcceptIncomingStickerAsync(
        string transferId,
        CancellationToken cancellationToken = default)
    {
        ConnectionContext? context = GetAuthenticatedContext();
        if (context is null || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        IncomingAttachmentTransfer? transfer;
        try
        {
            lock (context.AttachmentGate)
            {
                transfer = context.IncomingAttachment;
                if (transfer is null
                    || transfer.Accepted
                    || !transfer.IsSticker
                    || transfer.FileSize is <= 0 or > MaximumStickerBytes
                    || !string.Equals(transfer.Id, transferId, StringComparison.Ordinal))
                {
                    return false;
                }

                transfer.OpenMemory();
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or OutOfMemoryException
                                           or InvalidOperationException)
        {
            Debug.WriteLine(exception);
            return false;
        }

        RaiseAttachmentTransferStarted(new AttachmentTransferStartedEventArgs(
            transfer.Id,
            transfer.FileName,
            transfer.ContentType,
            transfer.FileSize,
            string.Empty,
            isIncoming: true,
            isSticker: transfer.IsSticker,
            isInlineImage: transfer.IsInlineImage));
        RaiseAttachmentProgress(transfer, 0, AttachmentTransferStage.Transferring, isIncoming: true);

        bool accepted;
        try
        {
            accepted = await SendProtocolMessageAsync(
                    context,
                    new NetworkMessage
                    {
                        Type = MessageProtocol.AttachmentAccept,
                        TransferId = transfer.Id
                    },
                    cancellationToken,
                    false)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException
                                           or IOException
                                           or InvalidDataException
                                           or ObjectDisposedException
                                           or CryptographicException)
        {
            Debug.WriteLine(exception);
            accepted = false;
        }

        if (accepted)
        {
            return true;
        }

        await FailIncomingTransferAsync(
                context,
                transfer,
                AttachmentTransferStage.Failed,
                "无法开始接收表情。",
                sendResult: false)
            .ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Accepts an incoming inline image into a bounded memory stream. No
    /// destination or partial file is created; verified bytes are exposed on
    /// the <see cref="AttachmentTransferCompletedEventArgs"/> event.
    /// </summary>
    public async Task<bool> AcceptIncomingImageAsync(
        string transferId,
        CancellationToken cancellationToken = default)
    {
        ConnectionContext? context = GetAuthenticatedContext();
        if (context is null || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        IncomingAttachmentTransfer? transfer;
        try
        {
            lock (context.AttachmentGate)
            {
                transfer = context.IncomingAttachment;
                if (transfer is null
                    || transfer.Accepted
                    || !transfer.IsInlineImage
                    || transfer.FileSize is <= 0 or > MaximumInlineImageBytes
                    || !string.Equals(transfer.Id, transferId, StringComparison.Ordinal))
                {
                    return false;
                }

                transfer.OpenMemory();
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or OutOfMemoryException
                                           or InvalidOperationException)
        {
            Debug.WriteLine(exception);
            return false;
        }

        RaiseAttachmentTransferStarted(new AttachmentTransferStartedEventArgs(
            transfer.Id,
            transfer.FileName,
            transfer.ContentType,
            transfer.FileSize,
            string.Empty,
            isIncoming: true,
            isSticker: transfer.IsSticker,
            isInlineImage: transfer.IsInlineImage));
        RaiseAttachmentProgress(transfer, 0, AttachmentTransferStage.Transferring, isIncoming: true);

        bool accepted;
        try
        {
            accepted = await SendProtocolMessageAsync(
                    context,
                    new NetworkMessage
                    {
                        Type = MessageProtocol.AttachmentAccept,
                        TransferId = transfer.Id
                    },
                    cancellationToken,
                    false)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException
                                           or IOException
                                           or InvalidDataException
                                           or ObjectDisposedException
                                           or CryptographicException)
        {
            Debug.WriteLine(exception);
            accepted = false;
        }

        if (accepted)
        {
            return true;
        }

        await FailIncomingTransferAsync(
                context,
                transfer,
                AttachmentTransferStage.Failed,
                "无法开始接收图片。",
                sendResult: false)
            .ConfigureAwait(false);
        return false;
    }

    public async Task RejectIncomingAttachmentAsync(
        string transferId,
        string reason = "对方取消接收。",
        CancellationToken cancellationToken = default)
    {
        ConnectionContext? context = GetAuthenticatedContext();
        if (context is null)
        {
            return;
        }

        IncomingAttachmentTransfer? transfer;
        lock (context.AttachmentGate)
        {
            transfer = context.IncomingAttachment;
            if (transfer is null
                || transfer.Accepted
                || !string.Equals(transfer.Id, transferId, StringComparison.Ordinal))
            {
                return;
            }

            context.IncomingAttachment = null;
        }

        transfer.DisposeAndDeletePartial();
        try
        {
            await SendProtocolMessageAsync(
                    context,
                    new NetworkMessage
                    {
                        Type = MessageProtocol.AttachmentReject,
                        TransferId = transfer.Id,
                        Reason = "rejected"
                    },
                    cancellationToken,
                    false)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }

        RaiseAttachmentCompleted(new AttachmentTransferCompletedEventArgs(
            transfer.Id,
            transfer.FileName,
            transfer.ContentType,
            transfer.FileSize,
            null,
            isIncoming: true,
            succeeded: false,
            AttachmentTransferStage.Rejected,
            reason,
            isSticker: transfer.IsSticker,
            isInlineImage: transfer.IsInlineImage));
    }

    private async Task<AttachmentMessageHandlingResult> HandleAttachmentMessageAsync(
        ConnectionContext context,
        NetworkMessage message)
    {
        if (string.Equals(message.Type, MessageProtocol.AttachmentOffer, StringComparison.OrdinalIgnoreCase))
        {
            return await HandleAttachmentOfferAsync(context, message).ConfigureAwait(false);
        }

        if (string.Equals(message.Type, MessageProtocol.AttachmentAccept, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsTransferControlMessage(message, allowReason: false))
            {
                return AttachmentMessageHandlingResult.ProtocolError;
            }

            OutgoingAttachmentTransfer? transfer;
            lock (context.AttachmentGate)
            {
                transfer = context.OutgoingAttachment;
            }

            if (transfer is null || !string.Equals(transfer.Id, message.TransferId, StringComparison.Ordinal))
            {
                return AttachmentMessageHandlingResult.ProtocolError;
            }

            MarkInboundActivity(context);
            transfer.Decision.TrySetResult(new AttachmentDecision(true, string.Empty));
            return AttachmentMessageHandlingResult.Handled;
        }

        if (string.Equals(message.Type, MessageProtocol.AttachmentReject, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsTransferControlMessage(message, allowReason: true))
            {
                return AttachmentMessageHandlingResult.ProtocolError;
            }

            OutgoingAttachmentTransfer? transfer;
            lock (context.AttachmentGate)
            {
                transfer = context.OutgoingAttachment;
            }

            if (transfer is null || !string.Equals(transfer.Id, message.TransferId, StringComparison.Ordinal))
            {
                return AttachmentMessageHandlingResult.ProtocolError;
            }

            MarkInboundActivity(context);
            transfer.Decision.TrySetResult(new AttachmentDecision(false, "对方已拒绝接收。"));
            return AttachmentMessageHandlingResult.Handled;
        }

        if (string.Equals(message.Type, MessageProtocol.AttachmentChunk, StringComparison.OrdinalIgnoreCase))
        {
            return await HandleAttachmentChunkAsync(context, message).ConfigureAwait(false);
        }

        if (string.Equals(message.Type, MessageProtocol.AttachmentComplete, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsTransferControlMessage(message, allowReason: false))
            {
                return AttachmentMessageHandlingResult.ProtocolError;
            }

            return await CompleteIncomingAttachmentAsync(context, message.TransferId!)
                .ConfigureAwait(false);
        }

        if (string.Equals(message.Type, MessageProtocol.AttachmentResult, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsTransferControlMessage(message, allowReason: true))
            {
                return AttachmentMessageHandlingResult.ProtocolError;
            }

            OutgoingAttachmentTransfer? transfer;
            lock (context.AttachmentGate)
            {
                transfer = context.OutgoingAttachment;
            }

            if (transfer is null || !string.Equals(transfer.Id, message.TransferId, StringComparison.Ordinal))
            {
                return AttachmentMessageHandlingResult.ProtocolError;
            }

            MarkInboundActivity(context);
            bool succeeded = string.Equals(message.Reason, "completed", StringComparison.Ordinal);
            transfer.Completion.TrySetResult(new AttachmentCompletion(
                succeeded,
                succeeded ? "发送完成，对方校验通过。" : "对方校验文件失败。"));
            return AttachmentMessageHandlingResult.Handled;
        }

        if (string.Equals(message.Type, MessageProtocol.AttachmentCancel, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsTransferControlMessage(message, allowReason: true))
            {
                return AttachmentMessageHandlingResult.ProtocolError;
            }

            return await HandleAttachmentCancelAsync(context, message.TransferId!).ConfigureAwait(false);
        }

        return AttachmentMessageHandlingResult.NotAttachment;
    }

    private async Task<AttachmentMessageHandlingResult> HandleAttachmentOfferAsync(
        ConnectionContext context,
        NetworkMessage message)
    {
        if (!IsValidAttachmentOffer(message))
        {
            return AttachmentMessageHandlingResult.ProtocolError;
        }

        IncomingAttachmentTransfer transfer = new(
            message.TransferId!,
            message.FileName!,
            message.ContentType!,
            message.FileSize!.Value,
            message.Sha256!,
            string.Equals(message.Reason, InlineImageOfferReason, StringComparison.Ordinal));
        bool busy;
        lock (context.AttachmentGate)
        {
            busy = context.IncomingAttachment is not null;
            if (!busy)
            {
                context.IncomingAttachment = transfer;
            }
        }

        MarkInboundActivity(context);
        if (busy)
        {
            await SendProtocolMessageAsync(
                    context,
                    new NetworkMessage
                    {
                        Type = MessageProtocol.AttachmentReject,
                        TransferId = transfer.Id,
                        Reason = "busy"
                    },
                    context.Cts.Token,
                    false)
                .ConfigureAwait(false);
            return AttachmentMessageHandlingResult.Handled;
        }

        RaiseAttachmentOffered(new IncomingAttachmentOfferEventArgs(
            transfer.Id,
            transfer.FileName,
            transfer.ContentType,
            transfer.FileSize,
            message.Timestamp ?? DateTimeOffset.Now,
            isSticker: transfer.IsSticker,
            isInlineImage: transfer.IsInlineImage));
        _ = ExpireIncomingOfferAsync(context, transfer);
        return AttachmentMessageHandlingResult.Handled;
    }

    private async Task<AttachmentMessageHandlingResult> HandleAttachmentChunkAsync(
        ConnectionContext context,
        NetworkMessage message)
    {
        if (message.TransferId is null
            || message.ChunkIndex is not int chunkIndex
            || chunkIndex < 0
            || string.IsNullOrWhiteSpace(message.Data)
            || message.Text is not null
            || message.Timestamp is not null
            || message.Reason is not null
            || message.FileName is not null
            || message.ContentType is not null
            || message.FileSize is not null
            || message.Sha256 is not null)
        {
            return AttachmentMessageHandlingResult.ProtocolError;
        }

        byte[] chunk;
        try
        {
            chunk = Convert.FromBase64String(message.Data);
        }
        catch (FormatException)
        {
            return AttachmentMessageHandlingResult.ProtocolError;
        }

        if (chunk.Length is <= 0 or > AttachmentChunkBytes)
        {
            CryptographicOperations.ZeroMemory(chunk);
            return AttachmentMessageHandlingResult.ProtocolError;
        }

        IncomingAttachmentTransfer? transfer;
        lock (context.AttachmentGate)
        {
            transfer = context.IncomingAttachment;
        }

        if (transfer is null
            || !transfer.Accepted
            || !string.Equals(transfer.Id, message.TransferId, StringComparison.Ordinal)
            || transfer.NextChunkIndex != chunkIndex
            || transfer.BytesReceived + chunk.Length > transfer.FileSize
            || transfer.DestinationStream is null)
        {
            CryptographicOperations.ZeroMemory(chunk);
            return AttachmentMessageHandlingResult.ProtocolError;
        }

        try
        {
            await transfer.DestinationStream.WriteAsync(chunk, context.Cts.Token).ConfigureAwait(false);
            transfer.Hash.AppendData(chunk);
            transfer.BytesReceived += chunk.Length;
            transfer.NextChunkIndex++;
            MarkInboundActivity(context);
            RaiseAttachmentProgress(
                transfer,
                transfer.BytesReceived,
                AttachmentTransferStage.Transferring,
                isIncoming: true);
            return AttachmentMessageHandlingResult.Handled;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ObjectDisposedException)
        {
            Debug.WriteLine(exception);
            await FailIncomingTransferAsync(
                    context,
                    transfer,
                    AttachmentTransferStage.Failed,
                    "写入目标文件失败。",
                    sendResult: true)
                .ConfigureAwait(false);
            return AttachmentMessageHandlingResult.Handled;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(chunk);
        }
    }

    private async Task<AttachmentMessageHandlingResult> CompleteIncomingAttachmentAsync(
        ConnectionContext context,
        string transferId)
    {
        IncomingAttachmentTransfer? transfer;
        lock (context.AttachmentGate)
        {
            transfer = context.IncomingAttachment;
        }

        if (transfer is null
            || !transfer.Accepted
            || !string.Equals(transfer.Id, transferId, StringComparison.Ordinal)
            || transfer.DestinationStream is null
            || (transfer.UsesMemory
                ? transfer.DestinationStream is not MemoryStream
                  || transfer.DestinationPath is not null
                  || transfer.PartialPath is not null
                : transfer.DestinationPath is null
                  || transfer.PartialPath is null))
        {
            return AttachmentMessageHandlingResult.ProtocolError;
        }

        MarkInboundActivity(context);
        RaiseAttachmentProgress(
            transfer,
            transfer.BytesReceived,
            AttachmentTransferStage.Verifying,
            isIncoming: true);
        bool succeeded = false;
        string message = transfer.UsesMemory
            ? transfer.IsInlineImage ? "图片校验失败。" : "表情校验失败。"
            : "文件校验失败，未保存不完整文件。";
        byte[]? contentBytes = null;
        try
        {
            Stream destinationStream = transfer.DestinationStream;
            await destinationStream.FlushAsync(context.Cts.Token).ConfigureAwait(false);
            if (transfer.UsesMemory && destinationStream is MemoryStream memoryStream)
            {
                contentBytes = memoryStream.ToArray();
            }

            destinationStream.Dispose();
            transfer.DestinationStream = null;
            byte[] actualHashBytes = transfer.Hash.GetHashAndReset();
            byte[] expectedHashBytes = Array.Empty<byte>();
            try
            {
                expectedHashBytes = Convert.FromHexString(transfer.Sha256);
                succeeded = transfer.BytesReceived == transfer.FileSize
                    && (contentBytes is null || contentBytes.LongLength == transfer.FileSize)
                    && CryptographicOperations.FixedTimeEquals(actualHashBytes, expectedHashBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actualHashBytes);
                if (expectedHashBytes.Length != 0)
                {
                    CryptographicOperations.ZeroMemory(expectedHashBytes);
                }
            }
            if (succeeded)
            {
                if (transfer.UsesMemory)
                {
                    message = transfer.IsInlineImage
                        ? "图片接收完成，校验通过。"
                        : "表情接收完成，校验通过。";
                }
                else
                {
                    File.Move(transfer.PartialPath!, transfer.DestinationPath!, overwrite: true);
                    transfer.PartialPath = null;
                    message = "接收完成，文件校验通过。";
                }
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or CryptographicException
                                           or FormatException
                                           or ObjectDisposedException)
        {
            Debug.WriteLine(exception);
            succeeded = false;
            message = "文件校验或保存失败。";
        }

        lock (context.AttachmentGate)
        {
            if (ReferenceEquals(context.IncomingAttachment, transfer))
            {
                context.IncomingAttachment = null;
            }
        }

        transfer.DisposeAndDeletePartial();
        AttachmentTransferCompletedEventArgs completedArgs = new(
            transfer.Id,
            transfer.FileName,
            transfer.ContentType,
            transfer.FileSize,
            succeeded && !transfer.UsesMemory ? transfer.DestinationPath : null,
            isIncoming: true,
            succeeded,
            succeeded ? AttachmentTransferStage.Completed : AttachmentTransferStage.Failed,
            message,
            transfer.IsSticker,
            succeeded && transfer.UsesMemory && contentBytes is not null
                ? contentBytes
                : ReadOnlyMemory<byte>.Empty,
            transfer.IsInlineImage);
        RaiseAttachmentCompleted(completedArgs);
        if (contentBytes is not null)
        {
            CryptographicOperations.ZeroMemory(contentBytes);
        }
        await SendProtocolMessageAsync(
                context,
                new NetworkMessage
                {
                    Type = MessageProtocol.AttachmentResult,
                    TransferId = transfer.Id,
                    Reason = succeeded ? "completed" : "hash_mismatch"
                },
                context.Cts.Token,
                false)
            .ConfigureAwait(false);
        return AttachmentMessageHandlingResult.Handled;
    }

    private Task<AttachmentMessageHandlingResult> HandleAttachmentCancelAsync(
        ConnectionContext context,
        string transferId)
    {
        IncomingAttachmentTransfer? incoming = null;
        OutgoingAttachmentTransfer? outgoing = null;
        lock (context.AttachmentGate)
        {
            if (context.IncomingAttachment is { } candidate
                && string.Equals(candidate.Id, transferId, StringComparison.Ordinal))
            {
                incoming = candidate;
                context.IncomingAttachment = null;
            }
            else if (context.OutgoingAttachment is { } sent
                     && string.Equals(sent.Id, transferId, StringComparison.Ordinal))
            {
                outgoing = sent;
            }
        }

        MarkInboundActivity(context);
        if (incoming is not null)
        {
            incoming.DisposeAndDeletePartial();
            RaiseAttachmentCompleted(new AttachmentTransferCompletedEventArgs(
                incoming.Id,
                incoming.FileName,
                incoming.ContentType,
                incoming.FileSize,
                null,
                isIncoming: true,
                succeeded: false,
                AttachmentTransferStage.Cancelled,
                "对方取消了发送。",
                incoming.IsSticker,
                isInlineImage: incoming.IsInlineImage));
            return Task.FromResult(AttachmentMessageHandlingResult.Handled);
        }

        if (outgoing is not null)
        {
            outgoing.Decision.TrySetResult(new AttachmentDecision(false, "对方取消了接收。"));
            outgoing.Completion.TrySetResult(new AttachmentCompletion(false, "对方取消了接收。"));
            return Task.FromResult(AttachmentMessageHandlingResult.Handled);
        }

        return Task.FromResult(AttachmentMessageHandlingResult.ProtocolError);
    }

    private async Task FailIncomingTransferAsync(
        ConnectionContext context,
        IncomingAttachmentTransfer transfer,
        AttachmentTransferStage stage,
        string message,
        bool sendResult)
    {
        lock (context.AttachmentGate)
        {
            if (ReferenceEquals(context.IncomingAttachment, transfer))
            {
                context.IncomingAttachment = null;
            }
        }

        transfer.DisposeAndDeletePartial();
        if (sendResult && Volatile.Read(ref context.CloseStarted) == 0)
        {
            try
            {
                await SendProtocolMessageAsync(
                        context,
                        new NetworkMessage
                        {
                            Type = MessageProtocol.AttachmentResult,
                            TransferId = transfer.Id,
                            Reason = "failed"
                        },
                        context.Cts.Token,
                        false)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(exception);
            }
        }

        RaiseAttachmentCompleted(new AttachmentTransferCompletedEventArgs(
            transfer.Id,
            transfer.FileName,
            transfer.ContentType,
            transfer.FileSize,
            null,
            isIncoming: true,
            succeeded: false,
            stage,
            message,
            isSticker: transfer.IsSticker,
            isInlineImage: transfer.IsInlineImage));
    }

    private async Task ExpireIncomingOfferAsync(
        ConnectionContext context,
        IncomingAttachmentTransfer transfer)
    {
        try
        {
            await Task.Delay(AttachmentDecisionTimeout, context.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        bool expired;
        lock (context.AttachmentGate)
        {
            expired = ReferenceEquals(context.IncomingAttachment, transfer) && !transfer.Accepted;
            if (expired)
            {
                context.IncomingAttachment = null;
            }
        }

        if (!expired)
        {
            return;
        }

        transfer.DisposeAndDeletePartial();
        await TrySendProtocolMessageAsync(
                context,
                new NetworkMessage
                {
                    Type = MessageProtocol.AttachmentReject,
                    TransferId = transfer.Id,
                    Reason = "timeout"
                })
            .ConfigureAwait(false);
        RaiseAttachmentCompleted(new AttachmentTransferCompletedEventArgs(
            transfer.Id,
            transfer.FileName,
            transfer.ContentType,
            transfer.FileSize,
            null,
            isIncoming: true,
            succeeded: false,
            AttachmentTransferStage.Failed,
            "接收确认已超时。",
            isSticker: transfer.IsSticker,
            isInlineImage: transfer.IsInlineImage));
    }

    private void AbortAttachmentTransfers(ConnectionContext context, ConnectionClosedReason reason)
    {
        IncomingAttachmentTransfer? incoming;
        OutgoingAttachmentTransfer? outgoing;
        lock (context.AttachmentGate)
        {
            incoming = context.IncomingAttachment;
            outgoing = context.OutgoingAttachment;
            context.IncomingAttachment = null;
            context.OutgoingAttachment = null;
        }

        incoming?.DisposeAndDeletePartial();
        string message = reason == ConnectionClosedReason.ApplicationClosing
            ? "程序关闭，传输已取消。"
            : "连接已断开，传输未完成。";
        if (incoming is not null)
        {
            RaiseAttachmentCompleted(new AttachmentTransferCompletedEventArgs(
                incoming.Id,
                incoming.FileName,
                incoming.ContentType,
                incoming.FileSize,
                null,
                isIncoming: true,
                succeeded: false,
                AttachmentTransferStage.Cancelled,
                message,
                incoming.IsSticker,
                isInlineImage: incoming.IsInlineImage));
        }

        if (outgoing is not null)
        {
            outgoing.Decision.TrySetCanceled();
            outgoing.Completion.TrySetCanceled();
        }
    }

    private AttachmentSendResult FinishOutgoingTransfer(
        OutgoingAttachmentTransfer transfer,
        bool succeeded,
        AttachmentTransferStage stage,
        string message)
    {
        RaiseAttachmentCompleted(new AttachmentTransferCompletedEventArgs(
            transfer.Id,
            transfer.FileName,
            transfer.ContentType,
            transfer.FileSize,
            transfer.LocalPath,
            isIncoming: false,
            succeeded,
            stage,
            message,
            transfer.IsSticker,
            isInlineImage: transfer.IsInlineImage));
        return new AttachmentSendResult(
            transfer.Id,
            succeeded,
            stage,
            message,
            transfer.IsSticker,
            transfer.IsInlineImage);
    }

    private async Task TrySendAttachmentCancelAsync(
        ConnectionContext context,
        string transferId,
        string reason)
    {
        if (Volatile.Read(ref context.CloseStarted) != 0)
        {
            return;
        }

        await TrySendProtocolMessageAsync(
                context,
                new NetworkMessage
                {
                    Type = MessageProtocol.AttachmentCancel,
                    TransferId = transferId,
                    Reason = reason
                })
            .ConfigureAwait(false);
    }

    private ConnectionContext? GetAuthenticatedContext()
    {
        lock (_stateGate)
        {
            return _activeConnection is { IsAuthenticated: true } context
                && Volatile.Read(ref context.CloseStarted) == 0
                ? context
                : null;
        }
    }

    private static bool IsValidAttachmentOffer(NetworkMessage message)
    {
        bool isInlineImage = string.Equals(
            message.Reason,
            InlineImageOfferReason,
            StringComparison.Ordinal);
        return message.TransferId is { Length: 32 } transferId
            && Guid.TryParseExact(transferId, "N", out _)
            && IsSafeOfferedFileName(message.FileName)
            && message.ContentType is { Length: > 0 } contentType
            && contentType.Length <= MaximumContentTypeCharacters
            && contentType.All(character => character is >= '!' and <= '~')
            && message.FileSize is >= 0 and <= MaximumAttachmentBytes
            && message.Sha256 is { Length: 64 } sha256
            && IsLowerHex(sha256)
            && (!IsStickerContentType(contentType)
                || (IsSupportedStickerFileName(message.FileName)
                    && message.FileSize is > 0 and <= MaximumStickerBytes))
            && (!isInlineImage
                || (IsInlineImageContentType(contentType)
                    && IsSupportedImageFileName(message.FileName, contentType)
                    && message.FileSize is > 0 and <= MaximumInlineImageBytes))
            && message.Text is null
            && message.Data is null
            && (message.Reason is null || isInlineImage)
            && message.ChunkIndex is null;
    }

    private static bool HasAttachmentPayload(NetworkMessage message)
    {
        return message.TransferId is not null
            || message.FileName is not null
            || message.ContentType is not null
            || message.FileSize is not null
            || message.Sha256 is not null
            || message.ChunkIndex is not null;
    }

    private static bool IsTransferControlMessage(NetworkMessage message, bool allowReason)
    {
        return message.TransferId is { Length: 32 } transferId
            && Guid.TryParseExact(transferId, "N", out _)
            && message.Text is null
            && message.Timestamp is null
            && message.Data is null
            && message.FileName is null
            && message.ContentType is null
            && message.FileSize is null
            && message.Sha256 is null
            && message.ChunkIndex is null
            && (allowReason ? message.Reason is { Length: > 0 and <= 64 } : message.Reason is null);
    }

    private static bool IsSafeOfferedFileName(string? fileName)
    {
        return !string.IsNullOrWhiteSpace(fileName)
            && fileName.Length <= MaximumAttachmentFileNameCharacters
            && string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
            && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && fileName is not "." and not "..";
    }

    private static bool IsSupportedStickerFileName(string? fileName)
    {
        string extension = Path.GetExtension(fileName ?? string.Empty);
        return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedImageFileName(string? fileName, string contentType)
    {
        string extension = Path.GetExtension(fileName ?? string.Empty);
        return contentType switch
        {
            "image/png" => string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase),
            "image/jpeg" => string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase),
            "image/gif" => string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase),
            "image/bmp" => string.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase),
            "image/webp" => string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool IsLowerHex(string value)
    {
        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeContentType(string? contentType, string fileName)
    {
        string normalized = contentType?.Trim() ?? string.Empty;
        if (normalized.Length is > 0 and <= MaximumContentTypeCharacters
            && normalized.All(character => character is >= '!' and <= '~'))
        {
            return normalized;
        }

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
    }

    private void RaiseAttachmentOffered(IncomingAttachmentOfferEventArgs args)
    {
        RaiseAttachmentEvent(AttachmentOffered, args);
    }

    private void RaiseAttachmentTransferStarted(AttachmentTransferStartedEventArgs args)
    {
        RaiseAttachmentEvent(AttachmentTransferStarted, args);
    }

    private void RaiseAttachmentProgress(
        AttachmentTransferState transfer,
        long bytesTransferred,
        AttachmentTransferStage stage,
        bool isIncoming)
    {
        if (stage == AttachmentTransferStage.Transferring
            && bytesTransferred > 0
            && bytesTransferred < transfer.FileSize
            && !transfer.ShouldReportProgress(bytesTransferred))
        {
            return;
        }

        RaiseAttachmentEvent(
            AttachmentTransferProgressChanged,
            new AttachmentTransferProgressEventArgs(
                transfer.Id,
                transfer.FileName,
                bytesTransferred,
                transfer.FileSize,
                isIncoming,
                stage,
                transfer.IsSticker,
                transfer.IsInlineImage));
    }

    private void RaiseAttachmentCompleted(AttachmentTransferCompletedEventArgs args)
    {
        RaiseAttachmentEvent(AttachmentTransferCompleted, args);
    }

    private void RaiseAttachmentEvent<TEventArgs>(EventHandler<TEventArgs>? handler, TEventArgs args)
        where TEventArgs : EventArgs
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, args);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    private enum AttachmentMessageHandlingResult
    {
        NotAttachment,
        Handled,
        ProtocolError
    }

    private readonly record struct AttachmentDecision(bool Accepted, string Reason);

    private readonly record struct AttachmentCompletion(bool Succeeded, string Message);

    private abstract class AttachmentTransferState
    {
        private long _lastProgressBytes;
        private long _lastProgressTick;

            protected AttachmentTransferState(
            string id,
            string fileName,
            string contentType,
            long fileSize,
            bool isInlineImage)
        {
            Id = id;
            FileName = fileName;
            ContentType = contentType;
            FileSize = fileSize;
            IsInlineImage = isInlineImage;
        }

        internal string Id { get; }

        internal string FileName { get; }

        internal string ContentType { get; }

        internal long FileSize { get; }

        internal bool IsSticker => IsStickerContentType(ContentType);

        internal bool IsInlineImage { get; }

        internal bool UsesMemory => IsSticker || IsInlineImage;

        internal bool ShouldReportProgress(long bytesTransferred)
        {
            long now = Environment.TickCount64;
            if (bytesTransferred - _lastProgressBytes < 256 * 1024
                && now - _lastProgressTick < 100)
            {
                return false;
            }

            _lastProgressBytes = bytesTransferred;
            _lastProgressTick = now;
            return true;
        }
    }

    private sealed class OutgoingAttachmentTransfer : AttachmentTransferState
    {
        internal OutgoingAttachmentTransfer(
            string id,
            string fileName,
            string contentType,
            long fileSize,
            string localPath,
            bool isInlineImage)
            : base(id, fileName, contentType, fileSize, isInlineImage)
        {
            LocalPath = localPath;
        }

        internal string LocalPath { get; }

        internal string Sha256 { get; set; } = string.Empty;

        internal TaskCompletionSource<AttachmentDecision> Decision { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<AttachmentCompletion> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class IncomingAttachmentTransfer : AttachmentTransferState
    {
        internal IncomingAttachmentTransfer(
            string id,
            string fileName,
            string contentType,
            long fileSize,
            string sha256,
            bool isInlineImage)
            : base(id, fileName, contentType, fileSize, isInlineImage)
        {
            Sha256 = sha256;
        }

        internal string Sha256 { get; }

        internal bool Accepted { get; private set; }

        internal string? DestinationPath { get; private set; }

        internal string? PartialPath { get; set; }

        internal Stream? DestinationStream { get; set; }

        internal IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        internal long BytesReceived { get; set; }

        internal int NextChunkIndex { get; set; }

        internal void OpenDestination(string destinationPath, string partialPath)
        {
            DestinationStream = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                AttachmentChunkBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            DestinationPath = destinationPath;
            PartialPath = partialPath;
            Accepted = true;
        }

        internal void OpenMemory()
        {
            long maximumMemoryBytes = IsSticker
                ? MaximumStickerBytes
                : IsInlineImage
                    ? MaximumInlineImageBytes
                    : 0;
            if (maximumMemoryBytes == 0 || FileSize is <= 0 || FileSize > maximumMemoryBytes)
            {
                throw new InvalidOperationException("Only bounded sticker or inline-image transfers can use memory storage.");
            }

            DestinationStream = new MemoryStream(checked((int)FileSize));
            DestinationPath = null;
            PartialPath = null;
            Accepted = true;
        }

        internal void DisposeAndDeletePartial()
        {
            if (DestinationStream is MemoryStream memoryStream)
            {
                try
                {
                    if (memoryStream.TryGetBuffer(out ArraySegment<byte> segment))
                    {
                        CryptographicOperations.ZeroMemory(segment.AsSpan());
                    }
                }
                catch (Exception exception) when (exception is ObjectDisposedException
                                                   or UnauthorizedAccessException)
                {
                    Debug.WriteLine(exception);
                }
            }

            try
            {
                DestinationStream?.Dispose();
            }
            catch (Exception exception)
            {
                Debug.WriteLine(exception);
            }

            DestinationStream = null;
            Hash.Dispose();
            if (string.IsNullOrWhiteSpace(PartialPath))
            {
                return;
            }

            try
            {
                File.Delete(PartialPath);
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or NotSupportedException)
            {
                Debug.WriteLine(exception);
            }

            PartialPath = null;
        }
    }
}
