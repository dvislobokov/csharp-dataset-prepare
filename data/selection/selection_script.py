#!/usr/bin/env python3
"""Deterministic, metadata-only repository selection for the C# FLC dataset.

Input : /data/dataset-test/data/csharp-search.jsonl  (7253 lines; 10 metadata fields)
Output: selected.jsonl, rejected.jsonl, license_unknown.jsonl, restricted_license.jsonl,
        pilot_candidates.md, report.md   (all under data/selection/)

NO network access. The jsonl descriptions are untrusted data: they are only ever
pattern-matched, never executed or interpreted. Run with: python3 -I selection_script.py

Every rule and threshold is justified from the observed distributions in report.md.
This script is pure and deterministic: same input -> byte-identical outputs.
"""
import json, re, math, collections, os, itertools

HERE = os.path.dirname(os.path.abspath(__file__))
import sys
# Optional arguments: <input search jsonl> <output dir> (defaults: the original search and this directory).
IN = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "..", "csharp-search.jsonl")
OUT = sys.argv[2] if len(sys.argv) > 2 else HERE

# ---------------------------------------------------------------------------
# License policy (SELECTION GOAL 1)
# ---------------------------------------------------------------------------
# Permissive allowlist = exactly the SPDX ids requested. Only these are eligible.
PERMISSIVE = {"MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "0BSD",
              "Unlicense", "ISC", "MS-PL", "Zlib"}
# Copyleft / weak-copyleft / reciprocal -> restricted bucket (not selected).
RESTRICTED_PREFIX = ("GPL", "LGPL", "AGPL", "MPL", "EPL", "OSL", "EUPL",
                     "MS-RL", "CC-BY-SA", "MulanPSL")

def license_bucket(lic):
    """Return 'permissive' | 'restricted' | 'unknown'."""
    if lic in PERMISSIVE:
        return "permissive"
    if lic and lic.startswith(RESTRICTED_PREFIX):
        return "restricted"
    # NOASSERTION, null, and permissive-ish ids NOT on the explicit allowlist
    # (MIT-0, WTFPL, CC0-1.0, BSL-1.0, PostgreSQL, AFL-3.0, CC-BY-4.0, ...) are
    # routed to 'unknown' for manual verification rather than silently trusted.
    return "unknown"

# ---------------------------------------------------------------------------
# Spam / malware / abuse heuristics (SELECTION GOAL 2)
# High precision: word-boundary matches, with explicit benign exceptions so that
# defensive/analysis/legitimate tooling is NOT rejected.
# ---------------------------------------------------------------------------
# Clear abuse signals. Matched against "<full_name> <description> <topics>".
ABUSE_TERMS = [
    r"\bcheats?\b", r"\baimbot\b", r"\bwallhack\b", r"\btriggerbot\b",
    r"\bno-?recoil\b", r"\bskin-?changer\b", r"game-?hacking", r"\bgamehacking\b",
    r"\bstealer\b", r"\bkeylogger\b", r"\bgrabber\b", r"\bexfiltrat", r"\bransomware\b",
    r"\bdrainer\b", r"wallet-?drainer", r"free-?robux", r"\brobux\b",
    r"\bspoofer\b", r"hwid-?spoof", r"\bundetected\b", r"\bexecutor\b",
    r"\bkeygen\b", r"\bcrack(ed|er|s)?\b", r"auto-?cracker",
    r"\bshellcode\b", r"\bc2\b", r"\bimplant\b", r"cobaltstrike", r"cobalt-?strike",
    r"privilege-?escalation", r"\bprivesc\b", r"lateral-?movement",
    r"\bamsi-?bypass\b", r"\bedr-?bypass\b", r"av-?bypass", r"anti-?cheat-?bypass",
    r"uac-?bypass", r"captcha-?bypass", r"recaptcha", r"captcha-?solv",
]
# Standalone offensive-security / abuse primitives expressed as topics or phrases.
ABUSE_TOPIC = {
    "aimbot", "wallhack", "cheat", "game-hacking", "gamehacking", "stealer",
    "keylogger", "ransomware", "cobaltstrike", "redteam", "red-teaming",
    "red-team", "privilege-escalation", "privesc", "exploitation-framework",
    "reverse-shell", "dcsync", "kerberoast", "lateral-movement", "shellcode",
}
# Benign contexts that neutralise a weak keyword hit (defensive / analysis / DI / AOP).
ABUSE_BENIGN = [
    r"malware-analysis", r"malware-protection", r"anti-malware", r"malware-detection",
    r"aspect-injector", r"dependency-inject", r"\bdi\b", r"inversion-of-control",
    r"code-injection engine", r"process-injection library",
    r"packet", r"captcha library", r"cheat engine clone", r"cheat engine tool",
]

