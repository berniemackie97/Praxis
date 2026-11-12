#!/usr/bin/env python3
"""Set Project Status to Blocked/Ready from native blocked-by links. Dry run unless --apply."""
import json, subprocess, re, sys
R="berniemackie97/Praxis"; PROJ="PVT_kwHOAqa_Ic4BqpmJ"; F="PVTSSF_lAHOAqa_Ic4BqpmJzho_xyU"
OPT={"Ready":"f30b269d","Blocked":"72cd6525"}
dry = "--apply" not in sys.argv
def gh(*a):
    p=subprocess.run(["gh",*a],text=True,capture_output=True)
    if p.returncode: raise RuntimeError(p.stderr)
    return p.stdout
items=json.loads(gh("project","item-list","3","--owner","berniemackie97","--format","json","--limit","1000"))["items"]
changes=[]; ready=[]; blocked=[]
for it in items:
    c=it.get("content") or {}
    if c.get("type")!="Issue" or c.get("repository")!=R: continue
    title=c.get("title",""); n=c["number"]
    if re.match(r"^\[(Epic|Feature|Roadmap)\]",title): continue
    st=it.get("status")
    state=json.loads(gh("api",f"repos/{R}/issues/{n}","--jq","{s:.state}"))["s"]
    if state!="open": continue
    if st in ("In Progress","In Review","Verification","Done"): continue
    bl=json.loads(gh("api",f"repos/{R}/issues/{n}/dependencies/blocked_by","--jq","[.[]|select(.state==\"open\")|.number]"))
    want="Blocked" if bl else "Ready"
    (blocked if bl else ready).append((n,title[:70],bl))
    if st!=want:
        changes.append((n,st,want))
        if not dry:
            gh("project","item-edit","--id",it["id"],"--project-id",PROJ,"--field-id",F,"--single-select-option-id",OPT[want])
print("READY:"); [print(" ",n,t) for n,t,_ in sorted(ready)]
print("BLOCKED:",len(blocked)); print("CHANGES:",len(changes)); [print(" ",c) for c in changes]
