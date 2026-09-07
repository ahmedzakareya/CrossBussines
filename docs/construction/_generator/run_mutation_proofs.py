#!/usr/bin/env python3
# ==============================================================================================
# Construction C1 — CONTROLLED MUTATION PROOFS
#
# A green test suite proves nothing on its own: a test that would pass even with the defect
# reinstated is not a guard. So each of the three remediations is proved by BREAKING it on purpose
# and showing the guarding test turns red.
#
#   C-01  restore delete-and-reinsert BOQ behaviour  -> the re-billing test must FAIL
#   C-02  remove the subcontract cap                 -> the hard-block test must FAIL
#   C-03  reproduce from today's BOQ, not the snapshot -> the historical-reproduction test must FAIL
#
# Every mutation is applied to a byte-exact copy, run, and then RESTORED from that copy, with the
# SHA-256 compared before and after. The script fails loudly if any file does not come back
# byte-identical, or if any mutation marker survives anywhere in the tree.
#
# Run from the repository root:  python docs/construction/_generator/run_mutation_proofs.py
# ==============================================================================================
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
MARKER = "MUTATION-PROOF-C1"

MUTATIONS = [
    {
        "id": "C-01",
        "title": "restore delete-and-reinsert BOQ behaviour",
        "file": "CrossBuy/BL/BoqService.cs",
        "test": "Previously_billed_work_cannot_become_billable_again_after_a_boq_edit",
        "anchor": "\t\t\tvar correlation = Guid.NewGuid();",
        "insert": (
            "\t\t\t// [{marker} C-01] the pre-C1 behaviour: delete every line, re-insert with new identities.\n"
            "\t\t\t_db.BoqItems.RemoveRange(existing);\n"
            "\t\t\t_db.BoqLineStates.RemoveRange(states);\n"
            "\t\t\tawait _db.SaveChangesAsync(ct);\n"
            "\t\t\texistingById.Clear();\n"
            "\t\t\tstateByItem.Clear();\n"
            "\t\t\tremoved.Clear();\n"
            "\t\t\tforeach (var mutantRow in input) mutantRow.Id = 0;\n"
        ),
    },
    {
        "id": "C-02",
        "title": "remove the subcontract certification cap",
        "file": "CrossBuy/BL/Construction/SubcontractScopeService.cs",
        "test": "Certification_beyond_assigned_quantity_is_hard_blocked",
        "replace": (
            "\t\t\tif (cumulativeQty > scope.CappedQuantity)",
            "\t\t\tif (false && cumulativeQty > scope.CappedQuantity)   // [{marker} C-02] cap removed",
        ),
    },
    {
        "id": "C-03",
        "title": "reproduce a certificate from today's BOQ instead of its snapshot",
        "file": "CrossBuy/BL/Construction/CommercialRevisionService.cs",
        "test": "A_posted_certificate_reproduces_the_rate_it_was_certified_at",
        "replace": (
            "\t\t\tif (s == null) return null;",
            (
                "\t\t\tif (s == null) return null;\n"
                "\t\t\t// [{marker} C-03] the pre-C1 behaviour: re-read the LIVE BOQ, so a later rate\n"
                "\t\t\t// change silently rewrites what the certificate is said to have certified.\n"
                "\t\t\tvar mutantItem = await _db.BoqItems.AsNoTracking()\n"
                "\t\t\t\t.FirstOrDefaultAsync(b => b.ID == s.BoqItemId && b.CompanyID == companyId, ct);\n"
                "\t\t\tif (mutantItem != null)\n"
                "\t\t\t\treturn new ReproducedCommercialValue\n"
                "\t\t\t\t{\n"
                "\t\t\t\t\tProgressBillingLineId = s.ProgressBillingLineId,\n"
                "\t\t\t\t\tCommercialRevisionId = s.CommercialRevisionId,\n"
                "\t\t\t\t\tBoqItemId = s.BoqItemId,\n"
                "\t\t\t\t\tContractedQuantity = mutantItem.Quantity,\n"
                "\t\t\t\t\tContractedRate = mutantItem.UnitPrice,\n"
                "\t\t\t\t\tCertifiedQuantity = s.CertifiedQuantity,\n"
                "\t\t\t\t\tCertifiedValue = s.CertifiedValue,\n"
                "\t\t\t\t\tCapturedAt = s.CapturedAt\n"
                "\t\t\t\t};\n"
            ),
        ),
    },
]


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def run(cmd, timeout=900):
    return subprocess.run(cmd, cwd=ROOT, shell=True, capture_output=True, text=True, timeout=timeout)


