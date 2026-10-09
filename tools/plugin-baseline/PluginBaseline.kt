package io.github.completionml.core.nn

import io.github.completionml.core.bpe.BpeTokenizer
import java.io.File
import java.nio.file.Path
import java.util.Base64
import java.util.concurrent.atomic.AtomicInteger

/**
 * Plugin-baseline harness (test-scope main in a clone of idea-ml-completion; not part of the engine repo).
 *   acc <model.cml> <tok.bpe> <cases.tsv> <out.tsv> <workers> [limit]     accuracy, plugin default Options (showThreshold gating done offline)
 *   lat <model.cml> <tok.bpe> <cases.tsv> <out.json> <threads> [n]         latency with NnKernels.best() (native q8)
 * cases.tsv: id, caret_kind, is_test, b64(path), b64(before), b64(after), b64(target)
 */
class Case(val id: String, val kind: String, val path: ByteArray, val before: ByteArray, val after: ByteArray, val target: ByteArray)

fun loadCases(f: File, limit: Int): List<Case> {
    val d = Base64.getDecoder(); val out = ArrayList<Case>()
    f.forEachLine { l ->
        if (out.size >= limit) return@forEachLine
        val p = l.split('\t')
        out += Case(p[0], p[1], d.decode(p[3]), d.decode(p[4]), d.decode(p[5]), d.decode(p[6]))
    }
    return out
}

fun esc(b: ByteArray) = String(b, Charsets.UTF_8).replace("\\", "\\\\").replace("\t", "\\t").replace("\n", "\\n").replace("\r", "\\r")
fun trimEndBytes(b: ByteArray): ByteArray { var e = b.size; while (e > 0 && (b[e - 1] == ' '.code.toByte() || b[e - 1] == '\t'.code.toByte() || b[e - 1] == '\r'.code.toByte() || b[e - 1] == '\n'.code.toByte())) e--; return b.copyOf(e) }
fun pct(s: List<Double>, q: Double): Double = if (s.isEmpty()) Double.NaN else s.sorted().let { it[minOf(it.size - 1, (q * it.size).toInt())] }

object PluginBaseline {
    @JvmStatic fun main(a: Array<String>) {
        when (a[0]) { "dbg" -> dbg(a); "acc" -> acc(a); "lat" -> lat(a); else -> error("mode") }
    }

    fun dbg(a: Array<String>) {
        val weights = NnFormat.read(File(a[1])); val tok = BpeTokenizer.load(Path.of(a[2]))
        val model = NnModel(weights, 4); val comp = NnCompletion(model, tok); val s = model.newSession(2100)
        for (c in loadCases(File(a[3]), a[4].toInt())) {
            val r = comp.complete(c.path, c.before, c.after, s)
            println("typed=[${esc(r.typed)}] toks=${r.tokens.map { esc(tok.tokenBytes(it)) }} text=[${esc(r.text)}] target=[${esc(c.target)}] before_tail=[${esc(c.before.takeLast(25).toByteArray())}]")
        }
    }

    fun acc(a: Array<String>) {
        val weights = NnFormat.read(File(a[1])); val tok = BpeTokenizer.load(Path.of(a[2]))
        val cases = loadCases(File(a[3]), if (a.size > 6) a[6].toInt() else Int.MAX_VALUE)
        val workers = a[5].toInt()
        val next = AtomicInteger(0)
        val res = arrayOfNulls<String>(cases.size)
        val t0 = System.nanoTime()
        val ts = (0 until workers).map {
            Thread {
                val model = NnModel(weights, 1); val comp = NnCompletion(model, tok, NnCompletion.Options(showThreshold = 0.0, suppressPunctOnly = false))
                val s = model.newSession(2100)
                while (true) {
                    val i = next.getAndIncrement(); if (i >= cases.size) break
                    val c = cases[i]
                    s.truncate(0)
                    val r = try { comp.complete(c.path, c.before, c.after, s) } catch (e: Throwable) { res[i] = "${c.id}\t${c.kind}\tERR\t${e.javaClass.simpleName}"; continue }
                    val exact = trimEndBytes(r.text).contentEquals(trimEndBytes(c.target))
                    val lastCh = if (c.before.isNotEmpty()) c.before.last().toInt().toChar() else ' '
                    val afterDot = lastCh == '.' || lastCh == '>' && c.before.size > 1 && c.before[c.before.size - 2].toInt().toChar() == '-' || lastCh == ':' && c.before.size > 1 && c.before[c.before.size - 2].toInt().toChar() == ':'
                    res[i] = listOf(c.id, c.kind, if (exact) 1 else 0, "%.5f".format(r.confProd), if (r.punctOnly) 1 else 0, if (r.repeated) 1 else 0, if (r.text.isEmpty()) 1 else 0,
                        r.stop, if (afterDot) 1 else 0, r.prompt.size, r.tokens.size, if (r.healMiss) 1 else 0, if (c.before.isNotEmpty() && (c.before.last() == 32.toByte() || c.before.last() == 9.toByte())) 1 else 0, c.target.size, esc(r.text), esc(c.target)).joinToString("\t")
                    if (i % 200 == 0) System.err.println("${a[1].substringAfterLast('/')} $i/${cases.size} ${(System.nanoTime() - t0) / 1e9}s")
                }
                model.close()
            }.also { it.start() }
        }
        ts.forEach { it.join() }
        File(a[4]).writeText("id\tkind\texact\tconf\tpunct\trepeated\tempty\tstop\tafter_dot\tprompt_tokens\tgen_tokens\theal_miss\tbefore_ws\ttarget_bytes\ttext\ttarget\n" + res.joinToString("\n") + "\n")
        System.err.println("done ${(System.nanoTime() - t0) / 1e9}s")
    }

