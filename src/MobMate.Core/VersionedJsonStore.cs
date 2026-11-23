using System.Text.Json;
using System.Text.Json.Serialization;

namespace MobMate.Core;

public sealed record VersionedJsonMigration(
    int FromVersion,
    Func<JsonElement, JsonElement> Migrate);

public sealed record VersionedJsonMigrationResult(int FromVersion, int ToVersion);

public enum VersionedJsonFailSoftReason
{
    MissingFile,
    CorruptJson,
    FutureSchemaVersion,
    MissingMigration,
    MigrationFailed,
    IoError
}

public sealed record VersionedJsonSaveResult(int SchemaVersion, bool BackupCreated);

public sealed class VersionedJsonLoadResult<T>
{
    internal VersionedJsonLoadResult(
        T value,
        int schemaVersion,
        IReadOnlyList<VersionedJsonMigrationResult> migrations,
        VersionedJsonFailSoftReason? failSoftReason,
        bool corruptFileMoved)
    {
        Value = value;
        SchemaVersion = schemaVersion;
        Migrations = migrations;
        FailSoftReason = failSoftReason;
        CorruptFileMoved = corruptFileMoved;
    }

    public T Value { get; }

    public int SchemaVersion { get; }

    public IReadOnlyList<VersionedJsonMigrationResult> Migrations { get; }

    public VersionedJsonFailSoftReason? FailSoftReason { get; }

    public bool CorruptFileMoved { get; }
}

public sealed class VersionedJsonStore<T>
{
    private readonly string _filePath;
    private readonly int _currentSchemaVersion;
    private readonly Func<T> _defaultFactory;
    private readonly IReadOnlyDictionary<int, VersionedJsonMigration> _migrations;
    private readonly JsonSerializerOptions _serializerOptions;

    public VersionedJsonStore(
        string filePath,
        int currentSchemaVersion,
        Func<T> defaultFactory,
        IEnumerable<VersionedJsonMigration>? migrations = null,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(defaultFactory);

        if (currentSchemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentSchemaVersion),
                "Schema version must be at least 1.");
        }

        _filePath = filePath;
        _currentSchemaVersion = currentSchemaVersion;
        _defaultFactory = defaultFactory;
        _serializerOptions = serializerOptions ?? new JsonSerializerOptions();
        _migrations = (migrations ?? [])
            .ToDictionary(migration => migration.FromVersion);
    }

    public async Task<VersionedJsonSaveResult> SaveAsync(
        T value,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_filePath))!;
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
        var backupCreated = false;

        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new StoreEnvelope<T>(_currentSchemaVersion, value),
                    _serializerOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(_filePath))
            {
                File.Copy(_filePath, GetBackupPath(), overwrite: true);
                backupCreated = true;
            }

            File.Move(tempPath, _filePath, overwrite: true);
            return new VersionedJsonSaveResult(_currentSchemaVersion, backupCreated);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public async Task<VersionedJsonLoadResult<T>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return CreateFallback(VersionedJsonFailSoftReason.MissingFile);
        }

        try
        {
            var json = await File.ReadAllBytesAsync(_filePath, cancellationToken);
            using var document = JsonDocument.Parse(json);

            var root = document.RootElement;
            var isLegacy = !root.TryGetProperty("schemaVersion", out var schemaVersionElement);
            var schemaVersion = isLegacy ? 0 : schemaVersionElement.GetInt32();
            var value = isLegacy ? root.Clone() : root.GetProperty("value").Clone();

            if (schemaVersion > _currentSchemaVersion)
            {
                return CreateCorruptFallback(VersionedJsonFailSoftReason.FutureSchemaVersion);
            }

            var migrationResults = new List<VersionedJsonMigrationResult>();
            while (schemaVersion < _currentSchemaVersion)
            {
                if (!_migrations.TryGetValue(schemaVersion, out var migration))
                {
                    return CreateFallback(VersionedJsonFailSoftReason.MissingMigration);
                }

                try
                {
                    value = migration.Migrate(value).Clone();
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    return CreateCorruptFallback(VersionedJsonFailSoftReason.MigrationFailed);
                }

                migrationResults.Add(new VersionedJsonMigrationResult(schemaVersion, schemaVersion + 1));
                schemaVersion++;
            }

            var deserialized = value.Deserialize<T>(_serializerOptions);
            if (deserialized is null)
            {
                return CreateCorruptFallback(VersionedJsonFailSoftReason.CorruptJson);
            }

            return new VersionedJsonLoadResult<T>(
                deserialized,
                schemaVersion,
                migrationResults,
                failSoftReason: null,
                corruptFileMoved: false);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return CreateCorruptFallback(VersionedJsonFailSoftReason.CorruptJson);
        }
        catch (IOException)
        {
            return CreateFallback(VersionedJsonFailSoftReason.IoError);
        }
        catch (UnauthorizedAccessException)
        {
            return CreateFallback(VersionedJsonFailSoftReason.IoError);
        }
    }

    private VersionedJsonLoadResult<T> CreateCorruptFallback(
        VersionedJsonFailSoftReason reason)
    {
        var moved = false;

        try
        {
            File.Move(_filePath, GetCorruptPath(), overwrite: true);
            moved = true;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return CreateFallback(reason, moved);
    }

    private VersionedJsonLoadResult<T> CreateFallback(
        VersionedJsonFailSoftReason reason,
        bool corruptFileMoved = false) =>
        new(
            _defaultFactory(),
            _currentSchemaVersion,
            Array.Empty<VersionedJsonMigrationResult>(),
            reason,
            corruptFileMoved);

    private string GetBackupPath() => $"{_filePath}.bak";

    private string GetCorruptPath() => $"{_filePath}.corrupt";

    private sealed record StoreEnvelope<TValue>(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("value")] TValue Value);
}
