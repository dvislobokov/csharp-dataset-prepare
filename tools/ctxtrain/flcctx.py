"""
flc-context/v1.1 prompt assembly (docs/CONTEXT_SPEC-RU.md), shared by the document generator, the evaluation and the
golden fixtures. Token-exact with the plugin engine (idea-ml-completion `NnCompletion` / `tools/nn/eval/eval_inline.py`):
`pretoken_boundary`, `line_end` and `stable_tail` are copied from eval_inline.py (same owner, same licence) so that this
module runs without PyTorch; `tests/test_flcctx.py` checks them against the originals when those are importable.

Text conventions: UTF-8 bytes of the editor text (no BOM, `\\n` line ends). Offsets are byte offsets.
"""
from __future__ import annotations

import os
import re
import sys
from dataclasses import dataclass

# ---------------------------------------------------------------------------------------------------- engine pieces
CTX = 2000                # prompt token budget of NnCompletion.Options (ctx), generation <= 48 on top
SUFFIX_TOKENS = 512       # promptSuffixTokens
MAX_PREFIX = 896          # promptMaxPrefix (v1.1 section 6)
DEPS_MAX = 48             # contextDepsMaxTokens
FACTS_MAX = 192           # contextFactsMaxTokens (incl. the two special tokens)
MAX_ITEMS = 24            # contextMaxItemsPerLine
PREFIX_WINDOW = 20000     # contextPrefixWindow (UTF-16 chars in the spec; bytes here, identical for ASCII identifiers)
CUT_STEP = 256
_CLOSERS = set(b" \t\r)]}>;,\"'`")
KEYS = ("RET", "EXPECT", "ARG", "LOCAL", "FIELD", "PROPERTY", "RECV", "MEMBER", "CALL")          # canonical output order
PRIORITY = ("EXPECT", "RECV", "CALL", "ARG", "LOCAL", "RET", "MEMBER", "FIELD", "PROPERTY")       # budget admission order
SEP = {"MEMBER": "; ", "CALL": " | "}


def engine_paths(engine: str):
    for sub in ("tools/nn/tokenizer", "tools/nn/train", "tools/nn/eval"):
        p = os.path.join(engine, sub)
        if p not in sys.path:
            sys.path.insert(0, p)


class Tok:
    """The plugin tokenizer (cmlbpe, go-code-1) plus the ids and tables the prompt needs."""

    def __init__(self, vocab_path: str):
        import cmlbpe
        self.cmlbpe = cmlbpe
        self.vocab = cmlbpe.Vocab.load(vocab_path)
        self.enc = cmlbpe.Encoder(self.vocab)
        sid = self.vocab.special_id
        self.eot, self.fim_prefix, self.fim_middle, self.fim_suffix = (sid("<|endoftext|>"), sid("<|fim_prefix|>"),
                                                                        sid("<|fim_middle|>"), sid("<|fim_suffix|>"))
        self.file_sep = sid("<|file_sep|>")
        self.r0, self.r1, self.r2 = sid("<|reserved_0|>"), sid("<|reserved_1|>"), sid("<|reserved_2|>")
        self.vocab_size, self.special_base = self.vocab.vocab_size, self.vocab.special_base
        self.line_start = [t[:1] in (b"\n", b"\r") for t in self.vocab.tokens] + [False] * (self.vocab_size - len(self.vocab.tokens))

    def encode(self, b: bytes) -> list[int]:
        return self.enc.encode_bytes(b)

    def decode(self, ids) -> bytes:
        return self.enc.decode_bytes([int(i) for i in ids])


def _is_word_byte(c):
    return 65 <= c <= 90 or 97 <= c <= 122 or c == 95 or c >= 128 or 48 <= c <= 57


def line_end(text: bytes, start: int):
    """eval_inline.line_end: (eol, eol_nl) — where the line content ends (before CR of CRLF) and where the LF is."""
    eol_nl = text.find(b"\n", start)
    if eol_nl < 0:
        eol_nl = len(text)
    eol = eol_nl - 1 if eol_nl > start and text[eol_nl - 1:eol_nl] == b"\r" else eol_nl
    return eol, eol_nl


def pretoken_boundary(tok: Tok, text: bytes, bol: int, cursor: int, eol: int, mode: str = "word-eol") -> int:
    """eval_inline.pretoken_boundary (BpeTokenizer.healBoundary): healing boundary at or before `cursor`."""
    start = bol - 1 if bol > 0 else 0
    pos = last = prev = start
    for t in tok.cmlbpe.pretokenize(text[start:eol]):
        if pos > cursor:
            break
        prev, last = last, pos
        pos += len(t)
    if pos <= cursor:
        prev, last = last, pos
    if mode != "boundary" and last == cursor and cursor > start and _is_word_byte(text[cursor - 1]):
        if mode == "word" or all(c in _CLOSERS for c in text[cursor:eol]):
            return prev
    return last


