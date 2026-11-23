#!/usr/bin/env python3
"""MobMate subtitle window - transparent click-through GTK WebKit2 overlay."""
import json, os, sys, urllib.request

# Force X11 backend so set_decorated(False) and RGBA transparency work under XWayland.
os.environ.setdefault("GDK_BACKEND", "x11")
os.environ.setdefault("DISPLAY", ":0")

import gi
gi.require_version("Gdk", "3.0")
gi.require_version("GLib", "2.0")
gi.require_version("Gtk", "3.0")
gi.require_version("WebKit2", "4.1")
import cairo
from gi.repository import Gdk, GLib, Gtk, WebKit2

# GTK's "draw" signal passes a foreign cairo.Context that PyGObject can only convert
# when the gi cairo foreign bridge (python3-gi-cairo) is installed. Detect it up front:
# when absent (seen on some Ubuntu installs) fall back to CSS transparency instead of
# cairo draw callbacks, otherwise the app-paintable window stays unpainted (invisible).
try:
    # Check the Context converter specifically; the plain cairo typelib can be present
    # (RectangleInt/Region work) while the Context bridge from python3-gi-cairo is absent.
    gi.require_foreign("cairo", "Context")
    HAVE_CAIRO_FOREIGN = True
except Exception:
    HAVE_CAIRO_FOREIGN = False

_base  = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:5000/subtitle.html"
url    = _base + ("&" if "?" in _base else "?") + "window=1"
HANDLE_H   = 18
MIN_HEIGHT = HANDLE_H + 80   # enough to show at least one caption card
POS_FILE   = os.path.expanduser("~/.config/MobMate/subtitle_position.json")
API_BASE   = "http://127.0.0.1:5000"

# Detect screen size early (needs a temporary Gdk init)
_tmp_screen = Gdk.Screen.get_default()
SCREEN_W = _tmp_screen.get_width()  if _tmp_screen else 1920
SCREEN_H = _tmp_screen.get_height() if _tmp_screen else 1080

# Default width: Math.Min(screen_w, 1600) — same as Windows SubtitleOverlayWindow
width  = int(sys.argv[2]) if len(sys.argv) > 2 else min(SCREEN_W, 1600)
height = int(sys.argv[3]) if len(sys.argv) > 3 else 220

# ---- Position persistence ----
def load_position():
    try:
        with open(POS_FILE) as f:
            return json.load(f)
    except Exception:
        return {}

_save_tid = [0]
def save_position():
    try:
        x, y = win.get_position()
        w, h = win.get_size()
        os.makedirs(os.path.dirname(POS_FILE), exist_ok=True)
        with open(POS_FILE, "w") as f:
            json.dump({"x": x, "y": y, "w": w, "h": h}, f)
    except Exception:
        pass
    return False  # GLib.timeout_add callback: don't repeat

def on_configure(widget, event):
    if _save_tid[0]:
        GLib.source_remove(_save_tid[0])
    _save_tid[0] = GLib.timeout_add(800, save_position)

# ---- Stop hearing and close ----
def stop_and_close():
    try:
        req = urllib.request.Request(f"{API_BASE}/api/hearing/stop", data=b"", method="POST")
        urllib.request.urlopen(req, timeout=2)
    except Exception:
        pass
    win.destroy()

# ---- Window ----
win = Gtk.Window()
win.set_title("MobMate Subtitle")
win.set_keep_above(True)
win.set_decorated(False)
win.set_app_paintable(True)

# Restore saved position/size, else use defaults
pos = load_position()
init_w = pos.get("w", width)
init_h = max(pos.get("h", height), MIN_HEIGHT)
win.set_default_size(init_w, init_h)
if "x" in pos and "y" in pos:
    win.move(pos["x"], pos["y"])
else:
    # Default position: bottomLeft (Left=30, Top=sh-h-60) — Windows SubtitleOverlayWindow parity
    default_x = 30
    default_y = max(0, SCREEN_H - init_h - 60)
    win.move(default_x, default_y)

