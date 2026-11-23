using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

internal sealed class RadioChatService : IDisposable
{
    private static readonly Regex SlotRegex = new("^p([0-2])\\.([1-9])$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HexColorRegex = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);
    private readonly object _lock = new();
    private readonly string _appRoot;
    private readonly string _configPath;
    private readonly string _defaultLang;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, string> _commands = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastCommandsMtimeUtc = DateTime.MinValue;
    private Thread? _previewThread;
    private PreviewForm? _previewForm;
    private System.Threading.Timer? _previewRefreshTimer;
    private System.Threading.Timer? _previewAutoHideTimer;
    private Func<PendingSpeechStatus?>? _pendingStatusProvider;
    private Func<int, int, Task<bool>>? _onSpeakSlotAsync;
    private KeyboardHookRunner? _keyboardHook;
    private int _currentPage = 0;
    private RadioChatConfig _config;

    public RadioChatService(string appRoot, string defaultLang, ILogger logger)
    {
        _appRoot = appRoot;
        _defaultLang = NormalizeLang(defaultLang);
        _logger = logger;
        _configPath = Path.Combine(appRoot, "radio_chat.json");
        _config = LoadOrDefaultConfig();
        EnsureCommandsFile();
        ReloadCommandsIfUpdated(force: true);
        _keyboardHook = new KeyboardHookRunner(this, _logger);
        _keyboardHook.Start();
        ReconfigureHotkey();
    }

    public RadioChatStatus GetStatus() => new()
    {
        Enabled = _config.Enabled,
        OverlayVisible = _previewForm != null && !_previewForm.IsDisposed,
        CommandCount = _commands.Count,
        CommandsPath = _config.CommandsPath,
        HotkeyRegistered = _keyboardHook?.IsRegistered == true
    };

    public RadioChatConfig GetConfig()
    {
        lock (_lock) return _config;
    }

    public void ApplyConfig(RadioChatConfig config)
    {
        lock (_lock)
        {
            _config = NormalizeConfig(config);
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath) ?? AppContext.BaseDirectory);
            File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, JsonOptions), new UTF8Encoding(false));
        }
        EnsureCommandsFile();
        ReloadCommandsIfUpdated(force: true);
        ReconfigureHotkey();
    }

    public void SetSpeakHandler(Func<int, int, Task<bool>> handler)
    {
        _onSpeakSlotAsync = handler;
    }

    public void SetPendingStatusProvider(Func<PendingSpeechStatus?> provider)
    {
        _pendingStatusProvider = provider;
    }

    public IReadOnlyDictionary<string, string> GetCommands()
    {
        ReloadCommandsIfUpdated(force: false);
        return new Dictionary<string, string>(_commands, StringComparer.OrdinalIgnoreCase);
    }

    public void UpsertCommand(int page, int digit, string text)
    {
        if (page < 0 || page > 2 || digit < 1 || digit > 9) return;
        var key = $"p{page}.{digit}";
        _commands[key] = text ?? string.Empty;
        SaveCommands();
    }

    public async Task<bool> SpeakSlotAsync(int page, int digit, Func<string, CancellationToken, Task> speakAsync, CancellationToken ct)
    {
        if (page < 0 || page > 2 || digit < 1 || digit > 9) return false;
        ReloadCommandsIfUpdated(force: false);
        var key = $"p{page}.{digit}";
        if (!_commands.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text)) return false;
        await speakAsync(text, ct);
        return true;
    }

    public void ReloadCommandsIfUpdated(bool force = false)
    {
        var path = ResolveCommandsPath();
        if (!File.Exists(path)) return;
        var mtime = File.GetLastWriteTimeUtc(path);
        if (!force && mtime <= _lastCommandsMtimeUtc) return;

        var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith("//") || line.StartsWith(';')) continue;
            var sep = line.IndexOf('=');
            if (sep <= 0) continue;
            var key = line[..sep].Trim();
            var value = line[(sep + 1)..].Trim();
            var m = SlotRegex.Match(key);
            if (!m.Success) continue;
            if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
                value = value[1..^1];
            next[key.ToLowerInvariant()] = value;
        }
        _commands.Clear();
        foreach (var kv in next) _commands[kv.Key] = kv.Value;
        _lastCommandsMtimeUtc = mtime;
    }

    public void ShowPreview(Func<PendingSpeechStatus?>? pendingStatusProvider = null)
    {
        _pendingStatusProvider = pendingStatusProvider;
        _previewAutoHideTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (_previewThread != null && _previewThread.IsAlive && _previewForm != null && !_previewForm.IsDisposed)
        {
            _previewForm.BeginInvoke((Action)(() => _previewForm.SetText(BuildPreviewText())));
            _previewForm.BeginInvoke((Action)(() => _previewForm.ApplyOverlay(_config.Overlay)));
            return;
        }

        var text = BuildPreviewText();
        _previewThread = new Thread(() =>
        {
            ApplicationConfiguration.Initialize();
            _previewForm = new PreviewForm(text, _config.Overlay);
            Application.Run(_previewForm);
        });
        _previewThread.SetApartmentState(ApartmentState.STA);
        _previewThread.IsBackground = true;
        _previewThread.Start();

        _previewRefreshTimer ??= new System.Threading.Timer(_ =>
        {
            try
            {
                if (_previewForm != null && !_previewForm.IsDisposed)
                    _previewForm.BeginInvoke((Action)(() => { _previewForm.ApplyOverlay(_config.Overlay); _previewForm.SetText(BuildPreviewText()); }));
            }
            catch { }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void ShowPreviewFor(TimeSpan duration, Func<PendingSpeechStatus?>? pendingStatusProvider = null)
    {
        ShowPreview(pendingStatusProvider);
        if (duration <= TimeSpan.Zero) return;
        _previewAutoHideTimer ??= new System.Threading.Timer(_ => HidePreview(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _previewAutoHideTimer.Change(duration, Timeout.InfiniteTimeSpan);
    }

    public void HidePreview()
    {
        try
        {
            _previewAutoHideTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _previewRefreshTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (_previewForm != null && !_previewForm.IsDisposed)
                _previewForm.BeginInvoke((Action)(() => _previewForm.Close()));
        }
        catch { }
    }

    public void Dispose()
    {
        HidePreview();
        _keyboardHook?.Dispose();
        _previewAutoHideTimer?.Dispose();
        _previewRefreshTimer?.Dispose();
    }

    private void ReconfigureHotkey()
    {
        _keyboardHook?.Configure(_config.Enabled, _config.Shortcut.Modifier, _config.Shortcut.Key);
    }

    private async Task OnDigitPressedAsync(int digit)
    {
        if (digit < 1 || digit > 9 || _onSpeakSlotAsync == null) return;
        var page = ResolveActivePage();
        try { await _onSpeakSlotAsync(page, digit); } catch { }
    }

    private void OnPageCycleRequested()
    {
        _currentPage = (_currentPage + 1) % 3;
        if (_previewForm != null && !_previewForm.IsDisposed)
            _previewForm.BeginInvoke((Action)(() => _previewForm.SetText(BuildPreviewText())));
    }

    private int ResolveActivePage()
    {
        var pending = _pendingStatusProvider?.Invoke();
        if (pending != null && string.Equals(pending.Mode, "wait", StringComparison.OrdinalIgnoreCase) && pending.PendingCount > 0)
            return -1;
        return _currentPage;
    }

    private string BuildPreviewText()
    {
        ReloadCommandsIfUpdated(force: false);
        var sb = new StringBuilder();
        var pending = _pendingStatusProvider?.Invoke();
        if (pending != null && string.Equals(pending.Mode, "wait", StringComparison.OrdinalIgnoreCase) && pending.PendingCount > 0)
        {
            sb.AppendLine($"Pending TTS ({pending.PendingCount})");
            sb.AppendLine($"Auto speak in {pending.RemainingSeconds}s");
            sb.AppendLine("----------------------------");
            foreach (var item in pending.Items.OrderBy(x => x.Slot).Where(x => x.Slot >= 1 && x.Slot <= 4))
                sb.AppendLine($"{item.Slot}: Speak  {item.Text}");
            sb.AppendLine("5: Cancel");
            return TrimPreviewLines(sb.ToString());
        }
        var page = Math.Max(0, Math.Min(2, _currentPage));
        sb.AppendLine($"Radio Chat  P{page}/2");
        sb.AppendLine($" {_config.Overlay.PageChangeKey} = Next Page");
        sb.AppendLine("----------------------------");
        for (int d = 1; d <= 9; d++)
        {
            var key = $"p{page}.{d}";
            _commands.TryGetValue(key, out var value);
            sb.AppendLine($"{d}: {(string.IsNullOrWhiteSpace(value) ? "—" : value.Trim())}");
        }
        return TrimPreviewLines(sb.ToString());
    }

    private string TrimPreviewLines(string text)
    {
        var max = Math.Max(4, 3 + _config.Overlay.MaxLines);
        var lines = text.Replace("\r\n", "\n").Split('\n').Where(x => x.Length > 0).Take(max).ToArray();
        return string.Join(Environment.NewLine, lines);
    }

    private void EnsureCommandsFile()
    {
        var target = ResolveCommandsPath();
        if (File.Exists(target)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? AppContext.BaseDirectory);
        var preset = Path.Combine(_appRoot, "libs", "preset", $"_radiocmd_{_defaultLang}.txt");
        if (!File.Exists(preset)) preset = Path.Combine(_appRoot, "libs", "preset", "_radiocmd_en.txt");
        if (File.Exists(preset)) File.Copy(preset, target, overwrite: false);
        else File.WriteAllText(target, "# Radio command presets\n", new UTF8Encoding(false));
    }

    private void SaveCommands()
    {
        var path = ResolveCommandsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        var lines = _commands.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Select(kv => $"{kv.Key} = \"{kv.Value.Replace("\"", "\\\"")}\"");
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        _lastCommandsMtimeUtc = File.GetLastWriteTimeUtc(path);
    }

    private string ResolveCommandsPath()
    {
        var rel = string.IsNullOrWhiteSpace(_config.CommandsPath) ? "_radiocmd.txt" : _config.CommandsPath.Trim();
        return Path.IsPathFullyQualified(rel) ? rel : Path.Combine(_appRoot, rel);
    }

    private RadioChatConfig LoadOrDefaultConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var parsed = JsonSerializer.Deserialize<RadioChatConfig>(File.ReadAllText(_configPath, Encoding.UTF8), JsonOptions);
                return NormalizeConfig(parsed ?? new RadioChatConfig());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RadioChat] config load失敗。defaultを使用します");
        }
        var cfg = NormalizeConfig(new RadioChatConfig());
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath) ?? AppContext.BaseDirectory);
        File.WriteAllText(_configPath, JsonSerializer.Serialize(cfg, JsonOptions), new UTF8Encoding(false));
        return cfg;
    }

    private static RadioChatConfig NormalizeConfig(RadioChatConfig src)
    {
        src.CommandsPath = string.IsNullOrWhiteSpace(src.CommandsPath) ? "_radiocmd.txt" : src.CommandsPath;
        src.Overlay ??= new RadioOverlayConfig();
        src.Overlay.Position = NormalizePosition(src.Overlay.Position);
        src.Overlay.FontSize = Math.Clamp(src.Overlay.FontSize, 10, 32);
        src.Overlay.Opacity = Math.Clamp(src.Overlay.Opacity, 0.2, 1.0);
        src.Overlay.Margin = Math.Clamp(src.Overlay.Margin, 0, 120);
        src.Overlay.MaxLines = Math.Clamp(src.Overlay.MaxLines, 4, 20);
        src.Overlay.Background = NormalizeColor(src.Overlay.Background, "#1D6F5A");
        src.Overlay.Foreground = NormalizeColor(src.Overlay.Foreground, "#FFFFFF");
        src.Overlay.PageChangeKey = string.IsNullOrWhiteSpace(src.Overlay.PageChangeKey) ? "Q" : src.Overlay.PageChangeKey.Trim().ToUpperInvariant();
        src.Shortcut.Display = string.IsNullOrWhiteSpace(src.Shortcut.Display) ? "Ctrl+Shift+F4" : src.Shortcut.Display;
        src.Shortcut.Modifier = string.IsNullOrWhiteSpace(src.Shortcut.Modifier) ? "ctrl+shift" : src.Shortcut.Modifier;
        src.Shortcut.Key = string.IsNullOrWhiteSpace(src.Shortcut.Key) ? "F4" : src.Shortcut.Key;
        return src;
    }

    private static string NormalizePosition(string? position)
    {
        var x = (position ?? "top_left").Trim().ToLowerInvariant().Replace('-', '_');
        return x is "top_left" or "top_right" or "bottom_left" or "bottom_right" ? x : "top_left";
    }

    private static string NormalizeColor(string? color, string fallback)
    {
        var x = (color ?? string.Empty).Trim();
        return HexColorRegex.IsMatch(x) ? x.ToUpperInvariant() : fallback;
    }

    private static string NormalizeLang(string lang)
    {
        var x = (lang ?? string.Empty).Trim().ToLowerInvariant();
        return x switch { "ja" or "en" or "ko" or "zh_cn" or "zh_tw" => x, _ => "en" };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed class PreviewForm : Form
    {
        private readonly Label _text;
        private RadioOverlayConfig _overlay;
        public PreviewForm(string preview, RadioOverlayConfig overlay)
        {
            _overlay = overlay;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12, 10, 12, 10);
            _text = new Label { AutoSize = true, Text = preview };
            Controls.Add(_text);
            ApplyOverlay(overlay);
        }
        public void SetText(string text) { _text.Text = text; FitAndMove(_overlay); }
        public void ApplyOverlay(RadioOverlayConfig overlay)
        {
            _overlay = overlay;
            BackColor = ParseColor(overlay.Background, System.Drawing.Color.FromArgb(0x1D, 0x6F, 0x5A));
            ForeColor = ParseColor(overlay.Foreground, System.Drawing.Color.White);
            Opacity = Math.Clamp(overlay.Opacity, 0.2, 1.0);
            _text.BackColor = BackColor;
            _text.ForeColor = ForeColor;
            _text.Font = new System.Drawing.Font("Meiryo UI", Math.Clamp(overlay.FontSize, 10, 32), System.Drawing.FontStyle.Regular);
            FitAndMove(overlay);
        }

        private void FitAndMove(RadioOverlayConfig overlay)
        {
            var cfg = overlay;
            PerformLayout();
            var screen = Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
            var margin = Math.Clamp(cfg.Margin, 0, 120);
            var x = screen.Left + margin;
            var y = screen.Top + margin;
            switch (NormalizePosition(cfg.Position))
            {
                case "top_right": x = screen.Right - Width - margin; break;
                case "bottom_left": y = screen.Bottom - Height - margin; break;
                case "bottom_right": x = screen.Right - Width - margin; y = screen.Bottom - Height - margin; break;
            }
            Location = new System.Drawing.Point(Math.Max(screen.Left, x), Math.Max(screen.Top, y));
        }

        private static System.Drawing.Color ParseColor(string hex, System.Drawing.Color fallback)
        {
            try { return System.Drawing.ColorTranslator.FromHtml(hex); } catch { return fallback; }
        }
    }

    private sealed class KeyboardHookRunner : IDisposable
    {
        private readonly RadioChatService _owner;
        private readonly ILogger _logger;
        private Thread? _thread;
        private uint _threadId;
        private nint _hook = IntPtr.Zero;
        private HookProc? _proc;
        private bool _enabled;
        private uint _modMask;
        private uint _vkKey;
        private bool _radioHeld;
        public bool IsRegistered => _hook != IntPtr.Zero;

        public KeyboardHookRunner(RadioChatService owner, ILogger logger)
        {
            _owner = owner;
            _logger = logger;
        }

        public void Start()
        {
            _thread = new Thread(ThreadMain) { IsBackground = true, Name = "RadioHook" };
            _thread.Start();
        }

        public void Configure(bool enabled, string modifier, string key)
        {
            _enabled = enabled;
            _modMask = ParseModifier(modifier);
            _vkKey = ParseKey(key);
        }

        private void ThreadMain()
        {
            _threadId = GetCurrentThreadId();
            _proc = HookCallback;
            var module = GetModuleHandle(null);
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, module, 0);
            if (_hook == IntPtr.Zero)
            {
                _logger.LogWarning("[RadioChat] keyboard hook registration failed: win32={Error}", Marshal.GetLastWin32Error());
                return;
            }
            _logger.LogInformation("[RadioChat] keyboard hook registered");
            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        private nint HookCallback(int nCode, nint wParam, nint lParam)
        {
            if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);
            if (!_enabled || _vkKey == 0) return CallNextHookEx(_hook, nCode, wParam, lParam);
            var evt = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var isDown = wParam == (nint)WM_KEYDOWN || wParam == (nint)WM_SYSKEYDOWN;
            var isUp = wParam == (nint)WM_KEYUP || wParam == (nint)WM_SYSKEYUP;

            if ((uint)evt.vkCode == _vkKey)
            {
                if (isDown && !_radioHeld && IsModifierMatch())
                {
                    _radioHeld = true;
                    _owner.ShowPreview(_owner._pendingStatusProvider);
                }
                else if (isUp && _radioHeld)
                {
                    _radioHeld = false;
                    _owner.HidePreview();
                }
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            if (_radioHeld && isDown)
            {
                var vk = (uint)evt.vkCode;
                if (vk >= 0x31 && vk <= 0x39) // 1..9
                {
                    var digit = (int)(vk - 0x30);
                    _ = _owner.OnDigitPressedAsync(digit);
                }
                else if (vk == ParseKey(_owner._config.Overlay.PageChangeKey))
                {
                    _owner.OnPageCycleRequested();
                }
            }

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private bool IsModifierMatch()
        {
            bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            bool alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            bool needShift = (_modMask & 0x1) != 0;
            bool needCtrl = (_modMask & 0x2) != 0;
            bool needAlt = (_modMask & 0x4) != 0;
            return shift == needShift && ctrl == needCtrl && alt == needAlt;
        }

        public void Dispose()
        {
            try
            {
                if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        private static uint ParseModifier(string s)
        {
            var x = (s ?? string.Empty).ToLowerInvariant();
            uint m = 0;
            if (x.Contains("shift")) m |= 0x1;
            if (x.Contains("ctrl")) m |= 0x2;
            if (x.Contains("alt")) m |= 0x4;
            return m;
        }

        private static uint ParseKey(string key)
        {
            var x = (key ?? string.Empty).Trim().ToUpperInvariant();
            if (x.Length == 1 && x[0] >= 'A' && x[0] <= 'Z') return (uint)x[0];
            if (x.Length == 1 && x[0] >= '0' && x[0] <= '9') return (uint)x[0];
            if (x.StartsWith("F") && int.TryParse(x[1..], out var fn) && fn >= 1 && fn <= 24) return (uint)(0x6F + fn);
            if (x == "SCROLLLOCK") return 0x91;
            if (x == "PAUSE") return 0x13;
            return 0x73; // F4
        }

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int WM_QUIT = 0x0012;
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;

        private delegate nint HookProc(int nCode, nint wParam, nint lParam);
        [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public nint dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public int pt_x; public int pt_y; }
        [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(nint hhk);
        [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] private static extern sbyte GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
        [DllImport("user32.dll")] private static extern bool TranslateMessage([In] ref MSG lpMsg);
        [DllImport("user32.dll")] private static extern nint DispatchMessage([In] ref MSG lpMsg);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? lpModuleName);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam);
    }
}

internal sealed class RadioChatConfig
{
    public bool Enabled { get; set; } = false;
    public string CommandsPath { get; set; } = "_radiocmd.txt";
    public RadioShortcut Shortcut { get; set; } = new();
    public RadioOverlayConfig Overlay { get; set; } = new();
}

internal sealed class RadioOverlayConfig
{
    public string Position { get; set; } = "top_left";
    public int FontSize { get; set; } = 16;
    public double Opacity { get; set; } = 0.78;
    public int Margin { get; set; } = 12;
    public int MaxLines { get; set; } = 12;
    public string Background { get; set; } = "#1D6F5A";
    public string Foreground { get; set; } = "#FFFFFF";
    public string PageChangeKey { get; set; } = "Q";
}

internal sealed class RadioShortcut
{
    public string Display { get; set; } = "Ctrl+Shift+F4";
    public string Modifier { get; set; } = "ctrl+shift";
    public string Key { get; set; } = "F4";
}

internal sealed class RadioChatStatus
{
    public bool Enabled { get; set; }
    public bool OverlayVisible { get; set; }
    public int CommandCount { get; set; }
    public string CommandsPath { get; set; } = "_radiocmd.txt";
    public bool HotkeyRegistered { get; set; }
}