def stable_tail(tok: Tok, ids: list[int], target: int, hard_cap: int) -> list[int]:
    """eval_inline.stable_tail (InlinePrompt.stableTail)."""
    if target <= 0 or len(ids) <= target:
        return ids
    s0 = len(ids) - target
    c = (s0 // CUT_STEP) * CUT_STEP
    limit = min(len(ids) - target // 2, s0 + 2 * CUT_STEP)
    ls = tok.line_start
    while c < limit and not ls[ids[c]]:
        c += 1
    if c >= limit:
        c = s0
    if len(ids) - c > hard_cap:
        c = len(ids) - hard_cap
    return ids[c:]


# ---------------------------------------------------------------------------------------------------- facts / profile
VALUE_TYPES = {"int", "long", "short", "byte", "sbyte", "uint", "ulong", "ushort", "char", "bool", "float", "double",
               "decimal", "nint", "nuint", "DateTime", "DateTimeOffset", "TimeSpan", "Guid", "DateOnly", "TimeOnly"}
_NULLABLE = re.compile(r"([A-Za-z_][A-Za-z0-9_]*|\]|>|\))\?(?!\?)")


def strip_ref_nullable(t: str | None) -> str | None:
    """v1.1 5.2: keep `?` only after the listed value types and tuples; drop it after reference types."""
    if not t:
        return t
    return _NULLABLE.sub(lambda m: m.group(0) if (m.group(1) in VALUE_TYPES or m.group(1) == ")") else m.group(1), t)


def compact_sig(sig: str) -> str:
    """PromptRenderer.CompactSig: "Task<Order?> GetByIdAsync(Guid id)" -> "GetByIdAsync(Guid id)->Task<Order?>"."""
    paren = sig.find("(")
    if paren < 0:
        return sig
    depth, split = 0, -1
    for i in range(paren - 1, -1, -1):
        c = sig[i]
        if c == ">":
            depth += 1
        elif c == "<":
            depth -= 1
        elif c == " " and depth == 0:
            split = i
            break
    return sig if split < 0 else sig[split + 1:] + "->" + sig[:split]


def _matching(s: str, i: int, o: str, c: str) -> int:
    depth = 0
    for k in range(i, len(s)):
        if s[k] == o:
            depth += 1
        elif s[k] == c:
            depth -= 1
            if depth == 0:
                return k
    return -1


def go_compact_sig(sig: str) -> str:
    """internal/render.CompactSig (Go): "Name[T any](p T) (R, error)" -> "Name[T any](p T)->(R, error)"."""
    op = sig.find("(")
    if op < 0:
        return sig
    br = sig.find("[")
    if 0 <= br < op:
        end = _matching(sig, br, "[", "]")
        if end > 0:
            p = sig.find("(", end)
            if p >= 0:
                op = p
    cl = _matching(sig, op, "(", ")")
    if cl < 0:
        return sig
    res = sig[cl + 1:].strip()
    return sig[:cl + 1] if not res else sig[:cl + 1] + "->" + res


LANG = "csharp"   # set by the caller: "csharp" | "go"


def _ty(t):
    return strip_ref_nullable(t) if LANG == "csharp" else t


def item_of(f: dict) -> str:
    kind, name = f.get("kind"), f.get("name") or ""
    over = f" +{f['overloads'] - 1}" if (f.get("overloads") or 1) > 1 else ""
    if LANG == "go":
        if f.get("signature"):
            return go_compact_sig(f["signature"])
        if kind == "type":
            return name
        return f"{name}:{f['type']}" if f.get("type") else name
    if kind == "method":
        return strip_ref_nullable(compact_sig(f.get("signature") or name)) + over
    if kind == "constructor":
        return strip_ref_nullable(f.get("signature") or "new()") + over
    if kind in ("type", "namespace"):
        return name
    t = strip_ref_nullable(f.get("type"))
    return f"{name}:{t}" if t else name


IDENT = re.compile(rb"[A-Za-z_][A-Za-z0-9_]*")


def prefix_identifiers(text: bytes, line_start: int) -> set[bytes]:
    """Identifiers of the visible prefix window (v1.1 5.3): [line_start - PREFIX_WINDOW, line_start)."""
    return set(IDENT.findall(text[max(0, line_start - PREFIX_WINDOW):line_start]))


def rank(facts: list[dict], seen: set[bytes]) -> list[dict]:
    """Stable re-rank: names used in the visible prefix first (the extractor order is kept otherwise)."""
    return sorted(facts, key=lambda f: 0 if (f.get("name") or "").lstrip("@").encode() in seen else 1)


def fact_lines(sem: dict, seen: set[bytes]) -> dict[str, list[str]]:
    """v1.1 keys -> item lists (before the budget). `sem` is a semantic row of the dataset (C# field names)."""
    out: dict[str, list[str]] = {}
    if sem.get("return_type"):
        out["RET"] = [_ty(sem["return_type"])]
    if sem.get("expected_type"):
        out["EXPECT"] = [_ty(sem["expected_type"])]
    out["ARG"] = [item_of(f) for f in rank(sem.get("parameters") or [], seen)]
    out["LOCAL"] = [item_of(f) for f in rank(sem.get("locals") or [], seen)]
    this = rank(sem.get("this_members") or [], seen)
    out["FIELD"] = [item_of(f) for f in this if f.get("kind") in ("field", "const", "event")]
    out["PROPERTY"] = [item_of(f) for f in this if f.get("kind") == "property"]
    if sem.get("receiver_type"):
        k = sem.get("receiver_kind")
        out["RECV"] = [f"{k} {sem['receiver_type']}" if k in ("type", "namespace", "package") else _ty(sem["receiver_type"])]
        out["MEMBER"] = [item_of(f) for f in rank(sem.get("members") or [], seen)]
    cands = []
    for c in sem.get("invocation_candidates") or []:
        sig = c.get("signature") or ""
        s = (go_compact_sig(sig) if LANG == "go" else strip_ref_nullable(compact_sig(sig)))
        if c.get("argument_index") is not None:
            s += f" @{c['argument_index']}"
        if c.get("parameter_name"):
            s += f" {c['parameter_name']}" + (f":{_ty(c['parameter_type'])}" if c.get("parameter_type") else "")
        cands.append(s)
    out["CALL"] = cands
    return {k: v[:MAX_ITEMS] for k, v in out.items() if v}


def facts_block(tok: Tok, lines: dict[str, list[str]], budget: int = FACTS_MAX) -> list[int]:
    """v1.1 5.4: token-budgeted block, [<|reserved_1|>] … [<|reserved_2|>]; empty list when nothing fits."""
    remaining = budget - 2
    admitted: dict[str, str] = {}
    for key in PRIORITY:
        items = list(lines.get(key) or [])
        while items:
            text = key + " " + SEP.get(key, " ").join(items)
            cost = len(tok.encode(text.encode())) + 1
            if cost <= remaining:
                break
            items.pop()
        if items:
            admitted[key] = text
            remaining -= cost
    if not admitted:
        return []
    body = "\n".join(admitted[k] for k in KEYS if k in admitted)
    return [tok.r1] + tok.encode(body.encode()) + [tok.r2]


def deps_ids(tok: Tok, roots: list[str], budget: int = DEPS_MAX) -> list[int]:
    """v1.1 4: longest head of the ordered roots whose encoding fits the budget; [<|reserved_0|>] + text."""
    best: list[int] = []
    for k in range(1, len(roots) + 1):
        ids = tok.encode(" ".join(roots[:k]).encode())
        if len(ids) > budget:
            break
        best = ids
    return [tok.r0] + best if best else []


# ---------------------------------------------------------------------------------------------------- prompt
@dataclass
class Prompt:
    ids: list[int]          # full prompt token ids
    typed: bytes            # bytes between the healed boundary and the caret (constrain the first generated tokens)
    boundary: int           # healed boundary (byte offset in the editor text)
    split_ok: bool          # encode(W[:I]) + encode(W[I:]) == stable-tail window (v1.1 2 invariant)
    facts_tokens: int
    deps_tokens: int


def build(tok: Tok, text: bytes, caret: int, path: str, deps: list[int] | None = None, facts: list[int] | None = None,
          reserve_context: bool = True) -> Prompt:
    """SPM prompt of v1.1 section 2 for the editor `text` (target already removed) and the caret byte offset.
    `deps`/`facts` are ready id lists (deps_ids / facts_block) or None. `reserve_context` keeps the constant
    reservation of section 6 (use the same value for every model that is compared)."""
    deps, facts = deps or [], facts or []
    bol = text.rfind(b"\n", 0, caret) + 1
    eol, _ = line_end(text, caret)
    boundary = pretoken_boundary(tok, text, bol, caret, eol)
    hdr = [tok.file_sep] + tok.encode(path.encode() + b"\n")
    a = max(0, boundary - 40000)
    if a > 0:
        nl = text.find(b"\n", a, boundary)
        a = nl + 1 if nl >= 0 else a
    pre = tok.encode(text[a:boundary])
    suf = tok.encode(text[eol:eol + 16000])[:SUFFIX_TOKENS]
    hard_cap = CTX - len(hdr) - len(suf) - 3 - ((DEPS_MAX + FACTS_MAX) if reserve_context else 0)
    budget = min(MAX_PREFIX if reserve_context else 1024, hard_cap)
    window = stable_tail(tok, pre, budget, hard_cap)
    w = tok.decode(window)
    # insertion point: start of the last pre-token that starts with CR/LF; none -> 0; caret in indentation -> end
    if boundary <= bol:
        cut = len(w)
    else:
        cut, pos = 0, 0
        for t in tok.cmlbpe.pretokenize(w):
            if t[:1] in (b"\n", b"\r"):
                cut = pos
            pos += len(t)
    left, right = tok.encode(w[:cut]), tok.encode(w[cut:])
    split_ok = left + right == window
    if not split_ok:          # never silently change the line's tokens: fall back to no block
        left, right, facts = window, [], []
    ids = [tok.fim_prefix, tok.fim_suffix] + suf + [tok.fim_middle] + deps + hdr + left + facts + right
    return Prompt(ids, text[boundary:caret], boundary, split_ok, len(facts), len(deps))
