using System.Collections.ObjectModel;

namespace LarkzeeChat.Models;

/// <summary>
/// A locally installed custom emoji pack. The pack metadata is persisted by
/// <see cref="Services.EmojiPackService"/>; the sticker files remain in the
/// pack's local storage directory.
/// </summary>
public sealed class EmojiPack
{
    public EmojiPack(
        string id,
        string name,
        string folderName,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc,
        IEnumerable<EmojiSticker> stickers,
        int version = 1)
    {
        Id = id;
        Name = name;
        FolderName = folderName;
        CreatedUtc = createdUtc;
        UpdatedUtc = updatedUtc;
        Version = version;
        Stickers = new ReadOnlyCollection<EmojiSticker>(
            (stickers ?? throw new ArgumentNullException(nameof(stickers))).ToList());
    }

    public string Id { get; }

    public string Name { get; }

    /// <summary>
    /// The safe direct-child folder name under the emoji-pack root.
    /// </summary>
    public string FolderName { get; }

    public int Version { get; }

    public DateTimeOffset CreatedUtc { get; }

    public DateTimeOffset UpdatedUtc { get; }

    public IReadOnlyList<EmojiSticker> Stickers { get; }
}

/// <summary>
/// Metadata for one locally stored PNG or GIF sticker.
/// </summary>
public sealed class EmojiSticker
{
    public EmojiSticker(
        string id,
        string fileName,
        string displayName,
        string contentType,
        long sizeBytes,
        string sha256)
    {
        Id = id;
        FileName = fileName;
        DisplayName = displayName;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
    }

    public string Id { get; }

    /// <summary>
    /// The generated safe file name stored inside the pack directory.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// The original file name without its extension, used for display only.
    /// </summary>
    public string DisplayName { get; }

    public string ContentType { get; }

    public long SizeBytes { get; }

    public string Sha256 { get; }
}

public sealed class EmojiPackImportFailure
{
    public EmojiPackImportFailure(string sourcePath, string reason)
    {
        SourcePath = sourcePath;
        Reason = reason;
    }

    public string SourcePath { get; }

    public string Reason { get; }
}

public sealed class EmojiPackImportResult
{
    public EmojiPackImportResult(
        EmojiPack? pack,
        IEnumerable<EmojiSticker> importedStickers,
        IEnumerable<EmojiPackImportFailure> rejectedFiles)
    {
        Pack = pack;
        ImportedStickers = new ReadOnlyCollection<EmojiSticker>(
            (importedStickers ?? throw new ArgumentNullException(nameof(importedStickers))).ToList());
        RejectedFiles = new ReadOnlyCollection<EmojiPackImportFailure>(
            (rejectedFiles ?? throw new ArgumentNullException(nameof(rejectedFiles))).ToList());
    }

    public EmojiPack? Pack { get; }

    public IReadOnlyList<EmojiSticker> ImportedStickers { get; }

    public IReadOnlyList<EmojiPackImportFailure> RejectedFiles { get; }

    public bool Succeeded => ImportedStickers.Count > 0 && RejectedFiles.Count == 0;
}
