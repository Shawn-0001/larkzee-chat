using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LarkzeeChat.Models;

namespace LarkzeeChat.Services;

/// <summary>
/// Stores user-imported PNG/GIF emoji packs under the current user's local
/// application data directory. The root is injectable so storage behavior can
/// be tested without touching the production profile.
/// </summary>
public sealed class EmojiPackService
{
    public const int MetadataVersion = 1;
    public const long MaximumStickerBytes = 1L * 1024 * 1024;
    public const string DefaultPackName = "自定义";

    private const int MaximumPackNameCharacters = 120;
    private const int MaximumDisplayNameCharacters = 160;
    private const int MaximumStickerDimension = 8192;
    private const string IndexFileName = "index.json";
    private const string PackMetadataFileName = "pack.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly object _gate = new();
    private readonly string _rootPath;

    public EmojiPackService()
        : this(GetDefaultRootPath())
    {
    }

    public EmojiPackService(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        EnsureRootDirectory();
    }

    /// <summary>
    /// The directory that can be opened by the UI in Windows Explorer.
    /// </summary>
    public string RootPath => _rootPath;

    /// <summary>
    /// Alias for callers that present this value as a storage location.
    /// </summary>
    public string StoragePath => _rootPath;

    public IReadOnlyList<EmojiPack> ListPacks()
    {
        lock (_gate)
        {
            return new ReadOnlyCollection<EmojiPack>(LoadPacksNoLock());
        }
    }

    /// <summary>
    /// Re-reads UTF-8 metadata from disk and returns the current valid packs.
    /// Invalid or tampered entries are ignored rather than surfaced as usable
    /// paths.
    /// </summary>
    public IReadOnlyList<EmojiPack> Refresh()
    {
        return ListPacks();
    }

