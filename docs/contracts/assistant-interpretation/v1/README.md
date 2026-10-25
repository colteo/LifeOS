# LifeOS Assistant interpretation contract, v1

The canonical contract between LifeOS (.NET, `AssistantInterpreterClient` + `AssistantInterpretationValidator`)
and `lifeos-ai` (Python, `lifeos_ai/assistant/schema.py`) for `POST /v1/assistant/interpret` (AI-007 F3).
Both test suites check themselves against these files; neither implementation is the reference.

| File | Content | Checked by |
| --- | --- | --- |
| `interpretation.schema.json` | the strict JSON schema of the model's output (every property required, `additionalProperties: false`, nullable instead of optional) | Python: equal to `INTERPRETATION_JSON_SCHEMA`; .NET: names, enum values and nullability equal to the records/enums |
| `protocol.json` | versions, locale, limits (lengths in Unicode code points), HTTP error codes and the .NET failure each maps to | both: constants; Python: the endpoint returns exactly these errors; .NET: the client maps each one |
| `request.example.json` | a minimised request (no id, no account, no value) | .NET: what the client sends is this JSON; Python: accepted unchanged |
| `cases.json` | golden interpretations with the expected verdict: `null` (valid), `"shape"` (refused while reading) or a rule code | both: same verdict, same code |

Tests: `src/python/lifeos-ai/tests/test_assistant_contract.py`, `tests/dotnet/LifeOS.UnitTests/Assistant/AssistantContractTests.cs`.

Changing the protocol: edit these files first (a new incompatible version goes in `v2/`), then make both
sides pass. Synthetic data only.
