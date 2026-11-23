using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
// WinForms と WPF が共存するため、曖昧な型はエイリアスで明示する
using WpfColor     = System.Windows.Media.Color;
using WpfColors    = System.Windows.Media.Colors;
using WpfBrush     = System.Windows.Media.SolidColorBrush;
using WpfFont      = System.Windows.Media.FontFamily;
using WpfTranslate = System.Windows.Media.TranslateTransform;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfVerticalAlignment = System.Windows.VerticalAlignment;

namespace MobMate.App.WebHost;

/// <summary>
/// WPF 透明オーバーレイ字幕ウィンドウ。
/// V1 Java HearingOverlayWindow / CaptionCardsView の C# 相当。
/// STA スレッド上で SubtitleOverlayService から生成される。
/// </summary>
public partial class SubtitleOverlayWindow : Window
{
    // ---- 設定 (インメモリ, デフォルト値) ----
    private int    _maxCards    = 6;
    private double _srcFontSize = 36;
    private double _trFontSize  = 23;
    private int    _doneKeepSeconds = 12;
    private string _overlayPosition = "bottomLeft";
    private WpfColor _cardBgColor = WpfColor.FromArgb(180, 0, 0, 0);

    // ---- 状態 ----
    private readonly HearingService  _hearing;
    private readonly DispatcherTimer _timer;
    private readonly List<CardEntry> _cards = new();
    private long _lastUpdateMs = 0;
    private readonly string? _positionSavePath;

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    // ---- コンストラクタ ----
    public SubtitleOverlayWindow(HearingService hearing, string? positionSavePath = null)
    {
        InitializeComponent();
        _hearing           = hearing;
        _positionSavePath  = positionSavePath;

        // 保存済み位置があれば復元、なければ画面下端にデフォルト配置
        var restored = TryRestorePosition();
        ApplyOverlayConfig(reposition: !restored);
        if (!restored && _overlayPosition == "custom")
        {
            var sw = SystemParameters.PrimaryScreenWidth;
            var sh = SystemParameters.PrimaryScreenHeight;
            Width  = Math.Min(sw, 1600);
            Left   = (sw - Width) / 2;
            Top    = sh - Height - 60;
        }

        // ポーリングタイマー (設定即時反映のため短め)
        _timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private sealed record CardEntry(UIElement Element, long CreatedMs);

    private void ApplyOverlayConfig(bool reposition = false)
    {
        var cfg = _hearing.GetConfig();
        _trFontSize = Math.Clamp(cfg.OverlayFontSize, 12, 72);
        _srcFontSize = Math.Max(10, Math.Round(_trFontSize * 0.64));
        _maxCards = Math.Clamp(cfg.OverlayHistory, 1, 12);
        _doneKeepSeconds = Math.Clamp(cfg.OverlayDoneKeepSeconds, 1, 60);
        _cardBgColor = BuildCardColor(cfg.OverlayBackground, cfg.OverlayOpacity);

        CardsPanel.Orientation = cfg.OverlayFlow.StartsWith("vertical", StringComparison.OrdinalIgnoreCase)
            ? WpfOrientation.Vertical : WpfOrientation.Horizontal;
        CardsPanel.HorizontalAlignment = cfg.OverlayFlow == "horizontalLeft" ? WpfHorizontalAlignment.Right : WpfHorizontalAlignment.Left;
        CardsPanel.VerticalAlignment = cfg.OverlayFlow == "verticalDown" ? WpfVerticalAlignment.Top : WpfVerticalAlignment.Bottom;

        if (reposition || cfg.OverlayPosition != _overlayPosition)
        {
            _overlayPosition = cfg.OverlayPosition;
            if (_overlayPosition != "custom") ApplyPresetPosition(_overlayPosition);
        }
    }

    private static WpfColor BuildCardColor(string bg, int opacity)
    {
        byte a = (byte)Math.Clamp((int)Math.Round(Math.Clamp(opacity, 0, 100) * 255.0 / 100.0), 0, 255);
        return bg switch
        {
            "green" => WpfColor.FromArgb(a, 0x10, 0x45, 0x35),
            "blue"  => WpfColor.FromArgb(a, 0x09, 0x24, 0x46),
            "gray"  => WpfColor.FromArgb(a, 0x22, 0x26, 0x28),
            "red"   => WpfColor.FromArgb(a, 0x45, 0x12, 0x18),
            _       => WpfColor.FromArgb(a, 0, 0, 0),
        };
    }

    private void ApplyPresetPosition(string position)
    {
        var sw = SystemParameters.PrimaryScreenWidth;
        var sh = SystemParameters.PrimaryScreenHeight;
        var currentWidth = double.IsNaN(Width) || Width <= 0 ? 1600 : Width;
        Width = Math.Min(sw, currentWidth);
        switch (position)
        {
            case "bottomRight":
                Left = sw - Width - 30;
                Top = sh - Height - 60;
                break;
            case "topCenter":
                Left = (sw - Width) / 2;
                Top = 60;
                break;
            default:
                Left = 30;
                Top = sh - Height - 60;
                break;
        }
    }

    // ---- 位置/サイズ 保存・復元 ----

    private void SavePosition()
    {
        if (_positionSavePath == null) return;
        try
        {
            var data = new { left = Left, top = Top, width = Width, height = Height };
            var json = JsonSerializer.Serialize(data);
            Directory.CreateDirectory(Path.GetDirectoryName(_positionSavePath)!);
            File.WriteAllText(_positionSavePath, json, System.Text.Encoding.UTF8);
        }
        catch { /* 保存失敗は無視 */ }
    }

    private bool TryRestorePosition()
    {
        if (_positionSavePath == null || !File.Exists(_positionSavePath)) return false;
        try
        {
            var json = File.ReadAllText(_positionSavePath, System.Text.Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Left   = root.GetProperty("left").GetDouble();
            Top    = root.GetProperty("top").GetDouble();
            Width  = root.GetProperty("width").GetDouble();
            Height = root.GetProperty("height").GetDouble();
            return true;
        }
        catch { return false; }
    }

    // ---- ポーリング ----
    private void OnTick(object? sender, EventArgs e)
    {
        ApplyOverlayConfig();
        RemoveExpiredCards();
        var st = _hearing.GetStatus();
        if (st.UpdatedAtMs <= _lastUpdateMs || string.IsNullOrWhiteSpace(st.SourceText))
            return;
        _lastUpdateMs = st.UpdatedAtMs;
        AddCard(st.SourceText, st.TranslatedText, st.SelectedEngine);
    }

    // ---- カード追加 ----
    private void AddCard(string src, string tr, string eng)
    {
        BringToFrontNoActivate();

        // 溢れた古いカードを削除
        while (_cards.Count >= _maxCards)
            RemoveOldestCard();

        var card = BuildCard(src, tr);
        _cards.Add(new CardEntry(card, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        CardsPanel.Children.Add(card);
        AnimateIn(card);

        // 最新以外を薄く (aged 効果)
        for (int i = 0; i < _cards.Count - 1; i++)
            _cards[i].Element.Opacity = 0.50;
    }

    // ---- 古いカードをフェードアウトして削除 ----
    private void RemoveOldestCard()
    {
        if (_cards.Count == 0) return;
        var oldest = _cards[0].Element;
        _cards.RemoveAt(0);

        var anim = new DoubleAnimation(0, TimeSpan.FromSeconds(0.40))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        anim.Completed += (_, _) => CardsPanel.Children.Remove(oldest);
        oldest.BeginAnimation(OpacityProperty, anim);
    }

    private void RemoveExpiredCards()
    {
        if (_cards.Count == 0) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long ttl = _doneKeepSeconds * 1000L;
        while (_cards.Count > 0 && now - _cards[0].CreatedMs > ttl)
            RemoveOldestCard();
    }

    // ---- カード Border 生成 ----
    private Border BuildCard(string src, string tr)
    {
        var stack = new StackPanel();

        if (!string.IsNullOrWhiteSpace(tr))
        {
            stack.Children.Add(new TextBlock
            {
                Text         = tr,
                FontSize     = _trFontSize,
                Foreground   = new WpfBrush(WpfColors.White),
                TextWrapping = TextWrapping.Wrap,
                FontFamily   = new WpfFont("Segoe UI,Yu Gothic UI,Meiryo UI,sans-serif"),
            });

            stack.Children.Add(new TextBlock
            {
                Text         = src,
                FontSize     = _srcFontSize,
                Foreground   = new WpfBrush(WpfColor.FromRgb(0x8E, 0xC8, 0xFF)),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 5, 0, 0),
                FontFamily   = new WpfFont("Segoe UI,Yu Gothic UI,Meiryo UI,sans-serif"),
            });
        }
        else
        {
            stack.Children.Add(new TextBlock
            {
                Text         = src,
                FontSize     = _trFontSize,
                Foreground   = new WpfBrush(WpfColors.White),
                TextWrapping = TextWrapping.Wrap,
                FontFamily   = new WpfFont("Segoe UI,Yu Gothic UI,Meiryo UI,sans-serif"),
            });
        }

        // NOTE: AllowsTransparency=True 環境では DropShadowEffect が
        //       WPF ソフトウェアレンダリングを強制して CPU 使用率が急騰するため
        //       Effect は一切設定しない。カード背景の暗色でコントラストを確保。
        return new Border
        {
            Background      = new WpfBrush(_cardBgColor),
            BorderBrush     = new WpfBrush(WpfColor.FromArgb(22, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(9),
            Padding         = new Thickness(14, 10, 14, 11),
            Margin          = new Thickness(0, 0, 10, 0),
            MaxWidth        = 420,
            Child           = stack,
            Opacity         = 0,
            RenderTransform = new WpfTranslate(0, 14),
        };
    }

    // ---- フェードイン + スライドアップ ----
    private static void AnimateIn(UIElement card)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur  = TimeSpan.FromSeconds(0.38);

        // Opacity 0 → 1
        card.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(dur)) { EasingFunction = ease });

        // TranslateTransform.Y 14 → 0
        if (card.RenderTransform is WpfTranslate tt)
            tt.BeginAnimation(WpfTranslate.YProperty,
                new DoubleAnimation(14, 0, new Duration(dur)) { EasingFunction = ease });
    }



    // ---- マウスドラッグで移動 ----
    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    // ---- 閉じるボタン ----
    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Close();
        Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        SavePosition();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        SavePosition();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        SavePosition();
        base.OnClosed(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        BringToFrontNoActivate();
    }

    private void BringToFrontNoActivate()
    {
        Topmost = true;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    // ---- 外部から閉じる (SubtitleOverlayService から) ----
    public void RequestClose(bool wait = false)
    {
        var op = Dispatcher.InvokeAsync(() =>
        {
            _timer.Stop();
            Close();
            Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        });

        if (wait)
        {
            try { op.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
    }
}