    /// <summary>
    /// Imports individual PNG/GIF files into the default 自定义 pack, or into
    /// the supplied pack name. Source files are copied into generated safe
    /// names so later movement/deletion of the originals does not matter.
    /// </summary>
    public EmojiPackImportResult ImportFiles(
        IEnumerable<string> filePaths,
        string? packName = null)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        return ImportFilesCore(filePaths, packName ?? DefaultPackName);
    }

    /// <summary>
    /// Imports the top-level PNG/GIF files in a directory. The resulting pack
    /// name defaults to the source directory name.
    /// </summary>
    public EmojiPackImportResult ImportFolder(
        string folderPath,
        string? packName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(folderPath);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or NotSupportedException
                                           or PathTooLongException)
        {
            return new EmojiPackImportResult(
                null,
                Array.Empty<EmojiSticker>(),
                [new EmojiPackImportFailure(folderPath, "表情包目录路径无效。")]);
        }

        if (!Directory.Exists(fullPath))
        {
            return new EmojiPackImportResult(
                null,
                Array.Empty<EmojiSticker>(),
                [new EmojiPackImportFailure(fullPath, "表情包目录不存在。")]);
        }

        string resolvedName = packName ?? new DirectoryInfo(fullPath).Name;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(fullPath, "*", SearchOption.TopDirectoryOnly)
                .Where(IsSupportedStickerFile)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or DirectoryNotFoundException
                                           or NotSupportedException)
        {
            return new EmojiPackImportResult(
                null,
                Array.Empty<EmojiSticker>(),
                [new EmojiPackImportFailure(fullPath, "无法读取表情包目录。")]);
        }

        return ImportFilesCore(files, resolvedName);
    }

    /// <summary>
    /// Removes a pack only when its generated folder resolves to a direct
    /// child of <see cref="RootPath"/>. A reparse-point folder is rejected so
    /// a tampered metadata file cannot redirect deletion elsewhere.
    /// </summary>
    public bool DeletePack(string packId)
    {
        if (!IsGuidId(packId))
        {
            return false;
        }

        lock (_gate)
        {
            List<EmojiPack> packs = LoadPacksNoLock();
            EmojiPack? pack = packs.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, packId, StringComparison.OrdinalIgnoreCase));
            if (pack is null)
            {
                return false;
            }

            if (!TryGetPackDirectory(pack, out string packDirectory)
                || !Directory.Exists(packDirectory))
            {
                return false;
            }

            DirectoryInfo directoryInfo = new(packDirectory);
            if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            try
            {
                Directory.Delete(packDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or NotSupportedException
                                               or DirectoryNotFoundException)
            {
                return false;
            }

            PersistIndexNoLock(packs
                .Where(candidate => !string.Equals(candidate.Id, pack.Id, StringComparison.Ordinal))
                .ToList());
            return true;
        }
    }

    public bool TryGetStickerPath(
        string packId,
        string stickerId,
        out string stickerPath)
    {
        stickerPath = string.Empty;
        if (!IsGuidId(packId) || !IsGuidId(stickerId))
        {
            return false;
        }

        lock (_gate)
        {
            EmojiPack? pack = LoadPacksNoLock().FirstOrDefault(candidate =>
                string.Equals(candidate.Id, packId, StringComparison.OrdinalIgnoreCase));
            EmojiSticker? sticker = pack?.Stickers.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, stickerId, StringComparison.OrdinalIgnoreCase));
            if (pack is null
                || sticker is null
                || !TryGetPackDirectory(pack, out string packDirectory)
                || !TryGetSafeFilePath(packDirectory, sticker.FileName, out string candidatePath)
                || !File.Exists(candidatePath))
            {
                return false;
            }

            stickerPath = candidatePath;
            return true;
        }
    }

    public string GetStickerPath(string packId, string stickerId)
    {
        return TryGetStickerPath(packId, stickerId, out string stickerPath)
            ? stickerPath
            : throw new FileNotFoundException("找不到指定的本地表情。", stickerId);
    }

    /// <summary>
    /// Exports the cached copy of a pack to a user-selected directory. The
    /// original import paths are never consulted; export always reads from
    /// the managed local cache.
    /// </summary>
    public int ExportPack(string packId, string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        lock (_gate)
        {
            EmojiPack? pack = LoadPacksNoLock().FirstOrDefault(candidate =>
                string.Equals(candidate.Id, packId, StringComparison.OrdinalIgnoreCase));
            if (pack is null || !TryGetPackDirectory(pack, out string packDirectory))
            {
                throw new FileNotFoundException("找不到要导出的表情包。", packId);
            }

            string exportRoot = Path.GetFullPath(destinationDirectory);
            string exportDirectory = Path.Combine(exportRoot, NormalizeExportFolderName(pack.Name));
            Directory.CreateDirectory(exportDirectory);

            int exported = 0;
            foreach (EmojiSticker sticker in pack.Stickers)
            {
                if (!TryGetSafeFilePath(packDirectory, sticker.FileName, out string sourcePath)
                    || !File.Exists(sourcePath))
                {
                    continue;
                }

                string extension = Path.GetExtension(sticker.FileName).ToLowerInvariant();
                string baseName = NormalizeExportFileName(sticker.DisplayName);
                string destinationPath = Path.Combine(exportDirectory, baseName + extension);
                int suffix = 2;
                while (File.Exists(destinationPath))
                {
                    destinationPath = Path.Combine(exportDirectory, $"{baseName}-{suffix}{extension}");
                    suffix++;
                }

                File.Copy(sourcePath, destinationPath, overwrite: false);
                exported++;
            }

            return exported;
        }
    }

    private EmojiPackImportResult ImportFilesCore(
        IEnumerable<string> filePaths,
        string packName)
    {
        string normalizedPackName = NormalizePackName(packName);
        string[] sources = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sources.Length == 0)
        {
            return new EmojiPackImportResult(
                null,
                Array.Empty<EmojiSticker>(),
                Array.Empty<EmojiPackImportFailure>());
        }

        lock (_gate)
        {
            List<EmojiPack> packs = LoadPacksNoLock();
            EmojiPack? pack = packs.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, normalizedPackName, StringComparison.Ordinal));
            bool createdPack = false;
            List<EmojiSticker> imported = [];
            List<EmojiPackImportFailure> rejected = [];

            foreach (string source in sources)
            {
                if (!TryReadSticker(source, out byte[]? bytes, out string extension, out string contentType, out string reason))
                {
                    rejected.Add(new EmojiPackImportFailure(source, reason));
                    continue;
                }

                try
                {
                    if (pack is null)
                    {
                        pack = CreatePackNoLock(normalizedPackName);
                        packs.Add(pack);
                        createdPack = true;
                    }

                    string stickerId = Guid.NewGuid().ToString("N");
                    string storedFileName = stickerId + extension;
                    if (!TryGetPackDirectory(pack, out string packDirectory)
                        || !TryGetSafeFilePath(packDirectory, storedFileName, out string destinationPath))
                    {
                        rejected.Add(new EmojiPackImportFailure(source, "表情包存储路径无效。"));
                        continue;
                    }

                    WriteAssetAtomically(destinationPath, bytes!);
                    byte[] digest = SHA256.HashData(bytes!);
                    string displayName = NormalizeDisplayName(Path.GetFileNameWithoutExtension(source));
                    imported.Add(new EmojiSticker(
                        stickerId,
                        storedFileName,
                        displayName,
                        contentType,
                        bytes!.LongLength,
                        Convert.ToHexString(digest).ToLowerInvariant()));
                    CryptographicOperations.ZeroMemory(digest);
                }
                catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException
                                                   or NotSupportedException
                                                   or PathTooLongException)
                {
                    rejected.Add(new EmojiPackImportFailure(source, "无法保存表情文件。"));
                }
                finally
                {
                    if (bytes is not null)
                    {
                        CryptographicOperations.ZeroMemory(bytes);
                    }
                }
            }

            if (imported.Count == 0)
            {
                if (createdPack && pack is not null && TryGetPackDirectory(pack, out string newPackDirectory))
                {
                    TryDeleteCreatedPackDirectory(newPackDirectory);
                    packs.Remove(pack);
                }

                return new EmojiPackImportResult(
                    pack,
                    imported,
                    rejected);
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            List<EmojiSticker> stickers = pack!.Stickers.Concat(imported).ToList();
            EmojiPack updatedPack = new(
                pack.Id,
                pack.Name,
                pack.FolderName,
                pack.CreatedUtc,
                now,
                stickers,
                MetadataVersion);
            int packIndex = packs.FindIndex(candidate =>
                string.Equals(candidate.Id, pack.Id, StringComparison.Ordinal));
            packs[packIndex] = updatedPack;
            PersistPackNoLock(updatedPack);
            PersistIndexNoLock(packs);
            return new EmojiPackImportResult(updatedPack, imported, rejected);
        }
    }

    private List<EmojiPack> LoadPacksNoLock()
    {
        EnsureRootDirectory();
        PersistedIndex? index = ReadIndexNoLock();
        List<EmojiPack> packs = [];
        if (index is not null)
        {
            foreach (PersistedPackReference reference in index.Packs ?? [])
            {
                if (!IsGuidId(reference.Id)
                    || !IsSafeStorageFolderName(reference.FolderName))
                {
                    continue;
                }

                EmojiPack? pack = ReadPackNoLock(reference.FolderName, reference.Id);
                if (pack is not null)
                {
                    packs.Add(pack);
                }
            }
        }

        if (packs.Count == 0)
        {
            packs = ScanPackDirectoriesNoLock();
        }

        return packs
            .GroupBy(pack => pack.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(pack => pack.Name, StringComparer.Ordinal)
            .ThenBy(pack => pack.Id, StringComparer.Ordinal)
            .ToList();
    }

    private List<EmojiPack> ScanPackDirectoriesNoLock()
    {
        List<EmojiPack> packs = [];
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(_rootPath, "*", SearchOption.TopDirectoryOnly)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or DirectoryNotFoundException
                                           or NotSupportedException)
        {
            return packs;
        }

        foreach (string directory in directories)
        {
            string folderName = Path.GetFileName(directory);
            if (!IsSafeStorageFolderName(folderName))
            {
                continue;
            }

            EmojiPack? pack = ReadPackNoLock(folderName, folderName);
            if (pack is not null)
            {
                packs.Add(pack);
            }
        }

        return packs;
    }

    private EmojiPack? ReadPackNoLock(string folderName, string expectedId)
    {
        if (!IsGuidId(expectedId)
            || !IsSafeStorageFolderName(folderName)
            || !TryGetSafeDirectoryPath(folderName, out string packDirectory))
        {
            return null;
        }

        string metadataPath = Path.Combine(packDirectory, PackMetadataFileName);
        PersistedPack? persisted;
        try
        {
            persisted = ReadJson<PersistedPack>(metadataPath);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or DecoderFallbackException
                                           or NotSupportedException)
        {
            return null;
        }

        if (persisted is null
            || persisted.Version != MetadataVersion
            || !string.Equals(persisted.Id, expectedId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(persisted.FolderName, folderName, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(persisted.Name))
        {
            return null;
        }

        List<EmojiSticker> stickers = [];
        foreach (PersistedSticker persistedSticker in persisted.Stickers ?? [])
        {
            if (!IsGuidId(persistedSticker.Id)
                || !IsSafeStoredFileName(persistedSticker.FileName)
                || !IsSupportedContentType(persistedSticker.ContentType)
                || persistedSticker.SizeBytes is < 0 or > MaximumStickerBytes
                || persistedSticker.Sha256 is not { Length: 64 } sha256
                || !IsLowerHex(sha256)
                || !TryGetSafeFilePath(packDirectory, persistedSticker.FileName, out string stickerPath)
                || !File.Exists(stickerPath))
            {
                continue;
            }

            try
            {
                if (new FileInfo(stickerPath).Length != persistedSticker.SizeBytes)
                {
                    continue;
                }
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or FileNotFoundException)
            {
                continue;
            }

            stickers.Add(new EmojiSticker(
                persistedSticker.Id,
                persistedSticker.FileName,
                NormalizeDisplayName(persistedSticker.DisplayName),
                persistedSticker.ContentType,
                persistedSticker.SizeBytes,
                persistedSticker.Sha256));
        }

        return new EmojiPack(
            persisted.Id,
            NormalizePackName(persisted.Name),
            folderName,
            persisted.CreatedUtc,
            persisted.UpdatedUtc,
            stickers,
            persisted.Version);
    }

    private EmojiPack CreatePackNoLock(string name)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string id = Guid.NewGuid().ToString("N");
        string folderName = id;
        if (!TryGetSafeDirectoryPath(folderName, out string packDirectory))
        {
            throw new IOException("表情包存储路径无效。");
        }

        Directory.CreateDirectory(packDirectory);
        return new EmojiPack(id, name, folderName, now, now, Array.Empty<EmojiSticker>(), MetadataVersion);
    }

    private void PersistPackNoLock(EmojiPack pack)
    {
        if (!TryGetPackDirectory(pack, out string packDirectory))
        {
            throw new IOException("表情包存储路径无效。");
        }

        Directory.CreateDirectory(packDirectory);
        PersistedPack persisted = new()
        {
            Version = MetadataVersion,
            Id = pack.Id,
            Name = pack.Name,
            FolderName = pack.FolderName,
            CreatedUtc = pack.CreatedUtc,
            UpdatedUtc = pack.UpdatedUtc,
            Stickers = pack.Stickers.Select(sticker => new PersistedSticker
            {
                Id = sticker.Id,
                FileName = sticker.FileName,
                DisplayName = sticker.DisplayName,
                ContentType = sticker.ContentType,
                SizeBytes = sticker.SizeBytes,
                Sha256 = sticker.Sha256
            }).ToList()
        };
        WriteJsonAtomically(Path.Combine(packDirectory, PackMetadataFileName), persisted);
    }

    private void PersistIndexNoLock(IReadOnlyList<EmojiPack> packs)
    {
        PersistedIndex index = new()
        {
            Version = MetadataVersion,
            Packs = packs.Select(pack => new PersistedPackReference
            {
                Id = pack.Id,
                Name = pack.Name,
                FolderName = pack.FolderName,
                UpdatedUtc = pack.UpdatedUtc
            }).ToList()
        };
        WriteJsonAtomically(Path.Combine(_rootPath, IndexFileName), index);
    }

    private PersistedIndex? ReadIndexNoLock()
    {
        try
        {
            PersistedIndex? index = ReadJson<PersistedIndex>(Path.Combine(_rootPath, IndexFileName));
            return index?.Version == MetadataVersion ? index : null;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or DecoderFallbackException
                                           or NotSupportedException)
        {
            return null;
        }
    }

    private static T? ReadJson<T>(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        try
        {
            string json = StrictUtf8.GetString(bytes);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(value, JsonOptions);
            File.WriteAllText(temporaryPath, json, StrictUtf8);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or NotSupportedException)
            {
                // Metadata remains valid at the target path; a leftover temp
                // file is ignored by the loader and can be removed later.
            }
        }
    }

    private static void WriteAssetAtomically(string destinationPath, byte[] bytes)
    {
        string temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or NotSupportedException)
            {
                // The generated destination remains unique; the temp file is
                // not referenced by pack metadata.
            }
        }
    }

    private static bool TryReadSticker(
        string sourcePath,
        out byte[]? bytes,
        out string extension,
        out string contentType,
        out string reason)
    {
        bytes = null;
        extension = string.Empty;
        contentType = string.Empty;
        reason = string.Empty;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(sourcePath);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or NotSupportedException
                                           or PathTooLongException)
        {
            reason = "表情文件路径无效。";
            return false;
        }

        extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (extension is not ".png" and not ".gif")
        {
            reason = "仅支持 PNG 和 GIF 表情。";
            return false;
        }

        if (!File.Exists(fullPath))
        {
            reason = "表情文件不存在。";
            return false;
        }

        try
        {
            FileInfo fileInfo = new(fullPath);
            if (fileInfo.Length is <= 0 or > MaximumStickerBytes)
            {
                reason = "表情文件必须大于 0 且不超过 1 MiB。";
                return false;
            }

            bytes = File.ReadAllBytes(fullPath);
            if (!TryValidateDecodedImage(bytes, extension, out reason))
            {
                CryptographicOperations.ZeroMemory(bytes);
                bytes = null;
                return false;
            }

            contentType = extension == ".png" ? "image/png" : "image/gif";
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or OutOfMemoryException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            if (bytes is not null)
            {
                CryptographicOperations.ZeroMemory(bytes);
                bytes = null;
            }

            reason = "无法读取或解析表情文件。";
            return false;
        }
    }

    private static bool TryValidateDecodedImage(
        byte[] bytes,
        string extension,
        out string reason)
    {
        reason = string.Empty;
        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            using Image image = Image.FromStream(
                stream,
                useEmbeddedColorManagement: false,
                validateImageData: true);
            if (image.Width <= 0
                || image.Height <= 0
                || image.Width > MaximumStickerDimension
                || image.Height > MaximumStickerDimension)
            {
                reason = "表情图片尺寸无效或过大。";
                return false;
            }

            Guid expectedFormat = extension == ".png" ? ImageFormat.Png.Guid : ImageFormat.Gif.Guid;
            if (image.RawFormat.Guid != expectedFormat)
            {
                reason = "文件扩展名与图片格式不匹配。";
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or ExternalException
                                           or OutOfMemoryException
                                           or InvalidOperationException)
        {
            reason = "表情文件不是有效的 PNG 或 GIF 图片。";
            return false;
        }
    }

    private static bool IsSupportedStickerFile(string path)
    {
        string extension = Path.GetExtension(path);
        return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedContentType(string? contentType)
    {
        return string.Equals(contentType, "image/png", StringComparison.Ordinal)
            || string.Equals(contentType, "image/gif", StringComparison.Ordinal);
    }

    private static bool IsSafeStoredFileName(string? fileName)
    {
        return !string.IsNullOrWhiteSpace(fileName)
            && fileName.Length <= 100
            && string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
            && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && Path.GetExtension(fileName).ToLowerInvariant() is ".png" or ".gif";
    }

    private static bool IsGuidId(string? value)
    {
        return value is { Length: 32 } && Guid.TryParseExact(value, "N", out _);
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

    private static string NormalizePackName(string? value)
    {
        string candidate = string.IsNullOrWhiteSpace(value) ? DefaultPackName : value.Trim();
        StringBuilder builder = new(candidate.Length);
        foreach (char character in candidate)
        {
            if (char.IsControl(character) || Path.GetInvalidFileNameChars().Contains(character))
            {
                builder.Append('_');
            }
            else
            {
                builder.Append(character);
            }

            if (builder.Length >= MaximumPackNameCharacters)
            {
                break;
            }
        }

        string normalized = builder.ToString().Trim().TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(normalized) || normalized is "." or ".."
            ? DefaultPackName
            : normalized;
    }

    private static string NormalizeDisplayName(string? value)
    {
        string candidate = string.IsNullOrWhiteSpace(value) ? "表情" : value.Trim();
        StringBuilder builder = new(candidate.Length);
        foreach (char character in candidate)
        {
            builder.Append(char.IsControl(character) ? '_' : character);
            if (builder.Length >= MaximumDisplayNameCharacters)
            {
                break;
            }
        }

        string normalized = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "表情" : normalized;
    }

    private static string NormalizeExportFolderName(string value)
    {
        string normalized = NormalizeExportFileName(value);
        return string.IsNullOrWhiteSpace(normalized) ? "表情包" : normalized;
    }

    private static string NormalizeExportFileName(string? value)
    {
        string candidate = string.IsNullOrWhiteSpace(value) ? "表情" : value.Trim();
        char[] invalid = Path.GetInvalidFileNameChars();
        StringBuilder builder = new(candidate.Length);
        foreach (char character in candidate)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        string normalized = builder.ToString().Trim().TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(normalized) ? "表情" : normalized;
    }

    private bool TryGetPackDirectory(EmojiPack pack, out string packDirectory)
    {
        packDirectory = string.Empty;
        if (!IsGuidId(pack.Id)
            || !IsSafeStorageFolderName(pack.FolderName)
            || !string.Equals(pack.Id, pack.FolderName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryGetSafeDirectoryPath(pack.FolderName, out packDirectory);
    }

    private bool TryGetSafeDirectoryPath(string folderName, out string directoryPath)
    {
        directoryPath = string.Empty;
        if (!IsSafeStorageFolderName(folderName))
        {
            return false;
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(_rootPath, folderName));
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or NotSupportedException
                                           or PathTooLongException)
        {
            return false;
        }

        if (!IsWithinRoot(candidate) || string.Equals(candidate, _rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        directoryPath = candidate;
        return true;
    }

    private bool TryGetSafeFilePath(
        string directoryPath,
        string fileName,
        out string filePath)
    {
        filePath = string.Empty;
        if (!IsSafeStoredFileName(fileName))
        {
            return false;
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(directoryPath, fileName));
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or NotSupportedException
                                           or PathTooLongException)
        {
            return false;
        }

        string fullDirectory;
        try
        {
            fullDirectory = Path.GetFullPath(directoryPath);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or NotSupportedException
                                           or PathTooLongException)
        {
            return false;
        }

        string relative = Path.GetRelativePath(fullDirectory, candidate);
        if (relative is "." or ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            return false;
        }

        filePath = candidate;
        return true;
    }

    private bool IsWithinRoot(string candidate)
    {
        string relative = Path.GetRelativePath(_rootPath, candidate);
        return relative is not "."
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static bool IsSafeStorageFolderName(string? folderName)
    {
        return folderName is { } candidate
            && IsGuidId(candidate)
            && string.Equals(candidate, Path.GetFileName(candidate), StringComparison.Ordinal)
            && candidate.IndexOfAny(Path.GetInvalidPathChars()) < 0;
    }

    private void EnsureRootDirectory()
    {
        Directory.CreateDirectory(_rootPath);
    }

    private static void TryDeleteCreatedPackDirectory(string packDirectory)
    {
        try
        {
            if (Directory.Exists(packDirectory)
                && !Directory.EnumerateFileSystemEntries(packDirectory).Any())
            {
                Directory.Delete(packDirectory, recursive: false);
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or NotSupportedException)
        {
            // A failed cleanup leaves an unreferenced generated directory; it
            // cannot be reached through a valid index entry.
        }
    }

    private static string GetDefaultRootPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LarkzeeChat",
            "EmojiPacks");
    }

    private sealed class PersistedIndex
    {
        public int Version { get; set; }

        public List<PersistedPackReference>? Packs { get; set; }
    }

    private sealed class PersistedPackReference
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string FolderName { get; set; } = string.Empty;

        public DateTimeOffset UpdatedUtc { get; set; }
    }

    private sealed class PersistedPack
    {
        public int Version { get; set; }

        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string FolderName { get; set; } = string.Empty;

        public DateTimeOffset CreatedUtc { get; set; }

        public DateTimeOffset UpdatedUtc { get; set; }

        public List<PersistedSticker>? Stickers { get; set; }
    }

    private sealed class PersistedSticker
    {
        public string Id { get; set; } = string.Empty;

        public string FileName { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string ContentType { get; set; } = string.Empty;

        public long SizeBytes { get; set; }

        public string Sha256 { get; set; } = string.Empty;
    }
}
