using System.Runtime.CompilerServices;
using Azure.Storage.Blobs;
using KraftverkUptime.Core.Storage;
using KraftverkUptime.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace KraftverkUptime.Infrastructure.Storage;

/// <summary>
/// Azure Blob / Azurite IFileStorage. Samme interface som LocalFileStorage.
/// Bruker Managed Identity i prod (DefaultAzureCredential) og connection string mot Azurite i dev.
/// </summary>
public sealed class BlobFileStorage : IFileStorage
{
    private readonly BlobContainerClient _container;

    public BlobFileStorage(BlobServiceClient serviceClient, IOptions<StorageOptions> options)
    {
        _container = serviceClient.GetBlobContainerClient(options.Value.ContainerName);
        _container.CreateIfNotExists();
    }

    public async Task<string> PutAsync(string path, Stream content, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(path);
        await blob.UploadAsync(content, overwrite: true, cancellationToken: ct).ConfigureAwait(false);
        return path;
    }

    public async Task<Stream> GetAsync(string path, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(path);
        var response = await blob.DownloadStreamingAsync(cancellationToken: ct).ConfigureAwait(false);
        return response.Value.Content;
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(path);
        var response = await blob.ExistsAsync(ct).ConfigureAwait(false);
        return response.Value;
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(path);
        await blob.DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<string> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in _container.GetBlobsAsync(prefix: prefix, cancellationToken: ct).ConfigureAwait(false))
        {
            yield return item.Name;
        }
    }
}