def abuse_reasons(r, hay):
    reasons = []
    benign = any(re.search(p, hay) for p in ABUSE_BENIGN)
    hits = [p for p in ABUSE_TERMS if re.search(p, hay)]
    topics = set(r["topics"])
    tp = topics & ABUSE_TOPIC
    # Require either a topic-level abuse tag (strong) or a keyword hit that is not
    # explained by a benign context.
    if tp:
        reasons.append("abuse_topic")
    elif hits and not benign:
        reasons.append("abuse_keyword")
    return reasons

# ---------------------------------------------------------------------------
# Low-signal / non-code heuristics (SELECTION GOAL 3)
# ---------------------------------------------------------------------------
# Repos that are lists/books/slides/docs rather than an idiomatic C# code project.
NONCODE_NAME = re.compile(r"(^awesome-|-awesome$|\bawesome-dotnet|roadmap|"
                          r"interview-questions|cheat-?sheet)", re.I)
NONCODE_DESC = re.compile(r"(curated list|awesome list|a collection of (awesome|resources|links)|"
                          r"my (presentations|slides)|public presentations|"
                          r"published by packt|slides? (deck|from)|lecture (notes|slides))", re.I)
# Educational (still real C#, kept but down-weighted & flagged, not rejected).
EDU = re.compile(r"(tutorial|\bcourse\b|bootcamp|\blearn(ing)?\b|homework|assignment|"
                 r"\bexercise(s)?\b|\bkata\b|leetcode|getting-started|quickstart|"
                 r"study-guide|workshop|for beginners)", re.I)
# Throwaway/auto-generated GitHub usernames (random stem + digits + generated suffix).
THROWAWAY_OWNER = re.compile(r"^[a-z]{5,}\d{2,}-(blip|bit|hue|boop|dotcom|coder|source|"
                             r"ltd|dev|cyber|gen|user|tmp)$", re.I)
# High-entropy owner name (long, no vowel structure) + digits -> likely bot.
def looks_gibberish(owner):
    o = owner.lower()
    if THROWAWAY_OWNER.match(o):
        return True
    stem = re.sub(r"[-_0-9]", "", o)
    if len(stem) >= 10 and re.search(r"\d{3,}", o):
        vowels = sum(c in "aeiou" for c in stem)
        if vowels / len(stem) < 0.22:  # consonant-heavy random string
            return True
    return False

# ---------------------------------------------------------------------------
# Size thresholds (SELECTION GOAL 3). size is repo KB incl. git history.
# Distribution (permissive): p5=178, p25=1687, median=9727, p90=208137 KB.
# ---------------------------------------------------------------------------
TINY_FLAG_KB = 500          # < ~0.5 MB -> flag "tiny" (little code likely)
TINY_REJECT_KB = 30         # < 30 KB AND no description AND no topics -> reject
LARGE_FLAG_KB = 1_000_000   # > ~1 GB -> flag "large"
ASSETS_RISK_KB = 300_000    # game/unity + > ~300 MB -> flag assets_heavy_risk
HUGE_ASSETS_REJECT_KB = 8_000_000  # > ~8 GB AND game/unity -> reject (asset dump)

GAME_RE = re.compile(r"(unity|unity3d|godot|monogame|\bxna\b|\bfna\b|stride|"
                     r"bepinex|melonloader|vrchat|\bmod(ding|loader|s)?\b|game-?engine)", re.I)

# ---------------------------------------------------------------------------
# Activity cutoff (SELECTION GOAL 4).
# Observed pushed_at range is 2025-06 .. 2026-10 (entire corpus is <16 months old),
# so a 24-month cutoff passes everything. We keep it as an explicit, documented gate
# (future-proofing for the 34k manifest) and otherwise reward recency via scoring.
# Manual exclusions (user decision 2026-10-09): very large repositories (GitHub size incl. history/assets) not wanted
# in the corpus. Kept as data so the selection stays reproducible; reason code "manual_exclude".
MANUAL_EXCLUDE = {
    "allenai/ai2thor",
    "ProteoWizard/pwiz",
    "Azure/azure-sdk-for-net",
    "Mapsui/Mapsui",
    "moorestech/moorestech",
    "umasteeringgroup/UMA",
    "ErikEJ/EFCorePowerTools",
}

