using System.Text.Json;

namespace MobMate.Core.Realtime;

public static class OnsetCapturePolicy
{
    // Compatibility name. Normal capture devices include software microphones;
    // device names do not establish physical routing or echo isolation.
    public static bool IsExplicitMicrophone(string devicesJson, string captureDeviceId)
        => IsExplicitInput(devicesJson, captureDeviceId);

    // Validate against the same capture enumeration used by normal Start. The
    // separate hearing/loopback path is not an input in this enumeration.
    public static bool IsExplicitInput(string devicesJson, string captureDeviceId)
        => ExplicitInputName(devicesJson, captureDeviceId).Length > 0;

    private static string ExplicitInputName(string devicesJson, string captureDeviceId)
    {
        if (string.IsNullOrWhiteSpace(captureDeviceId)) return "";
        try
        {
            using var document = JsonDocument.Parse(devicesJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array) return "";
            foreach (var device in devices.EnumerateArray())
            {
                if (!device.TryGetProperty("id", out var id) || id.GetString() != captureDeviceId ||
                    !device.TryGetProperty("name", out var name)) continue;
                return name.GetString()?.Trim().ToLowerInvariant() ?? "";
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        return "";
    }
}
