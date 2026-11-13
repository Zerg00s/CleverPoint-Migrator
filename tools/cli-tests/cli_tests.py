#!/usr/bin/env python3
"""
End-to-end tests for the cpm command line.

Drives the real binary the way a script or an AI agent does: arguments in, one JSON object and an exit
code out. Everything runs in a throwaway settings folder (CPM_HOME) whose path has a space and
non-ASCII letters in it on purpose, so nothing here touches real connections or history.

    python3 tools/cli-tests/cli_tests.py                 # everything
    python3 tools/cli-tests/cli_tests.py cli conn        # only some groups
    python3 tools/cli-tests/cli_tests.py --cpm <path>    # another build of cpm

Groups:
    cli     parsing, help, exit codes, job file reading and validation     (no network)
    agent   the agent guide and the skill installer, in a fake home folder (no network)
    conn    connections: add, test, export, import, remove, sign-in errors
    site    site discovery commands
    job     real jobs against the sandbox test site: modes, selections, options
    life    the job lifecycle: timeouts, cancel, a killed process, concurrent access
    report  reports: the catalog and file format (no network), then every report against the sandbox,
            many sites at once, failing sites, cancel, a killed process, resume and export

conn, site, job, life and the online half of report need the sandbox credentials the TestRunner uses
(the Secrets folder beside the repository). job and life write lists named "CliT-..." to the test
subsite; reports only read. Remove the lists afterwards with:
    cd tools/CleverPoint.Migrator.TestRunner && dotnet run -- cli-cleanup
"""
import json
import os
import shutil
import signal
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time
import uuid

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SOURCE_SITE = "https://gocleverpointcom.sharepoint.com/sites/DemoLargeSite"
SITE = SOURCE_SITE + "/migtest"
WEB = "/sites/DemoLargeSite/migtest"
LAB_SITE = "https://cleverpointlab.sharepoint.com"
SRC_LIB, SRC_LIB_PATH = "MigTest-SrcLib", WEB + "/MigTestSrcLib"
SRC_LIST, SRC_LIST_PATH = "MigTest-Source", WEB + "/Lists/MigTestSource"
BIG_LIB = "MigTest-BigLib"
ODD_LIB = "MigTest-Chars & Co's Lib"

WINDOWS = os.name == "nt"
ROOT_FOLDER = os.path.abspath(os.sep)     # somewhere that is certainly not the job file's folder
results = []          # (group, name, passed, detail)
captured = []         # every byte the tool printed, to prove no secret ever shows up
secrets = []          # values that must never appear in output
RUN = uuid.uuid4().hex[:5]      # keeps list names of this test run apart from earlier ones


class Result:
    def __init__(self, code, out, err):
        self.code, self.out, self.err = code, out, err
        try:
            self.json = json.loads(out) if out.strip().startswith("{") else None
        except ValueError:
            self.json = None

    @property
    def ok(self):
        return bool(self.json and self.json.get("ok"))

    @property
    def data(self):
        return (self.json or {}).get("data") or {}

    @property
    def error(self):
        return (self.json or {}).get("error") or {}


class Cpm:
    def __init__(self, exe, home):
        self.exe, self.home = exe, home

    def __call__(self, *args, env=None, cwd=None, timeout=600):
        environment = dict(os.environ, CPM_HOME=self.home)
        environment.update(env or {})
        p = subprocess.run([self.exe, *[str(a) for a in args]], capture_output=True, env=environment, cwd=cwd, timeout=timeout)
        out, err = p.stdout.decode("utf-8", "replace"), p.stderr.decode("utf-8", "replace")
        captured.append(out + err)
        return Result(p.returncode, out, err)


GROUP = "?"


def check(name, condition, detail=""):
    results.append((GROUP, name, bool(condition), "" if condition else str(detail)[:400]))
    print(f"  [{'PASS' if condition else 'FAIL'}] {name}" + ("" if condition else f"\n         {str(detail)[:400]}"))
    return bool(condition)


def write(path, text, encoding="utf-8"):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding=encoding, newline="\n") as f:
        f.write(text)
    return path


def job_file(work, name, spec):
    return write(os.path.join(work, name), json.dumps(spec, indent=2, ensure_ascii=False))


def base_job(**overrides):
    job = {"source": SITE, "target": SITE, "lists": [{"source": SRC_LIB, "targetTitle": "CliT-X"}]}
    job.update(overrides)
    return job


def issues(result):
    return [(d.get("at"), d.get("problem")) for d in (result.error.get("details") or [])]


def finish(cpm, job_id, timeout=420):
    """Waits for a job and returns the final `job wait` result."""
    return cpm("job", "wait", job_id, "--timeout", timeout, timeout=timeout + 60)


def start(cpm, work, name, spec, wait=True, timeout=420):
    path = job_file(work, name, spec)
    if wait:
        return cpm("job", "start", path, "--wait", "--timeout", timeout, timeout=timeout + 60)
    return cpm("job", "start", path)


def runs_of(result):
    return result.data.get("runs") or []


# ----------------------------------------------------------------------------------------------
# cli: parsing, help, exit codes, job files. No network.
# ----------------------------------------------------------------------------------------------
def test_cli(cpm, work):
    r = cpm()
    check("no arguments prints the overview and exits 2", r.code == 2 and "cpm agent guide" in r.out, r.out[:200])
    r = cpm("--help")
    check("--help exits 0 and lists commands", r.code == 0 and "job start" in r.out and "job exec" not in r.out, r.out[:200])
    r = cpm("help", "job", "start")
    check("help <command> shows its usage and examples", r.code == 0 and "Usage: cpm job start <file>" in r.out and "--wait" in r.out, r.out[:300])
    r = cpm("job", "--help")
    check("help for a group lists its commands", r.code == 0 and "job validate" in r.out, r.out[:200])
    r = cpm("job", "start", "--help")
    check("--help after a command shows that command", r.code == 0 and "--timeout" in r.out, r.out[:200])

    r = cpm("frobnicate")
    check("unknown command: usage error, exit 2, JSON", r.code == 2 and r.error.get("code") == "usage", r.out)
    r = cpm("job", "frobnicate")
    check("unknown subcommand names the commands in that group", r.code == 2 and "job start" in (r.error.get("hint") or ""), r.out)
    r = cpm("run", "list", "--bogus", "1")
    check("unknown option lists the valid ones", r.code == 2 and "--limit" in (r.error.get("hint") or ""), r.out)
    r = cpm("run", "list", "--limit")
    check("option without its value", r.code == 2 and "needs a value" in r.error.get("message", ""), r.out)
    r = cpm("run", "list", "--limit", "abc")
    check("non-numeric number option", r.code == 2 and "needs a number" in r.error.get("message", ""), r.out)
    r = cpm("run", "list", "--limit=3")
    check("--option=value form", r.code == 0 and r.ok, r.out)
    r = cpm("RUN", "LIST", "--LIMIT", "3")
    check("commands and options are case-insensitive", r.code == 0 and r.ok, r.out)
    r = cpm("run", "list", "--limit", "0")
    check("out-of-range limit is clamped, not an error", r.code == 0 and r.ok, r.out)
    r = cpm("run", "list", "--limit", "-5")
    check("negative limit is clamped", r.code == 0 and r.ok, r.out)
    r = cpm("run", "status", "1", "2")
    check("too many arguments", r.code == 2 and "Too many" in r.error.get("message", ""), r.out)
    r = cpm("run", "status")
    check("missing argument names it", r.code == 2 and "<runId>" in r.error.get("message", ""), r.out)
    r = cpm("run", "status", "abc")
    check("non-numeric run ID", r.code == 2, r.out)
    r = cpm("connection", "test")
    check("missing required option names it", r.code == 2 and "--site" in r.error.get("message", ""), r.out)

    r = cpm("version", "--home")
    check("--home without a folder", r.code == 2 and "--home needs a folder" in r.error.get("message", ""), r.out)
    r = cpm("connection", "test", "--site", "https://contoso.sharepoint.com/sites/A/SitePages/Home.aspx")
    check("a page address where a site is expected is refused with an explanation", r.code == 2 and "page" in r.error.get("message", ""), r.out)
    r = cpm("connection", "test", "--site", "https://contoso.sharepoint.com/sites/A/_layouts/15/viewlsts.aspx?view=14")
    check("a _layouts address is refused the same way", r.code == 2, r.out)
    r = cpm("connection", "test", "--site", "  https://contoso.sharepoint.com/sites/A/?web=1#x  ")
    check("spaces, a trailing slash, a query and a fragment are dropped from a site URL", r.code == 4 and "contoso.sharepoint.com" in r.error.get("message", ""), r.out)

    r = cpm("version")
    check("version: one line of JSON, nothing on stderr", r.ok and r.out.count("\n") == 1 and r.err == "" and "skillVersion" in r.data, r.out + r.err)
    r = cpm("version", "--pretty")
    check("--pretty indents and is still valid JSON", r.ok and r.out.count("\n") > 3, r.out)
    r = cpm("--pretty", "version")
    check("--pretty works before the command too", r.ok and r.out.count("\n") > 3, r.out)

    # An empty settings folder.
    r = cpm("connection", "list")
    check("empty home: no connections", r.ok and r.data.get("connections") == [], r.out)
    check("the settings folder in use is the isolated one", r.data.get("settingsFolder") == cpm.home, r.data.get("settingsFolder"))
    check("empty home: run list and job list work", cpm("run", "list").data.get("runs") == [] and cpm("job", "list").data.get("jobs") == [])
    r = cpm("job", "status", "j000000-nope")
    check("unknown job: not_found, exit 4", r.code == 4 and r.error.get("code") == "not_found", r.out)
    r = cpm("job", "wait", "j000000-nope", "--timeout", "1")
    check("waiting on an unknown job fails at once", r.code == 4, r.out)
    r = cpm("job", "cancel", "j000000-nope")
    check("cancelling an unknown job: not_found", r.code == 4, r.out)
    check("unknown run: not_found, exit 4", cpm("run", "status", "999999").code == 4)
    check("items of an unknown run: not_found", cpm("run", "items", "999999").code == 4)
    check("negative run ID is just not found", cpm("run", "status", "-1").code == 4)

    # Schema and template.
    r = cpm("schema", "job")
    props = r.data.get("properties") or {}
    check("schema job: wrapped, with properties", r.ok and {"source", "target", "lists", "options", "mode"} <= set(props), list(props))
    check("every top-level setting has a description", all("description" in v for v in props.values()), [k for k, v in props.items() if "description" not in v])
    nested = (props.get("lists") or {}).get("items", {}).get("properties", {})
    check("nested settings are described too", "sourcePath" in nested and all("description" in v for v in nested.values()), list(nested))
    r = cpm("schema", "job", "--raw")
    raw = None
    try:
        raw = json.loads(r.out)
    except ValueError:
        pass
    check("schema job --raw is the bare schema", r.code == 0 and raw and "$schema" in raw and "ok" not in raw, r.out[:200])
    r = cpm("job", "template", "--raw")
    template = None
    try:
        template = json.loads(r.out)
    except ValueError:
        pass
    check("job template --raw is a bare job", r.code == 0 and template and "lists" in template, r.out[:200])
    path = write(os.path.join(work, "template.json"), r.out)
    r = cpm("job", "validate", path)
    check("the template is a valid job (it fails only on the missing connection)", r.code == 4 and r.error.get("code") == "not_found" and "connection" in r.error.get("message", "").lower(), r.out)

    # Reading job files.
    r = cpm("job", "validate", os.path.join(work, "does not exist.json"))
    check("missing job file: not_found", r.code == 4, r.out)
    for name, text in [("empty.json", ""), ("blank.json", "   \n"), ("null.json", "null"), ("array.json", "[]"),
                       ("text.json", "hello"), ("cut.json", '{"source": "https://a.sharepoint.com", "lists": [')]:
        r = cpm("job", "validate", write(os.path.join(work, name), text))
        check(f"{name}: invalid, exit 5, with a reason", r.code == 5 and r.error.get("code") == "invalid" and issues(r), r.out)
    r = cpm("job", "validate", write(os.path.join(work, "comments.json"),
                                    '{\n  // a comment\n  "source": "%s", /* another */\n  "target": "%s",\n  "lists": [ { "source": "A", "targetTitle": "B", }, ],\n}\n' % (SITE, SITE)))
    check("comments and trailing commas are accepted", r.error.get("code") != "invalid", r.out)

    good = json.dumps(base_job())
    for name, encoding in [("utf8bom.json", "utf-8-sig"), ("utf16.json", "utf-16")]:
        r = cpm("job", "validate", write(os.path.join(work, name), good, encoding=encoding))
        check(f"a job file saved as {encoding} is read", r.error.get("code") != "invalid", r.out)
    r = cpm("job", "validate", job_file(work, "ünïcödé job.json", base_job(name="Überweisung «2026» 日本語 'quoted' \"double\"")))
    check("non-ASCII file name and job name", r.error.get("code") != "invalid", r.out)

    # Misspelled settings, at every level.
    for label, spec, expected in [
        ("top level", dict(base_job(), optons={}), "optons"),
        ("in a list", base_job(lists=[{"source": "A", "targetTitle": "B", "sorcePath": "/x"}]), "sorcePath"),
        ("in options", base_job(options={"versons": 3}), "versons"),
        ("in identityMapping", base_job(identityMapping={"user": {}}), "user"),
    ]:
        r = cpm("job", "validate", job_file(work, "typo.json", spec))
        check(f"misspelled setting {label} is rejected by name", r.code == 5 and any(expected in (p or "") for _, p in issues(r)), r.out)

    # Wrong types.
    for label, spec in [
        ("versions as text", base_job(options={"versions": "five"})),
        ("lists as an object", base_job(lists={"source": "A"})),
        ("copyPermissions as text", base_job(options={"copyPermissions": "yes"})),
        ("a date that is not a date", base_job(options={"modifiedSince": "last tuesday"})),
        ("itemIds as text", base_job(lists=[{"source": "A", "targetTitle": "B", "itemIds": ["one"]}])),
    ]:
        r = cpm("job", "validate", job_file(work, "type.json", spec))
        check(f"wrong type: {label}", r.code == 5 and issues(r), r.out)

    # Each rule, and where it points.
    other = "https://b.sharepoint.com/sites/y"
    rules = [
        ("source is not a URL", base_job(source="contoso"), "source"),
        ("source is http", base_job(source="http://a.sharepoint.com"), "source"),
        ("source is a page, not a site", base_job(source=SITE + "/SitePages/Home.aspx"), "source"),
        ("target is a _layouts page", base_job(target=SITE + "/_layouts/15/viewlsts.aspx"), "target"),
        ("target missing", {"source": SITE, "lists": [{"source": "A"}]}, "target"),
        ("unknown mode", base_job(mode="fast"), "mode"),
        ("unknown engine", base_job(engine="turbo"), "engine"),
        ("unknown existing mode", base_job(options={"existing": "replace"}), "options.existing"),
        ("unknown date field", base_job(options={"dateField": "touched"}), "options.dateField"),
        ("versions too low", base_job(options={"versions": 0}), "options.versions"),
        ("versions too high", base_job(options={"versions": 2001}), "options.versions"),
        ("parallel transfers out of range", base_job(options={"parallelFileTransfers": 99}), "options.parallelFileTransfers"),
        ("date range backwards", base_job(options={"modifiedSince": "2026-02-01T00:00:00Z", "modifiedBefore": "2026-01-01T00:00:00Z"}), "options.modifiedBefore"),
        ("restoreInheritance outside permissions mode", base_job(options={"restoreInheritance": True}), "options.restoreInheritance"),
        ("no lists", base_job(lists=[]), "lists"),
        ("lists and allLists together", base_job(target=other, allLists=True), "lists"),
        ("excludeLists without allLists", base_job(excludeLists=["A"]), "excludeLists"),
        ("allLists on the same site", {"source": SITE, "target": SITE, "allLists": True}, "allLists"),
        ("a list with no source", base_job(lists=[{"targetTitle": "B"}]), "lists[0]"),
        ("a list with a blank source", base_job(lists=[{"source": "   ", "targetTitle": "B"}]), "lists[0]"),
        ("relative sourcePath", base_job(lists=[{"sourcePath": "Shared Documents", "targetTitle": "B"}]), "lists[0].sourcePath"),
        ("relative targetPath", base_job(lists=[{"source": "A", "targetPath": "Lists/B"}]), "lists[0].targetPath"),
        ("relative sourceFolder", base_job(lists=[{"source": "A", "targetTitle": "B", "sourceFolder": "Folder"}]), "lists[0].sourceFolder"),
        ("relative selection path", base_job(lists=[{"source": "A", "targetTitle": "B", "paths": ["a/b"]}]), "lists[0].paths"),
        ("absolute targetFolder", base_job(lists=[{"source": "A", "targetTitle": "B", "targetFolder": "/x"}]), "lists[0].targetFolder"),
        ("same site without a target list", base_job(lists=[{"source": "A"}]), "lists[0].targetTitle"),
        ("same site, differing only in case and a slash", base_job(source=SITE.upper().replace("HTTPS", "https") + "/", lists=[{"source": "A"}]), "lists[0].targetTitle"),
        ("the second of two lists is wrong", base_job(lists=[{"source": "A", "targetTitle": "B"}, {"targetTitle": "C"}]), "lists[1]"),
        ("mapping CSV that does not exist", base_job(identityMapping={"csv": "no-such-file.csv"}), "identityMapping.csv"),
        ("unsupported schema version", base_job(schemaVersion=2), "schemaVersion"),
    ]
    for label, spec, where in rules:
        r = cpm("job", "validate", job_file(work, "rule.json", spec))
        check(f"rule: {label} -> {where}", r.code == 5 and where in [a for a, _ in issues(r)], r.out)

    # The Migration API engine cannot do these, and that is not an error: the classic engine takes over.
    for label, spec in [
        ("permissions mode", base_job(mode="permissions", engine="migrationApi")),
        ("copyPermissions", base_job(engine="migrationApi", options={"copyPermissions": True})),
        ("a target folder", base_job(engine="migrationApi", lists=[{"source": "A", "targetTitle": "B", "targetFolder": "x"}])),
        ("skip existing", base_job(engine="migrationApi", options={"existing": "skip"})),
        ("version history", base_job(engine="migrationApi", options={"versions": 5})),
    ]:
        r = cpm("job", "validate", job_file(work, "api-ok.json", spec))
        check(f"Migration API with {label} is accepted (it falls back, it does not fail)", r.error.get("code") != "invalid", r.out)

    r = cpm("job", "validate", job_file(work, "many.json", base_job(mode="fast", engine="turbo", options={"versions": 0})))
    check("all problems are reported at once, not one per attempt", r.code == 5 and len(issues(r)) >= 3, r.out)
    r = cpm("job", "validate", job_file(work, "case.json", base_job(mode="FULL", engine="Classic", options={"existing": "CopyIfNewer", "dateField": "Created"})))
    check("enumerated values are case-insensitive", r.error.get("code") != "invalid", r.out)
    r = cpm("job", "start", job_file(work, "bad-start.json", base_job(mode="fast")))
    check("start refuses an invalid job and creates none", r.code == 5 and cpm("job", "list").data.get("jobs") == [], r.out)

    # A history database written by an older version of the app is upgraded in place.
    old_home = os.path.join(work, "old home")
    os.makedirs(old_home)
    db = sqlite3.connect(os.path.join(old_home, "history.db"))
    db.executescript("""
        CREATE TABLE runs(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, source_url TEXT, source_list TEXT,
            target_url TEXT, target_list TEXT, engine TEXT, started_utc TEXT, finished_utc TEXT, status TEXT,
            copied INT DEFAULT 0, skipped INT DEFAULT 0, warnings INT DEFAULT 0, failed INT DEFAULT 0);
        CREATE TABLE run_items(id INTEGER PRIMARY KEY AUTOINCREMENT, run_id INT NOT NULL, item_type TEXT, source_path TEXT,
            target_path TEXT, status TEXT, message TEXT, size_bytes INT, duration_ms INT, item_url TEXT);
        CREATE TABLE item_map(pair TEXT NOT NULL, source_id INT NOT NULL, target_id INT NOT NULL, PRIMARY KEY(pair, source_id));
        INSERT INTO runs(name, source_url, source_list, target_url, target_list, engine, started_utc, finished_utc, status, copied)
            VALUES('old done', 'https://a', 'L', 'https://b', 'L', 'Classic', '2026-01-01T10:00:00Z', '2026-01-01T10:05:00Z', 'Completed', 7);
        INSERT INTO runs(name, source_url, source_list, target_url, target_list, engine, started_utc, status)
            VALUES('old stuck', 'https://a', 'L', 'https://b', 'L', 'Classic', '2026-01-01T11:00:00Z', 'Running');
        INSERT INTO run_items(run_id, item_type, source_path, target_path, status, message, size_bytes, duration_ms)
            VALUES(1, 'File', '/a/x.docx', '/b/x.docx', 'Copied', NULL, 10, 5), (1, 'File', '/a/y.docx', '/b/y.docx', 'Failed', 'boom', 0, 1);
    """)
    db.commit()
    db.close()
    old = Cpm(cpm.exe, old_home)
    r = old("run", "list")
    by_name = {x["name"]: x for x in r.data.get("runs", [])}
    check("an older history database opens", r.ok and len(by_name) == 2, r.out)
    check("its finished run is untouched", by_name.get("old done", {}).get("status") == "Completed", by_name)
    check("its run left 'Running' by a dead process becomes Interrupted", by_name.get("old stuck", {}).get("status") == "Interrupted", by_name)
    r = old("run", "items", "1")
    check("items of an old run are readable, failures first by default", r.ok and [i["status"] for i in r.data.get("items", [])] == ["Failed"], r.out)
    r = old("run", "items", "1", "--status", "all")
    check("--status all returns everything", r.data.get("returned") == 2, r.out)
    r = old("run", "items", "1", "--status", "weird")
    check("unknown item status", r.code == 2, r.out)
    r = old("run", "items", "1", "--after", "x")
    check("--after that is not a number", r.code == 2, r.out)
    r = old("run", "status", "1")
    check("run status of a finished old run", r.ok and r.data.get("run", {}).get("status") == "Completed" and r.data["run"].get("failed") == 1, r.out)
    out_csv = os.path.join(work, "new folder", "deeper", "run.csv")
    r = old("run", "export", "1", "--out", out_csv)
    check("export creates the folder it is asked to write into", r.ok and os.path.exists(out_csv), r.out)
    if os.path.exists(out_csv):
        text = open(out_csv, encoding="utf-8-sig").read()
        check("the exported CSV has a header and both rows", text.startswith("ItemType,") and text.count("\n") == 3, text[:200])
    r = old("report", "verify", "2")
    check("verifying an interrupted run with no connection: clear error, no crash", r.code in (4, 5) and r.json is not None, r.out)

    # Settings damaged by hand must not be silently replaced.
    bad_home = os.path.join(work, "bad home")
    write(os.path.join(bad_home, "settings.json"), '{ "Connections": [ { "Name": "precious"  <<< not json')
    bad = Cpm(cpm.exe, bad_home)
    r = bad("connection", "list")
    check("unreadable settings: the command still answers", r.json is not None, r.out + r.err)
    r = bad("connection", "add", "--site", "https://contoso.sharepoint.com", "--sign-in", "browser")
    kept = [f for f in os.listdir(bad_home) if f.startswith("settings") and f != "settings.json"]
    survived = any("precious" in open(os.path.join(bad_home, f), encoding="utf-8", errors="replace").read() for f in os.listdir(bad_home) if f.startswith("settings"))
    check("unreadable settings are kept as a backup before being rewritten", survived, f"files: {os.listdir(bad_home)}; backups: {kept}; add said: {r.out[:200]}")


