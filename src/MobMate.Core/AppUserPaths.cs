namespace MobMate.Core;

public static class AppUserPaths
{
    public const string CloudRootFolderName = "MobMateWhispTalk";
    public const string V2FolderName = "v2";

    public static string DocumentsRoot { get; } = ResolveDocumentsRoot();

    public static string CloudRoot { get; } =
        Path.Combine(DocumentsRoot, CloudRootFolderName);

    public static string V2Root { get; } =
        Path.Combine(CloudRoot, V2FolderName);

    public static string ConfigDir { get; } =
        Path.Combine(V2Root, "config");

    public static string ProfilesDir { get; } =
        Path.Combine(V2Root, "profiles");

    private static string ResolveDocumentsRoot()
    {
        var fallback = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var overridePath = Environment.GetEnvironmentVariable("MOBMATE_DOCUMENTS_ROOT")?.Trim();
        if (string.IsNullOrEmpty(overridePath) || !Path.IsPathFullyQualified(overridePath))
        {
            return fallback;
        }

        try
        {
            var fullPath = Path.GetFullPath(overridePath);
            Directory.CreateDirectory(fullPath);
            return fullPath;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return fallback;
        }
    }

    public static void EnsureV2Directories()
    {
        Directory.CreateDirectory(V2Root);
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(ProfilesDir);
    }
}
