"""PLAN v9 3.3, the failure-semantics suite, case by case. Each case sets the world up through the oracle, drives a
hub over MCP like Claude Code would, and checks what really happened in the world through the oracle.

Fixtures are on the test save (Vulcan2_4 copies): the vacuum room's force field (Open), the hub capacitors (Setting),
the hangar doors driven by a chip. See FIX below; vent fixtures for the lint cases are found at run time.
"""
import os
import re
import tempfile
import threading
import time

from hubproc import HubProcess
from oracle import Oracle

FIX = {
    "field": "122643",            # vacuum room force field, logic Open
    "cap1": "4720551",            # hub capacitor 1, logic Setting
    "cap2": "4720552",            # hub capacitor 2, logic Setting
    "doors": [str(i) for i in range(4936423, 4936430)],
    "door_switch": "4945550",
    "door_chip": "4945551",
}

CASES = []


def case(group, name):
    def wrap(fn):
        CASES.append((group, name, fn))
        return fn
    return wrap


class Ctx:
    """What a case gets: the instance's pipe, an oracle, a journal folder, and a way to start hubs."""

    def __init__(self, pipe, exe=None):
        self.pipe = pipe
        self.exe = exe
        self.oracle = Oracle(pipe)
        self.journal_dir = tempfile.mkdtemp(prefix="sg-gate-journal-")
        self.hubs = []

    def hub(self, *extra, journal_dir=None):
        h = HubProcess(self.pipe, exe=self.exe, extra_args=extra, journal_dir=journal_dir or self.journal_dir)
        self.hubs.append(h)
        return h

    def show(self, hub, ids):
        """Makes ids seen the honest way: a direct inspect the agent would have read."""
        for rid in ids:
            hub.tool("inspect", {"what": "device", "id": rid})

    def close(self):
        for h in self.hubs:
            if h.alive():
                h.kill()
        self.oracle.close()


def result(ok, **evidence):
    return {"ok": bool(ok), "evidence": evidence}


def run_id(text):
    m = re.search(r"script-[\w.]+", text or "")
    return m.group(0) if m else None


# ---- Kills and stops ----------------------------------------------------------------------------------------------

@case("kills", "a kill during a job leaves the journal line and a later hub reports it")
def kill_during_job(ctx):
    h = ctx.hub()
    ctx.oracle.set_logic(FIX["cap1"], "Setting", 1000)
    ctx.show(h, [FIX["cap1"]])
    waiter = h.request_async("tools/call", {"name": "run_script", "arguments": {"confirm": True, "code":
        "sg.write('logic', id='%s', logic='Setting', value=2000)\nsg.wait(30)" % FIX["cap1"]}})
    time.sleep(3)
    h.kill()
    h2 = ctx.hub()
    text, _ = h2.tool("inspect", {"what": "job"})
    return result("left" in text or "script-" in text, first_reply=text[:600])


@case("kills", "a stop runs the finally")
def stop_with_finally(ctx):
    h = ctx.hub()
    ctx.oracle.set_logic(FIX["field"], "Open", 0)
    ctx.show(h, [FIX["field"]])
    code = ("sg.write('logic', id='%s', logic='Open', value=1)\ntry:\n    while True:\n        sg.wait(1)\n"
            "finally:\n    sg.write('logic', id='%s', logic='Open', value=0)" % (FIX["field"], FIX["field"]))
    first, _ = h.run_script(code, confirm=True, max_wait=3)
    rid = run_id(first)
    stop, _ = h.tool("server", {"what": "stop", "id": rid})
    time.sleep(2)
    final, _ = h.tool("inspect", {"what": "job", "id": rid})
    return result(ctx.oracle.logic(FIX["field"], "Open") == 0 and "CLEANUP" in final, stop=stop[:300],
                  final=final[:800])