# ----------------------------------------------------------------------------------------------
# agent: guide and skill installer, in a fake home so nothing real is installed.
# ----------------------------------------------------------------------------------------------
def test_agent(cpm, work):
    r = cpm("agent", "guide")
    check("guide: plain text, exit 0", r.code == 0 and r.out.startswith("# CleverPoint Migrator") and r.json is None, r.out[:200])
    guide = r.out
    commands = [c["name"] for c in cpm("agent", "guide", "--json").data.get("commands", [])]
    check("guide --json lists the commands", len(commands) > 20 and "job start" in commands and "job exec" not in commands, commands)
    missing = [c for c in commands if f"cpm {c}" not in guide]
    check("every command in the table is in the guide text", not missing, missing)
    check("the guide documents every exit code", all(f"\n{code:>2}  " in guide for code in (0, 1, 2, 3, 4, 5, 6, 7, 10)), "")
    check("the guide documents job settings from the schema", all(s in guide for s in ("sourcePath", "restoreInheritance", "identityMapping", "allLists")), "")
    check("the guide stays short enough to read in one go", len(guide.split()) < 4200, f"{len(guide.split())} words")
    r = cpm("agent", "guide", "--skill-version", "0")
    check("an older skill is told to update, first line", r.out.startswith("YOUR SKILL FILE IS OUTDATED"), r.out[:120])
    current = cpm("version").data.get("skillVersion")
    check("the current skill gets no warning", cpm("agent", "guide", "--skill-version", current).out.startswith("# CleverPoint"))
    check("a newer skill than the tool knows gets no warning", cpm("agent", "guide", "--skill-version", 999).out.startswith("# CleverPoint"))
    check("a garbage skill version does not break the guide", cpm("agent", "guide", "--skill-version", "abc").out.startswith("# CleverPoint"))
    check("guide --json flags an outdated skill", cpm("agent", "guide", "--json", "--skill-version", "0").data.get("skillOutdated") is True)

    def agents(home, *args):
        # CPM_AGENT_HOME is what keeps this away from the real agent folders of whoever runs the tests.
        return cpm("agent", *args, env={"CPM_AGENT_HOME": home, "CPM_HOME": os.path.join(home, "cpm home")})

    def skill(home, agent):
        return os.path.join(home, "." + agent, "skills", "cleverpoint-migrator", "SKILL.md")

    empty = os.path.join(work, "home none")
    os.makedirs(empty)
    r = agents(empty, "install", "--no-wsl")
    check("no agents on the machine: nothing installed, said plainly", r.ok and r.data.get("installed") == [] and "No AI agent" in r.data.get("message", ""), r.out)
    check("and no agent folder was created", not any(os.path.exists(os.path.join(empty, d)) for d in (".claude", ".codex", ".gemini", ".agents")), os.listdir(empty))

    home = os.path.join(work, "home two agents")
    os.makedirs(os.path.join(home, ".claude"))
    os.makedirs(os.path.join(home, ".gemini"))
    r = agents(home, "install", "--no-wsl")
    installed = {i["agent"]: i for i in r.data.get("installed", [])}
    check("installs for each agent that is present, and only those", set(installed) == {"claude", "gemini"} and all(i["state"] == "installed" for i in installed.values()), r.out)
    check("codex, which is absent, got nothing", not os.path.exists(os.path.join(home, ".codex")))
    text = open(skill(home, "claude"), encoding="utf-8").read() if os.path.exists(skill(home, "claude")) else ""
    lines = text.split("\n")
    check("the skill starts with front matter", lines[:1] == ["---"] and lines[1].startswith("name: cleverpoint-migrator") and lines[2].startswith("description: ") and lines[3] == "---", lines[:4])
    raw = open(skill(home, "claude"), "rb").read() if os.path.exists(skill(home, "claude")) else b"?"
    check("the skill has no Windows line endings and no byte order mark", b"\r" not in raw and not raw.startswith(b"\xef\xbb\xbf"))
    check("the skill points at the guide with its own version", f"cpm agent guide --skill-version {current}" in text and f"skill-version: {current}" in text, text[:400])
    check("the skill is short", len(text.split()) < 450, f"{len(text.split())} words")

    r = agents(home, "status")
    check("status: both copies current", sorted(i["state"] for i in r.data.get("installed", [])) == ["current", "current"], r.out)
    r = agents(home, "install", "--no-wsl")
    check("installing again is harmless", r.ok and len(r.data.get("installed", [])) == 2 and len(agents(home, "status").data.get("installed", [])) == 2, r.out)

    # An edited copy is the user's and must survive a refresh.
    with open(skill(home, "gemini"), "a", encoding="utf-8") as f:
        f.write("\nmy own note\n")
    manifest_path = os.path.join(home, "cpm home", "agent-skills.json")
    manifest = json.load(open(manifest_path))
    for entry in manifest:
        entry["Version"] = 0            # pretend both were installed by an older app
    json.dump(manifest, open(manifest_path, "w"))
    old_text = "---\nname: cleverpoint-migrator\ndescription: old\n---\nold skill\n"
    # The claude copy really is what an older app installed: old text, matching hash.
    import hashlib
    write(skill(home, "claude"), old_text)
    for entry in manifest:
        if entry["Agent"] == "claude":
            entry["Hash"] = hashlib.sha256(old_text.encode()).hexdigest().upper()
    json.dump(manifest, open(manifest_path, "w"))
    r = agents(home, "status")
    states = {i["agent"]: i["state"] for i in r.data.get("installed", [])}
    check("status tells outdated from edited", states == {"claude": "outdated", "gemini": "edited"}, states)
    r = agents(home, "install", "--refresh")
    states = {i["agent"]: i["state"] for i in r.data.get("refreshed", [])}
    check("refresh updates the untouched copy and leaves the edited one", states == {"claude": "updated", "gemini": "edited"}, r.out)
    check("the updated copy now has the current text", f"skill-version: {current}" in open(skill(home, "claude"), encoding="utf-8").read())
    check("the edited copy still has the user's note", "my own note" in open(skill(home, "gemini"), encoding="utf-8").read())
    r = agents(home, "install", "--refresh")
    check("a second refresh has nothing to do", {i["agent"]: i["state"] for i in r.data.get("refreshed", [])}.get("claude") == "current", r.out)

    os.remove(skill(home, "claude"))
    manifest = json.load(open(manifest_path))
    for entry in manifest:
        entry["Version"] = 0
    json.dump(manifest, open(manifest_path, "w"))
    r = agents(home, "install", "--refresh")
    check("refresh does not bring back a copy the user deleted", {i["agent"]: i["state"] for i in r.data.get("refreshed", [])}.get("claude") == "missing" and not os.path.exists(skill(home, "claude")), r.out)

    os.makedirs(os.path.join(home, ".codex"))
    r = agents(home, "install", "--refresh")
    check("refresh never installs into a new place", not os.path.exists(skill(home, "codex")), r.out)
    r = agents(home, "install", "--no-wsl", "--agent", "codex,nonsense")
    check("--agent limits the install; unknown names are ignored", [i["agent"] for i in r.data.get("installed", [])] == ["codex"], r.out)

    write(manifest_path, "{ this is not json")
    r = agents(home, "status")
    check("a damaged install record does not break status", r.ok, r.out)
    r = agents(home, "install", "--no-wsl")
    check("nor a new install", r.ok and len(r.data.get("installed", [])) == 3, r.out)


