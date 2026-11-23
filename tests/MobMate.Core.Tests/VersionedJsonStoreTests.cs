using System.Text.Json;
using MobMate.Core;
using Xunit;

namespace MobMate.Core.Tests;

public sealed class VersionedJsonStoreTests : IDisposable
{
    private static readonly string TestRoot = Path.Combine(
        "temp",
        "versioned-json-store-tests");

    private readonly string _testDirectory = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoadRoundTripsCurrentSchema()
    {
        var path = GetPath();
        var store = CreateStore(path);

        var saveResult = await store.SaveAsync(new Settings("dark", 42));
        var loadResult = await store.LoadAsync();

        Assert.Equal(2, saveResult.SchemaVersion);
        Assert.False(saveResult.BackupCreated);
        Assert.Equal(new Settings("dark", 42), loadResult.Value);
        Assert.Equal(2, loadResult.SchemaVersion);
        Assert.Null(loadResult.FailSoftReason);
        Assert.Empty(loadResult.Migrations);
    }

    [Fact]
    public async Task SecondSaveAtomicallyReplacesCurrentAndKeepsPreviousBackup()
    {
        var path = GetPath();
        var store = CreateStore(path);
        await store.SaveAsync(new Settings("first", 1));

        var result = await store.SaveAsync(new Settings("second", 2));

        Assert.True(result.BackupCreated);
        var backupStore = CreateStore($"{path}.bak");
        var backup = await backupStore.LoadAsync();
        Assert.Equal(new Settings("first", 1), backup.Value);
        Assert.Equal(new Settings("second", 2), (await store.LoadAsync()).Value);
        Assert.Empty(Directory.EnumerateFiles(_testDirectory, ".settings.json.*.tmp"));
    }

    [Fact]
    public async Task LoadAppliesCallerSuppliedMigration()
    {
        var path = GetPath();
        Directory.CreateDirectory(_testDirectory);
        await File.WriteAllTextAsync(
            path,
            """{"schemaVersion":1,"value":{"theme":"legacy","volume":7}}""");
        var migration = new VersionedJsonMigration(
            1,
            value => JsonSerializer.SerializeToElement(new Settings(
                value.GetProperty("theme").GetString()!,
                value.GetProperty("volume").GetInt32() * 2)));
        var store = CreateStore(path, migration);

        var result = await store.LoadAsync();

        Assert.Equal(new Settings("legacy", 14), result.Value);
        Assert.Equal(2, result.SchemaVersion);
        Assert.Equal(new VersionedJsonMigrationResult(1, 2), Assert.Single(result.Migrations));
        Assert.Null(result.FailSoftReason);
    }

    [Fact]
    public async Task LegacyJsonWithoutVersionStartsMigrationChainAtSchemaZero()
    {
        var path = GetPath();
        Directory.CreateDirectory(_testDirectory);
        await File.WriteAllTextAsync(path, """{"theme":"legacy","volume":3}""");
        var migrations = new[]
        {
            new VersionedJsonMigration(
                0,
                value => JsonSerializer.SerializeToElement(new
                {
                    theme = value.GetProperty("theme").GetString(),
                    volume = value.GetProperty("volume").GetInt32() + 1
                })),
            new VersionedJsonMigration(
                1,
                value => JsonSerializer.SerializeToElement(new Settings(
                    value.GetProperty("theme").GetString()!,
                    value.GetProperty("volume").GetInt32() * 2)))
        };
        var store = CreateStore(path, migrations);

        var result = await store.LoadAsync();

        Assert.Equal(new Settings("legacy", 8), result.Value);
        Assert.Equal(
            [
                new VersionedJsonMigrationResult(0, 1),
                new VersionedJsonMigrationResult(1, 2)
            ],
            result.Migrations);
        Assert.Null(result.FailSoftReason);
    }

    [Fact]
    public async Task MigrationExceptionReturnsDefaultAndMovesFileAside()
    {
        var path = GetPath();
        Directory.CreateDirectory(_testDirectory);
        await File.WriteAllTextAsync(
            path,
            """{"schemaVersion":1,"value":{"theme":"legacy","volume":7}}""");
        var store = CreateStore(
            path,
            new VersionedJsonMigration(1, _ => throw new InvalidOperationException("failed")));

        var result = await store.LoadAsync();

        Assert.Equal(new Settings("default", 0), result.Value);
        Assert.Equal(VersionedJsonFailSoftReason.MigrationFailed, result.FailSoftReason);
        Assert.True(result.CorruptFileMoved);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists($"{path}.corrupt"));
    }

    [Fact]
    public async Task FutureSchemaReturnsDefaultAndMovesFileAside()
    {
        var path = GetPath();
        Directory.CreateDirectory(_testDirectory);
        await File.WriteAllTextAsync(
            path,
            """{"schemaVersion":3,"value":{"theme":"future","volume":9}}""");
        var store = CreateStore(path);

        var result = await store.LoadAsync();

        Assert.Equal(new Settings("default", 0), result.Value);
        Assert.Equal(VersionedJsonFailSoftReason.FutureSchemaVersion, result.FailSoftReason);
        Assert.True(result.CorruptFileMoved);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists($"{path}.corrupt"));
    }

    [Fact]
    public async Task CorruptJsonReturnsDefaultAndMovesFileAside()
    {
        var path = GetPath();
        Directory.CreateDirectory(_testDirectory);
        await File.WriteAllTextAsync(path, "{not-json");
        var store = CreateStore(path);

        var result = await store.LoadAsync();

        Assert.Equal(new Settings("default", 0), result.Value);
        Assert.Equal(VersionedJsonFailSoftReason.CorruptJson, result.FailSoftReason);
        Assert.True(result.CorruptFileMoved);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists($"{path}.corrupt"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }

        if (Directory.Exists(TestRoot) && !Directory.EnumerateFileSystemEntries(TestRoot).Any())
        {
            Directory.Delete(TestRoot);
        }
    }

    private string GetPath() => Path.Combine(_testDirectory, "settings.json");

    private static VersionedJsonStore<Settings> CreateStore(
        string path,
        params VersionedJsonMigration[] migrations) =>
        new(path, 2, () => new Settings("default", 0), migrations);

    public sealed record Settings(string Theme, int Volume);
}