def kill_testhost():
    """A lingering testhost holds CrossBuy.Tests.dll open, MSBuild then FAILS TO COPY the freshly
    compiled assembly, and `dotnet test` happily runs the STALE one. That silently invalidates a
    mutation proof — the mutated code would appear to change nothing — so it is killed every time."""
    subprocess.run("taskkill /F /IM testhost.exe", cwd=ROOT, shell=True,
                   capture_output=True, text=True)


def build():
    """Explicit build, so a compile failure is reported as a compile failure and never mistaken for
    a test result. Retries the file-lock case: a testhost that is still shutting down holds the test
    assembly open, MSBuild cannot copy the new one, and the STALE assembly would then be tested."""
    for attempt in range(3):
        kill_testhost()
        time.sleep(1.5)
        r = run("dotnet build CrossBuy.sln -v q --nologo")
        out = r.stdout + r.stderr
        if "Build succeeded" in out and "error CS" not in out:
            return True, out[-1500:]
        if "MSB3021" in out or "MSB3027" in out:
            continue          # lock, not a compile error — try again
        return False, out[-2000:]
    return False, "build kept failing on a locked test assembly"


def run_test(name):
    """Returns (passed, failed, raw_tail). failed = -1 means 'no test result at all' (build failure)."""
    ok, tail = build()
    if not ok:
        return 0, -1, "BUILD FAILED\n" + tail
    kill_testhost()
    r = run(f'dotnet test CrossBuy.Tests/CrossBuy.Tests.csproj --no-build --nologo '
            f'--filter "FullyQualifiedName~{name}"')
    out = r.stdout + r.stderr
    m = re.search(r"Failed:\s*(\d+),\s*Passed:\s*(\d+)", out)
    if m:
        return int(m.group(2)), int(m.group(1)), out[-1500:]
    return 0, -1, out[-1500:]


def apply_mutation(mut, text):
    # str.replace, never str.format: the mutation bodies contain C# object initialisers, and their
    # braces would be read as format placeholders.
    if "insert" in mut:
        anchor = mut["anchor"]
        if anchor not in text:
            raise RuntimeError(f"{mut['id']}: anchor not found — the source has moved, refusing to guess")
        return text.replace(anchor, mut["insert"].replace("{marker}", MARKER) + anchor, 1)
    old, new = mut["replace"]
    if old not in text:
        raise RuntimeError(f"{mut['id']}: text to replace not found — refusing to guess")
    return text.replace(old, new.replace("{marker}", MARKER), 1)