# ----------------------------------------------------------------------------------------------
# conn: connections.
# ----------------------------------------------------------------------------------------------
def test_conn(cpm, work, creds):
    scratch = Cpm(cpm.exe, os.path.join(work, "conn home"))
    r = scratch("connection", "add", "--site", "contoso", "--sign-in", "browser")
    check("add: not a URL", r.code == 2, r.out)
    r = scratch("connection", "add", "--site", "http://contoso.sharepoint.com", "--sign-in", "browser")
    check("add: http is refused", r.code == 2, r.out)
    r = scratch("connection", "add", "--site", SOURCE_SITE)
    check("add: neither --sign-in nor --file", r.code == 2 and "microsoft" in (r.error.get("hint") or ""), r.out)
    r = scratch("connection", "add", "--site", SOURCE_SITE, "--sign-in", "password")
    check("add: unknown sign-in kind", r.code == 2, r.out)
    r = scratch("connection", "add", "--site", SOURCE_SITE, "--sign-in", "microsoft")
    check("add microsoft without a client ID points at setup", r.code == 2 and "connection setup" in (r.error.get("hint") or ""), r.out)
    r = scratch("connection", "add", "--site", SOURCE_SITE, "--sign-in", "microsoft", "--client-id", "not-a-guid")
    check("add microsoft with a client ID that is not a GUID", r.code == 2, r.out)
    r = scratch("connection", "add", "--site", "https://example.com/sites/x", "--sign-in", "microsoft", "--client-id", str(uuid.uuid4()))
    check("add for a host that is not SharePoint Online: clear error", not r.ok and r.json is not None and "SharePoint" in r.error.get("message", ""), r.out)
    r = scratch("connection", "add", "--site", SOURCE_SITE, "--sign-in", "microsoft", "--client-id", str(uuid.uuid4()), "--tenant-id", str(uuid.uuid4()))
    check("add with a tenant ID that does not own the site", r.code == 5 and "does not own" in r.error.get("message", ""), r.out)
    check("nothing was saved by the failed attempts", scratch("connection", "list").data.get("connections") == [])

    client = str(uuid.uuid4())
    r = scratch("connection", "add", "--site", SOURCE_SITE + "/", "--sign-in", "microsoft", "--client-id", client)
    conn = r.data.get("connection", {})
    check("add microsoft: saved, tenant found from the site, not ready, next is login",
          r.ok and conn.get("tenantId") == creds["source"]["tenant id"].lower() and conn.get("ready") is False and "connection login" in r.data.get("next", ""), r.out)
    check("the trailing slash was dropped", conn.get("site") == SOURCE_SITE, conn.get("site"))
    r = scratch("connection", "test", "--site", SITE)
    check("test on a connection nobody signed in to: exit 3 with the login command", r.code == 3 and r.error.get("code") == "sign_in_required" and "connection login --site" in (r.error.get("hint") or ""), r.out)
    r = scratch("site", "lists", "--site", SITE)
    check("site lists without a sign-in: exit 3, no prompt", r.code == 3, r.out)
    r = scratch("job", "start", job_file(work, "needs-login.json", base_job()))
    check("job start without a sign-in: exit 3 and no job is created", r.code == 3 and scratch("job", "list").data.get("jobs") == [], r.out)
    r = scratch("job", "validate", job_file(work, "needs-login.json", base_job()))
    check("offline validate still works and reports the connection as not ready", r.ok and r.data.get("source", {}).get("ready") is False, r.out)
    r = scratch("job", "validate", job_file(work, "needs-login.json", base_job()), "--online")
    check("online validate without a sign-in: exit 3", r.code == 3, r.out)

    exported = os.path.join(work, "exported connection.json")
    r = scratch("connection", "export", "--site", SOURCE_SITE, "--out", exported)
    payload = json.load(open(exported)) if os.path.exists(exported) else {}
    check("export writes site, tenant and client ID and nothing else", r.ok and set(payload) == {"kind", "version", "siteUrl", "tenantId", "appId"} and payload.get("appId") == client, payload)
    other = Cpm(cpm.exe, os.path.join(work, "second computer"))
    r = other("connection", "add", "--site", SOURCE_SITE, "--file", exported)
    check("import on another computer gives the same connection, signed out", r.ok and r.data["connection"].get("clientId") == client and r.data["connection"].get("ready") is False, r.out)
    r = other("connection", "add", "--site", "https://cleverpointlab.sharepoint.com", "--file", exported)
    check("a connection file for another host is refused", r.code == 5, r.out)
    r = other("connection", "add", "--site", SOURCE_SITE, "--file", write(os.path.join(work, "garbage.json"), '{"kind": "something else"}'))
    check("a file that is not a connection file is refused", r.code == 5, r.out)
    r = other("connection", "add", "--site", SOURCE_SITE, "--file", os.path.join(work, "nope.json"))
    check("a connection file that does not exist", r.code == 4, r.out)

    # Two processes share the settings file. What one adds, the other must neither miss nor erase.
    shared = Cpm(cpm.exe, os.path.join(work, "shared home"))
    shared("connection", "add", "--site", "https://one.sharepoint.com", "--sign-in", "browser")
    first = [threading.Thread(target=lambda n=n: shared("connection", "add", "--site", f"https://tenant{n}.sharepoint.com", "--sign-in", "browser")) for n in range(6)]
    for t in first:
        t.start()
    for t in first:
        t.join()
    hosts = sorted(c["host"] for c in shared("connection", "list").data.get("connections", []))
    check("connections added by several processes at once: the settings file stays readable", hosts and "one.sharepoint.com" in hosts, hosts)

    # Adding the same site again updates it; another site on the same host is found by host.
    r = scratch("connection", "add", "--site", SOURCE_SITE, "--sign-in", "browser")
    check("adding the same site again replaces the sign-in kind, no duplicate",
          r.ok and r.data["connection"].get("signIn") == "browser" and len(scratch("connection", "list").data.get("connections", [])) == 1, r.out)
    r = scratch("connection", "export", "--site", SOURCE_SITE, "--out", os.path.join(work, "x.json"))
    check("a browser connection cannot be exported", r.code == 5, r.out)
    r = scratch("connection", "test", "--site", SITE)
    check("test on a browser connection with no session: exit 3", r.code == 3, r.out)
    if not WINDOWS:     # on Windows this would open the real sign-in window and wait for a person
        r = scratch("connection", "login", "--site", SOURCE_SITE)
        check("browser login off Windows: says it is not supported, does not hang", r.code != 0 and r.error.get("code") == "not_supported", r.out)
    r = scratch("connection", "logout", "--site", SOURCE_SITE)
    check("logout with nothing to forget is fine", r.ok, r.out)
    r = scratch("connection", "test", "--site", "https://unknown-tenant-xyz.sharepoint.com/sites/a")
    check("a site on a host with no connection: not_found naming the fix", r.code == 4 and "connection add" in (r.error.get("hint") or ""), r.out)

    # Certificates.
    src = creds["source"]
    args = ["connection", "add", "--site", SOURCE_SITE, "--sign-in", "certificate", "--client-id", src["app id"]]
    r = scratch(*args)
    check("certificate without --pfx", r.code == 2, r.out)
    r = scratch(*args, "--pfx", os.path.join(work, "missing.pfx"))
    check("certificate file that does not exist", r.code == 4, r.out)
    r = scratch(*args, "--pfx", src["pfx"], "--pfx-password-env", "CPM_TEST_UNSET_VARIABLE")
    check("password variable that is not set", r.code == 2 and "CPM_TEST_UNSET_VARIABLE" in r.error.get("message", ""), r.out)
    r = scratch(*args, "--pfx", src["pfx"], "--pfx-password-env", "CPM_TEST_PW", env={"CPM_TEST_PW": "definitely-wrong"})
    check("a wrong certificate password is accepted at add time (nothing is opened yet)", r.ok, r.out)
    r = scratch("connection", "test", "--site", SITE)
    check("and reported clearly when the connection is used", not r.ok and r.json is not None and "password" in r.error.get("message", "").lower() and "at " not in r.error.get("message", ""), r.out)
    r = scratch(*args, "--pfx", src["pfx"], "--pfx-password-env", "CPM_TEST_PW", env={"CPM_TEST_PW": src["cert password"]})
    check("certificate connection: ready at once", r.ok and r.data["connection"].get("ready") is True, r.out)
    r = scratch("connection", "test", "--site", SITE)
    check("test reads the site", r.ok and r.data.get("title"), r.out)
    r = scratch("connection", "test", "--site", SITE.replace("gocleverpointcom", "GOCLEVERPOINTCOM") + "/")
    check("host in capitals and a trailing slash still find the connection", r.ok, r.out)
    r = scratch("connection", "list")
    c = (r.data.get("connections") or [{}])[0]
    check("list shows the kind and no secret fields", c.get("signIn") == "certificate" and not any("pass" in k.lower() or "secret" in k.lower() for k in c), list(c))
    r = scratch("connection", "remove", "--site", SOURCE_SITE)
    check("remove", r.ok and scratch("connection", "list").data.get("connections") == [], r.out)
    check("removing it again: not_found", scratch("connection", "remove", "--site", SOURCE_SITE).code == 4)


# ----------------------------------------------------------------------------------------------
# site: discovery.
# ----------------------------------------------------------------------------------------------
def test_site(cpm, work):
    r = cpm("site", "lists", "--site", SITE)
    lists = {l["title"]: l for l in r.data.get("lists", [])}
    check("site lists: the fixtures are there with type and path",
          r.ok and lists.get(SRC_LIB, {}).get("type") == "library" and lists.get(SRC_LIB, {}).get("path") == SRC_LIB_PATH and lists.get(SRC_LIST, {}).get("type") == "list", list(lists)[:10])
    check("a list name with an ampersand and an apostrophe comes through intact", ODD_LIB in lists, [t for t in lists if "Chars" in t])
    r = cpm("site", "lists", "--site", SITE.upper().replace("HTTPS", "https") + "/")
    check("site URL in capitals with a trailing slash", r.ok and len(r.data.get("lists", [])) == len(lists), r.out[:200])
    r = cpm("site", "lists", "--site", SOURCE_SITE + "/no-such-subsite-" + RUN)
    check("a subsite that does not exist: not_found, with a hint, no HTTP dump", r.code == 4 and r.error.get("hint") and "$select" not in r.error.get("message", ""), r.out)
    r = cpm("site", "webs", "--site", SOURCE_SITE)
    check("site webs lists the test subsite", r.ok and any(w["url"].rstrip("/").lower() == SITE.lower() for w in r.data.get("webs", [])), r.out[:300])

    r = cpm("site", "folder", "--site", SITE, "--list", SRC_LIB_PATH)
    names = {e["name"]: e["type"] for e in r.data.get("entries", [])}
    check("site folder at the root: folders and files", r.ok and names.get("Folder-A") == "folder" and names.get("doc-01.bin") == "file", names)
    r = cpm("site", "folder", "--site", SITE, "--list", SRC_LIB_PATH, "--limit", "1")
    check("--limit truncates and says so", r.data.get("truncated") is True and len(r.data.get("entries", [])) == 1 and r.data.get("total", 0) > 1, r.out[:300])
    r = cpm("site", "folder", "--site", SITE, "--list", SRC_LIB_PATH, "--folder", SRC_LIB_PATH + "/Folder-A")
    check("site folder inside a folder", r.ok and any(e["name"] == "Sub-1" for e in r.data.get("entries", [])), r.out[:300])
    r = cpm("site", "folder", "--site", SITE, "--list", SRC_LIB_PATH, "--folder", SRC_LIB_PATH + "/No Such Folder")
    check("a folder that does not exist is an error, not an empty listing", r.code == 4, r.out)
    r = cpm("site", "folder", "--site", SITE, "--list", WEB + "/NoSuchLibrary" + RUN)
    check("a list that does not exist is an error", r.code == 4, r.out)
    r = cpm("site", "folder", "--site", SITE, "--list", SRC_LIST_PATH, "--limit", "200")
    ids = [e.get("itemId") for e in r.data.get("entries", []) if e["type"] == "item"]
    check("a plain list shows its items with IDs and titles", r.ok and len(ids) > 10 and all(isinstance(i, int) for i in ids)
          and all(e.get("name") for e in r.data.get("entries", [])), r.out[:300])

    r = cpm("site", "users", "--site", SITE)
    people = {p["name"]: p for p in r.data.get("principals", [])}
    check("site users lists people with sign-in names and counts both kinds", r.ok and r.data.get("users", 0) > 0 and r.data.get("groups", 0) > 0
          and all(p.get("signIn") for p in people.values()) and "Alexa Willams" in people, list(people)[:8])
    r = cpm("site", "users", "--site", SITE, "--type", "group", "--limit", "5000")
    check("--type group returns only groups, all of them", r.ok and r.data.get("total") == r.data.get("groups") and {p["type"] for p in r.data.get("principals", [])} == {"group"}, r.out[:200])
    r = cpm("site", "users", "--site", SITE, "--search", "JACKSON")
    check("--search finds by part of a name, any case", [p["name"] for p in r.data.get("principals", [])] == ["Bo Jackson"], r.out[:300])
    check("--type with a value that is neither", cpm("site", "users", "--site", SITE, "--type", "robot").code == 2)
    r = cpm("site", "users", "--site", SITE, "--limit", "2")
    check("site users --limit truncates and says so", len(r.data.get("principals", [])) == 2 and r.data.get("truncated") is True, r.out[:200])
    r = cpm("site", "folder", "--site", SITE, "--list", SRC_LIB_PATH)
    authors = {e["name"]: e.get("createdBy") for e in r.data.get("entries", [])}
    check("site folder shows who created each file", authors.get("doc-01.bin") == "Alexa Willams" and authors.get("doc-05.bin") == "Denis Molodtsov", authors)

    out = os.path.join(work, "maps", "template.csv")
    r = cpm("mapping", "template", "--source", SITE, "--target", SITE, "--out", out)
    check("mapping template writes the CSV, creating the folder", r.ok and os.path.exists(out), r.out)
    if os.path.exists(out):
        text = open(out, encoding="utf-8-sig").read()
        check("the CSV has the Type,Source,Target header and rows", text.splitlines()[0].replace('"', "") == "Type,Source,Target" and r.data.get("users", 0) > 0, text[:200])
        check("on the same site every account is prefilled", r.data.get("toFillIn") == 0, r.out)
    return ids


