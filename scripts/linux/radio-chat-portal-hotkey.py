#!/usr/bin/env python3
"""Wayland global shortcut helper for MobMate Radio Chat.

Uses xdg-desktop-portal GlobalShortcuts (official Wayland path). The first
run may show a desktop permission/configuration dialog. After activation, the
helper invokes radio-chat-trigger.sh toggle and keeps listening on D-Bus.
"""
import os
import subprocess
import sys
import uuid

import gi
gi.require_version("Gio", "2.0")
gi.require_version("GLib", "2.0")
from gi.repository import Gio, GLib

BUS = "org.freedesktop.portal.Desktop"
PATH = "/org/freedesktop/portal/desktop"
IFACE = "org.freedesktop.portal.GlobalShortcuts"
REQ_IFACE = "org.freedesktop.portal.Request"
SHORTCUT_ID = "toggle_radio_chat"
DESCRIPTION = "Toggle MobMate Radio Chat"


def desired_trigger():
    env = os.environ.get("MOBMATE_RADIO_HOTKEY")
    if env:
        return env
    path = os.path.expanduser("~/.config/kglobalshortcutsrc")
    try:
        in_section = False
        with open(path, "r", encoding="utf-8") as f:
            for raw in f:
                line = raw.strip()
                if line.startswith("[") and line.endswith("]"):
                    in_section = line.lower() == "[token_mobmate_radio]"
                    continue
                if in_section and line.startswith(f"{SHORTCUT_ID}="):
                    trigger = line.split("=", 1)[1].split(",", 1)[0].strip()
                    if trigger:
                        return trigger
    except Exception:
        pass
    return "CTRL+SHIFT+F4"


DEFAULT_TRIGGER = desired_trigger()


def log(message):
    print(f"[MobMateRadioHotkey] {message}", flush=True)


def wait_response(bus, handle, timeout_ms=120000):
    loop = GLib.MainLoop()
    result = {}

    def on_response(_conn, _sender, _path, _iface, _member, params, _data):
        result["params"] = params
        loop.quit()

    sub = bus.signal_subscribe(BUS, REQ_IFACE, "Response", handle, None,
                               Gio.DBusSignalFlags.NONE, on_response, None)
    GLib.timeout_add(timeout_ms, lambda: (loop.quit(), False)[1])
    loop.run()
    bus.signal_unsubscribe(sub)
    return result.get("params")


def request_call(bus, method, params, timeout_ms=120000):
    handle = bus.call_sync(BUS, PATH, IFACE, method, params,
                           GLib.VariantType.new("(o)"),
                           Gio.DBusCallFlags.NONE, -1, None).unpack()[0]
    response = wait_response(bus, handle, timeout_ms)
    if response is None:
        raise RuntimeError(f"{method} timed out")
    code, results = response.unpack()
    log(f"{method} response={code} results={results}")
    if code != 0:
        raise RuntimeError(f"{method} denied or failed: response={code}")
    return results


def create_session(bus):
    token = "mobmate" + uuid.uuid4().hex[:8]
    params = GLib.Variant("(a{sv})", ({
        "handle_token": GLib.Variant("s", token),
        "session_handle_token": GLib.Variant("s", "mobmate_radio"),
    },))
    results = request_call(bus, "CreateSession", params, timeout_ms=30000)
    return results["session_handle"]


def list_shortcuts(bus, session_handle):
    token = "mobmate" + uuid.uuid4().hex[:8]
    params = GLib.Variant("(oa{sv})", (session_handle, {
        "handle_token": GLib.Variant("s", token),
    }))
    try:
        return request_call(bus, "ListShortcuts", params, timeout_ms=30000).get("shortcuts", [])
    except Exception as ex:
        log(f"ListShortcuts skipped: {ex}")
        return []


def bind_shortcut(bus, session_handle):
    token = "mobmate" + uuid.uuid4().hex[:8]
    shortcuts = [(SHORTCUT_ID, {
        "description": GLib.Variant("s", DESCRIPTION),
        "preferred_trigger": GLib.Variant("s", DEFAULT_TRIGGER),
    })]
    params = GLib.Variant("(oa(sa{sv})sa{sv})", (session_handle, shortcuts, "", {
        "handle_token": GLib.Variant("s", token),
    }))
    return request_call(bus, "BindShortcuts", params, timeout_ms=120000).get("shortcuts", [])


