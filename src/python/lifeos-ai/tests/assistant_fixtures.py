"""AI-007 F3: synthetic assistant requests and interpretations (no real data)."""

import copy

MESSAGE = "Ho speso 5 euro per colazione con Pamela"

REQUEST = {
    "protocol_version": 1,
    "message": MESSAGE,
    "context": {
        "locale": "it-IT",
        "finance": {
            "open_draft": None,
            "expense_categories": ["Ristoranti", "Spesa"],
            "income_categories": ["Stipendio"],
        },
    },
}

ABSENT = {"state": "absent", "value": None, "span": None}
ABSENT_DATE = {
    "state": "absent",
    "kind": None,
    "days_ago": None,
    "weekday": None,
    "day": None,
    "month": None,
    "year": None,
    "span": None,
}
ABSENT_TIME = {"state": "absent", "hour": None, "minute": None, "span": None}

EXPENSE = {
    "protocol_version": 1,
    "intent": "finance_expense",
    "finance": {
        "type": ABSENT,
        "amount": {"state": "stated", "value": "5", "span": "5 euro"},
        "currency": {"state": "stated", "value": "EUR", "span": "euro"},
        "account": ABSENT,
        "category": {"state": "inferred", "value": "Ristoranti", "span": None},
        "note": {
            "state": "stated",
            "value": "colazione con Pamela",
            "span": "colazione con Pamela",
        },
        "date": ABSENT_DATE,
        "time": ABSENT_TIME,
    },
    "ambiguous_fields": [],
    "unsupported_reason": None,
}


def request(**changes) -> dict:
    data = copy.deepcopy(REQUEST)
    data.update(changes)
    return data


def expense(**finance_changes) -> dict:
    data = copy.deepcopy(EXPENSE)
    data["finance"].update(finance_changes)
    return data