# Enable RGBA visual
screen = win.get_screen()
visual = screen.get_rgba_visual()
if visual and screen.is_composited():
    win.set_visual(visual)

# ---- Layout ----
vbox = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
vbox.set_app_paintable(True)

# Handle bar
handle = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL)
handle.set_size_request(-1, HANDLE_H)
handle.set_app_paintable(True)

drag_zone = Gtk.EventBox()
drag_zone.set_app_paintable(True)
drag_zone.connect(
    "button-press-event",
    lambda w, e: win.begin_move_drag(1, int(e.x_root), int(e.y_root), e.time) if e.button == 1 else None
)
def _paint_transparent(w, cr):
    # cairo.Context foreign converter is missing when python3-gi-cairo is absent;
    # degrade gracefully (opaque handle) instead of spamming TypeError every frame.
    try:
        cr.set_source_rgba(0, 0, 0, 0)
        cr.paint()
    except Exception:
        pass
    return False
if HAVE_CAIRO_FOREIGN:
    drag_zone.connect("draw", _paint_transparent)

close_btn = Gtk.Button(label="✕")
close_btn.set_size_request(HANDLE_H + 6, HANDLE_H)
close_btn.set_relief(Gtk.ReliefStyle.NONE)
close_btn.connect("clicked", lambda _: stop_and_close())
css = Gtk.CssProvider()
css.load_from_data(b"""
button { background: rgba(40,40,40,0.7); color: #ccc; border-radius: 0; font-size: 10px; padding: 0; }
button:hover { background: rgba(180,40,40,0.85); color: #fff; }
""")
close_btn.get_style_context().add_provider(css, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)

handle.pack_start(drag_zone, True, True, 0)
handle.pack_end(close_btn, False, False, 0)

# WebView
webview = WebKit2.WebView()
transparent = Gdk.RGBA()
transparent.red = transparent.green = transparent.blue = transparent.alpha = 0.0
webview.set_background_color(transparent)
webview.load_uri(url)
webview.connect("close", lambda w: stop_and_close())

# Auto-resize: subtitle.html sets document.title = "resize:<px>" when cards change
def on_title_change(webview, _prop):
    title = webview.get_title() or ""
    if title.startswith("resize:"):
        try:
            content_h = int(title[7:])
            if content_h <= 0:
                return  # no cards: keep current size
            new_h = max(content_h + HANDLE_H, MIN_HEIGHT)
            cur_w, cur_h = win.get_size()
            if abs(new_h - cur_h) > 4:
                win.resize(cur_w, new_h)
        except Exception:
            pass

webview.connect("notify::title", on_title_change)

vbox.pack_start(handle, False, False, 0)
vbox.pack_start(webview, True, True, 0)
win.add(vbox)

# Input shape: only handle bar is interactive, rest is click-through
_input_shape_supported = [True]
def update_input_shape(widget, allocation):
    # cairo.Region foreign type is unavailable without python3-gi-cairo; in that case
    # skip click-through shaping (whole window stays interactive) rather than crash-spam.
    if not _input_shape_supported[0]:
        return
    try:
        rect = cairo.RectangleInt(0, 0, allocation.width, HANDLE_H)
        region = cairo.Region(rect)
        win.input_shape_combine_region(region)
    except Exception:
        _input_shape_supported[0] = False

win.connect("size-allocate", update_input_shape)
win.connect("configure-event", on_configure)
win.connect("destroy", Gtk.main_quit)

if not HAVE_CAIRO_FOREIGN:
    # Without the cairo foreign bridge the transparent draw callbacks are no-ops, leaving
    # an app-paintable window with nothing painted (invisible/black). Disable app-painting
    # and paint transparency via CSS so the overlay still renders.
    for _w in (win, vbox, handle, drag_zone):
        _w.set_app_paintable(False)
    _bg_css = Gtk.CssProvider()
    _bg_css.load_from_data(b"window, box, eventbox { background-color: transparent; }")
    Gtk.StyleContext.add_provider_for_screen(
        Gdk.Screen.get_default(), _bg_css, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)

win.show_all()

Gtk.main()
