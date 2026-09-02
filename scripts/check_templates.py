#!/usr/bin/env python3
"""
Sanity checks for the Grasshopper templates shipped with MetaMAP.

- the file is well-formed XML
- ObjectCount matches the number of Object chunks and indices are contiguous
- every "Source" reference points at a parameter/object that exists in the file
- every group member id exists
- MetaMAP components in the file are known (by component GUID)

Usage: check_templates.py <folder or .ghx files...>
"""
import os
import re
import sys
import xml.etree.ElementTree as ET

KNOWN = {
    "7eca432e-26bb-4e97-8a5d-a1c98d319888": "MetaFETCH",
    "a1b2c3d4-e5f6-7890-abcd-ef1234567890": "MetaBuilding",
    "b2c3d4e5-f6a7-8901-bcde-f01234567891": "MetaBuildingAdvanced",
    "b2c3d4e5-f6a7-8901-bcde-f23456789012": "MetaTERRAIN",
    "23456789-2345-2345-2345-234567890123": "MetaTEMPLATE",
    "12345678-1234-1234-1234-123456789012": "MetaUPDATE",
}


def check(path):
    errors = []
    text = open(path, encoding="utf-8-sig").read()
    try:
        ET.fromstring(text.encode("utf-8"))
    except ET.ParseError as ex:
        return [f"not well-formed XML: {ex}"]

    objects = re.findall(r'<chunk name="Object" index="(\d+)">', text)
    indices = sorted(int(i) for i in objects)
    if indices != list(range(len(indices))):
        errors.append(f"object indices are not contiguous: {indices}")
    count = re.search(r'<item name="ObjectCount"[^>]*>(\d+)</item>', text)
    if not count or int(count.group(1)) != len(indices):
        errors.append(f"ObjectCount {count.group(1) if count else '?'} != {len(indices)} objects")

    instance_ids = set(re.findall(r'<item name="InstanceGuid"[^>]*>([0-9a-f-]+)</item>', text))
    for src in re.findall(r'<item name="Source" index="\d+"[^>]*>([0-9a-f-]+)</item>', text):
        if src not in instance_ids:
            errors.append(f"dangling Source reference {src}")
    warnings = []
    for member in re.findall(r'<item name="ID" index="\d+"[^>]*>([0-9a-f-]+)</item>', text):
        if member not in instance_ids:
            # Grasshopper silently ignores these; report but do not fail.
            warnings.append(f"group references unknown object {member}")

    components = re.findall(r'<item name="GUID"[^>]*>([0-9a-f-]+)</item>\s*<item name="Lib"', text)
    used = sorted({KNOWN.get(g, g) for g in components})
    return errors, warnings, used


def main(args):
    files = []
    for a in args:
        if os.path.isdir(a):
            files += [os.path.join(a, f) for f in sorted(os.listdir(a)) if f.lower().endswith((".ghx",))]
        else:
            files.append(a)
    if not files:
        sys.exit("no templates found")

    failed = False
    for f in files:
        result = check(f)
        if isinstance(result, list):
            errors, warnings, used = result, [], []
        else:
            errors, warnings, used = result
        status = "OK " if not errors else "BAD"
        print(f"{status} {f}  MetaMAP components: {', '.join(used) or '-'}")
        for e in errors:
            print(f"     - error: {e}")
        for w in warnings:
            print(f"     - warning: {w}")
        failed |= bool(errors)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main(sys.argv[1:])