    fun lat(a: Array<String>) {
        val weights = NnFormat.read(File(a[1])); val tok = BpeTokenizer.load(Path.of(a[2]))
        val threads = a[5].toInt(); val want = if (a.size > 6) a[6].toInt() else 40
        val cases = loadCases(File(a[3]), Int.MAX_VALUE)
        val model = NnModel(weights, threads); val comp = NnCompletion(model, tok)
        println("kernels=${model.kernels.name} native=${io.github.completionml.core.nn.native.NativeLib.status} threads=$threads")
        val s = model.newSession(2100)
        // JIT warm-up: 25 cold completes, discarded
        for (c in cases.take(25)) { s.truncate(0); comp.complete(c.path, c.before, c.after, s) }
        // candidate pool: cases whose target has >= 4 ASCII bytes (so we can type 2 more chars); keep those with prompt 1300..1700 tokens
        class Row(val prompt: Int, val gen: Int, val cold: Double, val prefill: Double, val warm1: Double, val warm1Prompt: Int, val warm1Recomputed: Int, val warm2: Double, val kind: String)
        val rows = ArrayList<Row>(); var scanned = 0
        for (c in cases.drop(25)) {
            if (rows.size >= want) break
            if (c.target.size < 4 || c.target.any { it < 0 }) continue
            scanned++
            // different file each time unless same: reset cache so "cold" is cold
            s.truncate(0)
            val t0 = System.nanoTime(); val r = comp.complete(c.path, c.before, c.after, s); val cold = (System.nanoTime() - t0) / 1e6
            if (r.prompt.size < 1300 || r.prompt.size > 1700) continue
            // pure prefill of the same prompt, cold
            s.truncate(0); val p0 = System.nanoTime(); s.prefill(r.prompt); val pre = (System.nanoTime() - p0) / 1e6
            // re-establish cache state as after the cold call, then "type" one more char of the target, then another
            s.truncate(0); comp.complete(c.path, c.before, c.after, s)
            val b1 = c.before + c.target.copyOf(1)
            val before1 = s.length
            val w0 = System.nanoTime(); val r1 = comp.complete(c.path, b1, c.after, s); val warm1 = (System.nanoTime() - w0) / 1e6
            val b2 = c.before + c.target.copyOf(2)
            val w2 = System.nanoTime(); comp.complete(c.path, b2, c.after, s); val warm2 = (System.nanoTime() - w2) / 1e6
            rows += Row(r.prompt.size, r.tokens.size, cold, pre, warm1, r1.prompt.size, 0, warm2, c.kind)
        }
        fun st(f: (Row) -> Double) = mapOf("mean" to rows.map(f).average(), "p50" to pct(rows.map(f), 0.5), "p95" to pct(rows.map(f), 0.95), "min" to (rows.minOfOrNull(f) ?: Double.NaN), "max" to (rows.maxOfOrNull(f) ?: Double.NaN))
        val js = StringBuilder("{\n \"model\": \"${File(a[1]).name}\", \"threads\": $threads, \"kernels\": \"${model.kernels.name}\", \"n\": ${rows.size}, \"scanned\": $scanned,\n")
        fun put(n: String, m: Map<String, Double>) { js.append(" \"$n\": {" + m.entries.joinToString(", ") { "\"${it.key}\": %.2f".format(it.value) } + "},\n") }
        put("cold_complete_ms", st { it.cold }); put("cold_prefill_only_ms", st { it.prefill }); put("warm_keystroke1_ms", st { it.warm1 }); put("warm_keystroke2_ms", st { it.warm2 })
        put("prompt_tokens", st { it.prompt.toDouble() }); put("gen_tokens", st { it.gen.toDouble() })
        js.append(" \"cold_decode_per_token_ms_est\": %.2f\n}\n".format(rows.map { (it.cold - it.prefill) / maxOf(1, it.gen + 1) }.average()))
        File(a[4]).writeText(js.toString()); println(js)
        model.close()
    }
}