# ----------------------------------------------------------------------------------------------
# job: real jobs.
# ----------------------------------------------------------------------------------------------
def test_job(cpm, work, item_ids):
    def target(name):
        return f"CliT-{name}-{RUN}"

    def lib_job(key, lists=None, **more):
        return dict({"name": key, "source": SITE, "target": SITE,
                     "lists": lists or [{"source": SRC_LIB, "targetTitle": target(key)}]}, **more)

    def files_in(title_path):
        r = cpm("site", "folder", "--site", SITE, "--list", title_path, "--limit", "2000")
        return r, {e["name"]: e["type"] for e in r.data.get("entries", [])}

    def path_of(title):
        lists = {l["title"]: l for l in cpm("site", "lists", "--site", SITE).data.get("lists", [])}
        return lists.get(title, {}).get("path"), lists.get(title, {})

    # ---- a plain full copy, looked at from every side ----
    spec = lib_job("Basic", name="Basic «ünïcödé» 'quoted' \"double\" 日本")
    path = job_file(work, "basic job.json", spec)
    v = cpm("job", "validate", path, "--online")
    plan = (v.data.get("lists") or [{}])[0]
    check("validate --online: finds the source and sees the target is new", v.ok and plan.get("sourcePath") == SRC_LIB_PATH and plan.get("targetState") == "willBeCreated" and plan.get("sourceItems") == 15, v.out[:400])
    r = finish(cpm, cpm("job", "start", path).data.get("jobId"))
    run = (runs_of(r) or [{}])[0]
    check("basic copy completes, exit 0", r.code == 0 and r.data.get("status") == "Completed" and r.data.get("finished") is True, r.out[:400])
    check("the job name with quotes and non-ASCII letters survives", r.data.get("name") == spec["name"], r.data.get("name"))
    check("planned equals processed, 100 percent, no failures", run.get("planned") == 15 and run.get("processed") == 15 and run.get("percent") == 100 and run.get("failed") == 0, run)
    check("a clean job has no 'next' command", r.data.get("next") is None, r.data.get("next"))
    run_id = run.get("runId")
    tpath, tinfo = path_of(target("Basic"))
    check("the target library exists with the same item count", tinfo.get("items") == 15 and tinfo.get("type") == "library", tinfo)
    v = cpm("job", "validate", path, "--online")
    check("validate --online now sees the target exists, with its count", (v.data.get("lists") or [{}])[0].get("targetState") == "exists" and (v.data.get("lists") or [{}])[0].get("targetItems") == 15, v.out[:400])

    r = cpm("run", "items", run_id)
    check("run items defaults to failures and warnings: none here", r.ok and r.data.get("returned") == 0, r.out[:200])
    seen, after, pages = [], None, 0
    while True:
        args = ["run", "items", run_id, "--status", "all", "--limit", "7"] + (["--after", after] if after else [])
        page = cpm(*args)
        seen += [i["row"] for i in page.data.get("items", [])]
        pages += 1
        after = page.data.get("nextAfter")
        if not after or pages > 50:
            break
    everything = cpm("run", "items", run_id, "--status", "all", "--limit", "5000").data.get("returned")
    check("paging through items returns each row once, in order", len(seen) == everything and seen == sorted(set(seen)) and pages > 2, f"{len(seen)} of {everything} in {pages} pages")
    r = cpm("run", "items", run_id, "--status", "copied,skipped", "--limit", "500")
    check("several statuses at once", r.ok and {i["status"] for i in r.data.get("items", [])} <= {"Copied", "Skipped"} and r.data.get("returned", 0) > 15, r.out[:200])
    r = cpm("run", "status", run_id)
    check("run status agrees with the job", r.code == 0 and r.data.get("run", {}).get("status") == "Completed" and r.data["run"].get("jobId"), r.out[:300])
    r = cpm("run", "cancel", run_id)
    check("cancelling a finished run does nothing and says so", r.ok and r.data.get("cancelRequested") is False, r.out)
    out_csv = os.path.join(work, "exports", "basic.csv")
    r = cpm("run", "export", run_id, "--out", out_csv)
    check("run export has one line per item row", r.ok and os.path.exists(out_csv) and open(out_csv, encoding="utf-8-sig").read().count("\n") == everything + 1, r.out)
    r = cpm("report", "verify", run_id)
    check("report verify: clean, exit 0, report written", r.code == 0 and r.data.get("clean") is True and os.path.exists(r.data.get("report", "")), r.out[:400])

    # ---- existing-item modes ----
    r = start(cpm, work, "again.json", lib_job("Basic", options={"existing": "copyIfNewer"}))
    run = (runs_of(r) or [{}])[0]
    check("copyIfNewer on an up-to-date target: nothing fails, files are skipped", r.code == 0 and run.get("failed") == 0 and run.get("skipped", 0) >= 10, run)
    r = start(cpm, work, "skip.json", lib_job("Basic", options={"existing": "skip"}))
    run = (runs_of(r) or [{}])[0]
    check("skip mode leaves existing files alone", r.code == 0 and run.get("failed") == 0 and run.get("skipped", 0) >= 10, run)
    r = start(cpm, work, "over.json", lib_job("Basic", options={"existing": "overwrite", "versions": 3, "parallelFileTransfers": 1}))
    run = (runs_of(r) or [{}])[0]
    check("overwrite with 3 versions, one file at a time", r.code == 0 and run.get("failed") == 0 and run.get("copied", 0) >= 15, run)
    check("the target still has 15 items after three more runs (no duplicates)", path_of(target("Basic"))[1].get("items") == 15, path_of(target("Basic"))[1])

    # ---- modes ----
    r = start(cpm, work, "struct.json", lib_job("Struct", mode="structure"))
    check("structure mode creates the library with nothing in it", r.code == 0 and path_of(target("Struct"))[1].get("items") == 0, r.out[:300])
    r = start(cpm, work, "content.json", lib_job("Struct", mode="content"))
    check("content mode fills a library that already exists", r.code == 0 and path_of(target("Struct"))[1].get("items") == 15, r.out[:300])
    missing = lib_job("NeverMade", mode="content")
    v = cpm("job", "validate", job_file(work, "content-missing.json", missing), "--online")
    check("content mode into a missing list: online validation refuses, naming the list", v.code == 5 and target("NeverMade") in json.dumps(v.error), v.out[:400])
    r = start(cpm, work, "content-missing.json", missing)
    check("started anyway: the job ends as failed, exit 1, and created nothing", r.code == 1 and r.data.get("status") in ("Failed", "CompletedWithIssues") and path_of(target("NeverMade"))[0] is None, r.out[:400])
    check("and points at the run's failures", "run items" in (r.data.get("next") or ""), r.data.get("next"))
    failed_run = (runs_of(r) or [{}])[0].get("runId")
    if failed_run:
        items = cpm("run", "items", failed_run)
        check("the failure is readable in run items", items.data.get("returned", 0) >= 1 and any(i["status"] == "Failed" and i.get("message") for i in items.data.get("items", [])), items.out[:400])
        check("run status of a failed run exits 1", cpm("run", "status", failed_run).code == 1)
    r = start(cpm, work, "perm-missing.json", lib_job("NeverMade", mode="permissions"))
    check("permissions mode into a missing list: fails, exit 1, creates nothing", r.code == 1 and path_of(target("NeverMade"))[0] is None, r.out[:400])
    r = start(cpm, work, "perm.json", lib_job("Basic", mode="permissions", options={"restoreInheritance": True}))
    check("permissions mode on a copied library: completes and copies no files", r.code == 0 and path_of(target("Basic"))[1].get("items") == 15, r.out[:300])

    # ---- finding the source ----
    v = cpm("job", "validate", job_file(work, "typo-source.json", lib_job("T", lists=[{"source": "MigTest-SrcLibb", "targetTitle": target("T")}])), "--online")
    check("a misspelled source list: not_found pointing at site lists", v.code == 4 and "site lists" in (v.error.get("hint") or ""), v.out)
    r = start(cpm, work, "typo-source.json", lib_job("T", lists=[{"source": SRC_LIB, "targetTitle": target("T")}, {"source": "No Such List", "targetTitle": target("T2")}]))
    check("one bad list fails the whole job before anything is copied", r.code == 1 and r.data.get("status") == "Failed" and runs_of(r) == [] and "No Such List" in (r.data.get("error") or "") and path_of(target("T"))[0] is None, r.out[:400])
    for label, entry in [
        ("title in another case", {"source": SRC_LIB.upper()}),
        ("path with %20-style encoding and a trailing slash", {"sourcePath": SRC_LIB_PATH.replace("Mig", "%4Dig") + "/"}),
        ("path in another case", {"sourcePath": SRC_LIB_PATH.upper()}),
        ("a wrong title with the right path", {"source": "whatever", "sourcePath": SRC_LIB_PATH}),
    ]:
        v = cpm("job", "validate", job_file(work, "find.json", lib_job("F", lists=[dict(entry, targetTitle=target("F"))])), "--online")
        check(f"source found by {label}", v.ok and (v.data.get("lists") or [{}])[0].get("sourcePath") == SRC_LIB_PATH, v.out[:300])
    v = cpm("job", "validate", job_file(work, "mismatch.json", lib_job("M", lists=[{"source": SRC_LIB, "targetTitle": SRC_LIST}])), "--online")
    check("a library aimed at an existing list: refused before any damage", v.code == 5 and "library" in json.dumps(v.error), v.out[:400])

    # ---- targets named by path ----
    bpath, binfo = path_of(target("Basic"))
    spec = lib_job("ByPath", lists=[{"source": SRC_LIB, "targetPath": bpath}], mode="content", options={"existing": "skip"})
    v = cpm("job", "validate", job_file(work, "bypath.json", spec), "--online")
    plan = (v.data.get("lists") or [{}])[0]
    check("a target given only by path is recognized, with its real name", v.ok and plan.get("target") == target("Basic") and plan.get("targetState") == "exists" and plan.get("targetPath") == bpath, v.out[:400])
    r = start(cpm, work, "bypath.json", spec)
    check("and history records the run under that real name", r.code == 0 and (runs_of(r) or [{}])[0].get("target") == target("Basic"), r.out[:300])
    ghost = lib_job("Ghost", lists=[{"source": SRC_LIB, "targetPath": WEB + "/NoSuchLibrary" + RUN}])
    v = cpm("job", "validate", job_file(work, "ghost.json", ghost), "--online")
    check("a targetPath that does not exist is an error that explains what targetPath is for", v.code == 4 and "targetTitle" in (v.error.get("hint") or ""), v.out[:400])
    r = start(cpm, work, "ghost.json", ghost)
    check("started anyway: fails up front and creates no list under some other name", r.code == 1 and runs_of(r) == [] and path_of(SRC_LIB + " - Copy")[0] is None
          and not any(l["title"] == SRC_LIB and l["path"] != SRC_LIB_PATH for l in cpm("site", "lists", "--site", SITE).data.get("lists", [])), r.out[:400])

    # The same site written three ways is one site: a list copied through each spelling is updated,
    # never duplicated.
    lcount = None
    for label, spelling in [("plain", SITE), ("with a trailing slash", SITE + "/"), ("in capitals with a query", SITE.replace("gocleverpointcom", "GOCLEVERPOINTCOM") + "/?x=1")]:
        spec = {"source": spelling, "target": spelling, "lists": [{"source": SRC_LIST, "targetTitle": target("Spell")}], "options": {"existing": "overwrite"}}
        r = start(cpm, work, "spell.json", spec)
        now = path_of(target("Spell"))[1].get("items")
        if lcount is None:
            lcount = now
        check(f"site URL {label}: completes, and the list still has its original item count", r.code == 0 and now == lcount and now, f"{lcount} -> {now}; {r.out[:200]}")

    # ---- awkward names ----
    r = start(cpm, work, "odd.json", lib_job("Odd", lists=[{"source": ODD_LIB, "targetTitle": target("Odd & Co's \"lib\"")}]))
    run = (runs_of(r) or [{}])[0]
    check("a library with & and ' in its name, to a target with quotes: completes", r.code == 0 and run.get("failed") == 0 and run.get("processed", 0) >= 10, r.out[:400])

    # ---- selections ----
    r = start(cpm, work, "folder.json", lib_job("Folder", lists=[{"source": SRC_LIB, "targetTitle": target("Folder"), "sourceFolder": SRC_LIB_PATH + "/Folder-A", "targetFolder": "in/deep ünï"}]))
    run = (runs_of(r) or [{}])[0]
    tpath, _ = path_of(target("Folder"))
    listing, names = files_in(tpath) if tpath else (None, {})
    deep = cpm("site", "folder", "--site", SITE, "--list", tpath, "--folder", f"{tpath}/in/deep ünï") if tpath else None
    deep_names = {e["name"] for e in deep.data.get("entries", [])} if deep else set()
    check("sourceFolder + targetFolder: the folder's contents land in the target folder, without the folder itself",
          r.code == 0 and run.get("failed") == 0 and "in" in names and {"Sub-1", "doc-02.bin"} <= deep_names and "Folder-A" not in deep_names, f"{run} top={names} deep={deep_names}")
    r = start(cpm, work, "paths.json", lib_job("Paths", lists=[{"source": SRC_LIB, "targetTitle": target("Paths"),
                                                             "paths": [SRC_LIB_PATH + "/doc-01.bin", SRC_LIB_PATH + "/Folder-B"]}]))
    tpath, _ = path_of(target("Paths"))
    _, names = files_in(tpath) if tpath else (None, {})
    check("paths: exactly the picked file and folder arrive", r.code == 0 and set(names) == {"doc-01.bin", "Folder-B"}, names)
    r = start(cpm, work, "pattern.json", lib_job("Pattern", lists=[{"source": SRC_LIB, "targetTitle": target("Pattern"), "namePatterns": ["doc-0*.bin"]}]))
    tpath, _ = path_of(target("Pattern"))
    _, names = files_in(tpath) if tpath else (None, {})
    root_files = {n for n, t in names.items() if t == "file"}
    check("namePatterns: only matching files at the root", r.code == 0 and root_files == {"doc-01.bin", "doc-05.bin", "doc-09.bin"}, names)
    r = start(cpm, work, "nomatch.json", lib_job("NoMatch", lists=[{"source": SRC_LIB, "targetTitle": target("NoMatch"), "namePatterns": ["*.nothing"]}]))
    run = (runs_of(r) or [{}])[0]
    check("a pattern that matches nothing: completes cleanly with no files", r.code == 0 and run.get("failed") == 0, run)
    r = start(cpm, work, "future.json", lib_job("Future", options={"modifiedSince": "2099-01-01T00:00:00Z"}))
    run = (runs_of(r) or [{}])[0]
    tpath, tinfo = path_of(target("Future"))
    _, names = files_in(tpath) if tpath else (None, {})
    check("a date filter in the future: completes, copies no file", r.code == 0 and run.get("failed") == 0 and not any(t == "file" for t in names.values()), f"{run} {names}")

    # ---- lists ----
    r = start(cpm, work, "list.json", lib_job("List", lists=[{"sourcePath": SRC_LIST_PATH, "targetTitle": target("List"), "targetUrl": "Lists/" + target("List").replace("-", "")}]))
    run = (runs_of(r) or [{}])[0]
    check("a generic list copies, found by path", r.code == 0 and run.get("failed") == 0 and path_of(target("List"))[1].get("type") == "list", r.out[:300])
    count = path_of(target("List"))[1].get("items")
    r = start(cpm, work, "list.json", lib_job("List", lists=[{"sourcePath": SRC_LIST_PATH, "targetTitle": target("List")}], options={"existing": "copyIfNewer"}))
    check("copying the list again updates items, never duplicates them", r.code == 0 and path_of(target("List"))[1].get("items") == count, f"{count} -> {path_of(target('List'))[1].get('items')}")
    source_items = cpm("site", "folder", "--site", SITE, "--list", SRC_LIST_PATH, "--limit", "500").data.get("entries", [])
    in_folder = next((e for e in source_items if "(in folder)" in e["name"]), None)
    at_root = next((e for e in source_items if "(root)" in e["name"]), None)
    if check("the fixture list has an item in a folder and one at the root", in_folder and at_root, [e["name"] for e in source_items][:6]):
        r = start(cpm, work, "ids.json", lib_job("Ids", lists=[{"source": SRC_LIST, "targetTitle": target("Ids"), "itemIds": [in_folder["itemId"], at_root["itemId"]]}]))
        run = (runs_of(r) or [{}])[0]
        tpath, _ = path_of(target("Ids"))
        arrived = [e["name"] for e in cpm("site", "folder", "--site", SITE, "--list", tpath or "", "--limit", "500").data.get("entries", [])]
        check("itemIds: exactly the picked items arrive, including the one that lives in a folder",
              r.code == 0 and run.get("failed") == 0 and sorted(n for n in arrived if n.startswith("Item ")) == sorted([in_folder["name"], at_root["name"]]), f"{arrived} {run}")
        check("a new plain list is created under Lists/ by default", (tpath or "").startswith(WEB + "/Lists/"), tpath)

    r = start(cpm, work, "empty.json", lib_job("Empty", lists=[{"source": "Documents", "targetTitle": target("Empty")}]))
    run = (runs_of(r) or [{}])[0]
    check("an empty library: completes with nothing to do and no percent", r.code == 0 and run.get("processed") == 0 and run.get("percent") is None and run.get("failed") == 0, run)

    r = start(cpm, work, "twice.json", lib_job("Twice", lists=[{"source": SRC_LIB, "targetTitle": target("Twice")}, {"source": SRC_LIB, "targetTitle": target("Twice")}]))
    check("the same list twice in one job: two runs, both fine", r.code == 0 and len(runs_of(r)) == 2 and all(x["failed"] == 0 for x in runs_of(r)) and r.data.get("listsTotal") == 2, r.out[:400])

    # ---- identity mapping ----
    mapdir = os.path.join(work, "mapping job")
    write(os.path.join(mapdir, "map.csv"), "Type,Source,Target\nUser,nobody@example.com,someone@example.com\nGroup,Old Group,New Group\n")
    spec = lib_job("Mapped", identityMapping={"csv": "map.csv", "users": {"a@example.com": "b@example.com"}, "fallback": "", "useSaved": False})
    path = job_file(mapdir, "mapped.json", spec)
    v = cpm("job", "validate", path, "--online", cwd=ROOT_FOLDER)
    m = v.data.get("identityMapping", {})
    check("a mapping CSV named relative to the job file is found, whatever the current folder", v.ok and m.get("users") == 2 and m.get("groups") == 1, v.out[:400])
    r = cpm("job", "start", path, "--wait", "--timeout", "300", cwd=ROOT_FOLDER)
    check("and the background job finds it too", r.code == 0 and r.data.get("status") == "Completed", r.out[:400])

    # The mapping has to change who the target says created a file, with either engine. The fixture
    # has files by Alexa Willams and by Denis Molodtsov; Alexa is mapped to Bo Jackson.
    people = {p["name"]: p for p in cpm("site", "users", "--site", SITE, "--limit", "2000").data.get("principals", [])}
    alexa, bo = people.get("Alexa Willams"), people.get("Bo Jackson")
    _, basic_info = path_of(target("Basic"))
    created = {e["name"]: e.get("createdBy") for e in cpm("site", "folder", "--site", SITE, "--list", basic_info.get("path", "")).data.get("entries", [])}
    check("without a mapping, the copy keeps the source's authors", created.get("doc-01.bin") == "Alexa Willams" and created.get("doc-05.bin") == "Denis Molodtsov", created)
    if check("the fixture users exist", alexa and bo, list(people)[:10]):
        for engine in ("classic", "migrationApi"):
            name = "Map" + engine
            spec = lib_job(name, engine=engine, identityMapping={"users": {alexa.get("email") or alexa["signIn"]: bo["signIn"]}, "useSaved": False})
            r = start(cpm, work, name + ".json", spec, timeout=900)
            tpath, _ = path_of(target(name))
            created = {e["name"]: e.get("createdBy") for e in cpm("site", "folder", "--site", SITE, "--list", tpath or "").data.get("entries", [])}
            check(f"identity mapping is applied by the {engine} engine: Alexa's files now say Bo",
                  r.code == 0 and created.get("doc-01.bin") == "Bo Jackson" and created.get("Folder-A") == "Bo Jackson", f"{created} {r.out[:200]}")
            check(f"and files by unmapped users keep their author ({engine})", created.get("doc-05.bin") == "Denis Molodtsov", created)

    r = start(cpm, work, "api-folder.json", lib_job("ApiFolder", engine="migrationApi",
              lists=[{"source": SRC_LIB, "targetTitle": target("ApiFolder"), "sourceFolder": SRC_LIB_PATH + "/Folder-A"}]), timeout=900)
    tpath, _ = path_of(target("ApiFolder"))
    _, names = files_in(tpath) if tpath else (None, {})
    check("Migration API from inside a folder: same layout as the classic engine (contents at the top)",
          r.code == 0 and {"Sub-1", "doc-02.bin"} <= set(names) and "Folder-A" not in names, f"{names} {r.out[:300]}")

    # Asking the Migration API engine for something it cannot do switches that list to the classic
    # engine, visibly: in the plan before the run, on the run itself, and as the first line of its log.
    spec = lib_job("ApiVersions", engine="migrationApi", options={"versions": 3, "existing": "copyIfNewer"})
    v = cpm("job", "validate", job_file(work, "api-versions.json", spec), "--online")
    plan = (v.data.get("lists") or [{}])[0]
    check("the plan shows the classic engine will be used and why", v.ok and plan.get("engine") == "Classic" and "version" in (plan.get("engineNote") or ""), v.out[:400])
    r = start(cpm, work, "api-versions.json", spec)
    run = (runs_of(r) or [{}])[0]
    check("the run used the classic engine and completed", r.code == 0 and run.get("engine") == "Classic" and run.get("failed") == 0, r.out[:300])
    notes = [i.get("message") or "" for i in cpm("run", "items", run.get("runId"), "--status", "info", "--limit", "20").data.get("items", [])]
    check("the run log says why the engine was switched", any("Using the Classic engine instead" in n and "version" in n for n in notes), notes[:3])
    v = cpm("job", "validate", job_file(work, "api-plain.json", lib_job("ApiPlain", engine="migrationApi")), "--online")
    plan = (v.data.get("lists") or [{}])[0]
    check("a job the Migration API can do is planned with it, without a note", plan.get("engine") == "MigrationApi" and plan.get("engineNote") is None, v.out[:300])

    # ---- engines ----
    r = start(cpm, work, "api.json", lib_job("Api", engine="migrationApi", lists=[{"source": SRC_LIB, "targetTitle": target("Api")}, {"source": SRC_LIST, "targetTitle": target("ApiList")}]), timeout=900)
    engines = [x.get("engine") for x in runs_of(r)]
    check("Migration API job: the library uses it, the plain list falls back to classic", engines == ["MigrationApi", "Classic"], r.out[:500])
    check("both complete without failures", r.code == 0 and all(x["failed"] == 0 for x in runs_of(r)), r.out[:500])
    check("the library arrived complete through the Migration API", path_of(target("Api"))[1].get("items") == 15, path_of(target("Api"))[1])

    # ---- another tenant ----
    cross = {"name": "Cross tenant", "source": SITE, "target": LAB_SITE,
             "lists": [{"source": SRC_LIST, "targetTitle": target("XTenant")}],
             "identityMapping": {"useSaved": False}}
    v = cpm("job", "validate", job_file(work, "cross.json", cross), "--online")
    check("cross-tenant: validates online against both tenants", v.ok and (v.data.get("lists") or [{}])[0].get("targetState") == "willBeCreated", v.out[:400])
    r = start(cpm, work, "cross.json", cross)
    run = (runs_of(r) or [{}])[0]
    check("cross-tenant list copy completes without failed items", r.data.get("status") in ("Completed", "CompletedWithIssues") and run.get("failed") == 0 and run.get("processed", 0) >= 20, r.out[:400])
    alll = {"source": SITE, "target": LAB_SITE, "allLists": True, "excludeLists": [SRC_LIB, SRC_LIST_PATH]}
    v = cpm("job", "validate", job_file(work, "all.json", alll), "--online", timeout=900)
    planned = [l["source"] for l in v.data.get("lists", [])]
    check("allLists: plans every list except the excluded ones (by title and by path)", v.ok and len(planned) > 20 and SRC_LIB not in planned and SRC_LIST not in planned and BIG_LIB in planned, f"{len(planned)} lists; {v.out[:200]}")


