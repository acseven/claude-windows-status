#!/usr/bin/env python3
"""Mechanical Group Policy check (CLAUDE.md, items 1 and 5).

- policy/ClaudeStatus.admx and every policy/<language>/ClaudeStatus.adml parse as XML
- every $(string.X) / $(presentation.X) in the ADMX exists in every ADML (en-US is required),
  and all ADMLs define the same string and presentation ids
- every ADMX value name is read by src/ClaudeStatus/Policy.cs and vice versa
- registry types agree: decimal/enum/boolean policies -> ReadDword, text -> ReadList,
  enums with string values -> ReadString
- all policies use SOFTWARE\\Policies\\ClaudeStatus
Exits non-zero with a list of problems.
"""
import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

NS = {"p": "http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions"}
KEY = r"SOFTWARE\Policies\ClaudeStatus"
problems = []

adml_paths = sorted(glob.glob("policy/*/ClaudeStatus.adml"))
if "policy/en-US/ClaudeStatus.adml" not in [a.replace(os.sep, "/") for a in adml_paths]:
    print("FAIL: policy/en-US/ClaudeStatus.adml is missing")
    sys.exit(1)
try:
    admx_text = open("policy/ClaudeStatus.admx", encoding="utf-8").read()
    admx = ET.fromstring(admx_text)
    admls = {os.path.basename(os.path.dirname(a)): ET.fromstring(open(a, encoding="utf-8").read()) for a in adml_paths}
except (OSError, ET.ParseError) as e:
    print(f"FAIL: cannot read/parse policy files: {e}")
    sys.exit(1)

# References resolve in every language, and every language defines the same ids.
ids = {}
for lang, adml in admls.items():
    strings = {e.get("id") for e in adml.iterfind(".//p:stringTable/p:string", NS)}
    presentations = {e.get("id") for e in adml.iterfind(".//p:presentationTable/p:presentation", NS)}
    ids[lang] = (strings, presentations)
    for kind, name in re.findall(r"\$\((string|presentation)\.(\w+)\)", admx_text):
        if name not in (strings if kind == "string" else presentations):
            problems.append(f"ADMX references missing {kind} '{name}' in the {lang} ADML")
for lang, (strings, presentations) in ids.items():
    if (strings, presentations) != ids["en-US"]:
        diff = (strings ^ ids["en-US"][0]) | (presentations ^ ids["en-US"][1])
        problems.append(f"{lang} ADML ids differ from en-US: {', '.join(sorted(diff))}")

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
        if tag == "enum" and el.find("p:item/p:value/p:string", NS) is not None:
            admx_values[vn] = "string"
        else:
            admx_values[vn] = "list" if tag == "text" else "dword" if tag in ("decimal", "enum", "boolean") else tag

# Value names and types in the code.
code = open("src/ClaudeStatus/Policy.cs", encoding="utf-8").read()
if KEY.replace("\\", "\\\\") not in code and KEY not in code:
    problems.append(f"Policy.cs does not use {KEY}")
code_values = {n: "dword" for n in re.findall(r'ReadDword\("(\w+)"\)', code)}
code_values.update({n: "list" for n in re.findall(r'ReadList\("(\w+)"\)', code)})
code_values.update({n: "string" for n in re.findall(r'ReadString\("(\w+)"\)', code)})

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
print(f"GPO check OK: {len(admx_values)} values ({', '.join(sorted(admx_values))}); "
      f"references resolve in {', '.join(sorted(admls))}.")
