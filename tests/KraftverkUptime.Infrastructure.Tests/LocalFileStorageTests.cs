using System.Text;
using FluentAssertions;
using KraftverkUptime.Infrastructure.Options;
using KraftverkUptime.Infrastructure.Storage;
using Microsoft.Extensions.Options;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests;

public sealed class LocalFileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kvu-tests-" + Guid.NewGuid().ToString("N"));

    private LocalFileStorage CreateStorage() =>
        new(Microsoft.Extensions.Options.Options.Create(new StorageOptions { Provider = "Local", LocalRootPath = _root }));

    [Fact]
    public async Task Put_Then_Get_Roundtrip()
    {
        var s = CreateStorage();
        var bytes = Encoding.UTF8.GetBytes("hello");
        using var ms = new MemoryStream(bytes);

        var returned = await s.PutAsync("sub/test.txt", ms);
        returned.Should().Be("sub/test.txt");

        await using var stream = await s.GetAsync("sub/test.txt");
        using var reader = new StreamReader(stream);
        var read = await reader.ReadToEndAsync();
        read.Should().Be("hello");
    }

    [Fact]
    public async Task Rejects_Absolute_Or_Traversal_Paths()
    {
        var s = CreateStorage();
        await using var ms = new MemoryStream();

        var attempts = new[] { "/etc/passwd", "..", "../secret.txt" };
        foreach (var path in attempts)
        {
            var act = async () => await s.PutAsync(path, ms);
            await act.Should().ThrowAsync<ArgumentException>();
        }
    }

    [Fact]
    public async Task Exists_Returns_False_For_Missing()
    {
        var s = CreateStorage();
        (await s.ExistsAsync("missing.txt")).Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
        }
        GC.SuppressFinalize(this);
    }
}
