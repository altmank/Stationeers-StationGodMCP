"""The key guard check, in the guest: a StationGod key pressed while the Stationpedia search has focus must do nothing.

Pure parts (log matching, verdict) are tested offline in test_keyguard_check.py; the key sending is ctypes SendInput
with scan codes (Unity reads them more reliably than virtual keys) and runs only in the guest's interactive session.

Keys come from the mod's defaults (GhostSettings.cs): MenuKey Ctrl+Shift+G, ModeKey Ctrl+G, VisibleKey Ctrl+H.
"""
import time

OPEN = "[Ghosts] menu open"
CLOSED = "[Ghosts] menu closed"

# (name, scan code) for the keys used; extended keys are not needed here.
SCAN = {"ctrl": 0x1D, "shift": 0x2A, "g": 0x22, "h": 0x23, "esc": 0x01, "f1": 0x3B,
        "a": 0x1E, "s": 0x1F, "d": 0x20, "t": 0x14, "e": 0x12}
MENU_KEY = ("ctrl", "shift", "g")
MODE_KEY = ("ctrl", "g")
VISIBLE_KEY = ("ctrl", "h")


def menu_lines(text):
    """Every ghost menu line of a BepInEx log, in order: [(index, 'open' or 'closed', raw line)]."""
    found = []
    for index, line in enumerate(text.splitlines()):
        if OPEN in line:
            found.append((index, "open", line.strip()))
        elif CLOSED in line:
            found.append((index, "closed", line.strip()))
    return found


def lines_after(text, mark):
    """The menu lines that came after the first `mark` (a line count from a previous read)."""
    return [row for row in menu_lines(text) if row[0] >= mark]


def verdict(control_open, control_closed, case_lines, foreground=None):
    """(failures, notes). The control must see open then closed, else keys do not reach the game (not a guard result);
    the case must see no 'open' line after F1."""
    failures, notes = [], []
    if not control_open or not control_closed:
        missing = [n for n, ok in (("menu open", control_open), ("menu closed", control_closed)) if not ok]
        failures.append("keys do not reach the game: the control saw no " + " and no ".join(missing))
        return failures, notes
    opened = [row for row in case_lines if row[1] == "open"]
    if opened:
        failures.append(f"a ghost key fired while the Stationpedia search had focus: {opened[0][2]}")
    else:
        notes.append("no 'menu open' after F1 with typing and the menu key pressed")
    if foreground is not None:
        notes.append(f"foreground window: {foreground!r}")
    return failures, notes


# ---- guest-only key sending (ctypes) ----

def _send(scan, up):
    import ctypes
    from ctypes import wintypes

    class KeyBd(ctypes.Structure):
        _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD), ("dwFlags", wintypes.DWORD),
                    ("time", wintypes.DWORD), ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong))]

    class Union(ctypes.Union):
        _fields_ = [("ki", KeyBd), ("pad", ctypes.c_byte * 32)]

    class Input(ctypes.Structure):
        _fields_ = [("type", wintypes.DWORD), ("u", Union)]

    flags = 0x0008 | (0x0002 if up else 0)  # KEYEVENTF_SCANCODE, KEYEVENTF_KEYUP
    item = Input(1, Union(ki=KeyBd(0, scan, flags, 0, None)))
    ctypes.windll.user32.SendInput(1, ctypes.byref(item), ctypes.sizeof(item))


def press(*names, hold_s=0.15):
    """Hold the keys together, then release in reverse."""
    for name in names:
        _send(SCAN[name], up=False)
        time.sleep(0.03)
    time.sleep(hold_s)
    for name in reversed(names):
        _send(SCAN[name], up=True)
        time.sleep(0.03)


def foreground_title():
    import ctypes
    user32 = ctypes.windll.user32
    handle = user32.GetForegroundWindow()
    buf = ctypes.create_unicode_buffer(256)
    user32.GetWindowTextW(handle, buf, 256)
    return buf.value


def read_text(path):
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            return f.read()
    except OSError:
        return ""


def wait_for(path, kind, mark, timeout_s):
    """True when a menu line of `kind` appears after line `mark` within timeout_s."""
    end = time.monotonic() + timeout_s
    while time.monotonic() < end:
        if any(row[1] == kind for row in lines_after(read_text(path), mark)):
            return True
        time.sleep(0.5)
    return False


def run(bep_log, log, say=print):
    """The whole check against the live guest game. Returns (failures, notes); evidence goes into `log`."""
    log["foreground_before"] = foreground_title()
    say(f"keyguard: foreground {log['foreground_before']!r}")
    # a. control
    mark = len(read_text(bep_log).splitlines())
    press(*MENU_KEY)
    got_open = wait_for(bep_log, "open", mark, 8.0)
    press(*MENU_KEY)
    got_closed = wait_for(bep_log, "closed", mark, 8.0)
    if got_open and not got_closed:  # the menu may hold the key (typing state): Escape closes it
        press("esc")
        got_closed = wait_for(bep_log, "closed", mark, 5.0)
    log["control"] = [row[2] for row in lines_after(read_text(bep_log), mark)]
    say(f"keyguard control: open {got_open}, closed {got_closed}")
    if not (got_open and got_closed):
        return verdict(got_open, got_closed, [])
    # b. the case: F1 opens the Stationpedia and focuses its search
    mark = len(read_text(bep_log).splitlines())
    press("f1")
    time.sleep(4.0)
    log["foreground_case"] = foreground_title()
    for name in ("a", "s", "d", "t", "e"):
        press(name, hold_s=0.05)
    press(*MENU_KEY)
    time.sleep(0.5)
    press(*MODE_KEY)
    time.sleep(0.5)
    press(*VISIBLE_KEY)
    time.sleep(3.0)
    case = lines_after(read_text(bep_log), mark)
    log["case"] = [row[2] for row in case]
    press("esc")
    time.sleep(1.0)
    failures, notes = verdict(True, True, case, log["foreground_case"])
    return failures, notes
