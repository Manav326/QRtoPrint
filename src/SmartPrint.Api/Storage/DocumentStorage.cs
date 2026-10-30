using System.Buffers.Binary;
using System.Security.Cryptography;
using UglyToad.PdfPig;

namespace SmartPrint.Api.Storage;

public sealed record DocumentAnalysis(
    int PageCount,
    string PageSizesJson,
    string MediaType,
    string Extension);

public sealed class DocumentStorage(IConfiguration configuration, IWebHostEnvironment environment)
{
    private const long MaximumFileBytes = 20 * 1024 * 1024;
    private const int MaximumPages = 500;

    public async Task<StoredDocument> ValidateAndStoreAsync(
        Guid tenantId,
        Guid documentId,
        string originalFileName,
        Stream input,
        long declaredLength,
        CancellationToken cancellationToken)
    {
        if (declaredLength is <= 0 or > MaximumFileBytes)
        {
            throw new DocumentValidationException("File size must be between 1 byte and 20 MB.");
        }

        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        if (extension is not (".pdf" or ".jpg" or ".jpeg" or ".png"))
        {
            throw new DocumentValidationException("Only PDF, JPG, JPEG and PNG files are supported in this release.");
        }

        await using var memory = new MemoryStream((int)declaredLength);
        await input.CopyToAsync(memory, cancellationToken);
        if (memory.Length != declaredLength || memory.Length > MaximumFileBytes)
        {
            throw new DocumentValidationException("The uploaded file is incomplete or exceeds the 20 MB limit.");
        }

        var bytes = memory.ToArray();
        var analysis = Analyse(bytes, extension);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var storageKey = $"{tenantId:N}/{documentId:N}{extension}";
        var fullPath = ResolveStoragePath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using (var output = new FileStream(
            fullPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await output.WriteAsync(bytes, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }

        return new StoredDocument(storageKey, sha, bytes.LongLength, analysis);
    }

    public Task DeleteAsync(string storageKey)
    {
        var path = ResolveStoragePath(storageKey);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    public FileStream OpenRead(string storageKey)
    {
        var path = ResolveStoragePath(storageKey);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private string ResolveStoragePath(string storageKey)
    {
        var rootSetting = configuration["Documents:StorageRoot"];
        var root = Path.GetFullPath(string.IsNullOrWhiteSpace(rootSetting)
            ? Path.Combine(environment.ContentRootPath, "App_Data", "documents")
            : rootSetting);
        var full = Path.GetFullPath(Path.Combine(root, storageKey.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid document storage key.");
        }
        return full;
    }

    private static DocumentAnalysis Analyse(byte[] bytes, string extension)
    {
        if (extension == ".pdf")
        {
            if (bytes.Length < 8 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            {
                throw new DocumentValidationException("The file extension says PDF, but the file signature is invalid.");
            }

            try
            {
                using var document = PdfDocument.Open(new MemoryStream(bytes, writable: false));
                var pages = document.GetPages().Select(page => new
                {
                    widthPoints = Math.Round(page.Width, 2),
                    heightPoints = Math.Round(page.Height, 2),
                    orientation = page.Width > page.Height ? "Landscape" : page.Width < page.Height ? "Portrait" : "Square"
                }).ToArray();

                if (document.NumberOfPages is <= 0 or > MaximumPages)
                {
                    throw new DocumentValidationException($"PDFs must contain between 1 and {MaximumPages} pages.");
                }
                return new DocumentAnalysis(document.NumberOfPages, System.Text.Json.JsonSerializer.Serialize(pages), "application/pdf", ".pdf");
            }
            catch (DocumentValidationException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new DocumentValidationException("This PDF is damaged, password-protected or could not be analysed.");
            }
        }

        if (extension == ".png")
        {
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(signature) ||
                !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            {
                throw new DocumentValidationException("The PNG file signature is invalid.");
            }

            var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
            ValidateImageDimensions(width, height);
            return new DocumentAnalysis(1, ImagePageSize(width, height), "image/png", ".png");
        }

        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[2] != 0xFF)
        {
            throw new DocumentValidationException("The JPEG file signature is invalid.");
        }

        var dimensions = ReadJpegDimensions(bytes);
        ValidateImageDimensions(dimensions.Width, dimensions.Height);
        return new DocumentAnalysis(1, ImagePageSize(dimensions.Width, dimensions.Height), "image/jpeg", extension);
    }

    private static (uint Width, uint Height) ReadJpegDimensions(byte[] bytes)
    {
        var offset = 2;
        while (offset + 4 < bytes.Length)
        {
            if (bytes[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            while (offset < bytes.Length && bytes[offset] == 0xFF) offset++;
            if (offset >= bytes.Length) break;
            var marker = bytes[offset++];
            if (marker is 0xD8 or 0xD9 or >= 0xD0 and <= 0xD7 or 0x01) continue;
            if (offset + 2 > bytes.Length) break;
            var length = (bytes[offset] << 8) | bytes[offset + 1];
            if (length < 2 || offset + length > bytes.Length) break;

            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                if (length < 7) break;
                var height = (uint)((bytes[offset + 3] << 8) | bytes[offset + 4]);
                var width = (uint)((bytes[offset + 5] << 8) | bytes[offset + 6]);
                return (width, height);
            }
            offset += length;
        }
        throw new DocumentValidationException("The JPEG image dimensions could not be read.");
    }

    private static void ValidateImageDimensions(uint width, uint height)
    {
        if (width == 0 || height == 0 || (ulong)width * height > 100_000_000UL)
        {
            throw new DocumentValidationException("The image dimensions are invalid or too large to process safely.");
        }
    }

    private static string ImagePageSize(uint width, uint height) =>
        System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { pixelWidth = width, pixelHeight = height, orientation = width > height ? "Landscape" : width < height ? "Portrait" : "Square" }
        });
}

public sealed record StoredDocument(string StorageKey, string Sha256, long SizeBytes, DocumentAnalysis Analysis);

public sealed class DocumentValidationException(string message) : Exception(message);
