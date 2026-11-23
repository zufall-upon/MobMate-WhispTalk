#!/bin/sh
# MobMate Radio Chat desktop shortcut helper.
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
TRIGGER="$SCRIPT_DIR/radio-chat-trigger.sh"
DESKTOP_FILE="$HOME/.local/share/applications/mobmate-radio-chat.desktop"
GNOME_PATH="/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/mobmate-radio-chat/"
DEFAULT_BINDING="<Control><Shift>F4"

usage() {
    cat <<'EOF'
Usage: radio-chat-shortcut-helper.sh <command> [binding]

Commands:
  status                 Detect desktop and print recommended setup
  install-desktop        Create MobMate Radio Chat .desktop launcher
  install-kde [bind]     Register KDE global shortcut via kglobalshortcutsrc + .desktop
  install-gnome [bind]   Explicitly register GNOME custom shortcut
  uninstall-kde          Remove KDE kglobalshortcutsrc entry
  uninstall-gnome        Remove GNOME custom shortcut entry

Default binding: <Control><Shift>F4  (KDE: Ctrl+Shift+F4)

Notes:
  KDE/SteamOS: run install-kde to write kglobalshortcutsrc + .desktop.
    Shortcut is active after next KDE login (or immediately if KDE detects the file change).
  GNOME: install-gnome modifies user gsettings only when explicitly invoked.
EOF
}

need_trigger() {
    if [ ! -x "$TRIGGER" ]; then
        echo "ERROR: trigger script not executable: $TRIGGER" >&2
        exit 1
    fi
}

detect_desktop() {
    session="${XDG_SESSION_TYPE:-unknown}"
    desktop="${XDG_CURRENT_DESKTOP:-${DESKTOP_SESSION:-unknown}}"
    echo "session=$session"
    echo "desktop=$desktop"
    command -v gsettings >/dev/null 2>&1 && echo "gsettings=found" || echo "gsettings=missing"
    command -v qdbus >/dev/null 2>&1 && echo "qdbus=found" || echo "qdbus=missing"
    if command -v gdbus >/dev/null 2>&1 && gdbus introspect --session --dest org.freedesktop.portal.Desktop --object-path /org/freedesktop/portal/desktop >/dev/null 2>&1; then
        echo "portal=found"
    else
        echo "portal=missing"
    fi
}

install_desktop() {
    need_trigger
    mkdir -p "$(dirname -- "$DESKTOP_FILE")"
    cat > "$DESKTOP_FILE" <<EOF
[Desktop Entry]
Type=Application
Name=MobMate Radio Chat
Comment=Show MobMate Radio Chat overlay
Exec=$TRIGGER toggle
Terminal=false
Categories=Utility;
EOF
    chmod 0644 "$DESKTOP_FILE"
    echo "desktop-file=$DESKTOP_FILE"
    echo "command=$TRIGGER toggle"
}

# Convert GNOME/XDG binding format (<Control><Shift>F4) to KDE Qt format (Ctrl+Shift+F4)
to_kde_binding() {
    printf '%s' "$1" | sed \
        -e 's/<[Cc]ontrol>/Ctrl+/g' \
        -e 's/<[Ss]hift>/Shift+/g' \
        -e 's/<[Aa]lt>/Alt+/g' \
        -e 's/<[Mm]eta>/Meta+/g' \
        -e 's/<[Ss]uper>/Meta+/g' \
        -e 's/>//g' \
        -e 's/<//g'
}

# kglobalshortcutsrc desktop stem for our .desktop file
KDE_DESKTOP_STEM="mobmate-radio-chat.desktop"

install_kde() {
    need_trigger
    command -v kwriteconfig6 >/dev/null 2>&1 || command -v kwriteconfig5 >/dev/null 2>&1 || {
        echo "ERROR: kwriteconfig6/kwriteconfig5 not found" >&2
        exit 1
    }
    kwrite="$(command -v kwriteconfig6 2>/dev/null || command -v kwriteconfig5)"
    raw_binding="${1:-$DEFAULT_BINDING}"
    kde_binding="$(to_kde_binding "$raw_binding")"

    install_desktop >/dev/null
    "$kwrite" --file kglobalshortcutsrc \
        --group "services" --group "$KDE_DESKTOP_STEM" \
        --key "_k_friendly_name" "MobMate Radio Chat"
    "$kwrite" --file kglobalshortcutsrc \
        --group "services" --group "$KDE_DESKTOP_STEM" \
        --key "_launch" "$kde_binding,none,MobMate Radio Chat"

    # Rebuild KService cache so kglobalacceld can resolve the .desktop as a
    # component on next login (required for the _launch global shortcut to grab the key).
    if command -v kbuildsycoca6 >/dev/null 2>&1; then
        kbuildsycoca6 >/dev/null 2>&1 || true
    elif command -v kbuildsycoca5 >/dev/null 2>&1; then
        kbuildsycoca5 >/dev/null 2>&1 || true
    fi

    echo "kde-shortcut=$kde_binding"
    echo "desktop-file=$DESKTOP_FILE"
    echo "command=$TRIGGER toggle"
    echo "Note: LOG OUT and back in to activate the KDE shortcut (kglobalacceld registers it at login)."
    echo "Fallback: if it still does not work, open System Settings > Shortcuts and assign a key to 'MobMate Radio Chat'."
}

