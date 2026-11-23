using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

// ============================================================
//  VrchatOscConfig
// ============================================================

public sealed class VrchatOscConfig
{
    public bool   Enabled               { get; set; } = false;
    public string Host                  { get; set; } = "127.0.0.1";
    public int    Port                  { get; set; } = 9000;
    public bool   PlayNotificationSound { get; set; } = false;
    public bool   SendTranslated        { get; set; } = false;
    public int    MinIntervalMs         { get; set; } = 1500;
}

public sealed class VrchatOscTestRequest
{
    public string? Text { get; set; }
}

// ============================================================
//  OscMessageWriter (OSC 1.0 最小エンコーダ、外部ライブラリ不使用)
// ============================================================

internal static class OscMessageWriter
{
    /// <summary>
    /// /chatbox/input [s text, T/F sendImmediately, T/F playNotificationSound] を
    /// OSC 1.0 バイナリにエンコードする。
    /// - 文字列: UTF-8 + NUL 終端 + 4バイト境界まで 0x00 パディング
    /// - bool: type tag の 'T'/'F' のみで表現し、引数バイトは出力しない
    /// </summary>
    public static byte[] BuildChatboxInput(string text, bool sendImmediately, bool playNotificationSound)
    {
        using var ms = new MemoryStream();
        WritePaddedString(ms, "/chatbox/input");
        var typeTags = "," + "s"
            + (sendImmediately ? "T" : "F")
            + (playNotificationSound ? "T" : "F");
        WritePaddedString(ms, typeTags);
        WritePaddedString(ms, text);
        return ms.ToArray();
    }

    private static void WritePaddedString(MemoryStream ms, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        ms.Write(bytes, 0, bytes.Length);
        ms.WriteByte(0); // NUL 終端
        while (ms.Length % 4 != 0)
            ms.WriteByte(0); // 4バイト境界パディング
    }
}

// ============================================================
//  VrchatOscService
// ============================================================

/// <summary>
/// Realtime ASR の確定テキストを VRChat chatbox (UDP/OSC) へ送る。
/// - 送信失敗はログのみ（VRChat 未起動でもアプリは無傷）
/// - MinIntervalMs 未満の連投は最新メッセージで上書きして遅延送信
/// - 144 文字超は切り詰め
/// </summary>
public sealed class VrchatOscService : IDisposable
{
    private const int ChatboxMaxChars = 144;

    private readonly ILogger _logger;
    private readonly object  _lock = new();

    private VrchatOscConfig _config = new();
    private UdpClient? _udp;
    private string     _udpHost = "";
    private int        _udpPort;
    private long       _lastSentMs;
    private string?    _pendingText;
    private System.Threading.Timer? _pendingTimer;
    private int        _disposed;

    public VrchatOscService(ILogger logger)
    {
        _logger = logger;
    }

    public VrchatOscConfig GetConfig()
    {
        lock (_lock) return _config;
    }

    public void SetConfig(VrchatOscConfig config)
    {
        if (config.Port is < 1 or > 65535) config.Port = 9000;
        if (config.MinIntervalMs < 0) config.MinIntervalMs = 0;
        if (string.IsNullOrWhiteSpace(config.Host)) config.Host = "127.0.0.1";
        lock (_lock) _config = config;
    }

    /// <summary>
    /// テキストを chatbox へ送信する。前回送信から MinIntervalMs 未満の場合は
    /// pending を最新メッセージで上書きし、残り時間経過後に送信する。
    /// </summary>
    public void EnqueueText(string text)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (string.IsNullOrWhiteSpace(text)) return;

        lock (_lock)
        {
            var cfg = _config;
            if (!cfg.Enabled) return;

            var now  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var wait = _lastSentMs + cfg.MinIntervalMs - now;
            if (wait <= 0)
            {
                _pendingText = null;
                _pendingTimer?.Dispose();
                _pendingTimer = null;
                _lastSentMs = now;
                SendNowLocked(text, cfg);
                return;
            }

            // rate limit 中: 最新メッセージで上書き。タイマーは1本だけ維持。
            _pendingText = text;
            _pendingTimer ??= new System.Threading.Timer(
                _ => FlushPending(), null, (int)wait, Timeout.Infinite);
        }
    }

    /// <summary>
    /// UIの接続確認用。Enabled と rate limit を無視し、現在の host/port へ即時送信する。
    /// </summary>
    public bool SendTestText(string text)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        if (string.IsNullOrWhiteSpace(text)) return false;

        lock (_lock)
        {
            _pendingText = null;
            _pendingTimer?.Dispose();
            _pendingTimer = null;
            _lastSentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return SendNowLocked(text, _config, isTest: true);
        }
    }

    private void FlushPending()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_lock)
        {
            _pendingTimer?.Dispose();
            _pendingTimer = null;
            var text = _pendingText;
            _pendingText = null;
            var cfg = _config;
            if (text == null || !cfg.Enabled) return;
            _lastSentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            SendNowLocked(text, cfg);
        }
    }

    // 必ず _lock 保持中に呼ぶこと
    private bool SendNowLocked(string text, VrchatOscConfig cfg, bool isTest = false)
    {
        try
        {
            if (text.Length > ChatboxMaxChars)
                text = text.Substring(0, ChatboxMaxChars);

            if (_udp == null || _udpHost != cfg.Host || _udpPort != cfg.Port)
            {
                _udp?.Dispose();
                _udp = new UdpClient();
                _udp.Connect(cfg.Host, cfg.Port);
                _udpHost = cfg.Host;
                _udpPort = cfg.Port;
            }

            var payload = OscMessageWriter.BuildChatboxInput(text, true, cfg.PlayNotificationSound);
            _udp.Send(payload, payload.Length);
            if (isTest)
                _logger.LogInformation("[VrchatOsc] test sent {Bytes} bytes to {Host}:{Port}: [{Text}]",
                    payload.Length, cfg.Host, cfg.Port, text);
            else
                _logger.LogDebug("[VrchatOsc] sent {Bytes} bytes to {Host}:{Port}: [{Text}]",
                    payload.Length, cfg.Host, cfg.Port, text);
            return true;
        }
        catch (Exception ex)
        {
            // 送信失敗はログのみ (VRChat 未起動・ポート不達など)
            _logger.LogWarning(ex, "[VrchatOsc] send failed to {Host}:{Port}", cfg.Host, cfg.Port);
            try { _udp?.Dispose(); } catch { }
            _udp = null;
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_lock)
        {
            _pendingTimer?.Dispose();
            _pendingTimer = null;
            _pendingText = null;
            try { _udp?.Dispose(); } catch { }
            _udp = null;
        }
    }
}
