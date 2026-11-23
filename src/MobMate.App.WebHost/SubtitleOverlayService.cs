using System;
using System.Threading;
using System.Windows.Threading;

namespace MobMate.App.WebHost;

/// <summary>
/// WPF透明字幕オーバーレイの起動/停止を管理する static サービス。
/// STA スレッドを 1本維持し、SubtitleOverlayWindow を管理する。
/// </summary>
internal static class SubtitleOverlayService
{
    private static Thread?                 _thread;
    private static SubtitleOverlayWindow?  _window;
    private static bool                    _closing;
    private static readonly object         _lock = new();

    /// <summary>字幕ウィンドウを開く（すでに開いていれば前面に出す）</summary>
    public static void Open(HearingService hearing, string? positionSavePath = null)
    {
        while (true)
        {
            Thread? threadToWait = null;
            lock (_lock)
            {
                if (_closing)
                {
                    threadToWait = _thread;
                }
                else if (_thread?.IsAlive == true && _window is { } w)
                {
                    if (!w.Dispatcher.HasShutdownStarted && !w.Dispatcher.HasShutdownFinished)
                    {
                        try
                        {
                            w.Dispatcher.InvokeAsync(() =>
                            {
                                if (w.IsVisible)
                                {
                                    w.Activate();
                                    w.Topmost = true;
                                }
                            });
                            return;
                        }
                        catch
                        {
                            _closing = true;
                            threadToWait = _thread;
                        }
                    }
                    else
                    {
                        _closing = true;
                        threadToWait = _thread;
                    }
                }
                else
                {
                    StartWindowThread(hearing, positionSavePath);
                    return;
                }
            }

            if (threadToWait != null && threadToWait.IsAlive)
            {
                try { threadToWait.Join(3000); } catch { }
            }

            lock (_lock)
            {
                if (_thread == threadToWait)
                {
                    _thread = null;
                    _window = null;
                    _closing = false;
                }
            }
        }
    }

    private static void StartWindowThread(HearingService hearing, string? positionSavePath)
    {
        _closing = false;
        _thread = new Thread(() =>
        {
            SubtitleOverlayWindow? win = null;
            try
            {
                win = new SubtitleOverlayWindow(hearing, positionSavePath);
                lock (_lock) { _window = win; }
                win.Closed += (_, _) =>
                {
                    lock (_lock)
                    {
                        if (_window == win) _window = null;
                    }
                };
                win.Show();
                Dispatcher.Run();
            }
            finally
            {
                lock (_lock)
                {
                    if (_window == win) _window = null;
                    if (_thread == Thread.CurrentThread) _thread = null;
                    _closing = false;
                }
            }
        });
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Name         = "SubtitleOverlay";
        _thread.Start();
    }

    /// <summary>字幕ウィンドウを閉じる</summary>
    public static void Close()
    {
        Thread? thread;
        SubtitleOverlayWindow? w;
        lock (_lock)
        {
            thread = _thread;
            w = _window;
            if (thread != null || w != null) _closing = true;
        }

        try { w?.RequestClose(wait: true); } catch { }

        if (thread != null && thread.IsAlive)
        {
            try { thread.Join(3000); } catch { }
        }

        lock (_lock)
        {
            if (_thread == thread) _thread = null;
            if (_window == w) _window = null;
            _closing = false;
        }
    }
}
