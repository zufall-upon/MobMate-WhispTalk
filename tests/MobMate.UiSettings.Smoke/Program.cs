using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Playwright;

var options = Arguments.Parse(args);
var artifact = RestartArtifact.Load(options.ArtifactPath) ?? new RestartArtifact
{
    Platform = options.Platform,
    BrowserVerified = false,
    PortChange = new StageResult { Status = "not_run" },
    PackageUpdate = new StageResult { Status = "not_run" },
    Status = "running",
};

try
{
    artifact.Platform = options.Platform;
    artifact.BrowserVerified = false;
    var package = PackageManifest.Load(options.AppRoot);
    artifact.Commit = package.CommitSha;
    artifact.Status = "running";
    artifact.Error = null;
    artifact.PortChange ??= new StageResult { Status = "not_run" };
    artifact.PackageUpdate ??= new StageResult { Status = "not_run" };

    if (!File.Exists(options.BrowserExecutable))
    {
        artifact.PortChange = new StageResult { Status = "unverified" };
        artifact.PackageUpdate = new StageResult { Status = "unverified" };
        artifact.Status = "unverified";
        artifact.Error = $"Browser executable not found: {options.BrowserExecutable}";
        artifact.Save(options.ArtifactPath);
        Console.Error.WriteLine($"UNVERIFIED stage={options.Stage}: {artifact.Error}");
        return 1;
    }

    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    {
        ExecutablePath = options.BrowserExecutable,
        Headless = true,
    });
    var page = await browser.NewPageAsync();
    var (baseUrl, port) = await FindApplicationAsync(page, package.CommitSha, options.ExcludePort);
    artifact.BrowserVerified = true;

    switch (options.Stage)
    {
        case "write":
            artifact.SettingValue = $"restart-smoke-{Guid.NewGuid():N}";
            await WriteSettingAsync(page, baseUrl, artifact.SettingValue);
            await VerifySettingAsync(page, baseUrl, artifact.SettingValue);
            artifact.InitialPort = port;
            artifact.PortChange = new StageResult { Status = "pending", InitialPort = port };
            artifact.PackageUpdate = new StageResult { Status = "pending", PreviousCommit = package.CommitSha };
            break;
        case "port-change":
            RequirePriorWrite(artifact);
            await VerifySettingAsync(page, baseUrl, artifact.SettingValue!);
            if (port == artifact.InitialPort)
            {
                throw new InvalidOperationException($"Port did not change after restart: {port}");
            }
            artifact.PortChange = new StageResult
            {
                Status = "pass",
                InitialPort = artifact.InitialPort,
                RestartPort = port,
            };
            break;
        case "package-update":
            RequirePriorWrite(artifact);
            if (artifact.PortChange?.Status != "pass")
            {
                throw new InvalidOperationException("port_change stage has not passed");
            }
            await VerifySettingAsync(page, baseUrl, artifact.SettingValue!);
            artifact.PackageUpdate = new StageResult
            {
                Status = "pass",
                PreviousCommit = artifact.PackageUpdate?.PreviousCommit,
                CurrentCommit = package.CommitSha,
                RestartPort = port,
            };
            break;
        default:
            throw new InvalidOperationException($"Unknown stage: {options.Stage}");
    }

    artifact.Status = artifact.PortChange?.Status == "pass" && artifact.PackageUpdate?.Status == "pass"
        ? "pass"
        : "running";
    artifact.Save(options.ArtifactPath);
    Console.WriteLine($"PASS stage={options.Stage} platform={artifact.Platform} commit={artifact.Commit} port={port} status={artifact.Status}");
    return 0;
}
catch (Exception ex)
{
    artifact.Status = "failed";
    artifact.Error = ex.Message;
    artifact.Save(options.ArtifactPath);
    Console.Error.WriteLine($"FAIL stage={options.Stage}: {ex.Message}");
    return 1;
}

static void RequirePriorWrite(RestartArtifact artifact)
{
    if (string.IsNullOrWhiteSpace(artifact.SettingValue) || artifact.InitialPort is null)
    {
        throw new InvalidOperationException("write stage evidence is missing");
    }
}

static async Task<(string BaseUrl, int Port)> FindApplicationAsync(IPage page, string expectedCommit, int? excludePort)
{
    var deadline = DateTime.UtcNow.AddSeconds(60);
    while (DateTime.UtcNow < deadline)
    {
        for (var port = 5000; port < 5100; port++)
        {
            if (port == excludePort) continue;
            try
            {
                var url = $"http://127.0.0.1:{port}";
                var response = await page.GotoAsync($"{url}/api/release/status", new PageGotoOptions
                {
                    Timeout = 750,
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                });
                if (response?.Ok != true) continue;
                var json = await page.Locator("body").InnerTextAsync();
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("commitSha", out var commit) &&
                    string.Equals(commit.GetString(), expectedCommit, StringComparison.OrdinalIgnoreCase))
                {
                    return (url, port);
                }
            }
            catch (PlaywrightException)
            {
            }
        }
        await Task.Delay(250);
    }
    throw new InvalidOperationException($"Package host not found for commit {expectedCommit} on ports 5000-5099");
}