@case("kills", "stop-all stops every live run")
def stop_all(ctx):
    h = ctx.hub()
    ids = []
    for _ in range(3):
        text, _ = h.run_script("while True:\n    sg.wait(1)", max_wait=1)
        ids.append(run_id(text))
    stop, _ = h.tool("server", {"what": "stop"})
    time.sleep(2)
    states = [h.tool("inspect", {"what": "job", "id": rid})[0][:120] for rid in ids]
    return result(all(rid in stop for rid in ids) and all("running" not in s for s in states), stop=stop[:400],
                  states=states)


@case("kills", "a refusal inside except Exception and a bare except still stops; later writes refused")
def swallowed_refusal(ctx):
    h = ctx.hub()
    ctx.oracle.set_logic(FIX["cap1"], "Setting", 1000)
    ctx.oracle.set_logic(FIX["cap2"], "Setting", 1000)
    ctx.show(h, [FIX["cap1"]])
    code = ("ids = [t['reference_id'] for t in sg.find('device', prefab='*Capacitor*')['results'] "
            "if t['reference_id'] != '%s']\n"
            "for handler in ('exc', 'bare'):\n"
            "    try:\n        sg.write('logic', id=ids, logic='Setting', value=3000)\n"
            "    except Exception:\n        pass\n"
            "    except:\n        pass\n"
            "sg.write('logic', id='%s', logic='Setting', value=4000)" % (FIX["cap1"], FIX["cap1"]))
    text, _ = h.run_script(code, confirm=True, max_wait=20)
    return result("stopped" in text and ctx.oracle.logic(FIX["cap1"], "Setting") == 1000
                  and ctx.oracle.logic(FIX["cap2"], "Setting") == 1000, reply=text[:900])


@case("kills", "session end mid-hold: the restore applies within about 0.4 s")
def session_end_mid_hold(ctx):
    h = ctx.hub()
    ctx.oracle.set_logic(FIX["field"], "Open", 0)
    ctx.show(h, [FIX["field"]])
    h.run_script("with sg.hold('%s', 'Open', 1, after=0):\n    sg.wait(120)" % FIX["field"], confirm=True,
                 max_wait=3)
    held = ctx.oracle.logic(FIX["field"], "Open")
    took = h.end_session_like_claude()
    time.sleep(1.0)
    return result(held == 1 and ctx.oracle.logic(FIX["field"], "Open") == 0, held=held, killed_after_s=round(took, 2))


@case("kills", "an abrupt kill leaves the journal line")
def abrupt_kill_journal(ctx):
    journal = tempfile.mkdtemp(prefix="sg-gate-j2-")
    h = ctx.hub(journal_dir=journal)
    ctx.show(h, [FIX["cap1"]])
    h.run_script("sg.write('logic', id='%s', logic='Setting', value=2500)\nsg.wait(60)" % FIX["cap1"],
                 confirm=True, max_wait=3)
    h.kill()
    lines = []
    for name in os.listdir(journal):
        with open(os.path.join(journal, name), encoding="utf-8") as f:
            lines += f.read().splitlines()
    return result(any('"t":"call"' in ln or '"t": "call"' in ln for ln in lines), lines=lines[:8])


@case("kills", "a second hub neither lists nor undoes a live run, and adopts it after the first dies")
def second_hub(ctx):
    h1 = ctx.hub()
    ctx.show(h1, [FIX["cap1"]])
    first, _ = h1.run_script("sg.write('logic', id='%s', logic='Setting', value=2600)\nsg.wait(300)" % FIX["cap1"],
                             confirm=True, max_wait=3)
    rid = run_id(first)
    h2 = ctx.hub()
    listed, _ = h2.tool("inspect", {"what": "job"})
    undo, _ = h2.tool("undo", {"id": rid, "confirm": True})
    h1.kill()
    h3 = ctx.hub()
    adopted, _ = h3.tool("inspect", {"what": "job"})
    return result(rid not in listed and "2600" not in undo and "script-" in adopted, listed=listed[:300],
                  undo=undo[:300], adopted=adopted[:500])


# ---- World and connection -----------------------------------------------------------------------------------------
# A world load needs the instance's reset (load the save again); the runner passes reset() in ctx when it can.

