# CRM export → array-shaped entity groups

A CRM exports a customer as singular nested objects — one `address`, one `billing`, one
`billing.contact`. A downstream system models every entity group as repeatable, even where
today's cardinality is one, so each of those objects has to become a one-element array before
the record is accepted. Two commands.

| | |
|---|---|
| Scenario | One customer, three nested complex objects at two different depths |
| Outcome | `address`, `billing` and `billing.contact` are all one-element arrays; every primitive is untouched |

```sh
dotnet run --project samples/TLio.Sample.Cli -- \
  --input  docs/samples/customer-record-normalization/input.json \
  --script docs/samples/customer-record-normalization/script.json
```

## `setProperties` + `=scriptpath(*, ['object'], true)`

`kinds: ['object']` asks the find-mode shape of `=scriptpath()` for complex objects instead of
primitives — every object anywhere in the subtree, not just direct children:

```json
{ "command": "setProperties", "path": "$.customer",
  "properties": "=scriptpath(*,['object'],true)", "value": "=toArray()" }
```

That selection is generic on purpose — `setProperties` doesn't know or care that the value
function happens to be `=toArray()`; see [SetProperties.md](https://github.com/SQUORA-NL/TLio/blob/main/docs/ai-ref/commands/SetProperties.md)
and [ScriptPath.md](https://github.com/SQUORA-NL/TLio/blob/main/docs/ai-ref/functions/ScriptPath.md#find-mode-scriptpath-kinds-recursive).

## Why two commands, not one

`=toArray()` deep-clones the node it wraps. Run a single `setProperties` call over the whole
customer object and it would find `billing` **and** `billing.contact` in the same pass — but by
the time `billing` gets cloned into its array, `contact` hasn't been wrapped yet, so the clone
freezes `contact` in its pre-wrap, unwrapped shape. `contact`'s own replace then targets a node
that is no longer reachable from the document root and the wrap is silently lost — no warning,
no failure.

The fix is to work bottom-up, one `setProperties` step per level:

```json
[
  { "command": "setProperties", "path": "$.customer.billing",
    "properties": "=scriptpath(*,['object'],true)", "value": "=toArray()" },

  { "command": "setProperties", "path": "$.customer",
    "properties": "=scriptpath(*,['object'],true)", "value": "=toArray()" }
]
```

Step 1 wraps `billing.contact` while `billing` itself is still an ordinary object. Step 2 then
finds `address` and `billing` under `customer` — `billing` is already in its final shape, so
cloning it into an array carries the wrapped `contact` along for free. Match sets that are all
siblings (no object nested inside another matched object) don't need this — the ordering only
matters once a matched object contains another one.