# ----------------------------------------------------------------------------------------------
# life: the lifecycle of a job.
# ----------------------------------------------------------------------------------------------
def test_life(cpm, work):
    def big(name, extra=None):
        lists = [{"source": BIG_LIB, "targetTitle": f"CliT-{name}-{RUN}"}] + (extra or [])
        return {"name": name, "source": SITE, "target": SITE, "lists": lists, "options": {"parallelFileTransfers": 1}}

    def status(job_id):
        return cpm("job", "status", job_id)

    def wait_until(predicate, seconds=90):
        deadline = time.time() + seconds
        while time.time() < deadline:
            if predicate():
                return True
            time.sleep(1)
        return False

    def kill(pid):
        if WINDOWS:
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
        else:
            os.kill(pid, signal.SIGKILL)

    def exec_pid(job_id):
        """The process ID of the background process executing a job, or None."""
        if WINDOWS:
            query = ("Get-CimInstance Win32_Process -Filter \"Name='cpm.exe'\" | "
                     f"Where-Object {{ $_.CommandLine -like '*job exec {job_id}*' }} | ForEach-Object {{ $_.ProcessId }}")
            out = subprocess.run(["powershell", "-NoProfile", "-Command", query], capture_output=True, text=True).stdout.split()
            return int(out[0]) if out else None
        for pid in os.listdir("/proc"):
            if not pid.isdigit():
                continue
            try:
                command = open(f"/proc/{pid}/cmdline", "rb").read().decode("utf-8", "replace").split("\0")
            except OSError:
                continue
            if "exec" in command and job_id in command:
                return int(pid)
        return None

    # ---- start returns at once; states only move forward ----
    t0 = time.time()
    r = start(cpm, work, "life1.json", big("Life1", [{"source": SRC_LIB, "targetTitle": f"CliT-Life1b-{RUN}"}]), wait=False)
    took = time.time() - t0
    job = r.data.get("jobId")
    check("job start returns in a few seconds with a job ID and the next command", r.ok and took < 8 and job and job in r.data.get("next", ""), f"{took:.1f}s {r.out}")
    seen = [status(job).data.get("status") for _ in range(4)]
    check("a job that was just started is never reported as interrupted", set(seen) <= {"Queued", "Running"}, seen)
    check("status exits 0 while the job is running", status(job).code == 0)
    check("the job reaches Running with a live run", wait_until(lambda: any(x["status"] == "Running" for x in runs_of(status(job)))), status(job).out[:300])
    check("progress numbers appear while it runs", wait_until(lambda: (runs_of(status(job)) or [{}])[0].get("planned") == 425 and (runs_of(status(job)) or [{}])[0].get("processed", 0) > 0, 120), status(job).out[:400])
    r = cpm("job", "wait", job, "--timeout", "2")
    check("wait that runs out: exit 6, timedOut, job untouched", r.code == 6 and r.data.get("timedOut") is True and r.data.get("job", {}).get("status") == "Running", r.out[:300])
    r = cpm("job", "list")
    check("job list shows it running", any(j["jobId"] == job and j["status"] == "Running" for j in r.data.get("jobs", [])), r.out[:300])
    r = cpm("report", "verify", (runs_of(status(job)) or [{}])[0].get("runId"))
    check("verifying a run that is still going is refused", r.code == 5, r.out[:200])

    # ---- cancel from another process ----
    r = cpm("job", "cancel", job)
    check("cancel is accepted", r.ok and r.data.get("cancelRequested") is True, r.out)
    r = finish(cpm, job, 180)
    runs = runs_of(r)
    check("the job ends Cancelled, exit 7", r.code == 7 and r.data.get("status") == "Cancelled", r.out[:400])
    check("its run is Interrupted and kept what it had done", len(runs) == 1 and runs[0]["status"] == "Interrupted" and runs[0]["processed"] > 0, runs)
    check("the list after it was never started", r.data.get("listsStarted") == 1 and r.data.get("listsTotal") == 2, r.out[:300])
    check("cancelling again says it is already cancelled", cpm("job", "cancel", job).data.get("cancelRequested") is False)
    check("no process is left behind", wait_until(lambda: exec_pid(job) is None, 30))
    processed = runs[0]["processed"]
    time.sleep(3)
    check("a cancelled run does not keep counting", (runs_of(status(job)) or [{}])[0].get("processed") == processed)

    # ---- cancel before the background process has even started ----
    r = start(cpm, work, "life2.json", big("Life2"), wait=False)
    job = r.data.get("jobId")
    c = cpm("job", "cancel", job)
    r = finish(cpm, job, 120)
    check("cancel straight after start: ends Cancelled, never hangs", c.ok and r.data.get("status") == "Cancelled" and r.code == 7, r.out[:300])
    check("and at most one barely-started run exists", len(runs_of(r)) <= 1 and all(x["status"] == "Interrupted" for x in runs_of(r)), runs_of(r))

    # ---- the background process dies ----
    r = start(cpm, work, "life3.json", big("Life3"), wait=False)
    job = r.data.get("jobId")
    wait_until(lambda: (runs_of(status(job)) or [{}])[0].get("processed", 0) > 2, 120)
    pid = exec_pid(job)
    check("the background process can be found", pid is not None)
    if pid:
        kill(pid)
        wait_until(lambda: exec_pid(job) is None, 20)
        r = status(job)
        check("after the process is killed, status says Interrupted (exit 7), not Running forever",
              r.code == 7 and r.data.get("status") == "Interrupted" and all(x["status"] == "Interrupted" for x in runs_of(r)), r.out[:400])
        check("wait on it returns at once", cpm("job", "wait", job, "--timeout", "30").code == 7)
        r2 = cpm("job", "exec", job, "--foreground")
        check("re-executing a dead job does nothing", status(job).data.get("status") == "Interrupted" and len(runs_of(status(job))) == len(runs_of(r)), r2.out[:200])

    # ---- cancelling one run of a job: the job moves on ----
    r = start(cpm, work, "life4.json", big("Life4", [{"source": SRC_LIB, "targetTitle": f"CliT-Life4b-{RUN}"}]), wait=False)
    job = r.data.get("jobId")
    wait_until(lambda: (runs_of(status(job)) or [{}])[0].get("processed", 0) > 2, 120)
    first = (runs_of(status(job)) or [{}])[0].get("runId")
    c = cpm("run", "cancel", first)
    r = finish(cpm, job, 300)
    statuses = [x["status"] for x in runs_of(r)]
    check("run cancel stops that run only; the job carries on with the next list",
          c.data.get("cancelRequested") is True and statuses == ["Interrupted", "Completed"] and r.data.get("status") == "CompletedWithIssues" and r.code == 1, r.out[:400])

    # ---- many readers and two writers on one history database ----
    a = start(cpm, work, "conc-a.json", {"source": SITE, "target": SITE, "lists": [{"source": SRC_LIB, "targetTitle": f"CliT-ConcA-{RUN}"}]}, wait=False).data.get("jobId")
    b = start(cpm, work, "conc-b.json", {"source": SITE, "target": SITE, "lists": [{"source": SRC_LIST, "targetTitle": f"CliT-ConcB-{RUN}"}]}, wait=False).data.get("jobId")
    bad = []

    def hammer(args):
        for _ in range(12):
            x = cpm(*args)
            if not x.ok:
                bad.append((args, x.out[:200] + x.err[:200]))

    threads = [threading.Thread(target=hammer, args=(args,)) for args in
               (["run", "list"], ["job", "status", a], ["job", "status", b], ["job", "list"], ["run", "list", "--limit", "500"], ["job", "status", a])]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    check("72 status reads in 6 threads while two jobs write: every one answers", not bad, bad[:3])
    ra, rb = finish(cpm, a, 300), finish(cpm, b, 300)
    check("two jobs at the same time both complete", ra.code == 0 and rb.code == 0 and ra.data.get("status") == "Completed" and rb.data.get("status") == "Completed", ra.out[:200] + rb.out[:200])
    check("each job only shows its own runs", [x["source"] for x in runs_of(ra)] == [SRC_LIB] and [x["source"] for x in runs_of(rb)] == [SRC_LIST])
    again = cpm("job", "exec", a, "--foreground")
    check("executing a finished job again changes nothing", len(runs_of(status(a))) == 1 and status(a).data.get("status") == "Completed", again.out[:200])

    # ---- cancelling a Migration API job ----
    spec = big("LifeApi")
    spec["engine"] = "migrationApi"
    r = start(cpm, work, "life-api.json", spec, wait=False)
    job = r.data.get("jobId")
    wait_until(lambda: any(x["status"] == "Running" for x in runs_of(status(job))), 60)
    time.sleep(15)      # into reading and packaging
    t0 = time.time()
    cpm("job", "cancel", job)
    r = finish(cpm, job, 240)
    check("a Migration API job stops when cancelled, within a few minutes", r.data.get("status") == "Cancelled" and r.code == 7 and time.time() - t0 < 200,
          f"{time.time() - t0:.0f}s {r.out[:300]}")
    check("its run is Interrupted, not Completed", [x["status"] for x in runs_of(r)] == ["Interrupted"], runs_of(r))

    # ---- start --wait with a timeout ----
    r = start(cpm, work, "life5.json", big("Life5"), wait=True, timeout=3)
    job = (r.data.get("job") or {}).get("jobId")
    check("start --wait that runs out: exit 6 and the job ID is still there", r.code == 6 and r.data.get("timedOut") is True and job, r.out[:300])
    if job:
        cpm("job", "cancel", job)
        check("that job can then be cancelled normally", finish(cpm, job, 120).data.get("status") == "Cancelled")

    # ---- the job's log ----
    log = status(a).data.get("log")
    text = open(log, encoding="utf-8").read() if log and os.path.exists(log) else ""
    check("each job has a log file that says how it ended", "finished: Completed" in text, text[-300:])
    check("the log is only the job's own lines", not text.lstrip().startswith("{") and '"ok"' not in text, text[-300:])


def report_process(run_id):
    """The process ID of the background process executing a report run, or None."""
    if WINDOWS:
        query = ("Get-CimInstance Win32_Process -Filter \"Name='cpm.exe'\" | "
                 f"Where-Object {{ $_.CommandLine -like '*report exec {run_id}*' }} | ForEach-Object {{ $_.ProcessId }}")
        out = subprocess.run(["powershell", "-NoProfile", "-Command", query], capture_output=True, text=True).stdout.split()
        return int(out[0]) if out else None
    for pid in os.listdir("/proc"):
        if not pid.isdigit():
            continue
        try:
            command = open(f"/proc/{pid}/cmdline", "rb").read().decode("utf-8", "replace").split("\0")
        except OSError:
            continue
        if "exec" in command and run_id in command:
            return int(pid)
    return None


def kill_process(pid):
    if WINDOWS:
        subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
    else:
        os.kill(pid, signal.SIGKILL)


REPORT_IDS = ["sites", "onedrives", "lists", "storage", "version-limits", "set-version-limits", "documents", "file-versions", "permissions", "guests", "site-access", "subsites",
              "publishing", "customized-lists", "custom-columns", "broken-notebooks", "onedrive-lists", "onedrive-pages", "site-app-catalogs",
              "list-compare", "document-compare", "navigation-compare", "permissions-compare",
              "site-lock", "site-admins", "nav-sync", "fix-notebooks"]
ACTION_IDS = ["site-lock", "site-admins", "nav-sync", "fix-notebooks", "set-version-limits"]


