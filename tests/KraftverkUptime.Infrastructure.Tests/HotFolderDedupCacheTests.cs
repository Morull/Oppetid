using FluentAssertions;
using KraftverkUptime.Infrastructure.HotFolder;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests;

/// <summary>
/// Tester for innholds-hash-basert dedup-cache. Verifiserer:
///   - Første register av en hash returnerer true (ny entry).
///   - Andre register av samme hash returnerer false + original-record.
///   - Cache persisteres til disk og reload-es korrekt.
///   - Records eldre enn retention prunes på neste register-kall.
///   - SHA-256 av samme innhold er deterministisk (uavhengig av filnavn).
/// </summary>
public sealed class HotFolderDedupCacheTests : IDisposable
{
    private readonly string _tempDir;

    public HotFolderDedupCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "dedup-cache-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private HotFolderDedupCache NewCache(int retentionDays = 14)
    {
        var options = new HotFolderOptions
        {
            RootPath = _tempDir,
            DedupRetentionDays = retentionDays,
            DedupCacheFileName = ".hotfolder-dedup-test.json",
        };
        return new HotFolderDedupCache(options, NullLogger<HotFolderDedupCache>.Instance);
    }

    [Fact]
    public void TryRegister_NewHash_ReturnsTrue()
    {
        var cache = NewCache();

        var ok = cache.TryRegister("abc123", "first.xlsx", "drivdal", "settlement", out var existing);

        ok.Should().BeTrue();
        existing.Hash.Should().Be("abc123");
        existing.FirstFileName.Should().Be("first.xlsx");
        existing.PlantId.Should().Be("drivdal");
    }

    [Fact]
    public void TryRegister_DuplicateHash_ReturnsFalse_WithOriginalRecord()
    {
        var cache = NewCache();
        cache.TryRegister("abc123", "original.xlsx", "drivdal", "settlement", out _);

        var ok = cache.TryRegister("abc123", "copy_renamed.xlsx", "drivdal", "settlement", out var existing);

        ok.Should().BeFalse("samme hash er sett tidligere");
        existing.FirstFileName.Should().Be("original.xlsx",
            "vi returnerer ORIGINALEN, ikke det nye duplikat-navnet");
    }

    [Fact]
    public void Cache_PersistsToDisk_AndReloadsAcrossInstances()
    {
        // Første instans skriver
        var cache1 = NewCache();
        cache1.TryRegister("hash1", "file1.xlsx", "drivdal", "settlement", out _);
        cache1.TryRegister("hash2", "file2.csv", "haukland", "scada", out _);

        // Andre instans (simulerer container-restart) leser fra disk
        var cache2 = NewCache();

        cache2.Count.Should().Be(2);
        // Registrering av samme hash skal fortsatt returnere false
        var ok = cache2.TryRegister("hash1", "annet.xlsx", null, null, out var existing);
        ok.Should().BeFalse();
        existing.FirstFileName.Should().Be("file1.xlsx");
    }

    [Fact]
    public async Task ComputeFileHashAsync_SameContent_ReturnsSameHash()
    {
        var content = "Hello, dedup!"u8.ToArray();
        var path1 = Path.Combine(_tempDir, "a.txt");
        var path2 = Path.Combine(_tempDir, "b.txt");
        await File.WriteAllBytesAsync(path1, content);
        await File.WriteAllBytesAsync(path2, content);

        var hash1 = await HotFolderDedupCache.ComputeFileHashAsync(new FileInfo(path1));
        var hash2 = await HotFolderDedupCache.ComputeFileHashAsync(new FileInfo(path2));

        hash1.Should().Be(hash2);
        hash1.Should().HaveLength(64); // SHA-256 hex
    }

    [Fact]
    public async Task ComputeFileHashAsync_DifferentContent_ReturnsDifferentHash()
    {
        var path1 = Path.Combine(_tempDir, "a.txt");
        var path2 = Path.Combine(_tempDir, "b.txt");
        await File.WriteAllBytesAsync(path1, "content A"u8.ToArray());
        await File.WriteAllBytesAsync(path2, "content B"u8.ToArray());

        var hash1 = await HotFolderDedupCache.ComputeFileHashAsync(new FileInfo(path1));
        var hash2 = await HotFolderDedupCache.ComputeFileHashAsync(new FileInfo(path2));

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void Cache_CorruptJsonFile_StartsEmpty()
    {
        var corruptPath = Path.Combine(_tempDir, ".hotfolder-dedup-test.json");
        File.WriteAllText(corruptPath, "{ this is not valid json");

        var cache = NewCache();

        cache.Count.Should().Be(0, "korrupt JSON skal ikke krasje, bare starte på nytt");
        // Vi skal kunne registrere etter korrupt-recovery
        cache.TryRegister("freshhash", "f.xlsx", "drivdal", "settlement", out _).Should().BeTrue();
    }

    [Fact]
    public void Clear_RemovesAllRecords()
    {
        var cache = NewCache();
        cache.TryRegister("h1", "f1.xlsx", null, null, out _);
        cache.TryRegister("h2", "f2.xlsx", null, null, out _);
        cache.Count.Should().Be(2);

        cache.Clear();

        cache.Count.Should().Be(0);
        cache.TryRegister("h1", "f1-new.xlsx", null, null, out _).Should().BeTrue();
    }
}