ACTIVITY_CUTOFF = "2024-10-09T00:00:00Z"
DATE_MIN = "2025-06-02"   # observed min (for recency normalisation)
DATE_MAX = "2026-10-08"   # observed max

def to_days(ts):
    import datetime
    return (datetime.date.fromisoformat(ts[:10]) - datetime.date(2025, 1, 1)).days

# ---------------------------------------------------------------------------
# Category mapping (SELECTION GOAL 6). First match wins; ordered specific->generic.
# ---------------------------------------------------------------------------
CATEGORY_RULES = [
    # devtools first: a Roslyn analyzer / decompiler / VS extension is a devtool even
    # when it carries a 'unity' topic (e.g. ILSpy decompiles Unity assemblies).
    ("devtools",     r"\broslyn\b|source-?generator|sourcegenerator|\banalyzer(s)?\b|decompil|\bmsbuild\b|vs-?extension|visual-studio-extension|language-server|\bcompiler\b"),
    ("game/unity",   r"\bunity\b|unity3d|\bgodot\b|monogame|\bxna\b|\bfna\b|stride|game-?engine|gamedev|game-development|\bvrchat\b|bepinex|melonloader|monogame"),
    ("desktop-ui",   r"\bwpf\b|winforms|\bavalonia\b|avaloniaui|\bmaui\b|\buwp\b|winui|winui3|\bxaml\b|\buno-platform\b|fluent-design|desktop-app"),
    ("devtools",     r"visual-studio|\bide\b|code-generator|code-generation"),
    ("web/aspnet",   r"aspnet|asp-net|aspnetcore|asp-net-core|\bblazor\b|\bmvc\b|razor|webapi|web-api|\bsignalr\b|minimal-api|rest-api|graphql"),
    ("cloud/azure",  r"\bazure\b|\baws\b|\bgcp\b|serverless|kubernetes|\bk8s\b|\bdocker\b|aspire|cloud-native|microservice"),
    ("data/db/orm",  r"entity-framework|\befcore\b|\borm\b|\bdapper\b|\bsql\b|sql-server|postgres|\bmysql\b|sqlite|mongodb|\bredis\b|database"),
    ("ml/ai",        r"\bai\b|\bllm\b|\bml\b|machine-learning|deep-learning|neural|\bonnx\b|\bopenai\b|semantic-kernel|\brag\b|\bmcp\b|model-context-protocol|agent"),
    ("security",     r"\bsecurity\b|cryptograph|\boauth\b|\bjwt\b|authentication|identity|pentest|\binfosec\b|vulnerab|forensic|reverse-engineering"),
    ("networking",   r"\bhttp\b|\btcp\b|\budp\b|websocket|\bgrpc\b|proxy|\bnetworking\b|\bsocket\b|\bprotocol\b|\bmqtt\b"),
    ("desktop-tool", r"\bwindows\b|\btray\b|taskbar|\butility\b|\btool(s)?\b|launcher|\bcli\b|command-line|\bterminal\b|automation|powershell"),
    ("testing",      r"\btest(ing)?\b|\bunit-test|assertion|\bmock\b|\bbenchmark\b|\bxunit\b|\bnunit\b|fuzz"),
    ("iot/embedded", r"\biot\b|embedded|\barduino\b|raspberry|microcontroller|\bplc\b|nanoframework|\bgpio\b"),
    ("library/sdk",  r"\blibrary\b|\bsdk\b|\bnuget\b|\bapi\b|\bframework\b|\bwrapper\b|\bbindings?\b|\bclient\b|\btoolkit\b"),
]
CATEGORY_COMPILED = [(name, re.compile(rx, re.I)) for name, rx in CATEGORY_RULES]

def categorize(r, hay):
    for name, rx in CATEGORY_COMPILED:
        if rx.search(hay):
            return name
    return "other"

# ---------------------------------------------------------------------------
# Grouping / dedup (SELECTION GOAL 5) via union-find. Conservative (high precision):
#  - identical non-empty normalized description of length >= 40  -> same project/mirror
#  - same normalized repo name AND description Jaccard >= 0.5     -> same template family
# fork_of is NOT in the data, so it stays null and is flagged as unverified.
# ---------------------------------------------------------------------------
def norm_name(full):
    return re.sub(r"[-_.]", "", full.split("/")[1].lower())