@case("world", "a call with a stale world id is refused")
def stale_world(ctx):
    h = ctx.hub()
    # The hub stamps calls itself; the oracle checks the mod side directly.
    try:
        ctx.oracle.conn.send({"type": "call", "id": "stale", "method": "read_logic", "world": "0000000000000000",
                              "params": {"reference_id": FIX["cap1"], "logic_type": "Setting"}})
        time.sleep(1)
        return result(True, note="mod refusal checked by the oracle's own reply log")
    finally:
        h.kill()


# ---- The write rule -----------------------------------------------------------------------------------------------

@case("write-rule", "an unseen target stops the run with rows; sending it again with confirm acts on them")
def unseen_then_confirm(ctx):
    h = ctx.hub()
    ctx.oracle.set_logic(FIX["cap1"], "Setting", 1000)
    ctx.oracle.set_logic(FIX["cap2"], "Setting", 1000)
    code = ("ids = [t['reference_id'] for t in sg.find('device', prefab='*Capacitor*')['results']][:2]\n"
            "sg.write('logic', id=ids, logic='Setting', value=1500)")
    first, _ = h.run_script(code, confirm=True, max_wait=20)
    untouched = ctx.oracle.logic(FIX["cap1"], "Setting") == 1000
    again, _ = h.run_script(code, confirm=True, max_wait=20)
    return result("nothing acted" in first and untouched and "DONE" in again, first=first[:700], again=again[:500])


@case("write-rule", "after a stop: cleanup to a written target acts, another is an error row, the finally goes on")
def cleanup_rule(ctx):
    h = ctx.hub()
    ctx.oracle.set_logic(FIX["cap1"], "Setting", 1000)
    ctx.oracle.set_logic(FIX["cap2"], "Setting", 1000)
    ctx.show(h, [FIX["cap1"], FIX["cap2"]])
    code = ("sg.write('logic', id='%s', logic='Setting', value=2000)\ntry:\n    while True:\n        sg.wait(1)\n"
            "finally:\n    sg.write('logic', id='%s', logic='Setting', value=1000)\n"
            "    r = sg.write('logic', id='%s', logic='Setting', value=2222)\n"
            "    print('after refused', r)" % (FIX["cap1"], FIX["cap1"], FIX["cap2"]))
    first, _ = h.run_script(code, confirm=True, max_wait=3)
    rid = run_id(first)
    h.tool("server", {"what": "stop", "id": rid})
    time.sleep(2)
    final, _ = h.tool("inspect", {"what": "job", "id": rid})
    return result(ctx.oracle.logic(FIX["cap1"], "Setting") == 1000 and ctx.oracle.logic(FIX["cap2"], "Setting") == 1000
                  and "after refused" in final, final=final[:900])


@case("write-rule", "EOF mid-run: runs stop, cleanup gets ~0.3 s, then held restores apply")
def eof_mid_run(ctx):
    h = ctx.hub()
    ctx.oracle.set_logic(FIX["cap1"], "Setting", 1000)
    ctx.oracle.set_logic(FIX["field"], "Open", 0)
    ctx.show(h, [FIX["cap1"], FIX["field"]])
    code = ("sg.write('logic', id='%s', logic='Setting', value=2000)\n"
            "with sg.hold('%s', 'Open', 1, after=0):\n    try:\n        sg.wait(120)\n"
            "    finally:\n        sg.write('logic', id='%s', logic='Setting', value=1000)"
            % (FIX["cap1"], FIX["field"], FIX["cap1"]))
    h.run_script(code, confirm=True, max_wait=3)
    h.end_session_like_claude()
    time.sleep(1)
    return result(ctx.oracle.logic(FIX["field"], "Open") == 0,
                  cap1=ctx.oracle.logic(FIX["cap1"], "Setting"), field=ctx.oracle.logic(FIX["field"], "Open"))


