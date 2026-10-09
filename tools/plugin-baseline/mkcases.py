import pyarrow.parquet as pq, pyarrow as pa, glob, sys, random, base64, json, collections
lang, root, out, N = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4])
cols=["sample_id","repository_id","relative_path","source_sha256","caret_byte_offset","caret_kind","target_text","is_test"]
t=pa.concat_tables([pq.read_table(f,columns=cols) for f in sorted(glob.glob(f"{root}/data/samples/test-*.parquet"))])
print(lang,"samples",t.num_rows)
rows=t.to_pylist()
rows=[r for r in rows if r["target_text"] and r["target_text"].strip()]
by=collections.defaultdict(list)
for r in rows: by[r["caret_kind"]].append(r)
tot=len(rows); print({k:len(v) for k,v in sorted(by.items())})
floor=min(60, N//len(by)); rest=N-floor*len(by)
rnd=random.Random(20261009)
pick=[]; quota={}
for k,v in sorted(by.items()):
    q=min(len(v), floor+round(rest*len(v)/tot)); quota[k]=q
    v=sorted(v,key=lambda r:r["sample_id"]); pick+=rnd.sample(v,q)
print("quota",quota,"picked",len(pick))
need={(r["repository_id"],r["relative_path"]) for r in pick}
content={}
for f in sorted(glob.glob(f"{root}/data/corpus/test-*.parquet")):
    pf=pq.ParquetFile(f)
    for b in pf.iter_batches(columns=["repository_id","relative_path","sha256","content"],batch_size=64):
        for r in b.to_pylist():
            k=(r["repository_id"],r["relative_path"])
            if k in need: content[k]=(r["sha256"],r["content"])
st=collections.Counter(); ok=[]
for r in pick:
    k=(r["repository_id"],r["relative_path"])
    if k not in content: st["no_corpus_file"]+=1; continue
    sha,c=content[k]
    if sha!=r["source_sha256"]: st["sha_mismatch"]+=1; continue
    b=c.encode("utf-8"); o=r["caret_byte_offset"]; tg=r["target_text"].encode("utf-8")
    if b[o:o+len(tg)]!=tg:
        # BOM-shifted?
        if b[o-3:o-3+len(tg)]==tg: st["bom_adjusted(-3)"]+=1; o=o-3
        else: st["target_mismatch"]+=1; continue
    ok.append((r,b[:o],b[o+len(tg):]))
    st["ok"]+=1
print(dict(st))
e=lambda x: base64.b64encode(x).decode()
with open(out,"w") as f:
    for r,bf,af in ok:
        f.write("\t".join([r["sample_id"],r["caret_kind"],str(int(bool(r["is_test"]))),e(r["relative_path"].encode()),e(bf),e(af),e(r["target_text"].encode())])+"\n")
json.dump({"lang":lang,"samples_total":tot,"picked":len(pick),"status":dict(st),"quota":quota,"kinds_total":{k:len(v) for k,v in by.items()}},open(out+".meta.json","w"),indent=1)