def norm_desc(d):
    return re.sub(r"\s+", " ", re.sub(r"[^\w\s]", "", (d or "").lower())).strip()

STOP = {"a", "an", "the", "for", "and", "of", "to", "in", "with", "is", "net",
        "c", "on", "by", "your", "net10", "dotnet"}
def desc_tokens(d):
    return set(re.findall(r"[a-z0-9]+", (d or "").lower())) - STOP

def jaccard(a, b):
    return len(a & b) / len(a | b) if (a or b) else 0.0

class UF:
    def __init__(self, keys):
        self.p = {k: k for k in keys}
    def find(self, x):
        while self.p[x] != x:
            self.p[x] = self.p[self.p[x]]
            x = self.p[x]
        return x
    def union(self, a, b):
        ra, rb = self.find(a), self.find(b)
        if ra != rb:
            self.p[max(ra, rb)] = min(ra, rb)  # deterministic: keep lexicographically smaller root

# ===========================================================================
def main():
    with open(IN, encoding="utf-8") as f:
        raw = [json.loads(line) for line in f if line.strip()]

    # Stage 0: drop exact duplicate rows (search-pagination overlap), keep first.
    seen, repos = set(), []
    for r in raw:
        if r["full_name"] in seen:
            continue
        seen.add(r["full_name"])
        repos.append(r)
    n_total, n_unique = len(raw), len(repos)

    # Per-repo evaluation.
    recs = {}
    for r in repos:
        fn = r["full_name"]
        desc = r["description"] or ""
        hay = (fn + " " + desc + " " + " ".join(r["topics"])).lower()
        flags, reasons = [], []

        lb = license_bucket(r["license"])
        if lb == "restricted":
            reasons.append("restricted_license")
        elif lb == "unknown":
            reasons.append("license_unknown")

        reasons += abuse_reasons(r, hay)

        if looks_gibberish(r["owner"]) and not desc and not r["topics"]:
            reasons.append("gibberish_lowsignal")
        elif looks_gibberish(r["owner"]):
            flags.append("throwaway_owner_suspect")

        if NONCODE_NAME.search(fn.split("/")[1]) or NONCODE_DESC.search(desc):
            reasons.append("noncode_or_list")

        if not desc and not r["topics"]:
            flags.append("no_desc_no_topics")

        edu = bool(EDU.search(hay))
        if edu:
            flags.append("educational")

        sz = r["size"]
        is_game = bool(GAME_RE.search(hay))
        if sz < TINY_REJECT_KB and not desc and not r["topics"]:
            reasons.append("tiny_lowsignal")
        if sz < TINY_FLAG_KB:
            flags.append("tiny")
        if sz > LARGE_FLAG_KB:
            flags.append("large")
        if is_game and sz > ASSETS_RISK_KB:
            flags.append("assets_heavy_risk")
        if is_game and sz > HUGE_ASSETS_REJECT_KB:
            reasons.append("huge_asset_dump")

        if r["pushed_at"] < ACTIVITY_CUTOFF:
            reasons.append("inactive")

        if r["full_name"] in MANUAL_EXCLUDE:
            reasons.append("manual_exclude")

        cat = categorize(r, hay)

        # Priority score (SELECTION GOAL 7), 0..1000.
        stars_norm = min(1.0, math.log10(r["stars"] + 1) / math.log10(100000 + 1))
        d0, d1 = to_days(DATE_MIN), to_days(DATE_MAX)
        recency = max(0.0, min(1.0, (to_days(r["pushed_at"]) - d0) / (d1 - d0)))
        owner_bonus = 1.0 if r["owner_type"] == "Organization" else 0.0
        quality = (0.5 if desc else 0.0) + min(0.5, 0.1 * len(r["topics"]))
        if edu:
            quality *= 0.6
        score = 0.45 * stars_norm + 0.30 * recency + 0.10 * owner_bonus + 0.15 * quality
        priority = round(1000 * score)

        recs[fn] = dict(r=r, license_bucket=lb, flags=flags, reasons=reasons,
                        category=cat, priority=priority, is_game=is_game,
                        ndesc=norm_desc(desc), nname=norm_name(fn),
                        dtok=desc_tokens(desc))

    # Stage: grouping / dedup.
    uf = UF(list(recs.keys()))
    # (a) identical long description.
    by_desc = collections.defaultdict(list)
    for fn, rec in recs.items():
        if len(rec["ndesc"]) >= 40:
            by_desc[rec["ndesc"]].append(fn)
    for group in by_desc.values():
        if len(group) > 1:
            g = sorted(group)
            for other in g[1:]:
                uf.union(g[0], other)
    # (b) same name + description Jaccard >= 0.5.
    by_name = collections.defaultdict(list)
    for fn, rec in recs.items():
        by_name[rec["nname"]].append(fn)
    for group in by_name.values():
        if len(group) < 2:
            continue
        for a, b in itertools.combinations(sorted(group), 2):
            if jaccard(recs[a]["dtok"], recs[b]["dtok"]) >= 0.5:
                uf.union(a, b)

    # Assign group ids and pick canonical (max priority, then stars, org over user,
    # then full_name) per group.
    members = collections.defaultdict(list)
    for fn in recs:
        members[uf.find(fn)].append(fn)
    group_id = {}
    canonical = {}
    for root in sorted(members):
        g = members[root]
        gid = "grp_" + re.sub(r"[^a-z0-9]", "-", root.lower())
        best = max(g, key=lambda fn: (recs[fn]["priority"], recs[fn]["r"]["stars"],
                                      recs[fn]["r"]["owner_type"] == "Organization", fn))
        for fn in g:
            group_id[fn] = gid
        canonical[root] = best
        for fn in g:
            if fn != best:
                recs[fn]["reasons"].append("duplicate_of_group")

    # Owner cap (SELECTION GOAL 6): keep <= N best (by priority) permissive+clean repos
    # per owner; overflow -> reason owner_cap. N=15 balances diversity vs. the fact that a
    # few orgs (microsoft 147, dotnet 79, Unity-Technologies 56, Azure 44) would dominate.
    OWNER_CAP = 15
    def is_selectable(fn):
        return not recs[fn]["reasons"]  # empty reasons == passes every gate so far
    by_owner = collections.defaultdict(list)
    for fn in recs:
        if is_selectable(fn):
            by_owner[recs[fn]["r"]["owner"]].append(fn)
    for owner, fns in by_owner.items():
        if len(fns) <= OWNER_CAP:
            continue
        ranked = sorted(fns, key=lambda fn: (-recs[fn]["priority"], -recs[fn]["r"]["stars"], fn))
        for fn in ranked[OWNER_CAP:]:
            recs[fn]["reasons"].append("owner_cap")

    # Build outputs.
    selected, rejected, unknown_bucket, restricted_bucket = [], [], [], []
    for fn, rec in recs.items():
        r = rec["r"]
        if rec["reasons"]:
            row = {"full_name": fn, "reasons": sorted(set(rec["reasons"]))}
            rejected.append(row)
            if "license_unknown" in row["reasons"]:
                unknown_bucket.append({"full_name": fn, "license": r["license"],
                                       "stars": r["stars"], "reasons": row["reasons"]})
            if "restricted_license" in row["reasons"]:
                restricted_bucket.append({"full_name": fn, "license": r["license"],
                                          "stars": r["stars"], "reasons": row["reasons"]})
        else:
            selected.append({
                "url": f"https://github.com/{fn}",
                "repository_id": f"github.com/{fn}",
                "revision": None,
                "license": r["license"],
                "provenance": os.path.basename(IN),
                "priority": rec["priority"],
                "fork_of": None,
                "category": rec["category"],
                "group": group_id[fn],
                "stars": r["stars"],
                "pushed_at": r["pushed_at"],
                "size_kb": r["size"],
                "flags": rec["flags"],
            })

    selected.sort(key=lambda x: (-x["priority"], x["repository_id"]))
    rejected.sort(key=lambda x: x["full_name"])
    unknown_bucket.sort(key=lambda x: (-x["stars"], x["full_name"]))
    restricted_bucket.sort(key=lambda x: (-x["stars"], x["full_name"]))

    def write_jsonl(name, rows):
        with open(os.path.join(OUT, name), "w", encoding="utf-8") as f:
            for row in rows:
                f.write(json.dumps(row, ensure_ascii=False, sort_keys=True) + "\n")

    write_jsonl("selected.jsonl", selected)
    write_jsonl("rejected.jsonl", rejected)
    write_jsonl("license_unknown.jsonl", unknown_bucket)
    write_jsonl("restricted_license.jsonl", restricted_bucket)

    # ---- report.md ----
    reason_counts = collections.Counter(rr for row in rejected for rr in row["reasons"])
    cat_counts = collections.Counter(s["category"] for s in selected)
    lic_counts = collections.Counter(s["license"] for s in selected)
    owner_counts = collections.Counter(s["url"].split("/")[3] for s in selected)
    flag_counts = collections.Counter(fl for s in selected for fl in s["flags"])
    n_groups_sel = len({s["group"] for s in selected})

    def md_table(counter, cols=("key", "count"), n=None):
        items = counter.most_common(n)
        out = [f"| {cols[0]} | {cols[1]} |", "| --- | ---: |"]
        out += [f"| {k} | {v} |" for k, v in items]
        return "\n".join(out)

    rep = []
    rep.append("# C# repository selection report\n")
    rep.append("Metadata-only selection over `data/csharp-search.jsonl`. No repository was "
               "cloned, fetched, or executed; descriptions were treated as untrusted data and "
               "only pattern-matched. Reproduce with `python3 -I data/selection/selection_script.py`.\n")
    rep.append("## Funnel\n")
    rep.append(f"- Input rows: **{n_total}**\n"
               f"- After exact-duplicate-row dedup: **{n_unique}** unique `full_name` "
               f"({n_total - n_unique} duplicate rows removed; search-pagination overlap)\n"
               f"- Selected: **{len(selected)}** (in {n_groups_sel} dedup groups)\n"
               f"- Rejected: **{len(rejected)}**\n"
               f"  - license_unknown bucket: **{len(unknown_bucket)}**\n"
               f"  - restricted_license bucket: **{len(restricted_bucket)}**\n")
    rep.append("## Rejection reasons (a repo may carry several)\n")
    rep.append(md_table(reason_counts, ("reason", "count")) + "\n")
    rep.append("## Thresholds & justification (from observed distributions)\n")
    rep.append(
        "- **License gate**: permissive allowlist exactly as specified "
        f"({', '.join(sorted(PERMISSIVE))}). Copyleft/reciprocal -> restricted. "
        "NOASSERTION/null and permissive-ish ids NOT on the allowlist (MIT-0, WTFPL, "
        "CC0-1.0, BSL-1.0, PostgreSQL, AFL-3.0, CC-BY-4.0) -> license_unknown for manual "
        "verification. Observed licenses are dominated by MIT (3716) with sizeable "
        "NOASSERTION (772) and null (715) tails, so the unknown bucket is intentionally large.\n"
        "- **Activity cutoff** = 2024-10-09 (24 months). The corpus spans only 2025-06..2026-10, "
        "so this gate passes everything today; recency instead feeds the priority score. Kept "
        "as an explicit gate for the future 34k manifest.\n"
        f"- **Size**: repo size is KB incl. git history (permissive p5=178, median=9727, "
        f"p90=208137). Reject only `< {TINY_REJECT_KB} KB AND no description AND no topics` "
        f"(trivial), flag `< {TINY_FLAG_KB} KB` as `tiny`, flag `> {LARGE_FLAG_KB} KB` as "
        f"`large`. Game/Unity repos `> {ASSETS_RISK_KB} KB` are flagged `assets_heavy_risk` "
        f"(C# likely a minority of bytes) and only rejected when `> {HUGE_ASSETS_REJECT_KB} KB` "
        "(asset dump). Legitimate large repos (roslyn, azure-sdk) are deliberately kept.\n"
        f"- **Owner cap** = {OWNER_CAP} selected repos/owner (keep highest priority). Prevents a "
        "few orgs (microsoft 147, dotnet 79, Unity-Technologies 56, Azure 44 candidates) from "
        "dominating the corpus.\n"
        "- **Spam/abuse**: word-boundary keyword + topic match with benign exceptions "
        "(malware-analysis, dependency-injection, packet libraries, cheat-engine clones are "
        "NOT rejected). High precision by design; borderline offensive-security tooling is "
        "rejected under `abuse_*` and listed for audit.\n"
        "- **Non-code**: awesome-lists, roadmaps, cheat-sheets, slide/presentation and curated-"
        "list repos -> `noncode_or_list`. Educational repos (tutorial/course/learn) contain real "
        "C# and are KEPT but flagged `educational` and down-weighted (quality*0.6).\n"
        "- **Dedup/grouping**: union-find over (a) identical normalized description len>=40 "
        "(mirrors/clones) and (b) same repo name + description Jaccard>=0.5 (template families). "
        "Canonical = highest priority, org over user on ties; others -> `duplicate_of_group` but "
        "keep a shared `group` id for split isolation.\n")
    rep.append("## Selection — category distribution\n")
    rep.append(md_table(cat_counts, ("category", "count")) + "\n")
    rep.append("## Selection — license distribution\n")
    rep.append(md_table(lic_counts, ("license", "count")) + "\n")
    rep.append("## Selection — top owners (after cap)\n")
    rep.append(md_table(owner_counts, ("owner", "count"), 20) + "\n")
    rep.append("## Selection — quality flags\n")
    rep.append(md_table(flag_counts, ("flag", "count")) + "\n")
    rep.append("## Borderline cases (kept, flagged for later review)\n")
    bl = [s for s in selected if "throwaway_owner_suspect" in s["flags"]
          or "assets_heavy_risk" in s["flags"]][:12]
    rep.append("\n".join(f"- `{s['url'].split('github.com/')[1]}` "
                         f"(cat={s['category']}, flags={s['flags']}, stars={s['stars']})"
                         for s in bl) + "\n")
    rep.append("## Known limitations (metadata-only)\n")
    rep.append(
        "- **Language share unverified**: GitHub `size` includes all files/history; we cannot "
        "confirm C# is the majority language or that assets/binaries do not dominate. Verify at "
        "ingestion (file discovery stage already measures bytes by extension).\n"
        "- **Fork/archived status unknown**: not present in the data, so `fork_of` is always "
        "null and no archived flag exists. Forks and archived repos must be detected at clone "
        "time (GitHub API) and merged into groups before caret expansion.\n"
        "- **Real license unverified**: the SPDX id is GitHub's best guess; the actual LICENSE "
        "file and per-file headers must be confirmed before any repo enters the training corpus. "
        "NOASSERTION/unknown repos are quarantined, not trusted.\n"
        "- **Generated code**: `.g.cs`/`.Designer.cs`/migrations cannot be seen from metadata; "
        "excluded later by the extraction file filters.\n"
        "- **Dedup is conservative**: only identical/high-overlap descriptions are merged. "
        "Genuine forks with divergent descriptions (e.g. the several space-station-14 builds) "
        "stay in separate groups and must be regrouped by fork lineage at ingestion.\n")
    with open(os.path.join(OUT, "report.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(rep))

    # ---- pilot_candidates.md ----
    # Pick a diverse, permissive, recently-active, moderately-sized shortlist.
    def pick(pred):
        cands = [s for s in selected
                 if pred(s)
                 and 2000 <= s["size_kb"] <= 400000
                 and s["pushed_at"] >= "2026-01-01"
                 and not s["flags"]]
        return cands[0] if cands else None
    wanted = [
        ("library/sdk", lambda s: s["category"] == "library/sdk"),
        ("web/aspnet", lambda s: s["category"] == "web/aspnet"),
        ("desktop-ui", lambda s: s["category"] == "desktop-ui"),
        ("game/unity", lambda s: s["category"] == "game/unity"),
        ("devtools (Roslyn)", lambda s: s["category"] == "devtools"),
        ("data/db/orm", lambda s: s["category"] == "data/db/orm"),
        ("ml/ai (modern C#)", lambda s: s["category"] == "ml/ai"),
    ]
    pm = ["# Pilot shortlist (5-8 diverse repos for the next multi-repo pilot)\n",
          "All permissive, recently active, moderately sized. Language share, fork/archived "
          "status and the real LICENSE file must still be verified at checkout.\n"]
    used = set()
    for label, pred in wanted:
        s = pick(lambda x: pred(x) and x["url"] not in used)
        if s:
            used.add(s["url"])
            fn = s["url"].split("github.com/")[1]
            pm.append(f"- **{fn}** — {label}; {s['stars']} stars, {s['size_kb']} KB, "
                      f"license {s['license']}, pushed {s['pushed_at'][:10]}, priority {s['priority']}.")
    with open(os.path.join(OUT, "pilot_candidates.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(pm) + "\n")

    return dict(n_total=n_total, n_unique=n_unique, selected=len(selected),
                rejected=len(rejected), unknown=len(unknown_bucket),
                restricted=len(restricted_bucket), reason_counts=dict(reason_counts),
                cat_counts=dict(cat_counts))

if __name__ == "__main__":
    stats = main()
    print(json.dumps(stats, indent=2))
