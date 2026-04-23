using System.Runtime.CompilerServices;
using KraftverkUptime.Core.Storage;
using KraftverkUptime.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace KraftverkUptime.Infrastructure.Storage;

/// <summary>
/// Lokalt filsystem-basert IFileStorage. V1 dev.
/// Alle stier er relative til StorageOptions.LocalRootPath.
/// </summary>
public sealed class LocalFileStorage : IFileStorage
{
    private readonly string _rootPath;

    public LocalFileStorage(IOptions<StorageOptions> options)
    {
        _rootPath = Path.GetFullPath(options.Value.LocalRootPath);
        Directory.CreateDirectory(_rootPath);
    }

    public async Task<string> PutAsync(string path, Stream content, CancellationToken ct = default)
    {
        var full = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var fs = File.Create(full);
        await content.CopyToAsync(fs, ct).ConfigureAwait(false);
        return path;
    }

    public Task<Stream> GetAsync(string path, CancellationToken ct = default)
    {
        var full = Resolve(path);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"Storage path not found: {path}", full);
        }
        Stream s = File.OpenRead(full);
        return Task.FromResult(s);
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        return Task.FromResult(File.Exists(Resolve(path)));
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var full = Resolve(path);
        if (File.Exists(full))
        {
            File.Delete(full);
        }
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var full = Resolve(prefix);
        var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full) ?? _rootPath;
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        var pattern = Directory.Exists(full) ? "*" : Path.GetFileName(full) + "*";
        foreach (var file in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            yield return Path.GetRelativePath(_rootPath, file).Replace(Path.DirectorySeparatorChar, '/');
            await Task.Yield();
        }
    }

    private string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must not be empty.", nameof(path));
        }

        if (Path.IsPathRooted(path) || path.Contains(".."))
        {
            throw new ArgumentException("Absolute paths and '..' traversal are not allowed.", nameof(path));
        }

        return Path.Combine(_rootPath, path.Replace('/', Path.DirectorySeparatorChar));
    }
}
