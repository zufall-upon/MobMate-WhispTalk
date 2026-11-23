#if DEBUG && QWEN_PRIVATE_VULKAN
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MobMate.Core.Asr;

// Uses the actual private pin, sparse corrupt fixtures and a fake CPU worker.
// It never starts the real model or changes the retained artifacts.
internal static class QwenPrivateVulkanSmoke
{
    [DllImport("libc", SetLastError = true)] private static extern int link(string oldpath, string newpath);

    internal static async Task<int> Run(string artifactRoot, string output)
    {
        artifactRoot = Path.GetFullPath(artifactRoot);
        output = Path.GetFullPath(output);
        if (!output.StartsWith(artifactRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Directory.Exists(output))
            throw new ArgumentException("A new output under the private artifact root is required");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        string? failure = null;
        var old = Environment.GetEnvironmentVariable(SpeechPrivateVulkanRuntime.RootVariable);
        var marker = Path.Combine(output, "cpu-args.txt");
        var oldMarker = Environment.GetEnvironmentVariable("QWEN_FAKE_ARGS");
        try
        {
            void Check(string name, bool value)
            {
                if (!value) throw new InvalidDataException(name);
                checks.Add(name);
            }
            var valid = await SpeechPrivateVulkanRuntime.VerifyAsync(artifactRoot, CancellationToken.None);
            Check("actual pinned archive and executable accepted", valid == Path.Combine(artifactRoot, "runtime", "speech"));
            var gpu = SpeechWorker.CreateStartInfo(valid, "/fixture/model.gguf", "gpu", linux: true);
            Check("private GPU argv requests gpu and four threads", gpu.ArgumentList.SequenceEqual(
                new[] { "worker", "/fixture/model.gguf", "--device", "gpu", "--threads", "4" }) &&
                gpu.Environment["SPEECH_NO_UPDATE_CHECK"] == "1");

            var script = Path.Combine(output, "fake-speech");
            var model = Path.Combine(output, "fake.gguf");
            await File.WriteAllTextAsync(script, "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$QWEN_FAKE_ARGS\"\nprintf '%s\\n' '{\"type\":\"ready\",\"protocol\":4,\"version\":\"0.10.1\"}'\nwhile IFS= read -r line; do :; done\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await File.WriteAllTextAsync(model, "fixture");
            var package = new PreparedAddonPackage(output, script, model);
            Environment.SetEnvironmentVariable("QWEN_FAKE_ARGS", marker);
            Environment.SetEnvironmentVariable(SpeechPrivateVulkanRuntime.RootVariable, null);
            await using (var worker = await SpeechWorker.StartAsync(package, AddonCatalog.QwenId, "ja", TimeSpan.FromSeconds(5)))
                Check("default CPU worker starts", worker.Initialized);
            Check("actual CPU startup argv remains cpu and four threads", (await File.ReadAllLinesAsync(marker)).SequenceEqual(
                new[] { "worker", model, "--device", "cpu", "--threads", "4" }));
            await File.WriteAllTextAsync(marker, "must not start again");

            var badArchive = MakeFixture(output, "bad-archive", artifactRoot, archive: false);
            var badExecutable = MakeFixture(output, "bad-executable", artifactRoot, archive: true);
            foreach (var bad in new[] { badArchive, badExecutable })
            {
                Environment.SetEnvironmentVariable(SpeechPrivateVulkanRuntime.RootVariable, bad);
                var rejected = false;
                try { await SpeechWorker.StartAsync(package, AddonCatalog.QwenId, "ja", TimeSpan.FromSeconds(5)); }
                catch (InvalidDataException) { rejected = true; }
                Check(Path.GetFileName(bad) + " same-size corruption rejected without CPU fallback", rejected &&
                    await File.ReadAllTextAsync(marker) == "must not start again");
            }
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            Environment.SetEnvironmentVariable(SpeechPrivateVulkanRuntime.RootVariable, old);
            Environment.SetEnvironmentVariable("QWEN_FAKE_ARGS", oldMarker);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { checks, failure },
            new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;
    }

    private static string MakeFixture(string output, string name, string actual, bool archive)
    {
        var root = Path.Combine(output, name);
        var runtime = Path.Combine(root, "runtime");
        Directory.CreateDirectory(runtime);
        const string zip = "speech-0.10.1-linux-x64-vulkan.zip";
        var targetZip = Path.Combine(root, zip);
        var targetExe = Path.Combine(runtime, "speech");
        if (archive)
        {
            if (link(Path.Combine(actual, zip), targetZip) != 0) throw new IOException("hardlink archive failed");
            Sparse(targetExe, 51756176);
            File.SetUnixFileMode(targetExe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else
        {
            Sparse(targetZip, 32437928);
            if (link(Path.Combine(actual, "runtime", "speech"), targetExe) != 0) throw new IOException("hardlink executable failed");
        }
        return root;
    }

    private static void Sparse(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
        stream.Position = length / 2;
        stream.WriteByte(1);
    }
}
#endif
