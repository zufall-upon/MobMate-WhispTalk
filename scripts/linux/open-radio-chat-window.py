#!/usr/bin/env python3
"""MobMate Radio Chat overlay window for Linux."""
import json, os, sys, urllib.request

os.environ.setdefault("GDK_BACKEND", "x11")
os.environ.setdefault("DISPLAY", ":0")

import gi
gi.require_version("Gdk", "3.0")
gi.require_version("GLib", "2.0")
gi.require_version("Gtk", "3.0")
from gi.repository import Gdk, GLib, Gtk

API = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:5000"
current_page = 0
commands = {}
config = {}
pending = {}

def api_json(path, data=None):
    try:
        body = None if data is None else json.dumps(data).encode("utf-8")
        req = urllib.request.Request(API + path, data=body, headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=2) as r:
            return json.loads(r.read().decode("utf-8"))
    except Exception:
        return {}

def normalize_pos(value):
    v = str(value or "top_left").lower().replace("-", "_")
    return v if v in ("top_left", "top_right", "bottom_left", "bottom_right") else "top_left"

def css_color(value, fallback):
    v = str(value or "")
    return v if len(v) == 7 and v.startswith("#") else fallback

win = Gtk.Window()
win.set_title("MobMate Radio Chat")
win.set_keep_above(True)
win.set_decorated(False)
win.set_resizable(False)
win.set_border_width(12)
box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
win.add(box)
labels = [Gtk.Label(xalign=0.0) for _ in range(14)]
for lab in labels:
    lab.set_selectable(False)
    box.pack_start(lab, False, False, 0)

css = Gtk.CssProvider()

def apply_style():
    ov = (config.get("overlay") or {})
    bg = css_color(ov.get("background"), "#1D6F5A")
    fg = css_color(ov.get("foreground"), "#FFFFFF")
    fs = max(10, min(32, int(ov.get("fontSize") or 16)))
    opacity = max(0.2, min(1.0, float(ov.get("opacity") or 0.78)))
    win.set_opacity(opacity)
    css.load_from_data(f"window {{ background: {bg}; }} label {{ color: {fg}; font: {fs}px sans-serif; }}".encode("utf-8"))
    Gtk.StyleContext.add_provider_for_screen(Gdk.Screen.get_default(), css, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)

def move_window():
    ov = (config.get("overlay") or {})
    margin = max(0, min(120, int(ov.get("margin") or 12)))
    screen = Gdk.Screen.get_default()
    sw = screen.get_width() if screen else 1920
    sh = screen.get_height() if screen else 1080
    win.show_all()
    while Gtk.events_pending(): Gtk.main_iteration_do(False)
    w, h = win.get_size()
    pos = normalize_pos(ov.get("position"))
    x = margin if "left" in pos else max(0, sw - w - margin)
    y = margin if "top" in pos else max(0, sh - h - margin)
    win.move(x, y)

def refresh_data():
    global commands, config, pending
    config = api_json("/api/radio/config") or config
    data = api_json("/api/radio/commands")
    commands = {str(c.get("key", "")).lower(): c.get("text", "") for c in data.get("commands", [])} if data else commands
    pending = api_json("/api/pending-speech/status") or pending
    render()
    return True

def render():
    apply_style()
    use_pending = str(pending.get("mode", "")).lower() == "wait" and int(pending.get("pendingCount") or 0) > 0
    lines = []
    if use_pending:
        lines += [f"Pending TTS ({pending.get('pendingCount', 0)})", f"Auto speak in {pending.get('remainingSeconds', 0)}s", "----------------------------"]
        for item in pending.get("items", [])[:4]:
            lines.append(f"{item.get('slot')}: Speak  {item.get('text', '')}")
        lines.append("5: Cancel")
    else:
        page_key = (config.get("overlay") or {}).get("pageChangeKey") or "Q"
        lines += [f"Radio Chat  P{current_page}/2", f" {page_key} = Next Page", "----------------------------"]
        for d in range(1, 10):
            text = (commands.get(f"p{current_page}.{d}") or "").strip() or "—"
            lines.append(f"{d}: {text}")
    max_lines = max(4, min(20, int((config.get("overlay") or {}).get("maxLines") or 12))) + 3
    for i, lab in enumerate(labels):
        lab.set_text(lines[i] if i < min(len(lines), max_lines) else "")
    move_window()

def speak_digit(digit):
    use_pending = str(pending.get("mode", "")).lower() == "wait" and int(pending.get("pendingCount") or 0) > 0
    page = -1 if use_pending else current_page
    api_json("/api/radio/speak", {"page": page, "digit": digit})
    GLib.timeout_add(200, refresh_data)

def hide_and_close():
    api_json("/api/radio/preview", {"show": False})
    win.destroy()

def on_key(_widget, event):
    global current_page
    key = Gdk.keyval_name(event.keyval) or ""
    if key == "Escape":
        hide_and_close(); return True
    if key in tuple(str(i) for i in range(1, 10)):
        speak_digit(int(key)); return True
    page_key = str((config.get("overlay") or {}).get("pageChangeKey") or "Q").upper()
    if key.upper() == page_key:
        current_page = (current_page + 1) % 3
        render(); return True
    return False

def poll_visible():
    status = api_json("/api/radio/status")
    if status and not status.get("overlayVisible", True):
        win.destroy(); return False
    return True

win.connect("key-press-event", on_key)
win.connect("destroy", Gtk.main_quit)
refresh_data()
win.show_all()
win.present()
GLib.timeout_add(500, poll_visible)
GLib.timeout_add(1000, refresh_data)
Gtk.main()
