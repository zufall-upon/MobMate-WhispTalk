using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MobMate.Core.Tts;

// Keep the normal tar path on all platforms. Only GNU tar's child exec failure
// is evidence that the external bzip2 could not be started.
internal static class LinuxBzip2Fallback
{
    internal readonly record struct TarResult(int ExitCode, string Error);

    internal static Task ExtractAsync(string archive, string destination, CancellationToken ct) =>
        ExtractAsync(archive, destination, ct, OperatingSystem.IsLinux(), RunTarAsync, Decompress);

    internal static async Task ExtractAsync(string archive, string destination, CancellationToken ct, bool linux,
        Func<string, string, bool, CancellationToken, Task<TarResult>> runTar,
        Action<string, Stream, CancellationToken> decompress)
    {
        var result = await runTar(archive, destination, true, ct);
        if (result.ExitCode == 0) return;
        if (!linux || !IsBzip2ExecFailure(result))
            throw new InvalidOperationException($"tar exited {result.ExitCode}: {result.Error}");

        ct.ThrowIfCancellationRequested();
        var tarPath = Path.Combine(Path.GetTempPath(), $"kokoro-{Guid.NewGuid():N}.tar");
        var created = false;
        try
        {
            using (var tar = new FileStream(tarPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                decompress(archive, tar, ct);
            }
            ct.ThrowIfCancellationRequested();
            result = await runTar(tarPath, destination, false, ct);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"tar exited {result.ExitCode}: {result.Error}");
        }
        finally
        {
            if (created) { try { File.Delete(tarPath); } catch { } }
        }
    }

    internal static bool IsBzip2ExecFailure(TarResult result) =>
        result.ExitCode == 2 && result.Error.StartsWith("tar (child): bzip2: Cannot exec: ", StringComparison.Ordinal);

    private static async Task<TarResult> RunTarAsync(string archive, string destination, bool compressed, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo("tar")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(compressed ? "-xjf" : "-xf");
        psi.ArgumentList.Add(archive);
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(destination);
        // GNU tar's diagnostic must be stable for the narrowly scoped test above.
        if (OperatingSystem.IsLinux()) psi.Environment["LC_ALL"] = "C";
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start tar process");
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            await stderr;
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("tar extraction timed out");
        }
        return new TarResult(process.ExitCode, await stderr);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BzStream
    {
        internal IntPtr NextIn;
        internal uint AvailableIn, TotalInLow, TotalInHigh;
        internal IntPtr NextOut;
        internal uint AvailableOut, TotalOutLow, TotalOutHigh;
        internal IntPtr State, Allocate, Free, Opaque;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Version();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Init(ref BzStream stream, int verbosity, int small);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Step(ref BzStream stream);

    private static void Decompress(string archive, Stream output, CancellationToken ct) =>
        Decompress(archive, output, ct, "libbz2.so.1.0");

    internal static bool IsCompatibleVersion(string? version) =>
        version != null && version.StartsWith("1.0.", StringComparison.Ordinal);

    internal static void Decompress(string archive, Stream output, CancellationToken ct, string libraryName)
    {
        ct.ThrowIfCancellationRequested();
        var library = NativeLibrary.Load(libraryName);
        try
        {
            T Get<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
            var version = Get<Version>("BZ2_bzlibVersion");
            var init = Get<Init>("BZ2_bzDecompressInit");
            var step = Get<Step>("BZ2_bzDecompress");
            var end = Get<Step>("BZ2_bzDecompressEnd");
            if (!IsCompatibleVersion(Marshal.PtrToStringAnsi(version())))
                throw new InvalidOperationException("Incompatible libbz2 version");

            using var input = File.OpenRead(archive);
            var inputBuffer = new byte[131072];
            var outputBuffer = new byte[131072];
            var inputPin = GCHandle.Alloc(inputBuffer, GCHandleType.Pinned);
            try
            {
                var outputPin = GCHandle.Alloc(outputBuffer, GCHandleType.Pinned);
                try
                {
                    int available = input.Read(inputBuffer, 0, inputBuffer.Length);
                    if (available == 0) throw new InvalidDataException("Empty bzip2 archive");
                    var stream = new BzStream { NextIn = inputPin.AddrOfPinnedObject(), AvailableIn = (uint)available };
                    do
                    {
                        ct.ThrowIfCancellationRequested();
                        int code = init(ref stream, 0, 0);
                        if (code != 0) throw new InvalidDataException($"libbz2 initialization failed: {code}");
                        try
                        {
                            do
                            {
                                ct.ThrowIfCancellationRequested();
                                if (stream.AvailableIn == 0)
                                {
                                    available = input.Read(inputBuffer, 0, inputBuffer.Length);
                                    if (available == 0) throw new InvalidDataException("Unexpected end of bzip2 archive");
                                    stream.NextIn = inputPin.AddrOfPinnedObject();
                                    stream.AvailableIn = (uint)available;
                                }
                                var before = stream.AvailableIn;
                                stream.NextOut = outputPin.AddrOfPinnedObject();
                                stream.AvailableOut = (uint)outputBuffer.Length;
                                code = step(ref stream);
                                if (code != 0 && code != 4) throw new InvalidDataException($"libbz2 decode failed: {code}");
                                int written = outputBuffer.Length - (int)stream.AvailableOut;
                                output.Write(outputBuffer, 0, written);
                                if (code == 0 && before == stream.AvailableIn && written == 0)
                                    throw new InvalidDataException("libbz2 decoder made no progress");
                            } while (code != 4);
                        }
                        finally { end(ref stream); }
                        if (stream.AvailableIn == 0)
                        {
                            available = input.Read(inputBuffer, 0, inputBuffer.Length);
                            stream.NextIn = inputPin.AddrOfPinnedObject();
                            stream.AvailableIn = (uint)available;
                        }
                    } while (stream.AvailableIn != 0); // bzip2 supports concatenated streams
                }
                finally { outputPin.Free(); }
            }
            finally { inputPin.Free(); }
        }
        finally { NativeLibrary.Free(library); }
    }
}