def test_report_offline(cpm, work):
    """Everything about reports that needs no network: the catalog, the file format, what is refused."""
    r = cpm("report", "list")
    listed = {x["id"]: x for x in r.data.get("reports", [])}
    check("report list names every report", r.ok and all(i in listed for i in REPORT_IDS), list(listed))
    check("each report says what it runs against", all(x.get("scope") in ("sites", "tenant", "sitePairs") and x.get("summary") for x in listed.values()), r.out[:300])
    check("tenant-wide reports are marked as needing the admin site", listed.get("sites", {}).get("needsTenantAdmin") is True and "needsTenantAdmin" not in listed.get("lists", {}))

    r = cpm("report", "list", "--report", "lists")
    table = (r.data.get("tables") or [{}])[0]
    names = [c["name"] for c in table.get("columns", [])]
    check("one report in full: settings with defaults and help", r.ok and {s["name"] for s in r.data.get("settings", [])} == {"includeHidden", "includeSubsites"}
          and all("help" in s and "default" in s for s in r.data["settings"]), r.out[:400])
    check("its columns start with the site every row carries", names[:1] == ["site"] and "listTitle" in names and "itemCount" in names, names)
    check("column types are given", {c["name"]: c["type"] for c in table.get("columns", [])}.get("itemCount") == "number", table)
    r = cpm("report", "list", "--report", "nope")
    check("an unknown report: not_found, with the real ones in the hint", r.code == 4 and "lists" in r.error.get("hint", ""), r.out)

    r = cpm("report", "template", "lists")
    check("template: the report, example sites and its settings at their defaults",
          r.ok and r.data.get("report") == "lists" and len(r.data.get("sites", [])) == 2 and r.data.get("options") == {"includeHidden": False, "includeSubsites": False}, r.out)
    r = cpm("report", "template", "sites")
    check("template of a tenant-wide report asks for a tenant, not sites", r.ok and "tenant" in r.data and "sites" not in r.data, r.out)
    r = cpm("report", "template", "lists", "--raw")
    try:
        raw = json.loads(r.out)
    except ValueError:
        raw = {}
    check("--raw prints the file itself", raw.get("report") == "lists" and "ok" not in raw, r.out[:200])
    check("template of an unknown report", cpm("report", "template", "nope").code == 4)

    r = cpm("schema", "report")
    props = r.data.get("properties", {})
    check("schema report describes the file", r.ok and all(k in props for k in ("report", "sites", "sitesFile", "allSites", "pairs", "tenant", "options", "parallel", "apply"))
          and props["report"].get("description"), list(props))

    # What is refused before anything runs.
    site = "https://contoso.sharepoint.com/sites/Finance"
    r = cpm("report", "start")
    check("start with nothing: usage", r.code == 2 and "report list" in r.error.get("hint", ""), r.out)
    r = cpm("report", "start", "--report", "nope", "--sites", site)
    check("start of an unknown report: invalid", r.code == 5 and "nope" in r.error.get("message", ""), r.out)
    r = cpm("report", "start", "--report", "lists")
    check("no sites given: says which options give them", r.code == 5 and "sitesFile" in r.error.get("message", ""), r.out)
    r = cpm("report", "start", "--report", "lists", "--sites", "contoso")
    check("a site that is not a URL", r.code == 5 and "not a site URL" in r.error.get("message", ""), r.out)
    r = cpm("report", "start", "--report", "lists", "--sites", site + "/SitePages/Home.aspx")
    check("a page URL instead of a site", r.code == 5 and "page" in r.error.get("message", ""), r.out)
    r = cpm("report", "start", "--report", "sites", "--sites", site)
    check("a tenant-wide report given sites: both problems named", r.code == 5 and len(r.error.get("details", [])) >= 2, r.out)
    r = cpm("report", "start", "--report", "lists", "--sites", site, "--tenant", "https://contoso.sharepoint.com")
    check("a per-site report given a tenant is not silently accepted", r.code in (4, 5), r.out)
    r = cpm("report", "start", "--report", "lists", "--sites", site, "--set", "bogus=true")
    check("an unknown setting: names the real ones", r.code == 5 and "includeHidden" in json.dumps(r.error), r.out)
    r = cpm("report", "start", "--report", "lists", "--sites", site, "--set", "includeHidden=maybe")
    check("a setting of the wrong type", r.code == 5 and "true or false" in json.dumps(r.error), r.out)
    r = cpm("report", "start", "--report", "lists", "--sites", site, "--set", "includeHidden")
    check("--set without a value: usage", r.code == 2, r.out)
    r = cpm("report", "start", "--report", "lists", "--sites", site, "--parallel", "99")
    check("parallel out of range", r.code == 5 and "parallel" in json.dumps(r.error), r.out)
    r = cpm("report", "start", "--report", "lists", "--sites", site, "--apply")
    check("apply on a report that only reads", r.code == 5 and "apply" in json.dumps(r.error), r.out)
    r = cpm("report", "start", "--report", "lists", "--sites", site, "--type", "onedrive")
    check("--type without --all-sites: usage", r.code == 2, r.out)
    r = cpm("report", "start", "--report", "lists", "--sites", site, "--sites-file", os.path.join(work, "x.txt"))
    check("two ways of giving sites at once", r.code == 5 and "not both" in json.dumps(r.error), r.out)
    r = cpm("report", "start", "--report", "lists", "--sites-file", os.path.join(work, "missing.txt"))
    check("a sites file that does not exist", r.code == 5 and "does not exist" in json.dumps(r.error), r.out)
    r = cpm("report", "start", "--report", "lists", "--sites", site)
    check("no connection for the site: not_found with the command to add one", r.code == 4 and "connection add" in r.error.get("hint", ""), r.out)

    # Compare reports take pairs; actions change sites and say so.
    check("actions, and only actions, are marked as changing sites", {i for i, x in listed.items() if x.get("changesData")} == set(ACTION_IDS),
          {i for i, x in listed.items() if x.get("changesData")})
    r = cpm("report", "start", "--report", "list-compare", "--sites", site)
    check("a compare report given single sites: asks for pairs", r.code == 5 and "pairs" in json.dumps(r.error), r.out)
    r = cpm("report", "start", "--report", "list-compare", "--source", site)
    check("--source without --target: usage", r.code == 2, r.out)
    r = cpm("report", "template", "document-compare")
    check("the template of a compare report has a pair", r.ok and r.data.get("pairs", [{}])[0].get("source") and r.data["pairs"][0].get("target"), r.out)
    r = cpm("report", "template", "site-lock")
    check("the template of an action carries apply: false", r.ok and r.data.get("apply") is False, r.out)
    r = cpm("report", "list", "--report", "site-lock")
    state = next((x for x in r.data.get("settings", []) if x["name"] == "state"), {})
    check("a setting with fixed values lists them", state.get("choices") == ["ReadOnly", "NoAccess", "Unlock"], r.out[:300])
    r = cpm("report", "validate", "--report", "site-lock", "--sites", site)
    check("an action whose required setting is missing is refused before it starts", r.code == 5 and "ReadOnly" in json.dumps(r.error), r.out)
    r = cpm("report", "validate", "--report", "site-lock", "--sites", site, "--set", "state=Frozen")
    check("a value outside the fixed ones is refused", r.code == 5 and "one of" in json.dumps(r.error), r.out)
    r = cpm("report", "validate", "--report", "site-lock", "--sites", site, "--set", "state=readonly")
    check("an action validates as a preview unless told to apply", r.ok and "Preview" in (r.data.get("changes") or ""), r.out)
    r = cpm("report", "validate", "--report", "site-lock", "--sites", site, "--set", "state=ReadOnly", "--apply")
    check("with --apply the validation says it changes sites", r.ok and "CHANGES" in (r.data.get("changes") or ""), r.out)
    r = cpm("report", "validate", "--report", "site-admins", "--sites", site)
    check("site-admins with nobody to add or remove", r.code == 5 and "add or remove" in json.dumps(r.error), r.out)
    r = cpm("report", "validate", "--report", "site-admins", "--sites", site, "--set", "add=a@contoso.com;b@contoso.com,remove=B@contoso.com")
    check("site-admins with the same account in add and remove", r.code == 5 and "both" in json.dumps(r.error), r.out)
    check("report apply of an unknown run: not_found", cpm("report", "apply", "r000000-nope00").code == 4)

    # Report files.
    r = cpm("report", "validate", os.path.join(work, "no-such-report.json"))
    check("a report file that does not exist", r.code == 4, r.out)
    r = cpm("report", "validate", write(os.path.join(work, "bad.json"), "{ not json"))
    check("a report file that is not JSON", r.code == 5, r.out)
    r = cpm("report", "validate", write(os.path.join(work, "typo.json"), json.dumps({"report": "lists", "sites": [site], "optoins": {}})))
    check("a misspelled property is refused, not ignored", r.code == 5 and "optoins" in json.dumps(r.error), r.out)
    r = cpm("report", "validate", write(os.path.join(work, "v9.json"), json.dumps({"schemaVersion": 9, "report": "lists", "sites": [site]})))
    check("an unknown file version", r.code == 5 and "schemaVersion" in json.dumps(r.error), r.out)

    sites_file = write(os.path.join(work, "reports", "sites.csv"),
                       "﻿Site URL,Owner\r\n" + site + ",someone\r\n" + site + "/\r\n\r\n# a comment\r\nnot a url\r\n"
                       "https://contoso.sharepoint.com/sites/HR?x=1,other\r\n")
    spec = write(os.path.join(work, "reports", "lists.json"), json.dumps({"report": "lists", "sitesFile": "sites.csv", "options": {"includeHidden": True}}))
    r = cpm("report", "validate", spec, cwd=ROOT_FOLDER)
    check("a sites file beside the report file is found from any folder; header, blanks, comments and repeats are dealt with",
          r.ok and r.data.get("sites") == 2 and any("skipped" in n for n in r.data.get("notes") or []), r.out)
    r = cpm("report", "validate", "--report", "lists", "--sites-file", sites_file)
    check("validate takes options instead of a file", r.ok and r.data.get("sites") == 2, r.out)
    r = cpm("report", "validate", "--report", "lists", "--sites-file", write(os.path.join(work, "empty.txt"), "Site\n\n"))
    check("a sites file with no URLs in it", r.code == 5 and "no site URLs" in json.dumps(r.error), r.out)

    # Runs that do not exist.
    for command in (["status"], ["wait"], ["cancel"], ["resume"], ["sites"], ["rows"], ["export"], ["delete"]):
        r = cpm("report", *command, "r000000-nope00")
        check(f"report {command[0]} of an unknown run: not_found", r.code == 4 and "report runs" in r.error.get("hint", ""), r.out)
    r = cpm("report", "runs")
    check("report runs on a new settings folder: empty", r.ok and r.data.get("runs") == [], r.out)
    r = cpm("report", "--help")
    check("report --help lists the report commands and not the hidden one", "report start" in r.out and "report rows" in r.out and "report exec" not in r.out, r.out[:300])


