"""Drift between the .NET Action Agent (source of truth) and the lab's versioned mirror.

Reads the .NET sources directly: if a tool definition, the step limit, the change policy
or an error code changes there, these tests fail until dotnet_mirror.py gets a new
loop/tool version.
"""

import json
import re
from pathlib import Path

import pytest

from lifeos_ai_evals.evaluators.action_agent import dotnet_mirror as net

DOTNET = Path(__file__).resolve().parents[3] / "src/dotnet"
TOOLS = DOTNET / "LifeOS.Application/ActionAgent/ActionAgentTools.cs"
HANDLER = DOTNET / "LifeOS.Application/ActionAgent/RunActionAgentHandler.cs"
PROPOSAL = DOTNET / "LifeOS.Domain/ActionAgent/ProposedAction.cs"


def constants(path: Path) -> dict[str, str]:
    text = path.read_text(encoding="utf-8")
    return dict(re.findall(r"public const \w+ (\w+) = \"?([^\";]+)\"?m?;", text))


def dotnet_definitions() -> list[dict]:
    text = TOOLS.read_text(encoding="utf-8")
    names = constants(TOOLS)
    found = []
    pattern = re.compile(
        r"new\((\w+),\s*((?:\"(?:[^\"\\]|\\.)*\"\s*\+?\s*)+),\s*Schema\(\"\"\"(.*?)\"\"\"\)\)",
        re.S,
    )
    for constant, description, schema in pattern.findall(text):
        found.append(
            {
                "name": names[constant],
                "description": "".join(
                    re.findall(r"\"((?:[^\"\\]|\\.)*)\"", description)
                ),
                "parameters": json.loads(schema),
            }
        )
    return found


def test_read_tool_definitions_match_the_dotnet_definitions_exactly():
    definitions = dotnet_definitions()
    assert [d["name"] for d in definitions] == [
        "get_weekly_review",
        "get_budget_status",
    ]
    # Order-sensitive: the JSON the provider receives must be identical.
    assert json.dumps(definitions) == json.dumps(net.READ_TOOLS)


def test_loop_constants_and_error_codes_match_dotnet():
    tools, handler, proposal = constants(TOOLS), constants(HANDLER), constants(PROPOSAL)
    assert tools["ToolSchemaVersion"] == net.TOOL_SCHEMA_VERSION
    assert int(handler["MaxSteps"]) == net.MAX_STEPS
    assert proposal["MaxChangeFactor"].rstrip("m") == str(net.MAX_CHANGE_FACTOR)
    for name in (
        "InvalidOutput", "UnknownTool", "RepeatedToolCall", "MaxStepsReached",
        "ProposalMonthNotAllowed", "ProposalNotGrounded", "ProposalWithoutBudget",
        "InvalidProposal",
    ):  # fmt: skip
        mirror = {
            "MaxStepsReached": net.MAX_STEPS_REACHED,
        }.get(name) or getattr(net, re.sub(r"(?<!^)(?=[A-Z])", "_", name).upper())
        assert handler[name] == mirror, name


@pytest.mark.parametrize(
    "fragment",
    [
        # Proposal validation order mirrored by dotnet_mirror.validate_proposal.
        "if (!run.Scope.TargetMonths.Contains(month))",
        "if (!run.Observations.TryGetValue((month, currency), out var observation))",
        "if (observation.Amount is not { } current)",
        # Repeated-call detection and the max-step rule mirrored by loop.run.
        "if (!run.CallKeys.Add(ActionAgentTools.CallKey(tool, arguments)))",
        "if (step == MaxSteps)",
        # Targets: the current and the next local month (model.parse_input).
        "var targets = new List<AgentMonth> { current, current.Next() };",
    ],
)
def test_mirrored_dotnet_logic_is_still_present(fragment):
    assert fragment in HANDLER.read_text(encoding="utf-8")


def test_mirrored_tool_messages_are_still_present():
    text = TOOLS.read_text(encoding="utf-8")
    assert '"get_weekly_review takes no arguments."' in text
    assert "The month must be one of: " in text
    assert (
        "Arguments must be an object with integer year and month and a 3-letter" in text
    )
