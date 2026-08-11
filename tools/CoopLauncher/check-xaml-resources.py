#!/usr/bin/env python3
"""Fail if any {StaticResource K} in the launcher XAML has no matching x:Key.

A dangling StaticResource does NOT fail the WPF build — it resolves lazily to
DependencyProperty.UnsetValue and crashes at render time (often only in a trigger
state like hover), leaving a ghost window. See COOP-OPS-WORKFLOW.md rule #10.

Run from anywhere:  python tools/CoopLauncher/check-xaml-resources.py
Exit 0 = clean, 1 = dangling reference(s) found.
"""
import re, sys, glob, os

here = os.path.dirname(os.path.abspath(__file__))
files = sorted(glob.glob(os.path.join(here, "*.xaml")))

defined, used = set(), {}
for fp in files:
    txt = open(fp, encoding="utf-8").read()
    for m in re.finditer(r'x:Key="([^"]+)"', txt):
        defined.add(m.group(1))
for fp in files:
    txt = open(fp, encoding="utf-8").read()
    for m in re.finditer(r'\{StaticResource\s+([^}]+?)\s*\}', txt):
        used.setdefault(m.group(1).strip(), set()).add(os.path.basename(fp))

dangling = sorted(k for k in used if k not in defined)
if dangling:
    print("DANGLING StaticResource references (no x:Key defines them):")
    for k in dangling:
        print(f"  - {k}   used in {sorted(used[k])}")
    sys.exit(1)
print(f"OK: {len(used)} StaticResource references all resolve across {len(files)} XAML file(s).")
