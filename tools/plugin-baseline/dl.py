from huggingface_hub import snapshot_download
for r,n in [("dvislobokov/csharp-ml-complation","cs"),("dvislobokov/go-ml-complation","go")]:
    snapshot_download(r,repo_type="dataset",local_dir=f"data/{n}",allow_patterns=["data/samples/test-*","data/corpus/test-*"],max_workers=8)
print("done")