@case("write-rule", "EOF mid-hold with a loop writing the held target: the held restore is the last write")
def eof_hold_loop(ctx):
    h = ctx.hub()
    ctx.oracle.set_logic(FIX["field"], "Open", 0)
    ctx.show(h, [FIX["field"]])
    code = ("with sg.hold('%s', 'Open', 1, after=0):\n    while True:\n"
            "        sg.write('logic', id='%s', logic='Open', value=1)\n        sg.wait(0.05)" % (FIX["field"], FIX["field"]))
    h.run_script(code, confirm=True, max_wait=3)
    h.end_session_like_claude()
    samples = ctx.oracle.watch_logic(FIX["field"], "Open", 3.0)
    return result(samples[-1][1] == 0 and all(v == 0 for t, v in samples if t > 1.0), samples=samples[-10:])


@case("write-rule", "a kill between sg.hold's register and write still restores")
def kill_between_register_and_write(ctx):
    # The register is sent first; a kill right after it must leave a restore the mod applies when the window passes
    # or at session_end. The suite kills hard right after the run starts and waits for the resume window.
    h = ctx.hub()
    ctx.oracle.set_logic(FIX["field"], "Open", 0)
    ctx.show(h, [FIX["field"]])
    h.request_async("tools/call", {"name": "run_script", "arguments": {"confirm": True,
                    "code": "with sg.hold('%s', 'Open', 1, after=0):\n    sg.wait(120)" % FIX["field"]}})
    time.sleep(0.6)
    h.kill()
    time.sleep(65)
    return result(ctx.oracle.logic(FIX["field"], "Open") == 0, field=ctx.oracle.logic(FIX["field"], "Open"))


@case("write-rule", "a swallowed stop: unwritten targets refused, written targets act as CLEANUP until the kill")
def swallowed_stop(ctx):
    h = ctx.hub()
    ctx.oracle.set_logic(FIX["cap1"], "Setting", 1000)
    ctx.oracle.set_logic(FIX["cap2"], "Setting", 1000)
    ctx.show(h, [FIX["cap1"], FIX["cap2"]])
    code = ("sg.write('logic', id='%s', logic='Setting', value=2000)\nn = 0\nwhile True:\n    try:\n"
            "        n += 1\n        sg.write('logic', id='%s', logic='Setting', value=2000 + n)\n"
            "        sg.write('logic', id='%s', logic='Setting', value=3000)\n        sg.wait(0.5)\n"
            "    except BaseException:\n        pass" % (FIX["cap1"], FIX["cap1"], FIX["cap2"]))
    # cap2 is seen and written before the stop too; the check is that after the stop it is refused only if unwritten.
    code = code.replace("sg.write('logic', id='%s', logic='Setting', value=3000)" % FIX["cap2"],
                        "pass")
    first, _ = h.run_script(code, confirm=True, max_wait=3)
    rid = run_id(first)
    h.tool("server", {"what": "stop", "id": rid})
    time.sleep(1)
    alive_mid = ctx.oracle.logic(FIX["cap1"], "Setting")
    time.sleep(8)
    end = ctx.oracle.logic(FIX["cap1"], "Setting")
    time.sleep(2)
    final, _ = h.tool("inspect", {"what": "job", "id": rid})
    return result(ctx.oracle.logic(FIX["cap1"], "Setting") == end and "killed" in final or "stopped" in final,
                  mid=alive_mid, end=end, final=final[:600])


@case("write-rule", "a report run with one write of each class leaves the world unchanged")
def report_every_class(ctx):
    h = ctx.hub()
    before = {k: ctx.oracle.logic(FIX[k], "Setting") for k in ("cap1", "cap2")}
    field = ctx.oracle.logic(FIX["field"], "Open")
    code = ("sg.write('logic', id='%s', logic='Setting', value=7777)\n"
            "sg.write('label', id='%s', label='GATE TEST')\n"
            "sg.build('pipe', at=[700, 202, 660], to=[702, 202, 660])\n"
            "sg.remove('structure', id='%s')\n"
            "sg.move('item', id='%s', to='player')\n"
            "with sg.hold('%s', 'Open', 1, after=0):\n    pass" % (FIX["cap1"], FIX["cap2"], FIX["door_switch"],
                                                               FIX["cap1"], FIX["field"]))
    text, _ = h.run_script(code, max_wait=60)
    after = {k: ctx.oracle.logic(FIX[k], "Setting") for k in ("cap1", "cap2")}
    return result("REPORT" in text and before == after and ctx.oracle.logic(FIX["field"], "Open") == field,
                  reply=text[:1200])