def test_report(cpm, work):
    """Reports against the sandbox: every report, many sites, failures in the middle, cancel, kill, resume, export."""
    def run(*args, timeout=600):
        return cpm("report", "start", *args, "--wait", "--timeout", timeout, timeout=timeout + 60)

    def rows(run_id, *args):
        return cpm("report", "rows", run_id, *args, "--limit", "1000").data.get("rows", [])

    def one_row(run_id, table=None):
        got = rows(run_id, *(["--table", table] if table else []))
        return got[0] if got else {}

    bogus = SOURCE_SITE.rsplit("/", 1)[0] + "/no-such-site-" + RUN

    # --- tenant-wide ---
    r = run("--report", "sites", "--tenant", SITE)
    sites_run = r.data.get("runId")
    total = r.data.get("rows", 0)
    check("sites: every site collection of the tenant, from any site of it", r.code == 0 and r.data.get("status") == "Completed" and total > 5
          and r.data.get("summary", {}).get("sites") == total, r.out[:500])
    check("a tenant-wide run reports no per-site counters", "sitesTotal" not in r.data, r.out[:300])
    page = cpm("report", "rows", sites_run, "--limit", "3")
    check("rows: a page, and where the next one starts", page.ok and page.data.get("returned") == 3 and page.data.get("more") is True and page.data.get("total") == total
          and page.data.get("next") == "--offset 3", page.out[:300])
    second = cpm("report", "rows", sites_run, "--limit", "3", "--offset", "3").data.get("rows", [])
    check("rows --offset continues without overlap", len(second) == 3 and not {x["url"] for x in second} & {x["url"] for x in page.data["rows"]}, second)
    found = rows(sites_run, "--where", "url=" + SOURCE_SITE.upper())
    check("rows --where finds one site, whatever the case", len(found) == 1 and found[0].get("template") and found[0].get("storageUsedBytes", 0) > 0, found)
    check("a site's lock state comes from the tenant, in words", found and found[0].get("lockState") == "Unlocked", found[:1])
    part = rows(sites_run, "--where", "url~DemoLargeSite", "--columns", "url,title")
    check("rows --columns returns only those", part and all(set(x) <= {"url", "title"} for x in part), part[:2])
    r = cpm("report", "rows", sites_run, "--where", "url~sharepoint", "--count")
    check("rows --count", r.ok and r.data.get("matching") == total, r.out)
    check("rows with a column the table does not have: usage, naming the real ones",
          cpm("report", "rows", sites_run, "--where", "nope=1").code == 2 and "url" in cpm("report", "rows", sites_run, "--columns", "nope").error.get("hint", ""))
    check("rows with a table the report does not have", cpm("report", "rows", sites_run, "--table", "nope").code == 4)
    check("rows with a malformed filter", cpm("report", "rows", sites_run, "--where", "justtext").code == 2)

    r = run("--report", "onedrives", "--tenant", SOURCE_SITE)
    check("onedrives: completes and counts what it lists", r.code == 0 and r.data.get("summary", {}).get("onedrives", 0) == r.data.get("rows"), r.out[:400])

    # --- per site, with a site that does not exist in the middle ---
    r = run("--report", "lists", "--sites", ",".join([SITE, bogus, SOURCE_SITE, LAB_SITE]), "--name", "CliT lists " + RUN)
    lists_run = r.data.get("runId")
    check("a run with one bad site among good ones: completed with issues, exit 1", r.code == 1 and r.data.get("status") == "CompletedWithIssues"
          and (r.data.get("sitesTotal"), r.data.get("sitesDone"), r.data.get("sitesFailed")) == (4, 3, 1), r.out[:500])
    check("the failure is counted by kind and resume is offered", r.data.get("failures") == {"notFound": 1} and r.data.get("canResume") is True, r.out[:500])
    failed = cpm("report", "sites", lists_run, "--failed").data.get("sites", [])
    check("report sites --failed says which site and why", len(failed) == 1 and failed[0]["site"] == bogus and failed[0]["status"] == "notFound" and "404" in failed[0]["error"], failed)
    every = cpm("report", "sites", lists_run).data
    check("report sites lists every site with its row count", every.get("total") == 4 and sum(s["rows"] for s in every["sites"]) == r.data.get("rows"), every)
    mine = rows(lists_run, "--site", SITE + "/")
    seen = cpm("site", "lists", "--site", SITE).data.get("lists", [])
    check("lists agrees with `site lists` on the test site", mine and {x["title"] for x in seen} <= {x["listTitle"] for x in mine},
          sorted({x["title"] for x in seen} - {x["listTitle"] for x in mine})[:5])
    library = next((x for x in mine if x["listTitle"] == SRC_LIB), {})
    check("a library row has its type, count, dates and versioning", library.get("listType") == "Document library" and library.get("itemCount", 0) > 0
          and library.get("created") and library.get("versioning") in ("Major", "Major and minor") and library.get("listUrl", "").startswith(SITE), library)
    check("rows of two tenants sit in one table", {x["site"] for x in rows(lists_run, "--columns", "site")} == {SITE, SOURCE_SITE, LAB_SITE})
    check("the run is listed with its name", any(x["runId"] == lists_run and x["name"] == "CliT lists " + RUN for x in cpm("report", "runs").data.get("runs", [])))

    r = run("--report", "lists", "--sites", SITE, "--set", "includeHidden=true")
    hidden = rows(r.data.get("runId"), "--where", "hidden=true")
    check("lists with includeHidden adds the hidden lists", r.code == 0 and len(hidden) > 0 and r.data.get("rows") > len(mine), f"{r.data.get('rows')} vs {len(mine)}")
    r = run("--report", "lists", "--sites", SOURCE_SITE, "--set", "includeSubsites=true")
    check("lists with includeSubsites also has the lists of the subsite", r.code == 0 and any(x.get("webUrl") == SITE for x in rows(r.data.get("runId"), "--where", "listTitle=" + SRC_LIB)), r.out[:300])

    r = run("--report", "subsites", "--sites", SOURCE_SITE)
    subs = rows(r.data.get("runId"))
    check("subsites finds the test subsite with its parent and level", r.code == 0 and any(x["subsiteUrl"] == SITE and x["parentUrl"] == SOURCE_SITE and x["level"] == 1 for x in subs), subs[:3])

    r = run("--report", "site-access", "--sites", ",".join([SITE, bogus]))
    access = {x["site"]: x for x in rows(r.data.get("runId"))}
    check("site-access never fails a site: the answer is the row", r.code == 0 and r.data.get("status") == "Completed" and access.get(SITE, {}).get("status") == "accessible"
          and access.get(bogus, {}).get("status") == "notFound" and r.data.get("summary") == {"accessible": 1, "notFound": 1}, r.out[:500])

    for report in ("publishing", "guests", "customized-lists", "custom-columns"):
        r = run("--report", report, "--sites", SITE)
        check(f"{report}: completes on the test site", r.code == 0 and r.data.get("status") == "Completed" and r.data.get("sitesDone") == 1, r.out[:400])
    r = run("--report", "customized-lists", "--sites", SITE, "--set", "customizedOnly=false")
    check("customized-lists with customizedOnly=false lists every list with its form type", r.code == 0 and r.data.get("rows") == r.data.get("summary", {}).get("lists")
          and all(x.get("formType") for x in rows(r.data.get("runId"))), r.out[:400])

    # --- the heavy scans: files, versions, permissions, notebooks ---
    def header(run_id, table):
        """The first line of the CSV export of one table."""
        out = os.path.join(work, "exports", f"{run_id}.csv")
        files = cpm("report", "export", run_id, "--format", "csv", "--out", out).data.get("files") or []
        match = [f for f in files if f.endswith(f"-{table}.csv")]
        return open(match[0], encoding="utf-8-sig").readline().strip() if match else ""

    r = run("--report", "documents", "--sites", SITE)
    docs_run = r.data.get("runId")
    files_total = r.data.get("summary", {}).get("files", 0)
    check("documents: a row per file, counted and sized", r.code == 0 and files_total > 20 and r.data.get("rows") == files_total
          and r.data["summary"].get("sizeBytes", 0) > 0 and r.data["summary"].get("libraries", 0) > 1, r.out[:400])
    mine = rows(docs_run, "--where", "library=" + SRC_LIB)
    one = next((x for x in mine if x.get("sizeBytes", 0) > 0), {})
    check("a file row has its library, size, people, dates, version and address",
          one.get("fileName") and one.get("created") and one.get("modified") and one.get("createdBy") and one.get("version")
          and one.get("fileUrl", "").startswith(SITE + "/") and one["fileUrl"].endswith(one["fileName"]), one)
    check("files in a folder say which folder", any(x.get("folder") for x in mine), [x.get("fileName") for x in mine][:5])
    check("the folder level columns stay out of the export unless asked for", "Level1" not in header(docs_run, "documents"), header(docs_run, "documents"))
    extension = one.get("extension") or "txt"
    r = run("--report", "documents", "--sites", SITE, "--set", f"extensions={extension};nosuchtype,folderLevels=true")
    only = rows(r.data.get("runId"), "--columns", "extension,level1,fileUrl")
    check("documents with extensions lists only those; a list setting takes values separated by semicolons",
          r.code == 0 and 0 < r.data.get("rows") < files_total and {x.get("extension") for x in only} == {extension}, r.out[:300])
    check("with folderLevels each row carries the library as level 1, and the export gains the columns",
          all(x.get("level1", "").startswith(SITE + "/") for x in only) and "Level1" in header(r.data.get("runId"), "documents"), only[:2])

    r = run("--report", "file-versions", "--sites", SITE)
    fv = rows(r.data.get("runId"), "--table", "files")
    check("file-versions: every file once, with what its versions weigh", r.code == 0 and r.data.get("summary", {}).get("files") == files_total
          and r.data["summary"].get("versions", 0) >= files_total and r.data.get("tables", {}).get("files") == files_total, r.out[:400])
    check("per file, all versions is the current one plus the older ones",
          fv and all(x.get("allVersionsBytes", 0) == x.get("currentVersionBytes", 0) + x.get("olderVersionsBytes", 0) and x.get("versionCount", 0) >= 1 for x in fv),
          [x for x in fv if x.get("allVersionsBytes", 0) != x.get("currentVersionBytes", 0) + x.get("olderVersionsBytes", 0)][:2])
    check("nothing could not be read", not any(x.get("note") for x in fv), [x for x in fv if x.get("note")][:2])
    check("a report with two tables asks which one", cpm("report", "rows", r.data.get("runId")).code == 2)
    r = run("--report", "file-versions", "--sites", SITE, "--set", "versionRows=true,onlyWithHistory=true")
    hist = rows(r.data.get("runId"), "--table", "files")
    check("onlyWithHistory keeps the files with more than one version, versionRows adds a row per version",
          r.code == 0 and all(x.get("versionCount", 0) > 1 for x in hist) and r.data.get("tables", {}).get("versions", 0) == sum(x["versionCount"] for x in hist), r.out[:400])

    r = run("--report", "permissions", "--sites", SITE)
    perm_run = r.data.get("runId")
    perms = rows(perm_run, "--table", "permissions")
    check("permissions: the site's own, with the kind of principal and the level", r.code == 0
          and any(x["objectType"] == "Site" and x.get("principalType") == "SharePoint group" and x.get("permissionLevels") for x in perms), perms[:2])
    check("site collection administrators are listed, though no role assignment names them",
          any(x.get("permissionLevels") == "Site collection administrator" for x in perms), [x.get("permissionLevels") for x in perms][:6])
    check("the pass-through Limited Access levels are left out by default", not any("Limited Access" in x.get("permissionLevels", "") for x in perms),
          [x.get("permissionLevels") for x in perms if "Limited" in x.get("permissionLevels", "")][:3])
    check("objects are named and addressed", all(x.get("objectUrl", "").startswith("https://") and x.get("principal") for x in perms), perms[:1])
    check("the summary counts objects with their own permissions and the lists looked at",
          r.data.get("summary", {}).get("uniqueObjects", 0) >= 1 and r.data["summary"].get("listsScanned", 0) > 5 and r.data["summary"].get("permissions") == len(perms), r.out[:400])
    check("sharing link addresses are not written unless asked for", not any("linkUrl" in x for x in rows(perm_run, "--table", "sharingLinks")))
    r = run("--report", "permissions", "--sites", SITE, "--set", "limitedAccess=true,expandGroups=true,includeItems=false")
    wide = rows(r.data.get("runId"), "--table", "permissions")
    check("expandGroups adds the members of each group, marked with the group they come through",
          r.code == 0 and any(x.get("through") for x in wide) and len(wide) > len([x for x in perms if x["objectType"] in ("Site", "Library", "List")]), r.out[:300])
    check("includeItems=false leaves folders, files and items alone", not any(x["objectType"] in ("Folder", "File", "Item") for x in wide))

    r = run("--report", "broken-notebooks", "--sites", SITE)
    check("broken-notebooks: completes, and broken ones are a subset of the notebooks found", r.code == 0 and r.data.get("status") == "Completed"
          and r.data.get("summary", {}).get("broken", 0) <= r.data.get("summary", {}).get("notebooks", 0), r.out[:400])

    # --- storage: where the space goes, and how much of it is older versions ---
    # Hidden libraries are left out here so the numbers can be held against the documents report, which leaves them out too.
    r = run("--report", "storage", "--sites", SITE, "--set", "includeHidden=false")
    st_run = r.data.get("runId")
    st = r.data.get("summary", {})
    check("storage: totals for the site, split into current and older versions", r.code == 0 and st.get("files") == files_total
          and st.get("totalBytes", 0) >= st.get("currentBytes", 0) > 0 and st.get("versionBytes", -1) >= 0
          and st["totalBytes"] >= st["currentBytes"] + st["versionBytes"], r.out[:500])
    libs = rows(st_run, "--table", "folders", "--where", "depth=0")
    check("the libraries' totals add up to the site's", sum(x["totalBytes"] for x in libs) == st.get("totalBytes") and sum(x["files"] for x in libs) == st.get("files"),
          f"{sum(x['totalBytes'] for x in libs)} vs {st.get('totalBytes')}")
    lib = [x for x in rows(st_run, "--table", "folders", "--where", "library=" + SRC_LIB)]
    by_path = {x.get("path", ""): x for x in lib}
    top, sub_a, sub_1 = by_path.get("", {}), by_path.get("Folder-A", {}), by_path.get("Folder-A/Sub-1", {})
    check("a folder's numbers include its subfolders, and its own files are counted apart",
          sub_a.get("files") == sub_a.get("ownFiles", 0) + sub_1.get("files", -1) and sub_a.get("totalBytes") == sub_a.get("ownBytes", 0) + sub_1.get("totalBytes", -1)
          and sub_a.get("depth") == 1 and sub_1.get("depth") == 2, [sub_a, sub_1])
    check("a library is the sum of its top folders and the files at its top",
          top.get("files") == top.get("ownFiles", 0) + sum(x["files"] for x in lib if x.get("depth") == 1), top)
    check("storage agrees with the documents report on what the current versions take",
          st.get("currentBytes") == cpm("report", "status", docs_run).data.get("summary", {}).get("sizeBytes"),
          f"{st.get('currentBytes')} vs {cpm('report', 'status', docs_run).data.get('summary', {}).get('sizeBytes')}")
    kinds = rows(st_run, "--table", "types")
    check("file types add up to the same total", sum(x["totalBytes"] for x in kinds) == st.get("totalBytes") and sum(x["files"] for x in kinds) == st.get("files"), kinds[:2])
    check("lists are measured too, and archived files are counted even when there are none",
          st.get("listBytes", 0) > 0 and all(k in st for k in ("archivedFiles", "archivedBytes"))
          and one_row(st_run, "overview").get("listBytes") == st.get("listBytes"), st)
    check("without hidden libraries nothing is said about the Preservation Hold Library, which is one", "preservationHoldBytes" not in st, st)
    known = {"PDF", "Office documents", "OneNote", "Images", "Video", "Audio", "Archives", "Email", "Other"}
    check("every file type says what kind of file it is", bool(kinds) and all(x.get("kind") in known for x in kinds)
          and all(x["kind"] == "Office documents" for x in kinds if x["extension"] in ("docx", "xlsx", "pptx"))
          and all(x["kind"] == "PDF" for x in kinds if x["extension"] == "pdf"), [(x.get("extension"), x.get("kind")) for x in kinds][:8])
    by_folder = rows(st_run, "--table", "folderKinds")
    check("kinds by folder add up to the same total, and to each folder's own files",
          sum(x["totalBytes"] for x in by_folder) == st.get("totalBytes") and sum(x["files"] for x in by_folder) == st.get("files")
          and all(x.get("kind") in known for x in by_folder)
          and sum(x["files"] for x in by_folder if x.get("library") == SRC_LIB and x.get("path", "") == "Folder-A") == sub_a.get("ownFiles"),
          f"{sum(x['totalBytes'] for x in by_folder)} vs {st.get('totalBytes')}")
    big = cpm("report", "rows", st_run, "--table", "files", "--sort", "totalBytes", "--desc", "--limit", "5").data.get("rows", [])
    check("rows --sort --desc gives the largest first", len(big) == 5 and [x["totalBytes"] for x in big] == sorted((x["totalBytes"] for x in big), reverse=True)
          and big[0]["totalBytes"] == max(x["totalBytes"] for x in rows(st_run, "--table", "files")), [x.get("totalBytes") for x in big])
    small = cpm("report", "rows", st_run, "--table", "folders", "--sort", "library", "--limit", "3", "--where", "depth=0")
    check("rows --sort on text with a filter: A first, and how many match", small.ok and [x["library"] for x in small.data["rows"]] == sorted(x["library"] for x in libs)[:3]
          and small.data.get("matching") == len(libs), small.out[:300])
    check("--desc without --sort, and --sort on a column that is not there: usage",
          cpm("report", "rows", st_run, "--table", "files", "--desc").code == 2 and cpm("report", "rows", st_run, "--table", "files", "--sort", "nope").code == 2)
    r = run("--report", "storage", "--sites", SITE, "--set", "largestFiles=3,includeHidden=false")
    check("largestFiles limits the files listed, not the totals", r.code == 0 and r.data.get("tables", {}).get("files") == 3 and r.data.get("summary", {}).get("totalBytes") == st.get("totalBytes"), r.out[:300])
    over = one_row(st_run, "overview")
    check("for a subsite the overview has what was scanned and says why the site collection's figures are left out",
          over.get("libraryBytes") == st.get("totalBytes") and "usedBytes" not in over and "subsite" in over.get("note", "").lower(), over)
    r = run("--report", "storage", "--sites", LAB_SITE, "--set", "includeSubsites=false")
    whole = one_row(r.data.get("runId"), "overview")
    check("for a site collection the overview splits what SharePoint says the site uses into libraries, recycle bin and the rest",
          r.code == 0 and whole.get("usedBytes", 0) > 0 and "recycleBinBytes" in whole and "note" not in whole and "preservationHoldBytes" in whole
          and whole.get("otherBytes", 0) == max(0, whole["usedBytes"] - whole.get("libraryBytes", 0) - whole.get("recycleBinBytes", 0)), whole)

    # --- OneDrives, app catalogs ---
    r = run("--report", "onedrive-lists", "--sites", SITE)
    made = rows(r.data.get("runId"))
    check("onedrive-lists lists the lists people made, and none of SharePoint's own", r.code == 0 and any(x["listTitle"] == SRC_LIST for x in made)
          and not any(x["listTitle"] in ("TaxonomyHiddenList", "User Information List", "Composed Looks") for x in made)
          and not any(x.get("listType") == "Document library" for x in made), [x["listTitle"] for x in made][:8])
    check("a report made for OneDrives says so", cpm("report", "list", "--report", "onedrive-lists").data.get("meantFor", "").startswith("onedrive"))
    r = run("--report", "onedrive-lists", "--all-sites", SITE, "--type", "onedrive", "--parallel", "8")
    check("--all-sites --type onedrive scans the tenant's OneDrives", r.code in (0, 1) and r.data.get("sitesTotal", 0) > 0
          and all("/personal/" in x["site"] for x in rows(r.data.get("runId"))), r.out[:400])
    r = run("--report", "onedrive-pages", "--sites", SOURCE_SITE)
    pages = rows(r.data.get("runId"))
    check("onedrive-pages lists the pages of a site, with who changed them and when", r.code == 0 and pages
          and all(x["pageName"].lower().endswith(".aspx") and x.get("pageUrl", "").startswith(SOURCE_SITE) and x.get("modified") for x in pages), pages[:1])
    r = run("--report", "site-app-catalogs", "--sites", SITE, "--set", "includeSitesWithout=true")
    check("site-app-catalogs on a site without one says so", r.code == 0 and r.data.get("summary") == {"sitesWithoutCatalog": 1}
          and one_row(r.data.get("runId"), "catalogs").get("apps") == 0, r.out[:300])

    # --- source against target ---
    r = run("--report", "list-compare", "--source", SITE, "--target", SITE)
    same = rows(r.data.get("runId"))
    check("list-compare of a site with itself: every list matches", r.code == 0 and len(same) > 5 and {x["status"] for x in same} == {"Match"}
          and r.data.get("summary") == {"lists": len(same), "match": len(same)}, r.out[:400])
    check("each row has both counts, both addresses and the pair it belongs to",
          all(x.get("site") == SITE and x.get("targetSite") == SITE and x.get("sourceUrl") and x.get("targetUrl") and "sourceCount" in x for x in same), same[:1])
    r = run("--report", "list-compare", "--source", SITE, "--target", LAB_SITE)
    across = rows(r.data.get("runId"))
    check("list-compare across tenants: lists missing on the target are source only, extra ones target only",
          r.code == 0 and r.data.get("summary", {}).get("sourceOnly", 0) > 0 and any(x["status"] == "Source only" and "targetCount" not in x for x in across)
          and any(x["status"] == "Target only" for x in across), r.out[:400])
    check("the system lists a migration never moves are left out", not any(x["listTitle"] in ("Style Library", "Form Templates", "Site Assets") for x in same))

    r = run("--report", "document-compare", "--source", SITE, "--target", SITE)
    dc = r.data
    check("document-compare of a site with itself: every file found, no differences", r.code == 0 and dc.get("summary", {}).get("found", 0) > 20
          and all(dc["summary"].get(k) == 0 for k in ("sourceOnly", "targetOnly", "sizeIssues")) and dc["summary"]["sourceFiles"] == dc["summary"]["targetFiles"]
          and dc.get("tables", {}).get("differences", 0) == 0, r.out[:500])
    check("one row per library says it matches", {x["status"] for x in rows(dc.get("runId"), "--table", "libraries")} == {"Match"})
    r = run("--report", "document-compare", "--source", SITE, "--target", SITE, "--set", "includeMatches=true,includeFolders=false")
    check("includeMatches writes a row per file that is fine", r.code == 0 and r.data.get("tables", {}).get("differences") == r.data.get("summary", {}).get("found")
          and r.data["summary"]["found"] == r.data["summary"]["sourceFiles"], r.out[:400])
    r = run("--report", "document-compare", "--source", SITE, "--target", LAB_SITE)
    check("document-compare across tenants: libraries that are not on the target are one finding each, not a row per file",
          r.code == 0 and r.data.get("summary", {}).get("librariesSourceOnly", 0) > 0
          and any(x["status"] == "Source only" for x in rows(r.data.get("runId"), "--table", "libraries")), r.out[:400])
    pairs = write(os.path.join(work, "rep", "pairs.csv"), "Source,Target\r\n" + SITE + "," + SITE + "/\r\n" + SITE + "," + bogus + "\r\nnot a pair\r\n")
    r = cpm("report", "start", "--report", "list-compare", "--pairs-file", pairs, "--wait", "--timeout", "300")
    check("a pairs file: header and bad lines skipped, a pair whose target is missing fails alone",
          r.code == 1 and (r.data.get("sitesTotal"), r.data.get("sitesDone"), r.data.get("failures")) == (2, 1, {"notFound": 1}), r.out[:400])

    r = run("--report", "navigation-compare", "--source", SOURCE_SITE, "--target", SOURCE_SITE)
    nav_same = rows(r.data.get("runId"))
    check("navigation-compare of a site with itself: every link matches", r.code == 0 and nav_same and {x["status"] for x in nav_same} == {"Match"}
          and {x["navigation"] for x in nav_same} <= {"Quick launch", "Top navigation"}, r.out[:400])
    check("links inside the site are written relative to it", any(x.get("sourceLink", "").startswith("~") for x in nav_same), [x.get("sourceLink") for x in nav_same][:5])
    r = run("--report", "navigation-compare", "--source", SOURCE_SITE, "--target", SITE, "--set", "includeMatches=false")
    nav_diff = rows(r.data.get("runId"))
    check("navigation-compare of two different sites: links on one side only, and no matching rows when not asked for",
          r.code == 0 and r.data.get("summary", {}).get("sourceOnly", 0) + r.data.get("summary", {}).get("targetOnly", 0) > 0
          and "Match" not in {x["status"] for x in nav_diff}, r.out[:400])

    r = run("--report", "permissions-compare", "--source", SITE, "--target", SITE, "--set", "includeMatches=true")
    pc = rows(r.data.get("runId"))
    check("permissions-compare of a site with itself: everything matches", r.code == 0 and pc and {x["status"] for x in pc} == {"Match"}
          and r.data.get("summary", {}).get("match") == len(pc), r.out[:400])
    check("the site's own groups are compared by their role, levels by their kind",
          any(x["principal"].startswith("Site owners group") and x.get("sourceLevels") == "Full Control" for x in pc), [x.get("principal") for x in pc][:5])
    r = run("--report", "permissions-compare", "--source", SITE, "--target", SITE)
    check("without includeMatches only differences are listed, and the matches are still counted",
          r.code == 0 and r.data.get("tables", {}).get("differences", 0) == 0 and r.data.get("summary", {}).get("match", 0) == len(pc), r.out[:300])
    r = run("--report", "permissions-compare", "--source", SITE, "--target", LAB_SITE)
    check("permissions-compare across tenants: principals on one side only are listed as such", r.code == 0
          and {x["status"] for x in rows(r.data.get("runId"))} & {"Source only", "Target only", "Different level", "Inheritance differs"}, r.out[:400])

    # --- actions: a preview first, changes only when applied ---
    def one(run_id, table=None):
        got = rows(run_id, *(["--table", table] if table else []))
        return got[0] if got else {}

    r = run("--report", "site-lock", "--sites", SOURCE_SITE, "--set", "state=ReadOnly")
    lock_preview = r.data.get("runId")
    row = one(lock_preview)
    check("site-lock without apply is a preview: it says what it would do", r.code == 0 and r.data.get("mode", "").startswith("preview")
          and row.get("action") == "Would set to read-only" and row.get("stateBefore") == "Unlocked" and r.data.get("summary") == {"wouldChange": 1}, r.out[:400])
    check("the status of a preview points at `report apply`", f"cpm report apply {lock_preview}" in (r.data.get("next") or ""), r.data.get("next"))
    again = run("--report", "site-lock", "--sites", SOURCE_SITE, "--set", "state=ReadOnly")
    check("the preview changed nothing: the site still reads and reports as unlocked",
          one(again.data.get("runId")).get("stateBefore") == "Unlocked" and cpm("site", "lists", "--site", SOURCE_SITE).ok, again.out[:400])
    # Version history limits: what every library works by, and the action that changes it, as a preview only.
    r = run("--report", "version-limits", "--sites", SOURCE_SITE)
    limits = one(r.data.get("runId"), "limits")
    libraries = rows(r.data.get("runId"), "--table", "libraries")
    check("version-limits: the site's setting, and its libraries counted by the limits they work by",
          r.code == 0 and limits.get("siteSetting") and limits.get("versionedLibraries", 0) > 0
          and limits.get("versionedLibraries") == limits.get("automatic", 0) + limits.get("deletedAfterDays", 0) + limits.get("keptForever", 0)
          and r.data.get("summary", {}).get("librariesKeepingVersionsForever") == limits.get("keptForever"), [r.out[:300], limits])
    check("version-limits: every library says its limits in words",
          bool(libraries) and all(x.get("versionLimits") for x in libraries)
          and all(x["versionLimits"] == "Automatic" or "versions" in x["versionLimits"] or x["versionLimits"] == "No versioning" for x in libraries),
          [x.get("versionLimits") for x in libraries][:6])
    check("version-limits: the tenant's default is read through the admin site", limits.get("tenantDefault") in ("Automatic",) or "versions" in limits.get("tenantDefault", ""), limits)
    forever = sum(1 for x in rows(st_run, "--table", "folders", "--where", "depth=0") if x.get("versionLimits", "").endswith("kept forever"))
    check("storage says the same per library, and counts the libraries that keep versions forever",
          st.get("librariesKeepingVersionsForever") == forever and one_row(st_run, "overview").get("librariesKeepingVersionsForever") == forever, [forever, st])
    r = run("--report", "set-version-limits", "--sites", SOURCE_SITE, "--set", "limits=Automatic,trim=Automatic")
    change = one(r.data.get("runId"), "changes")
    named = rows(r.data.get("runId"), "--table", "libraries")
    check("the preview names every library whose limits would change", len(named) == change.get("librariesToChange", 0)
          and all(x.get("library") and x.get("limitsNow") != "Automatic" and x.get("limitsAfter") == "Automatic" for x in named), [change, named[:2]])
    check("set-version-limits without apply is a preview: what it would set, how many libraries, and that a trim is permanent",
          r.code == 0 and r.data.get("mode", "").startswith("preview") and change.get("limitsAfter") == "Automatic" and change.get("limitsBefore")
          and "cannot be brought back" in change.get("trim", "") and r.data.get("summary") == {"wouldChange": 1}, [r.out[:300], change])
    after = one(run("--report", "version-limits", "--sites", SOURCE_SITE).data.get("runId"), "limits")
    check("the preview changed nothing and queued nothing", after.get("siteSetting") == limits.get("siteSetting") and after.get("keptForever") == limits.get("keptForever")
          and "trimJob" not in after and "limitsJob" not in after, after)
    r = cpm("report", "validate", "--report", "set-version-limits", "--sites", SOURCE_SITE)
    check("set-version-limits needs to be told which limits", r.code == 5 and "limits takes Automatic, Manual or TenantDefault" in r.out, r.out[:300])
    r = cpm("report", "validate", "--report", "set-version-limits", "--sites", SOURCE_SITE, "--set", "limits=Manual,expireAfterDays=5")
    check("limits SharePoint would refuse are refused before the run", r.code == 5 and "expireAfterDays" in r.out, r.out[:300])
    r = cpm("report", "validate", "--report", "set-version-limits", "--sites", SOURCE_SITE, "--set", "limits=Automatic,trim=OlderThanDays,trimDays=7")
    check("a trim by age under 30 days is refused", r.code == 5 and "trimDays" in r.out, r.out[:300])

    r = run("--report", "site-lock", "--sites", SOURCE_SITE, "--set", "state=Unlock")
    noop = r.data.get("runId")
    check("asking for the state a site already has is no change", r.code == 0 and one(noop).get("action") == "Already unlocked" and r.data.get("summary") == {"unchanged": 1}, r.out[:300])
    # Applying a preview that found nothing to change exercises the whole apply path without touching the site.
    r = cpm("report", "apply", noop, "--wait", "--timeout", "300")
    applied = r.data.get("runId")
    check("report apply starts a new run of the same action that says it was applied", r.code == 0 and applied not in (None, noop)
          and r.data.get("mode", "").startswith("applied") and r.data.get("name", "").endswith("(applied)") and one(applied).get("action") == "Already unlocked", r.out[:400])
    check("a run that was applied cannot be applied again", cpm("report", "apply", applied).code == 5)
    check("a report that only reads has nothing to apply", cpm("report", "apply", lists_run).code == 5)

    nobody = f"nobody-{RUN}@example.com"
    r = run("--report", "site-admins", "--sites", SOURCE_SITE, "--set", f"add={nobody}")
    check("site-admins preview: would add", r.code == 0 and one(r.data.get("runId")).get("action") == "Would add" and one(r.data.get("runId")).get("account") == nobody, r.out[:300])
    r = run("--report", "site-admins", "--sites", SOURCE_SITE, "--set", f"remove={nobody}")
    check("removing someone who is not an administrator is no change", r.code == 0 and one(r.data.get("runId")).get("action") == "Was not an administrator", r.out[:300])

    r = run("--report", "nav-sync", "--source", SITE, "--target", SOURCE_SITE)
    nav = one(r.data.get("runId"))
    check("nav-sync preview: both sides' navigation, and whether the target would change", r.code == 0 and nav.get("sourceNavigation") in ("Horizontal", "Vertical")
          and nav.get("targetMenu") in ("Mega menu", "Cascading") and nav.get("action") in ("Would change the target", "Already the same"), nav)
    check("running the preview again gives the same answer: nothing was changed",
          one(run("--report", "nav-sync", "--source", SITE, "--target", SOURCE_SITE).data.get("runId")).get("action") == nav.get("action"))
    r = run("--report", "fix-notebooks", "--sites", SITE)
    check("fix-notebooks preview completes and changes nothing", r.code == 0 and r.data.get("mode", "").startswith("preview") and "changed" not in (r.data.get("summary") or {}), r.out[:300])

    # --- a report file, with its sites beside it ---
    write(os.path.join(work, "rep", "my sites.txt"), "﻿" + SITE + "\r\n" + SITE + "/\r\n" + LAB_SITE + "\r\n")
    spec = write(os.path.join(work, "rep", "access.json"), json.dumps({"report": "site-access", "name": "From a file", "sitesFile": "my sites.txt", "parallel": 2}))
    v = cpm("report", "validate", spec, "--online", cwd=ROOT_FOLDER)
    check("validate --online opens every site and names the connection used", v.ok and v.data.get("sitesOpened") == 2 and v.data.get("sitesWithProblems") == 0
          and all(c.get("signIn") == "certificate" for c in v.data.get("connections", [])), v.out[:500])
    r = cpm("report", "start", spec, "--wait", "--timeout", "300", cwd=ROOT_FOLDER)
    check("a report file runs from any folder; a site listed twice is scanned once", r.code == 0 and r.data.get("sitesTotal") == 2 and r.data.get("name") == "From a file", r.out[:400])
    v = cpm("report", "validate", "--report", "site-access", "--sites", ",".join([SITE, bogus]), "--online")
    check("validate --online reports the site that cannot be opened, exit 1", v.code == 1 and v.data.get("sitesWithProblems") == 1 and v.data["problems"][0]["status"] == "notFound", v.out[:400])

    # --- many sites: taken from an earlier run, and from the tenant itself ---
    r = run("--report", "site-access", "--from-report", sites_run, "--parallel", "8")
    many_run = r.data.get("runId")
    check("--from-report scans the sites an earlier sites run found", r.code == 0 and r.data.get("sitesTotal") == total and r.data.get("rows") == total, r.out[:400])
    r = run("--report", "site-access", "--all-sites", SITE, "--url-contains", "DemoLargeSite")
    check("--all-sites with --url-contains narrows the tenant down", r.code == 0 and 1 <= r.data.get("sitesTotal", 0) < total
          and all("demolargesite" in x["site"].lower() for x in rows(r.data.get("runId"))), r.out[:400])
    check("starting from a run that has no sites in it is refused when it runs",
          run("--report", "site-access", "--from-report", lists_run).data.get("status") == "Failed")

    # --- export ---
    out = os.path.join(work, "exports", "lists " + RUN)
    r = cpm("report", "export", lists_run, "--out", out)
    book = (r.data.get("files") or [""])[0]
    sheets = []
    if os.path.exists(book):
        import zipfile
        with zipfile.ZipFile(book) as z:
            sheets = [part for part in z.namelist() if part.startswith("xl/worksheets/")]
            workbook = z.read("xl/workbook.xml").decode("utf-8")
    check("export: an Excel workbook with a summary, the sites and the table", r.ok and book.endswith(".xlsx") and len(sheets) == 3
          and all(n in workbook for n in ('name="Summary"', 'name="Sites"', 'name="Lists"')), r.out[:300])
    r = cpm("report", "export", lists_run, "--format", "csv", "--out", out + ".csv")
    files = r.data.get("files") or []
    text = open(files[0], encoding="utf-8-sig").read() if files and os.path.exists(files[0]) else ""
    check("export --format csv: one file per table and one for the sites", r.ok and len(files) == 2 and files[0].endswith("-lists.csv") and files[1].endswith("-sites.csv"), files)
    check("the CSV has readable headings and a line per row", text.startswith("Site,Web URL,List title") and len(text.strip().splitlines()) == cpm("report", "status", lists_run).data.get("rows") + 1, text[:120])
    check("export with an unknown format: usage", cpm("report", "export", lists_run, "--format", "pdf").code == 2)

    # --- cancel, a killed process, resume ---
    def wait_until(predicate, seconds=90):
        deadline = time.time() + seconds
        while time.time() < deadline:
            if predicate():
                return True
            time.sleep(0.5)
        return False

    def status(run_id):
        return cpm("report", "status", run_id)

    # One site at a time over the whole tenant: slow enough to stop in the middle.
    slow = ["--report", "lists", "--from-report", sites_run, "--parallel", "1"]
    full = run("--report", "lists", "--from-report", sites_run, "--parallel", "8")
    expected = (full.data.get("rows"), full.data.get("sitesDone"), full.data.get("summary"))

    r = cpm("report", "start", *slow)
    victim = r.data.get("runId")
    check("start returns at once with the run ID and what to do next", r.ok and r.data.get("status") == "Queued" and victim in r.data.get("next", ""), r.out)
    check("a running report shows progress", wait_until(lambda: status(victim).data.get("sitesDone", 0) >= 3, 60), status(victim).out[:400])
    live = status(victim).data
    check("status while running: not finished, says what it is on, exit 0", live.get("finished") is False and live.get("now") and status(victim).code == 0, live)
    check("rows can be read while the report runs", len(rows(victim)) > 0)
    check("a running report cannot be deleted or resumed", cpm("report", "delete", victim).code == 5 and cpm("report", "resume", victim).code == 5)
    r = cpm("report", "cancel", victim)
    check("cancel is accepted", r.ok and r.data.get("cancelRequested") is True, r.out)
    check("the run stops within seconds", wait_until(lambda: status(victim).data.get("finished"), 30), status(victim).out[:300])
    stopped = status(victim)
    check("cancelled: exit 7, sites done so far are kept, resume is offered",
          stopped.code == 7 and stopped.data.get("status") == "Cancelled" and 0 < stopped.data.get("sitesDone", 0) < total and stopped.data.get("canResume") is True, stopped.out[:400])
    check("cancelling again says it is already cancelled", cpm("report", "cancel", victim).data.get("cancelRequested") is False)
    r = cpm("report", "resume", victim, "--wait", "--timeout", "600", timeout=660)
    check("resume finishes the rest; the totals equal an uninterrupted run (nothing twice, nothing missing)",
          (r.data.get("rows"), r.data.get("sitesDone"), r.data.get("summary")) == expected, f"{(r.data.get('rows'), r.data.get('sitesDone'))} vs {expected[:2]}")

    r = cpm("report", "start", *slow)
    victim = r.data.get("runId")
    wait_until(lambda: status(victim).data.get("sitesDone", 0) >= 3, 60)
    pid = report_process(victim)
    if check("the report runs in its own process", pid is not None, "no process found"):
        kill_process(pid)
        wait_until(lambda: report_process(victim) is None, 20)
        dead = status(victim)
        check("a killed report is reported as interrupted, exit 7, not as running forever", dead.data.get("status") == "Interrupted" and dead.code == 7, dead.out[:300])
        r = cpm("report", "resume", victim, "--wait", "--timeout", "600", timeout=660)
        check("resume after a kill: the totals again equal an uninterrupted run",
              (r.data.get("rows"), r.data.get("sitesDone"), r.data.get("summary")) == expected, f"{(r.data.get('rows'), r.data.get('sitesDone'))} vs {expected[:2]}")
    log = status(victim).data.get("log")
    text = open(log, encoding="utf-8").read() if log and os.path.exists(log) else ""
    check("the run has a log that says how it ended, and holds only its own lines", "finished:" in text and '"ok"' not in text, text[-300:])

    clean = run("--report", "site-access", "--sites", SITE)
    check("a run with every site scanned has nothing to resume", cpm("report", "resume", clean.data.get("runId")).code == 5)

    # --- delete ---
    folder = os.path.join(cpm.home, "reports", many_run)
    r = cpm("report", "delete", many_run)
    check("delete removes the run and its files", r.ok and not os.path.exists(folder) and cpm("report", "status", many_run).code == 4, r.out)