def qt_key_code(trigger):
    mods = 0
    key = None
    for part in (trigger or "").replace(" ", "").split("+"):
        lower = part.lower()
        if lower in ("ctrl", "control"):
            mods |= 0x04000000
        elif lower == "shift":
            mods |= 0x02000000
        elif lower == "alt":
            mods |= 0x08000000
        elif lower in ("meta", "super", "win"):
            mods |= 0x10000000
        elif lower.startswith("f") and lower[1:].isdigit():
            n = int(lower[1:])
            if 1 <= n <= 35:
                key = 0x01000030 + (n - 1)
        elif len(part) == 1:
            key = ord(part.upper())
    if key is None:
        raise RuntimeError(f"unsupported shortcut trigger: {trigger}")
    return mods | key


def set_kglobalaccel_shortcut(bus, trigger):
    key = qt_key_code(trigger)
    action = ["token_mobmate_radio", SHORTCUT_ID, DESCRIPTION]
    params = GLib.Variant("(asa(ai))", (action, [([key],)]))
    try:
        bus.call_sync("org.kde.kglobalaccel", "/kglobalaccel", "org.kde.KGlobalAccel",
                      "setForeignShortcutKeys", params, None, Gio.DBusCallFlags.NONE,
                      5000, None)
        log(f"KGlobalAccel shortcut set: {trigger} ({key})")
    except Exception as ex:
        # KDE may restart/disconnect kglobalacceld while applying the new key.
        # The write still takes effect; the next service restart re-lists it.
        if "NoReply" in str(ex) or "disconnected" in str(ex):
            log(f"KGlobalAccel shortcut set requested: {trigger} ({key})")
            return
        raise


def trigger_description(shortcuts):
    for shortcut_id, options in shortcuts or []:
        if shortcut_id != SHORTCUT_ID:
            continue
        value = options.get("trigger_description") if hasattr(options, "get") else None
        if value:
            return str(value)
    return ""


def main():
    mode = "run"
    args = sys.argv[1:]
    if args and args[0] in ("run", "configure"):
        mode = args.pop(0)
    trigger = args[0] if args else os.path.join(
        os.path.dirname(__file__), "radio-chat-trigger.sh")
    if not os.path.exists(trigger):
        raise FileNotFoundError(trigger)

    bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
    session_handle = create_session(bus)
    shortcuts = list_shortcuts(bus, session_handle)
    if mode == "configure":
        set_kglobalaccel_shortcut(bus, DEFAULT_TRIGGER)
        shortcuts = bind_shortcut(bus, session_handle)
        log(f"configured shortcuts={shortcuts}")
        return
    active_trigger = trigger_description(shortcuts)
    if not shortcuts or (active_trigger and active_trigger != DEFAULT_TRIGGER):
        if active_trigger and active_trigger != DEFAULT_TRIGGER:
            log(f"rebinding shortcut {active_trigger} -> {DEFAULT_TRIGGER}")
            set_kglobalaccel_shortcut(bus, DEFAULT_TRIGGER)
        shortcuts = bind_shortcut(bus, session_handle)
    log(f"active shortcuts={shortcuts}")

    def on_activated(_conn, _sender, _path, _iface, _member, params, _data):
        _session, shortcut_id, _timestamp, _options = params.unpack()
        log(f"Activated {shortcut_id}")
        if shortcut_id == SHORTCUT_ID:
            subprocess.Popen([trigger, "toggle"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    bus.signal_subscribe(BUS, IFACE, "Activated", PATH, None,
                         Gio.DBusSignalFlags.NONE, on_activated, None)
    log("listening")
    GLib.MainLoop().run()


if __name__ == "__main__":
    try:
        main()
    except Exception as ex:
        log(f"ERROR: {ex}")
        sys.exit(1)
