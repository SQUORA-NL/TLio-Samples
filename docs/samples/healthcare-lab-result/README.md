# HL7 v2 lab result → FHIR-shaped bundle

A renal panel comes back from the lab as an HL7 v2 `ORU^R01` message in XML. The receiving system
wants FHIR JSON: LOINC codes instead of local test codes, `final` instead of `F`, ISO instants
instead of `CCYYMMDDHHMMSS`, and numeric values.

| | |
|---|---|
| Input | HL7 v2.5.1 `ORU^R01`, XML encoding — MSH / PID / OBR / 4 × OBX |
| Output | FHIR-shaped `Bundle` in JSON with four `Observation`s |
| Notable | Potassium 5.6 mmol/L flagged `HH` → interpretation **Critical high** |

```sh
dotnet run --project samples/TLio.Sample.Cli -- \
  --input  docs/samples/healthcare-lab-result/input.xml \
  --script docs/samples/healthcare-lab-result/script.json
```

## Three vocabularies crossed in the path

HL7 v2 XML names its elements `OBX.3`, `PID.5.1` — dots and all. XPath handles that without
ceremony, and each code crossing is a predicate joining the map to the message:

```
code   =fetch(/ORU_R01/map/testCodes/item[local = /ORU_R01/OBX[1]/OBX.3]/loinc)
status =fetch(/ORU_R01/map/statuses/item[hl7  = /ORU_R01/OBX[1]/OBX.11]/fhir)
flag   =fetch(/ORU_R01/map/interpretations/item[hl7 = /ORU_R01/OBX[1]/OBX.8]/display)
```

| HL7 | FHIR |
|---|---|
| `KREAT` | LOINC `2160-0` Creatinine |
| `EGFR` | LOINC `33914-3` Glomerular filtration rate |
| `F` (OBX.11) | `final` |
| `HH` (OBX.8) | `HH` / Critical high |
| `F` (PID.8) | `female` |

## Dates are text, so the conversion is string work

There is no date type to parse into — in XML, JSON and YAML alike a date is text. `20260821103500`
becomes an ISO instant with `substring` and `concat`, and nothing else:

```
=concat(=substring(…,0,4),'-',=substring(…,4,2),'-',=substring(…,6,2),'T',
        =substring(…,8,2),':',=substring(…,10,2),':',=substring(…,12,2),'Z')
```

## Arrays in XML are `item` elements

The bundle is built **in XML** and converted at the end. FHIR is full of arrays — `coding`,
`name`, `given`, `interpretation`, `referenceRange` — and an XML array is an element whose
children share one name. Writing them as the canonical `item` is what makes them JSON arrays
rather than objects on the other side of the boundary:

```xml
<coding>
  <item>
    <system>http://loinc.org</system>
    <code>=fetch(…)</code>
  </item>
</coding>
```

## The two things `convert` cannot decide for you

**XML always has exactly one root element**, so converting to JSON always produces exactly one
top-level key. FHIR wants `resourceType`, `type`, `subject` at the top, so the wrapper is moved
away after the boundary:

```json
{ "command": "move", "fromPath": "$.Bundle", "toPath": "$" }
```

**XML carries no types.** `valueQuantity.value` is a FHIR decimal, so `inferTypes` is on — and
that also turns the all-digit BSN into a number, which it must not be. One command puts it back:

```json
{ "command": "put", "path": "$.subject.identifier[0].value",
  "value": "=toString($.subject.identifier[0].value)" }
```

That one repair is the whole argument for the other strategy. The
[AFD 1.0 migration](../sivi-afd1-to-afd2/) converts **first** with `inferTypes` off and then types
each field deliberately, which costs nothing extra and gets leading zeros and cents right as a
side effect. Use `inferTypes` when most fields are genuinely numeric and the exceptions are few;
use explicit typing when the source is a flat file.

## The script is JSON notation, the paths are XPath

Right up to the boundary every path here is XPath, in a script written as a JSON array — which is
the clearest demonstration in the whole set that **the notation a script is written in is
independent of the format of the data it transforms**. Only the paths follow the document, and
they change at the boundary because that is when the document changes.

The XML notation would do this job too; `convert` is a command like any other in all three, and
the boundary split reads the script in whichever one it finds. A host that runs such a script has
to give the engines behind its section executors that notation — `UseXmlScripts()` /
`UseYamlScripts()` — or the section parses to nothing and the executor says so rather than
passing the document through.

## Known simplification

Four OBX segments become four `<item>` blocks written out longhand, because TLio has no loop and
`copy` will not take a filtered `fromPath`. A message with a variable number of results needs the
[AFD migration's](../sivi-afd1-to-afd2/) copy-then-`remove`-by-filter approach instead.

The output is **FHIR-shaped, not schema-valid**: observations sit in an `observations` array
rather than in `Bundle.entry[].resource`, and there is no `fullUrl`. It is close enough to read
as FHIR and short enough to read at all.