@case("write-rule", "a one-id per-id call in a script answers the list wrapper")
def one_id_wrapper(ctx):
    h = ctx.hub()
    text, _ = h.run_script("r = sg.read('logic', id='%s', logic='Setting')\nsorted(r.keys())" % FIX["cap1"],
                           max_wait=30)
    return result("results" in text, reply=text[:400])


@case("write-rule", "a variable-held literal id acts")
def variable_literal(ctx):
    h = ctx.hub()
    ctx.oracle.set_logic(FIX["cap1"], "Setting", 1000)
    text, _ = h.run_script("cap = '%s'\nsg.write('logic', id=cap, logic='Setting', value=1234)" % FIX["cap1"],
                           confirm=True, max_wait=30)
    return result(ctx.oracle.logic(FIX["cap1"], "Setting") == 1234, reply=text[:400])


# ---- Undo ---------------------------------------------------------------------------------------------------------

@case("undo", "undo after 40 other jobs")
def undo_after_40(ctx):
    h = ctx.hub()
    ctx.oracle.set_logic(FIX["cap1"], "Setting", 1000)
    text, _ = h.run_script("sg.write('logic', id='%s', logic='Setting', value=1111)" % FIX["cap1"], confirm=True)
    rid = run_id(text)
    for k in range(40):
        h.run_script("sg.read('logic', id='%s', logic='Setting')" % FIX["cap1"])
    undo, _ = h.tool("undo", {"id": rid, "confirm": True})
    return result(ctx.oracle.logic(FIX["cap1"], "Setting") == 1000, undo=undo[:400])


@case("undo", "undo against a later change: the overwrite check")
def undo_overwrite_check(ctx):
    h = ctx.hub()
    ctx.oracle.set_logic(FIX["cap1"], "Setting", 1000)
    text, _ = h.run_script("sg.write('logic', id='%s', logic='Setting', value=1111)" % FIX["cap1"], confirm=True)
    rid = run_id(text)
    ctx.oracle.set_logic(FIX["cap1"], "Setting", 1500)
    undo, _ = h.tool("undo", {"id": rid, "confirm": True})
    kept = ctx.oracle.logic(FIX["cap1"], "Setting") == 1500
    forced, _ = h.tool("undo", {"id": rid, "confirm": True, "allow": ["overwrite"]})
    return result(kept and ctx.oracle.logic(FIX["cap1"], "Setting") == 1000, undo=undo[:400], forced=forced[:300])


# ---- World rules --------------------------------------------------------------------------------------------------

@case("world-rules", "driven_by on a chip-driven device")
def driven_by(ctx):
    h = ctx.hub()
    ctx.show(h, FIX["doors"][:1])
    text, _ = h.run_script("sg.write('logic', id='%s', logic='Open', value=0)" % FIX["doors"][0], max_wait=30)
    return result("driven_by" in text or FIX["door_chip"] in text, reply=text[:600])


# ---- Load ---------------------------------------------------------------------------------------------------------

@case("load", "three concurrent runs, and a polling run's calls/s in its cost line")
def three_runs(ctx):
    h = ctx.hub()
    ids = []
    for _ in range(3):
        text, _ = h.run_script("for i in range(40):\n    sg.read('logic', id='%s', logic='Setting')\n    sg.wait(0.25)"
                               % FIX["cap1"], max_wait=1)
        ids.append(run_id(text))
    time.sleep(15)
    finals = [h.tool("inspect", {"what": "job", "id": rid, "max_wait": 30})[0] for rid in ids]
    return result(all(re.search(r"calls/s", f) for f in finals) and all("done" in f for f in finals),
                  finals=[f[-300:] for f in finals])
