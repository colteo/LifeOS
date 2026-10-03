"""Provider-neutral identity/configuration; adapters own provider wiring."""

import json
from dataclasses import asdict, dataclass
from pathlib import Path


@dataclass(frozen=True)
class ExperimentVariant:
    provider: str
    model: str
    prompt_version: str
    context_version: str
    structured_output: str
    generation_settings: dict

    @classmethod
    def load(cls, path: Path):
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
            if not isinstance(data, dict) or set(data) != set(cls.__dataclass_fields__):
                raise ValueError("invalid experiment fields")
            for key in set(data) - {"generation_settings"}:
                if not isinstance(data[key], str) or not data[key].strip():
                    raise ValueError(
                        "experiment identity must contain nonempty strings"
                    )
            settings = data["generation_settings"]
            if not isinstance(settings, dict):
                raise ValueError("generation_settings must be an object")
            # Generic contract: provider-specific validation belongs to adapters.
            json.dumps(settings, allow_nan=False)
            return cls(**data)
        except (TypeError, json.JSONDecodeError) as exc:
            raise ValueError("invalid experiment JSON") from exc

    def identity(self):
        return asdict(self)


def create_experiment(path: Path, adapters: dict):
    """Resolve explicitly registered adapters without provider-specific CLI flags."""
    variant = ExperimentVariant.load(path)
    factory = adapters.get(variant.provider)
    if factory is None:
        raise ValueError("provider adapter unavailable for this evaluator")
    return factory(variant)