# ----------------------------------------------------------------------------------------------
def load_credentials():
    """The sandbox credentials the TestRunner uses. Values are used, never printed."""
    folder = REPO
    while folder != os.path.dirname(folder) and not os.path.isdir(os.path.join(folder, "Secrets")):
        folder = os.path.dirname(folder)
    folder = os.path.join(folder, "Secrets")
    if not os.path.isdir(folder):
        return None
    creds = {}
    for key, name in (("source", "Source Tenant - Migrator App.txt"), ("target", "Target Tenant - Migrator App.txt")):
        values = {}
        with open(os.path.join(folder, name), encoding="utf-8-sig") as f:
            for line in f:
                if ":" in line:
                    k, v = line.split(":", 1)
                    values[k.strip().lower()] = v.strip()
        values["pfx"] = os.path.normpath(os.path.join(folder, values["cert path"]))
        secrets.append(values["cert password"])
        if values.get("app secret"):
            secrets.append(values["app secret"])
        creds[key] = values
    return creds


def main():
    global GROUP
    args = sys.argv[1:]
    exe = os.path.join(REPO, "src", "CleverPoint.Migrator.Cli", "bin", "Debug", "net8.0", "cpm.exe" if WINDOWS else "cpm")
    if "--cpm" in args:
        i = args.index("--cpm")
        exe = os.path.abspath(args[i + 1])
        del args[i:i + 2]
    groups = args or ["cli", "agent", "conn", "site", "job", "life", "report"]
    if not os.path.exists(exe):
        sys.exit(f"cpm not found at {exe}. Build it: dotnet build src/CleverPoint.Migrator.Cli")

    root = tempfile.mkdtemp(prefix="cpm tests ")
    home = os.path.join(root, "hömé with spaces")
    work = os.path.join(root, "wörk files")
    os.makedirs(home)
    os.makedirs(work)
    cpm = Cpm(exe, home)
    print(f"cpm: {exe}\nhome: {home}\nrun: {RUN}")
    started = time.time()
    try:
        creds = load_credentials()
        online = [g for g in groups if g in ("conn", "site", "job", "life")]
        report_online = "report" in groups and bool(creds)
        if (online or "report" in groups) and not creds:
            print("No Secrets folder found: skipping " + ", ".join(online + (["report (online half)"] if "report" in groups else [])))
            groups = [g for g in groups if g not in online]

        for GROUP in [g for g in ("cli", "agent") if g in groups]:
            print(f"\n=== {GROUP} ===")
            (test_cli if GROUP == "cli" else test_agent)(cpm, work)

        if "conn" in groups:
            GROUP = "conn"
            print("\n=== conn ===")
            test_conn(cpm, work, creds)

        if "report" in groups:
            GROUP = "report"
            print("\n=== report (offline) ===")
            test_report_offline(cpm, work)

        if any(g in groups for g in ("site", "job", "life")) or report_online:
            GROUP = "setup"
            for key, site in (("source", SOURCE_SITE), ("target", LAB_SITE)):
                c = creds[key]
                r = cpm("connection", "add", "--site", site, "--sign-in", "certificate", "--client-id", c["app id"],
                        "--tenant-id", c["tenant id"], "--pfx", c["pfx"], "--pfx-password-env", "CPM_TEST_PW", env={"CPM_TEST_PW": c["cert password"]})
                check(f"setup: {key} connection", r.ok, r.out)
            item_ids = []
            if "site" in groups or "job" in groups:
                GROUP = "site"
                print("\n=== site ===")
                item_ids = test_site(cpm, work)
            if "job" in groups:
                GROUP = "job"
                print("\n=== job ===")
                test_job(cpm, work, item_ids)
            if "life" in groups:
                GROUP = "life"
                print("\n=== life ===")
                test_life(cpm, work)
            if report_online:
                GROUP = "report"
                print("\n=== report ===")
                test_report(cpm, work)

        GROUP = "all"
        everything = "\n".join(captured)
        leaked = [i for i, s in enumerate(secrets) if s and s in everything]
        check("no certificate password or app secret appeared in any output", not leaked, f"secret #{leaked} was printed")
        check("no command ever wrote a stack trace to its output", "   at CleverPoint." not in everything and "   at System." not in everything,
              [c[:300] for c in captured if "   at System." in c or "   at CleverPoint." in c][:2])
    finally:
        # The settings folder holds the sandbox certificate password; it must not outlive the run.
        shutil.rmtree(root, ignore_errors=True)

    failed = [r for r in results if not r[2]]
    print("\n==================== RESULTS ====================")
    for group in dict.fromkeys(r[0] for r in results):
        mine = [r for r in results if r[0] == group]
        print(f"  {group:6} {sum(1 for r in mine if r[2])}/{len(mine)}")
    for group, name, _, detail in failed:
        print(f"  [FAIL] {group}: {name}\n         {detail}")
    print(f"  {len(results) - len(failed)}/{len(results)} passed in {time.time() - started:.0f}s")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