uninstall_kde() {
    command -v kwriteconfig6 >/dev/null 2>&1 || command -v kwriteconfig5 >/dev/null 2>&1 || {
        echo "ERROR: kwriteconfig6/kwriteconfig5 not found" >&2
        exit 1
    }
    kwrite="$(command -v kwriteconfig6 2>/dev/null || command -v kwriteconfig5)"
    "$kwrite" --file kglobalshortcutsrc \
        --group "services" --group "$KDE_DESKTOP_STEM" \
        --key "_k_friendly_name" --delete 2>/dev/null || true
    "$kwrite" --file kglobalshortcutsrc \
        --group "services" --group "$KDE_DESKTOP_STEM" \
        --key "_launch" --delete 2>/dev/null || true
    echo "removed=services/$KDE_DESKTOP_STEM"
}

gnome_list_has_path() {
    gsettings get org.gnome.settings-daemon.plugins.media-keys custom-keybindings | grep -F "'$GNOME_PATH'" >/dev/null 2>&1
}

install_gnome() {
    need_trigger
    command -v gsettings >/dev/null 2>&1 || { echo "ERROR: gsettings not found" >&2; exit 1; }
    binding="${1:-$DEFAULT_BINDING}"
    install_desktop >/dev/null
    current=$(gsettings get org.gnome.settings-daemon.plugins.media-keys custom-keybindings)
    if ! gnome_list_has_path; then
        if [ "$current" = "@as []" ] || [ "$current" = "[]" ]; then
            next="['$GNOME_PATH']"
        else
            next=$(printf "%s" "$current" | sed "s/]$/, '$GNOME_PATH']/")
        fi
        gsettings set org.gnome.settings-daemon.plugins.media-keys custom-keybindings "$next"
    fi
    base="org.gnome.settings-daemon.plugins.media-keys.custom-keybinding:$GNOME_PATH"
    gsettings set "$base" name "MobMate Radio Chat"
    gsettings set "$base" command "$TRIGGER toggle"
    gsettings set "$base" binding "$binding"
    echo "gnome-shortcut=$binding"
    echo "command=$TRIGGER toggle"
}

uninstall_gnome() {
    command -v gsettings >/dev/null 2>&1 || { echo "ERROR: gsettings not found" >&2; exit 1; }
    current=$(gsettings get org.gnome.settings-daemon.plugins.media-keys custom-keybindings)
    next=$(printf "%s" "$current" | sed "s#'$GNOME_PATH', ##g; s#, '$GNOME_PATH'##g; s#'$GNOME_PATH'##g")
    gsettings set org.gnome.settings-daemon.plugins.media-keys custom-keybindings "$next"
    echo "removed=$GNOME_PATH"
}

case "${1:-status}" in
    status)
        detect_desktop
        echo "trigger=$TRIGGER"
        desktop="${XDG_CURRENT_DESKTOP:-${DESKTOP_SESSION:-unknown}}"
        case "$desktop" in
            *KDE*|*plasma*|*Plasma*)
                echo "recommended: install-kde (writes kglobalshortcutsrc + .desktop; re-login may be needed)"
                ;;
            *GNOME*|*gnome*)
                echo "recommended: install-gnome [binding]"
                ;;
            *)
                echo "recommended: install-kde (KDE/SteamOS) or install-gnome (GNOME)"
                ;;
        esac
        ;;
    install-desktop)
        install_desktop
        ;;
    install-kde)
        install_kde "${2:-$DEFAULT_BINDING}"
        ;;
    uninstall-kde)
        uninstall_kde
        ;;
    install-gnome)
        install_gnome "${2:-$DEFAULT_BINDING}"
        ;;
    uninstall-gnome)
        uninstall_gnome
        ;;
    -h|--help|help)
        usage
        ;;
    *)
        usage >&2
        exit 2
        ;;
esac
