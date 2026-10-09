# Prompt format `flc-prompt/v1` (line mode)

Produced by `flc-dataset render`. Canonical data is model-agnostic; this serialization is versioned and can be regenerated
with different budgets without re-extracting.

```text
<|cs|><|path|>src/Ordering.API/Application/DomainEventHandlers/ValidateOrAddBuyer...Handler.cs
<|sem|>
RET Task
ARG cancellationToken:CancellationToken domainEvent:OrderStartedDomainEvent
LOCAL buyer:Buyer buyerExisted:bool cardTypeId:int
FIELD _buyerRepository:IBuyerRepository _logger:ILogger
METHOD Handle(OrderStartedDomainEvent domainEvent, CancellationToken cancellationToken)->Task
RECV Domain.Seedwork.IUnitOfWork
MEMBER SaveChangesAsync(CancellationToken cancellationToken)->Task<int>; SaveEntitiesAsync(CancellationToken cancellationToken)->Task<bool>; Dispose()->void
<|code|>
        await _buyerRepository.UnitOfWork
            .<|complete|>
```
completion (loss only here): `SaveEntitiesAsync(cancellationToken);<|eol|>`

(real record from the eShop pilot; code window shortened here.)

## Rules

* Order: stable metadata → semantic facts → most recent code. Code is cut **from the left on a line boundary**, never near the caret.
* `<|sem|>` is omitted when no resolved facts exist (syntax-only samples, fallbacks). The model sees both shapes.
* Right context / suffix is **never** rendered (left-to-right model). It stays in the canonical sample for validation and future FIM.
* Stop token: dedicated `<|eol|>`, not `\n`. BPE tokenizers merge `\n` with the next line's indentation, which makes
  "stop at the first newline" ambiguous at the token level and mixes CRLF/LF. Multi-line mode (future) gets `<|end_completion|>`.
* All `<|…|>` markers must become dedicated special-token ids; they never appear as literal text in C# files.
* Budgets: `--max-code-chars` (default 4000) and `--max-semantic-chars` (default 900) in UTF-16 chars until a tokenizer is
  pinned. Semantic lines are admitted by priority `EXPECT, RECV, CALL, ARG, LOCAL, RET, MEMBER, FIELD/PROPERTY, METHOD`, list
  items trimmed from the end; members already used in the visible prefix are ranked first by the extractor.

## Vocabulary

| Key | Meaning |
|---|---|
| `RET` | return type of the enclosing member |
| `EXPECT T` | bound expected type at the caret (argument/return/assignment/initializer/condition) |
| `ARG` | parameters in scope (method, lambda, primary constructor) |
| `LOCAL` | locals declared before the caret and usable there |
| `FIELD` / `PROPERTY` / `METHOD` | members of `this` (static-context aware) |
| `RECV` | receiver type after `.`/`?.` (`type X` for static access, `namespace X`) |
| `MEMBER` | accessible members of the receiver, `Name(params)->Ret`, `+n` = more overloads |
| `CALL` | overload candidates at an argument position: `sig @i param:type` |

## Leakage policy

Facts come exclusively from the semantic sidecar computed on the target-free snapshot. `MEMBER`/`CALL` list *all* accessible
candidates (capped), never the one the target uses; a 100M model must learn to choose. Flow-state claims (`flow=not-null`)
are not emitted until Roslyn confirms them on the snapshot.

## Inference parity

The IDE service builds the same prompt from the live document (prefix only), the same semantic extractor on the live
snapshot, decodes until `<|eol|>`, strips it, and trims any duplicated existing suffix (`)`, `;`) before inserting.
