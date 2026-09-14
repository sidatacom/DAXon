# QT3Test — W3C conformance runner

Runs the official W3C test suites against this engine and reports pass / fail / skip:

- **QT3 (FOTS)** — XPath and XQuery 1.0 / 2.0 / 3.0 / 3.1, [github.com/w3c/qt3tests](https://github.com/w3c/qt3tests)
- **XSLT 3.0** — [github.com/w3c/xslt30-test](https://github.com/w3c/xslt30-test)

Both suites ship **data only**: an XML catalog describing the cases, their dependencies and their
expected assertions, and by design no runner for any particular engine. Every vendor writes its own
driver against its own API. This is that driver for `OutSmart.DAXon`.

This is a conformance sweep, not a fast per-change check — a full run takes a few minutes. It is not
wired into CI; run it when you change something that could plausibly move conformance.

## Getting the corpora

They are not part of this repository. They are large (~450 MB unpacked), they belong to the W3C, and
vendoring thousands of third-party files into our history would help nobody. Download them instead:

```
pwsh tests/QT3Test/fetch-corpus.ps1
```

or, on a POSIX shell:

```
sh tests/QT3Test/fetch-corpus.sh
```

This unpacks `tests/QT3Test/qt3tests/` and `tests/QT3Test/xslt30-test/` at the revisions listed in
[`corpus.pin`](corpus.pin) (~96 MB of downloads), and stamps each with the revision it fetched so a
second run is a no-op. Both are git-ignored.

Pass `-Latest` (PowerShell) or `--latest` (shell) to take current upstream HEAD instead. That is
useful for spotting new upstream tests, but the failure baseline below describes the **pinned**
revisions only — differences you see on HEAD may be upstream churn rather than anything here.

## Running

```
dotnet build tests/QT3Test -c Release
```

```
tests/QT3Test/bin/Release/net472/QT3Test.exe qt3tests xslt30-test
```

Arguments are corpus roots; naming both runs them as one pool. Anything after the roots filters test
sets by name, so `QT3Test.exe qt3tests fn-substring` runs a single set while you work on it.

Each test set runs in a child process, so one hanging or stack-exhausting case cannot take the whole
sweep down with it. `QT3_FAILDUMP=<file>` writes every failure as `set/case :: reason`, which is what
you want for triage and for comparing runs; the file is rewritten on every run.

## Reading the result

A clean run over the pinned revisions looks like this:

```
TEST-SETS: 662   PASS: 38554   FAIL: 17   SKIP: 7851   HUNG-sets: 0   CRASHED-sets: 0
  qt3tests       PASS: 30312   FAIL: 0   SKIP: 1509   (100.0%)
  xslt30-test    PASS: 8242   FAIL: 17   SKIP: 6342   (99.8%)
```

**The gate is the set of failing ids, not the count** — a fix and a regression can cancel out in a
total. [`BASELINE-FAILS.txt`](BASELINE-FAILS.txt) holds the 17 known failures, and a run is clean when
the difference is empty:

```
diff <(sed 's/ :: .*//' tests/QT3Test/BASELINE-FAILS.txt | sort -u) <(sed 's/ :: .*//' faildump.txt | sort -u)
```

Those 17 are two catalog cases plus the XML 1.1 wall: the suite feeds XML 1.1 documents, which the
.NET XML reader this port builds on does not accept. They are a platform boundary, not open defects.

Skips are not failures and not gaps in the engine either — they are cases whose declared dependencies
this build does not claim: schema awareness, static typing, `advanced-uca-fallback` (reorder codes and
alternate weighting need full ICU), remote HTTP resources, and similar.

## Notes

- The runner targets `net472`; the engine ships `net472` and `net10.0` builds, and the runner uses the former.
- Set `QTDBG=1` to see the engine's per-case warnings; they are silenced by default because they
  dominate the log and slow the sweep down.
- Updating a corpus is a deliberate change: bump the revision in `corpus.pin`, re-run the sweep, and
  re-baseline `BASELINE-FAILS.txt` in the same commit, so the two never describe different worlds.