def main():
    results = []
    backup_dir = tempfile.mkdtemp(prefix="cb_c1_mutation_backup_")
    print(f"backups: {backup_dir}\n")
    ok_overall = True

    try:
        for mut in MUTATIONS:
            path = os.path.join(ROOT, mut["file"])
            backup = os.path.join(backup_dir, mut["id"] + "-" + os.path.basename(mut["file"]))
            shutil.copy2(path, backup)
            digest_before = sha256(path)

            print(f"=== {mut['id']} — {mut['title']} ===")
            print(f"  file : {mut['file']}")
            print(f"  test : {mut['test']}")

            # 1. baseline: the guarding test must PASS before the mutation
            p, f_, _tail0 = run_test(mut["test"])
            print(f"  baseline      : passed={p} failed={f_}")
            if f_ == -1: print("    ! no test result:", _tail0.strip().splitlines()[-1][:160] if _tail0.strip() else "(empty)")
            baseline_ok = p >= 1 and f_ == 0

            # 2. mutate
            with open(path, "r", encoding="utf-8-sig") as fh:
                original = fh.read()
            mutated = apply_mutation(mut, original)
            with open(path, "w", encoding="utf-8-sig") as fh:
                fh.write(mutated)

            p2, f2, tail = run_test(mut["test"])
            print(f"  mutated       : passed={p2} failed={f2}   <-- must be failed>=1")
            if f2 == -1: print("    ! no test result:", tail.strip().splitlines()[-1][:160] if tail.strip() else "(empty)")
            mutation_detected = f2 >= 1

            # 3. restore, byte-exactly — then stamp mtime to NOW.
            # copy2 would preserve the ORIGINAL mtime, which is older than the artifacts compiled
            # from the mutated source, so MSBuild would skip the rebuild and the STALE MUTATED
            # assembly would be tested. The content is byte-identical either way; the sha check below
            # is what proves that.
            shutil.copy2(backup, path)
            os.utime(path, None)
            digest_after = sha256(path)
            restored = digest_before == digest_after
            print(f"  restored      : sha256 identical = {restored}")

            p3, f3, _ = run_test(mut["test"])
            print(f"  after restore : passed={p3} failed={f3}")
            recovered = p3 >= 1 and f3 == 0

            passed_proof = baseline_ok and mutation_detected and restored and recovered
            ok_overall = ok_overall and passed_proof
            print(f"  RESULT        : {'PROVEN' if passed_proof else 'NOT PROVEN'}\n")

            results.append({
                "id": mut["id"], "title": mut["title"], "file": mut["file"], "test": mut["test"],
                "baseline_passed": p, "baseline_failed": f_,
                "mutated_passed": p2, "mutated_failed": f2,
                "restored_sha_identical": restored,
                "after_restore_passed": p3, "after_restore_failed": f3,
                "sha_before": digest_before, "sha_after": digest_after,
                "proven": passed_proof,
            })
    finally:
        shutil.rmtree(backup_dir, ignore_errors=True)

    # 4. no marker may survive ANYWHERE in the tree
    print("=== marker sweep ===")
    leftover = []
    for base, dirs, names in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in (".git", "bin", "obj", "node_modules", ".vs")]
        for n in names:
            if not n.endswith((".cs", ".sql", ".cshtml", ".md", ".csproj", ".json", ".py")):
                continue
            p = os.path.join(base, n)
            # this script legitimately contains the marker string
            if os.path.abspath(p) == os.path.abspath(__file__):
                continue
            # the results file RECORDS the marker by design; it is evidence, not a leftover
            if os.path.basename(p) == 'mutation-proof-results.json':
                continue
            try:
                with open(p, "r", encoding="utf-8", errors="ignore") as fh:
                    if MARKER in fh.read():
                        leftover.append(os.path.relpath(p, ROOT))
            except OSError:
                pass

    if leftover:
        ok_overall = False
        print(f"  MARKERS LEFT BEHIND: {leftover}")
    else:
        print("  no mutation marker survives anywhere in the tree")

    out_path = os.path.join(ROOT, "docs", "construction", "mutation-proof-results.json")
    with open(out_path, "w", encoding="utf-8") as fh:
        json.dump({"marker": MARKER, "leftover_markers": leftover, "mutations": results}, fh, indent=2)
    print(f"\nwrote {os.path.relpath(out_path, ROOT)}")

    print("\n== RESULT ==")
    print("  ALL MUTATION PROOFS PASSED" if ok_overall else "  MUTATION PROOFS FAILED")
    return 0 if ok_overall else 1


if __name__ == "__main__":
    sys.exit(main())
