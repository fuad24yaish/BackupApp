using System.IO.Compression;
using System.Security.Cryptography;

namespace BackupApp.Core;

/// <summary>
/// Content-addressed store: each unique file content is saved once as a gzip-compressed
/// blob at objects/&lt;first two hash chars&gt;/&lt;rest&gt;.gz, keyed by the SHA-256 of the
/// uncompressed content. Identical content across files/versions shares one blob.
/// </summary>
public sealed class BlobStore
{
    private readonly string _objectsDir;
    private readonly string _tmpDir;

    public string StoreDirectory { get; }

    public BlobStore(string storeDirectory)
    {
        StoreDirectory = Path.GetFullPath(storeDirectory);
        _objectsDir = Path.Combine(StoreDirectory, "objects");
        _tmpDir = Path.Combine(StoreDirectory, "tmp");
        Directory.CreateDirectory(_objectsDir);
        Directory.CreateDirectory(_tmpDir);
    }

    /// <summary>Store-relative location of a blob; shared with the mirror so replicas have identical layout.</summary>
    public static string RelativeBlobPath(string hash) =>
        Path.Combine("objects", hash[..2], hash[2..] + ".gz");

    private string BlobPath(string hash) => Path.Combine(StoreDirectory, RelativeBlobPath(hash));

    public bool Contains(string hash) => File.Exists(BlobPath(hash));

    /// <summary>Hashes and stores the stream's content; returns (sha256 hex, uncompressed size).</summary>
    public (string Hash, long Size) Store(Stream source)
    {
        string tmp = Path.Combine(_tmpDir, Guid.NewGuid().ToString("N") + ".tmp");
        long size = 0;
        using var sha = SHA256.Create();
        try
        {
            using (var tmpFs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            using (var gz = new GZipStream(tmpFs, CompressionLevel.Fastest))
            {
                var buf = new byte[81920];
                int n;
                while ((n = source.Read(buf, 0, buf.Length)) > 0)
                {
                    sha.TransformBlock(buf, 0, n, null, 0);
                    gz.Write(buf, 0, n);
                    size += n;
                }
            }
            sha.TransformFinalBlock([], 0, 0);
            string hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();

            string final = BlobPath(hash);
            if (File.Exists(final))
            {
                File.Delete(tmp);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                File.Move(tmp, final);
            }
            return (hash, size);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    public Stream OpenRead(string hash)
    {
        var fs = new FileStream(BlobPath(hash), FileMode.Open, FileAccess.Read, FileShare.Read);
        return new GZipStream(fs, CompressionMode.Decompress);
    }

    public void ExtractTo(string hash, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        using var src = OpenRead(hash);
        using var dst = new FileStream(targetPath, FileMode.Create, FileAccess.Write);
        src.CopyTo(dst);
    }

    /// <summary>All blobs on disk with their compressed size and write time (for GC).</summary>
    public IEnumerable<(string Hash, long SizeBytes, DateTime WrittenUtc)> EnumerateBlobs()
    {
        var root = new DirectoryInfo(_objectsDir);
        if (!root.Exists) yield break;
        foreach (var sub in root.EnumerateDirectories())
        {
            if (sub.Name.Length != 2) continue;
            foreach (var f in sub.EnumerateFiles("*.gz"))
                yield return (sub.Name + Path.GetFileNameWithoutExtension(f.Name), f.Length, f.LastWriteTimeUtc);
        }
    }

    public bool TryDelete(string hash)
    {
        try
        {
            File.Delete(BlobPath(hash));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public long GetStoreSizeBytes()
    {
        long total = 0;
        var dir = new DirectoryInfo(_objectsDir);
        if (!dir.Exists) return 0;
        foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            total += f.Length;
        return total;
    }
}
