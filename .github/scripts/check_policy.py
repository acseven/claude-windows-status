#!/usr/bin/env python3
"""Mechanical Group Policy check (CLAUDE.md, items 1 and 5).

- policy/ClaudeStatus.admx and policy/en-US/ClaudeStatus.adml parse as XML
- every $(string.X) / $(presentation.X) in the ADMX exists in the ADML
- every ADMX value name is read by src/ClaudeStatus/Policy.cs and vice versa
- registry types agree: decimal/enum/boolean policies -> ReadDword, text -> ReadList
- all policies use SOFTWARE\\Policies\\ClaudeStatus
Exits non-zero with a list of problems.
"""
import re
import sys
import xml.etree.ElementTree as ET

NS = {"p": "http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions"}
KEY = r"SOFTWARE\Policies\ClaudeStatus"
problems = []

try:
    admx_text = open("policy/ClaudeStatus.admx", encoding="utf-8").read()
    adml_text = open("policy/en-US/ClaudeStatus.adml", encoding="utf-8").read()
    admx = ET.fromstring(admx_text)
    adml = ET.fromstring(adml_text)
except (OSError, ET.ParseError) as e:
    print(f"FAIL: cannot read/parse policy files: {e}")
    sys.exit(1)

# References resolve.
strings = {e.get("id") for e in adml.iterfind(".//p:stringTable/p:string", NS)}
presentations = {e.get("id") for e in adml.iterfind(".//p:presentationTable/p:presentation", NS)}
for kind, name in re.findall(r"\$\((string|presentation)\.(\w+)\)", admx_text):
    if name not in (strings if kind == "string" else presentations):
        problems.append(f"ADMX references missing {kind} '{name}' in the ADML")

# Value names and types in the ADMX.
admx_values = {}  # value name -> "dword" | "list"
for pol in admx.iterfind(".//p:policies/p:policy", NS):
    name = pol.get("name")
    if pol.get("key") != KEY:
        problems.append(f"policy {name}: key is {pol.get('key')!r}, expected {KEY!r}")
    if pol.get("valueName"):  # boolean policy (enabledValue/disabledValue)
        admx_values[pol.get("valueName")] = "dword"
    for el in pol.iterfind("p:elements/*", NS):
        tag = el.tag.split("}")[1]
        vn = el.get("valueName")
        if not vn:
            continue
        admx_values[vn] = "list" if tag == "text" else "dword" if tag in ("decimal", "enum", "boolean") else tag

# Value names and types in the code.
code = open("src/ClaudeStatus/Policy.cs", encoding="utf-8").read()
if KEY.replace("\\", "\\\\") not in code and KEY not in code:
    problems.append(f"Policy.cs does not use {KEY}")
code_values = {n: "dword" for n in re.findall(r'ReadDword\("(\w+)"\)', code)}
code_values.update({n: "list" for n in re.findall(r'ReadList\("(\w+)"\)', code)})

for vn, t in admx_values.items():
    if vn not in code_values:
        problems.append(f"ADMX value '{vn}' is not read by Policy.cs")
    elif code_values[vn] != t:
        problems.append(f"'{vn}': ADMX type {t}, Policy.cs reads it as {code_values[vn]}")
for vn in code_values:
    if vn not in admx_values:
        problems.append(f"Policy.cs reads '{vn}', which the ADMX does not define")

if problems:
    print("GPO check FAILED:")
    for p in problems:
        print(" -", p)
    sys.exit(1)
print(f"GPO check OK: {len(admx_values)} values ({', '.join(sorted(admx_values))}); references resolve.")
