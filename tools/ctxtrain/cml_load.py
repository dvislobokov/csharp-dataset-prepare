"""
Load a plugin neural model (`.cml`, CMLN v1, idea-ml-completion docs/NN-FORMAT.md) into the engine's PyTorch `CodeLM`
(tools/nn/train/model.py), so that a shipped model can be fine-tuned or evaluated without its lost .pt checkpoint.
int8 matrices are dequantised exactly (W = q * scale per output column); `[in, out]` is transposed back to nn.Linear's
`[out, in]`; `tok_emb` is `[dModel, vocab]` -> Embedding `[vocab, dModel]`.
"""
from __future__ import annotations

import struct

import numpy as np


def read_cml(path: str):
    with open(path, "rb") as f:
        buf = f.read()
    assert buf[:4] == b"CMLN", "not a neural .cml"
    version, mlen = struct.unpack_from("<II", buf, 4)
    assert version == 1, version
    meta = dict(line.split("=", 1) for line in buf[12:12 + mlen].decode().split("\n") if line)
    p = 12 + mlen
    (count,) = struct.unpack_from("<I", buf, p)
    p += 4
    tensors = {}
    for _ in range(count):
        (nlen,) = struct.unpack_from("<H", buf, p)
        p += 2
        name = buf[p:p + nlen].decode()
        p += nlen
        dtype, rank = buf[p], buf[p + 1]
        p += 2
        dims = struct.unpack_from("<" + "I" * rank, buf, p)
        p += 4 * rank
        data_off, scale_off = struct.unpack_from("<QQ", buf, p)
        p += 16
        if dtype == 0:
            tensors[name] = np.frombuffer(buf, dtype="<f4", count=dims[0], offset=data_off).copy()
        else:
            rows, cols = dims
            q = np.frombuffer(buf, dtype=np.int8, count=rows * cols, offset=data_off).reshape(rows, cols).astype(np.float32)
            s = np.frombuffer(buf, dtype="<f4", count=cols, offset=scale_off)
            tensors[name] = q * s[None, :]
    return meta, tensors


def config_from_meta(meta: dict):
    from model import ModelConfig
    return ModelConfig(vocab_size=int(meta["vocabSize"]), d_model=int(meta["dModel"]), n_layers=int(meta["nLayers"]),
                       n_heads=int(meta["nHeads"]), n_kv_heads=int(meta["nKvHeads"]), ffn_dim=int(meta["ffnDim"]),
                       max_context=int(meta["maxContext"]), rope_theta=float(meta["ropeTheta"]), norm_eps=float(meta["normEps"]),
                       tied_embeddings=meta.get("tiedEmbeddings", "true") == "true", name=meta.get("preset", "from-cml"))


def load_model(path: str):
    """CodeLM with the weights of the .cml (float32), plus the meta dict."""
    import torch
    from model import CodeLM
    meta, t = read_cml(path)
    cfg = config_from_meta(meta)
    m = CodeLM(cfg)
    sd = {"tok_emb.weight": torch.from_numpy(t["tok_emb"].T.copy()),
          "final_norm.weight": torch.from_numpy(t["final_norm"])}
    for i in range(cfg.n_layers):
        p = f"layers.{i}."
        sd[p + "attn_norm.weight"] = torch.from_numpy(t[p + "attn_norm"])
        sd[p + "ffn_norm.weight"] = torch.from_numpy(t[p + "ffn_norm"])
        for n, mod in (("wq", "attn.wq"), ("wk", "attn.wk"), ("wv", "attn.wv"), ("wo", "attn.wo"),
                       ("w1", "mlp.w1"), ("w3", "mlp.w3"), ("w2", "mlp.w2")):
            sd[p + mod + ".weight"] = torch.from_numpy(t[p + n].T.copy())
    if not cfg.tied_embeddings:
        sd["lm_head.weight"] = torch.from_numpy(t["lm_head"].T.copy())
    missing, unexpected = m.load_state_dict(sd, strict=False)
    assert not [k for k in missing if "rope" not in k] and not unexpected, (missing, unexpected)
    return m, meta
