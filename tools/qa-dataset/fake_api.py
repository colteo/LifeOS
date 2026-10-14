"""In-memory stand-in for the LifeOS API, shaped like the real contracts (camelCase JSON). Test-only."""
import copy
from urllib.parse import parse_qs, urlsplit

from importer import ApiFailure

CONFIG = {
    "baseUrl": "http://127.0.0.1:5050",
    "userId": "00000000-0000-0000-0000-000000000002",
    "accountId": "00000000-0000-0000-0000-000000000003",
    "categories": {
        "income": "00000000-0000-0000-0000-000000000010", "groceries": "00000000-0000-0000-0000-000000000011",
        "dining": "00000000-0000-0000-0000-000000000012", "transport": "00000000-0000-0000-0000-000000000013",
        "home": "00000000-0000-0000-0000-000000000014", "subscriptions": "00000000-0000-0000-0000-000000000015",
        "travel": "00000000-0000-0000-0000-000000000016", "other": "00000000-0000-0000-0000-000000000016",
    },
}


class FakeApi:
    def __init__(self, config=CONFIG):
        self.base_url = config["baseUrl"]
        self.config = config
        self.calls = []
        self.fail_at = None          # n-th write (1-based) fails
        self.status = 400            # HTTP status of the failure; None = transport failure
        self.persist_failure = False  # the failing write is committed before the failure (timeout after commit)
        self.user = {"userId": config["userId"], "onboardingStatus": "Completed", "defaultCurrency": "EUR",
                     "displayName": "QA", "email": "qa@example.invalid"}
        self.budgets, self.rules, self.states, self.planned, self.planned_states = {}, [], {}, [], {}
        self.transactions, self.meals, self.journal = [], [], []
        self.counter = 0

    def writes(self):
        return [c for c in self.calls if c[0] in ("POST", "PUT")]

    def new_id(self):
        self.counter += 1
        return f"10000000-0000-0000-0000-{self.counter:012d}"

    def request(self, method, path, payload=None):
        self.calls.append((method, path))
        parts = urlsplit(path)
        route, query = parts.path.rstrip("/"), {k: v[0] for k, v in parse_qs(parts.query).items()}
        segments = route.split("/")[2:]
        if method == "GET":
            return copy.deepcopy(self.get(segments, query))
        n = len(self.writes())
        snapshot = None if n != self.fail_at or self.persist_failure else copy.deepcopy(self.__dict__)
        result = self.write(method, segments, query, copy.deepcopy(payload))
        if n == self.fail_at:
            if snapshot is not None:
                calls = self.calls
                self.__dict__.update(snapshot)
                self.calls = calls
            raise ApiFailure(self.status)
        return copy.deepcopy(result)

    # ---- reads
    def get(self, s, q):
        if s == ["me"]:
            return self.user
        if s == ["accounts"]:
            return [{"id": self.config["accountId"], "name": "Conto QA", "type": "Checking", "currency": "EUR"}]
        if s == ["categories"]:
            income = self.config["categories"]["income"]
            ids = sorted(set(self.config["categories"].values()))
            return [{"id": i, "name": i[-4:], "type": "Income" if i == income else "Expense", "parentCategoryId": None} for i in ids]
        if s[0] == "budgets":
            budget = self.budgets.get((int(s[1]), int(s[2])))
            return {"budget": budget}
        if s == ["recurring"]:
            months = [(y, m) for y in range(int(q["fromYear"]), int(q["toYear"]) + 1) for m in range(1, 13)
                      if (int(q["fromYear"]), int(q["fromMonth"])) <= (y, m) <= (int(q["toYear"]), int(q["toMonth"]))]
            occurrences = []
            for rule in self.rules:
                for y, m in months:
                    if (rule["startYear"], rule["startMonth"]) <= (y, m) <= (rule["endYear"], rule["endMonth"]):
                        status, tx = self.states.get((rule["id"], y, m), ("Due", None))
                        occurrences.append({"ruleId": rule["id"], "year": y, "month": m, "status": status, "transactionId": tx})
            return {"rules": self.rules, "occurrences": occurrences}
        if s == ["planned-expenses"]:
            return [{**p, **dict(zip(("status", "transactionId"), self.planned_states.get(p["id"], ("Due", None))))}
                    for p in self.planned if q["from"] <= p["scheduledDate"] <= q["to"]]
        if s == ["transactions"]:
            return self.transactions
        if s == ["journal"]:
            cursor = int(q.get("cursor", 0))
            items = self.journal[cursor:cursor + 20]
            return {"items": items, "nextCursor": str(cursor + 20) if len(self.journal) > cursor + 20 else None}
        if s == ["nutrition", "meals"]:
            return [m for m in self.meals if m["date"] == q["date"]]
        raise AssertionError("unexpected GET " + "/".join(s))

    # ---- writes
    def transaction(self, kind, amount, note, category_id, occurred):
        row = {"id": self.new_id(), "type": kind, "amount": amount, "currency": "EUR", "accountId": self.config["accountId"],
               "sourceAccountId": None, "destinationAccountId": None, "categoryId": category_id,
               "occurredAtUtc": occurred.replace("Z", "+00:00"), "note": note, "createdAtUtc": "2026-10-14T00:00:00+00:00"}
        self.transactions.append(row)
        return row

    def write(self, method, s, q, p):
        if method == "PUT" and s[0] == "budgets":
            self.budgets[(int(s[1]), int(s[2]))] = {"id": self.new_id(), "year": int(s[1]), "month": int(s[2]), "amount": p["amount"]}
            return None
        if method == "POST" and s == ["recurring"]:
            rule = {"id": self.new_id(), **{k: v for k, v in p.items()}}
            self.rules.append(rule)
            return rule
        if method == "POST" and s[0] == "recurring":
            rule = next(r for r in self.rules if r["id"] == s[1])
            key, action = (rule["id"], int(s[2]), int(s[3])), s[4]
            status, tx = self.states.get(key, ("Due", None))
            if action == "confirm" and status == "Confirmed":
                return {"transactionId": tx}
            if action == "skip" and status == "Skipped":
                return {"transactionId": None}
            if status != "Due":
                raise ApiFailure(409)
            if action == "skip":
                self.states[key] = ("Skipped", None)
                return {"transactionId": None}
            row = self.transaction(rule["type"], p["amount"], p["note"], rule["categoryId"], p["occurredAtUtc"])
            self.states[key] = ("Confirmed", row["id"])
            return {"transactionId": row["id"]}
        if method == "POST" and s == ["planned-expenses"]:
            item = {"id": self.new_id(), **p, "currency": "EUR"}
            self.planned.append(item)
            return {"id": item["id"]}
        if method == "POST" and s[0] == "planned-expenses":
            item = next(i for i in self.planned if i["id"] == s[1])
            status, tx = self.planned_states.get(item["id"], ("Due", None))
            action = s[2]
            if (status, action) == ("Confirmed", "confirm"):
                return {"transactionId": tx}
            if (status, action) == ("Cancelled", "cancel"):
                return {"transactionId": None}
            if status != "Due":
                raise ApiFailure(409)
            if action == "cancel":
                self.planned_states[item["id"]] = ("Cancelled", None)
                return {"transactionId": None}
            row = self.transaction("Expense", p["amount"], p["note"], item["categoryId"], p["occurredAtUtc"])
            self.planned_states[item["id"]] = ("Confirmed", row["id"])
            return {"transactionId": row["id"]}
        if method == "POST" and s == ["transactions"]:
            assert "userId" not in p
            return self.transaction(p["type"], p["amount"], p["note"], p["categoryId"], p["occurredAtUtc"])
        if method == "POST" and s == ["nutrition", "meals"]:
            meal = {"id": self.new_id(), "description": p["description"], "mealType": p["mealType"], "date": p["date"],
                    "time": p["time"], "occurredAtUtc": None, "nutrition": None}
            self.meals.append(meal)
            return meal
        if method == "PUT" and s[:2] == ["nutrition", "meals"]:
            meal = next(m for m in self.meals if m["id"] == s[2])
            meal["nutrition"] = {k: p[k] for k in ("caloriesKcal", "proteinGrams", "carbsGrams", "fatGrams")}
            meal["nutrition"]["source"] = p["source"]
            return meal
        if method == "POST" and s == ["journal"]:
            entry = {"id": self.new_id(), **p, "occurredAtUtc": p["occurredAtUtc"].replace("Z", "+00:00")}
            self.journal.append(entry)
            return entry
        raise AssertionError("unexpected " + method + " " + "/".join(s))

    def created(self):
        return {"budgets": len(self.budgets), "rules": len(self.rules), "states": len(self.states), "planned": len(self.planned),
                "plannedStates": len(self.planned_states), "transactions": len(self.transactions), "meals": len(self.meals),
                "analyzedMeals": sum(m["nutrition"] is not None for m in self.meals), "journal": len(self.journal)}
