#!/usr/bin/env python3
"""Aggregate acc-*.tsv (PluginBaseline acc) into a metrics dict. usage: analyze.py out_dir tag -> prints JSON"""
import csv, sys, json, glob, os, collections
csv.field_size_limit(10**9)
def unesc(s):
    out=[];i=0
    while i<len(s):
        c=s[i]
        if c=="\\" and i+1<len(s):
            n=s[i+1]; out.append({"t":"\t","n":"\n","r":"\r","\\":"\\"}.get(n,"\\"+n)); i+=2
        else: out.append(c); i+=1
    return "".join(out)
def load(f):
    rows=[]
    for r in csv.DictReader(open(f), delimiter="\t", quoting=csv.QUOTE_NONE):
        if r["exact"]=="ERR" or r["conf"] is None: continue
        r["exact"]=int(r["exact"]); r["conf"]=float(r["conf"]); r["punct"]=int(r["punct"]); r["repeated"]=int(r["repeated"]); r["empty"]=int(r["empty"])
        r["after_dot"]=int(r["after_dot"]); r["before_ws"]=int(r["before_ws"]); r["healm"]=int(r["heal_miss"])
        # lenient: if caret is after a typed space/tab and the model emitted a leading space, drop it (IDE would not insert a doubled space)
        txt=unesc(r["text"]).rstrip(" \t\r\n"); tg=unesc(r["target"]).rstrip(" \t\r\n")
        r["exact_lenient"]=int(r["exact"] or (r["before_ws"] and txt.lstrip(" ")==tg and txt!=""))
        rows.append(r)
    return rows
def shown(r,thr,suppress=True): return r["conf"]>=thr and not r["repeated"] and not r["empty"] and not (suppress and r["punct"]) 
def gate(r,thr): return shown(r,0.5 if r["after_dot"] else thr)
def stats(rows,pred,key="exact"):
    n=len(rows); s=[r for r in rows if pred(r)]
    return {"n":n,"shown":len(s),"shown_rate":len(s)/n if n else 0,"precision":(sum(r[key] for r in s)/len(s)) if s else None,"correct_shown_of_all":(sum(r[key] for r in s)/n) if n else 0}
def analyze(rows):
    n=len(rows); out={"n":n}
    for key in ("exact","exact_lenient"):
        o={}
        o["exact_match_all_positions_unconditional"]=sum(r[key] for r in rows)/n
        o["exact_match_non_punct_only"]=sum(r[key] for r in rows if not r["punct"])/max(1,sum(1 for r in rows if not r["punct"]))
        for thr in (0.5,0.7,0.8): o[f"gate_{thr}"]=stats(rows,lambda r:shown(r,thr),key)
        o["plugin_policy(0.7, 0.5 after dot)"]=stats(rows,lambda r:gate(r,0.7),key)
        o["gate_0.7_closers_shown"]=stats(rows,lambda r:shown(r,0.7,False),key)
        kinds=collections.defaultdict(list)
        for r in rows: kinds[r["kind"]].append(r)
        o["by_kind"]={k:{"n":len(v),"exact_all":sum(r[key] for r in v)/len(v),"gate_0.5":stats(v,lambda r:shown(r,0.5),key),"gate_0.7":stats(v,lambda r:shown(r,0.7),key),"gate_0.8":stats(v,lambda r:shown(r,0.8),key),"plugin_policy":stats(v,lambda r:gate(r,0.7),key)} for k,v in sorted(kinds.items())}
        out[key]=o
    out["after_dot_positions"]=sum(r["after_dot"] for r in rows)
    out["before_ws_positions"]=sum(r["before_ws"] for r in rows)
    out["heal_miss"]=sum(r["healm"] for r in rows)
    pt=sorted(int(r["prompt_tokens"]) for r in rows); out["prompt_tokens_p50_p95"]=[pt[len(pt)//2],pt[int(len(pt)*.95)]]
    return out
if __name__=="__main__":
    d,tag=sys.argv[1],sys.argv[2]; res={}
    for f in sorted(glob.glob(f"{d}/acc-*-{tag}.tsv")):
        m=os.path.basename(f)[4:-len(tag)-5]; res[m]=analyze(load(f))
    print(json.dumps(res,indent=1))