static async Task WriteSettingAsync(IPage page, string baseUrl, string value)
{
    await page.GotoAsync(baseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
    await page.EvaluateAsync("""
        async ({ key, value }) => {
          const current = await fetch('/api/ui-settings', { cache: 'no-store' });
          if (!current.ok) throw new Error(`GET /api/ui-settings HTTP ${current.status}`);
          const document = await current.json();
          document.settings = document.settings || {};
          document.settings[key] = value;
          document.browserMigrationCompleted = true;
          const saved = await fetch('/api/ui-settings', {
            method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(document)
          });
          if (!saved.ok) throw new Error(`PUT /api/ui-settings HTTP ${saved.status}: ${await saved.text()}`);
        }
        """, new { key = "mobmate.uiLanguage", value });
}

static async Task VerifySettingAsync(IPage page, string baseUrl, string expected)
{
    await page.GotoAsync(baseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
    var actual = await page.EvaluateAsync<string>("""
        async key => {
          const response = await fetch('/api/ui-settings', { cache: 'no-store' });
          if (!response.ok) throw new Error(`GET /api/ui-settings HTTP ${response.status}`);
          const document = await response.json();
          return document.settings?.[key] ?? null;
        }
        """, "mobmate.uiLanguage");
    if (!string.Equals(actual, expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Persistent UI setting mismatch: expected={expected} actual={actual ?? "<null>"}");
    }
}

sealed class Arguments
{
    public required string BrowserExecutable { get; init; }
    public required string AppRoot { get; init; }
    public required string ArtifactPath { get; init; }
    public required string Platform { get; init; }
    public required string Stage { get; init; }
    public int? ExcludePort { get; init; }

    public static Arguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Arguments must be --name value pairs");
            values[args[i]] = args[i + 1];
        }
        string Required(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Required argument missing: {name}");
        return new Arguments
        {
            BrowserExecutable = Path.GetFullPath(Required("--browser-executable")),
            AppRoot = Path.GetFullPath(Required("--app-root")),
            ArtifactPath = Path.GetFullPath(Required("--artifact")),
            Platform = Required("--platform"),
            Stage = Required("--stage"),
            ExcludePort = values.TryGetValue("--exclude-port", out var excluded) ? int.Parse(excluded) : null,
        };
    }
}

sealed class PackageManifest
{
    [JsonPropertyName("commitSha")]
    public string CommitSha { get; init; } = "";

    public static PackageManifest Load(string appRoot)
    {
        if (!Directory.Exists(appRoot)) throw new DirectoryNotFoundException($"App root not found: {appRoot}");
        var paths = Directory.GetFiles(appRoot, "package-manifest.json", SearchOption.AllDirectories);
        if (paths.Length != 1) throw new InvalidOperationException($"Expected one package-manifest.json under {appRoot}, found {paths.Length}");
        var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(paths[0]))
            ?? throw new InvalidOperationException($"Invalid package manifest: {paths[0]}");
        if (string.IsNullOrWhiteSpace(manifest.CommitSha)) throw new InvalidOperationException("Package manifest commitSha is missing");
        return manifest;
    }
}

sealed class RestartArtifact
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
    [JsonPropertyName("commit")] public string Commit { get; set; } = "";
    [JsonPropertyName("browser_verified")] public bool BrowserVerified { get; set; }
    [JsonPropertyName("port_change")] public StageResult? PortChange { get; set; }
    [JsonPropertyName("package_update")] public StageResult? PackageUpdate { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "failed";
    [JsonPropertyName("initial_port")] public int? InitialPort { get; set; }
    [JsonPropertyName("setting_value")] public string? SettingValue { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }

    public static RestartArtifact? Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<RestartArtifact>(File.ReadAllText(path))
        : null;

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

sealed class StageResult
{
    [JsonPropertyName("status")] public string Status { get; set; } = "pending";
    [JsonPropertyName("initial_port")] public int? InitialPort { get; set; }
    [JsonPropertyName("restart_port")] public int? RestartPort { get; set; }
    [JsonPropertyName("previous_commit")] public string? PreviousCommit { get; set; }
    [JsonPropertyName("current_commit")] public string? CurrentCommit { get; set; }
}
