"""API-only import: approval gate, reconciliation before any write, durable resume state, fail-closed
ambiguous creates. Uses only existing authenticated LifeOS endpoints; Gym is never imported (ADR-008)."""
import json
import os
from contextlib import contextmanager
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlencode, urlsplit
from urllib.request import HTTPRedirectHandler, ProxyHandler, Request, build_opener
from uuid import UUID

from dataset import CATEGORIES, GYM_IMPORT_STATUS, IMPORTABLE_MODULES, digest, months_between, parse_instant, validate

LOOPBACK_HOSTS = ("localhost", "127.0.0.1", "::1")
# Actionable guidance per HTTP status. Server bodies are never printed (they may echo personal data).
STATUS_GUIDANCE = {
    400: "validation rejected the request: check the config mapping (category types, account currency) and the manifest",
    401: "authentication failed or the access token expired: obtain a new token; rerunning is safe",
    403: "the token is not allowed to perform this operation",
    404: "a referenced account, category or resource was not found for this user: check the config ids",
    409: "the server state conflicts with this request: inspect the record in the app; nothing was overwritten",
    422: "the server could not process the request",
    429: "rate limited: wait, then rerun; reconciliation skips everything already imported",
    None: "network failure or timeout: the outcome of the last write is uncertain; rerun to reconcile",
}
# Statuses that prove a create was NOT applied, so the key may be planned again on the next run.
NOT_APPLIED = (400, 401, 403, 404, 409, 422, 429)


class ImportFailure(Exception):
    pass


class ApiFailure(ImportFailure):
    def __init__(self, status=None):
        self.status = status
        super().__init__(f"API request failed (HTTP {status})" if status else "API transport or response failure")


def target_url(url, allow_remote=False):
    """Loopback by default. A remote origin needs --allow-remote, HTTPS and an origin-only URL."""
    parts = urlsplit(url or "")
    if parts.username or parts.password or parts.query or parts.fragment or parts.path not in ("", "/") or not parts.hostname:
        raise ImportFailure("The API base URL must be a bare origin without credentials, path or query")
    try:
        parts.port
    except ValueError:
        raise ImportFailure("Invalid API port") from None
    if parts.hostname in LOOPBACK_HOSTS:
        if parts.scheme not in ("http", "https"):
            raise ImportFailure("Unsupported URL scheme")
    elif not allow_remote:
        raise ImportFailure("Only loopback API origins are allowed without --allow-remote")
    elif parts.scheme != "https":
        raise ImportFailure("A remote API origin must use HTTPS")
    return url.rstrip("/")


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class Api:
    def __init__(self, base_url, token, allow_remote=False):
        self.base_url = target_url(base_url, allow_remote)
        if not token or any(c.isspace() for c in token):
            raise ImportFailure("A valid LifeOS access token is required in LIFEOS_QA_TOKEN")
        self.token = token
        # No proxies and no redirects: credentials must never leave the approved origin.
        self.opener = build_opener(ProxyHandler({}), NoRedirect())

    def request(self, method, path, payload=None):
        if not path.startswith("/api/"):
            raise ImportFailure("Invalid API route")
        request = Request(self.base_url + path, data=json.dumps(payload).encode() if payload is not None else None,
                          headers={"Authorization": "Bearer " + self.token, "Content-Type": "application/json",
                                   "Accept": "application/json"}, method=method)
        try:
            with self.opener.open(request, timeout=30) as response:
                body = response.read()
                return json.loads(body) if body else None
        except HTTPError as error:
            raise ApiFailure(error.code) from None
        except (URLError, OSError, ValueError):
            raise ApiFailure() from None


def save(path, value, pretty=True):
    """Atomic, durable write. The resume state is saved compactly: it is rewritten after every step."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    with temporary.open("w", encoding="utf-8") as stream:
        stream.write(json.dumps(value, indent=2 if pretty else None, ensure_ascii=False))
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


@contextmanager
def locked(path):
    lock = Path(str(path) + ".lock")
    lock.parent.mkdir(parents=True, exist_ok=True)
    try:
        fd = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    except FileExistsError:
        raise ImportFailure("Import lock exists. Verify no importer is running before removing a stale lock") from None
    try:
        os.close(fd)
        yield
    finally:
        lock.unlink()


def check_config(config, allow_remote=False):
    url = target_url(config["baseUrl"], allow_remote)
    for field in ("userId", "accountId"):
        UUID(config[field])
    categories = config["categories"]
    if set(categories) != set(CATEGORIES):
        raise ImportFailure("config.categories must map exactly these keys: " + ", ".join(sorted(CATEGORIES)))
    for value in categories.values():
        UUID(value)
    return url


def binding(manifest, config, allow_remote=False):
    validate(manifest)
    url = check_config(config, allow_remote)
    return {"manifestSha256": manifest["sha256"], "configSha256": digest(config), "baseUrl": url,
            "userId": config["userId"], "remote": urlsplit(url).hostname not in LOOPBACK_HOSTS}


def check_approval(manifest, config, approval, allow_remote=False):
    expected = binding(manifest, config, allow_remote)
    if any(approval.get(k) != v for k, v in expected.items()) or approval.get("approved") is not True:
        raise ImportFailure("Explicit approval does not match this manifest, user and target configuration")
    if approval.get("dedicatedTestAccount") is not True:
        raise ImportFailure("Approval must confirm dedicatedTestAccount: the target is the separate QA account")
    try:
        expiry = datetime.fromisoformat(approval["expiresAtUtc"].replace("Z", "+00:00"))
        if expiry.utcoffset() is None or expiry <= datetime.now(timezone.utc):
            raise ValueError()
    except (KeyError, ValueError, AttributeError):
        raise ImportFailure("Approval must have a future expiresAtUtc with an explicit offset") from None
    return expected


# ---------------------------------------------------------------- steps

class Step:
    """One idempotency unit. Creates (POST of a new resource) are not idempotent; actions on a logical
    month/item and PUTs are idempotent server-side, so an uncertain attempt may be retried."""

    def __init__(self, module, key, kind, idempotent, row):
        self.module, self.key, self.kind, self.idempotent, self.row = module, key, kind, idempotent, row
        self.remote_id = None


def steps_for(manifest):
    steps = []
    for row in manifest["records"]["finance"]:
        idempotent = row["kind"] in ("budget", "recurringAction", "plannedAction")
        steps.append(Step("finance", row["key"], row["kind"], idempotent, row))
    for row in manifest["records"]["nutrition"]:
        steps.append(Step("nutrition", row["key"], "meal", False, row))
        if row["nutrition"] is not None:
            steps.append(Step("nutrition", row["key"] + "#nutrition", "mealNutrition", True, row))
    for row in manifest["records"]["journal"]:
        steps.append(Step("journal", row["key"], "journal", False, row))
    return steps


def marker_matches(rows, field, key):
    marker = f"[{key}]"
    matches = [r for r in rows if marker in (r.get(field) or "")]
    if len(matches) > 1:
        raise ImportFailure(f"Duplicate remote marker for {key}: manual investigation required")
    return matches[0] if matches else None


def same(left, right):
    if isinstance(right, float) or isinstance(left, float):
        return left is not None and right is not None and abs(float(left) - float(right)) < 0.00005
    if isinstance(right, str) and isinstance(left, str) and right.endswith("Z") and "T" in right:
        return parse_instant(left) == parse_instant(right)
    return left == right


def require_same(key, remote, expected):
    for field, value in expected.items():
        if not same(remote.get(field), value):
            raise ImportFailure(f"Remote record {key} differs in '{field}': refusing to overwrite or duplicate")


def request_for(step, config, manifest, ids):
    """(method, path, payload) for a step; ids maps parent keys to remote ids."""
    row, offset = step.row, manifest["utcOffsetMinutes"]
    account, category = config["accountId"], config["categories"].get(row.get("category"))
    q = urlencode({"utcOffsetMinutes": offset})
    if step.kind == "budget":
        return "PUT", f"/api/budgets/{row['year']}/{row['month']}/{quote(manifest['currency'])}", {"amount": row["amount"]}
    if step.kind == "recurringRule":
        return "POST", "/api/recurring", {**row["payload"], "accountId": account, "categoryId": category}
    if step.kind == "recurringAction":
        path = f"/api/recurring/{ids[row['rule']]}/{row['year']}/{row['month']}/{row['action']}?{q}"
        return "POST", path, row.get("payload")
    if step.kind == "plannedExpense":
        return "POST", "/api/planned-expenses", {**row["payload"], "accountId": account, "categoryId": category}
    if step.kind == "plannedAction":
        return "POST", f"/api/planned-expenses/{ids[row['planned']]}/{row['action']}?{q}", row.get("payload")
    if step.kind == "transaction":
        return "POST", "/api/transactions", {**row["payload"], "accountId": account, "categoryId": category,
                                             "sourceAccountId": None, "destinationAccountId": None}
    if step.kind == "meal":
        return "POST", "/api/nutrition/meals", row["payload"]
    if step.kind == "mealNutrition":
        return "PUT", f"/api/nutrition/meals/{ids[row['key']]}/nutrition", {**row["nutrition"], "source": "UserAdjusted"}
    if step.kind == "journal":
        return "POST", "/api/journal", row["payload"]
    raise ImportFailure("Unknown step kind")


def read_remote(api, manifest):
    """Read-only snapshot of everything reconciliation needs (all owned by the token's user)."""
    start, end = date.fromisoformat(manifest["startDate"]), date.fromisoformat(manifest["endDate"])
    offset, currency = manifest["utcOffsetMinutes"], manifest["currency"]
    months = months_between(start, end)
    remote = {"budgets": {}}
    for year, month in months:
        first = datetime(year, month, 1, tzinfo=timezone.utc) - timedelta(minutes=offset)
        following = datetime(year + month // 12, month % 12 + 1, 1, tzinfo=timezone.utc) - timedelta(minutes=offset)
        query = urlencode({"fromUtc": first.isoformat().replace("+00:00", "Z"),
                           "toUtc": following.isoformat().replace("+00:00", "Z"), "utcOffsetMinutes": offset})
        remote["budgets"][(year, month)] = api.request("GET", f"/api/budgets/{year}/{month}/{quote(currency)}?{query}")["budget"]
    (from_year, from_month), (to_year, to_month) = months[0], months[-1]
    remote["recurring"] = api.request("GET", "/api/recurring?" + urlencode({
        "fromYear": from_year, "fromMonth": from_month, "toYear": to_year, "toMonth": to_month, "utcOffsetMinutes": offset}))
    remote["planned"] = api.request("GET", "/api/planned-expenses?" + urlencode({
        "from": str(start), "to": str(end), "utcOffsetMinutes": offset}))
    # Half-open UTC range wide enough for every local day of the window.
    remote["transactions"] = api.request("GET", "/api/transactions?" + urlencode({
        "fromUtc": f"{start - timedelta(days=1)}T00:00:00Z", "toUtc": f"{end + timedelta(days=2)}T00:00:00Z"}))
    remote["journal"], cursor, seen = [], None, set()
    while True:
        page = api.request("GET", "/api/journal?" + urlencode({"limit": 50, **({"cursor": cursor} if cursor else {})}))
        remote["journal"].extend(page["items"])
        cursor = page["nextCursor"]
        if not cursor:
            break
        if cursor in seen:
            raise ImportFailure("Journal pagination did not advance")
        seen.add(cursor)
    remote["meals"] = []
    for row in manifest["records"]["nutrition"]:
        remote["meals"].extend(api.request("GET", "/api/nutrition/meals?" + urlencode({"date": row["date"]})))
    return remote


def reconcile(step, remote, config, manifest, ids, warnings):
    """Return the remote id when the step is already applied, None when it still has to run.
    Raises ImportFailure on any conflicting remote state (nothing is overwritten)."""
    row = step.row
    if step.kind == "budget":
        budget = remote["budgets"][(row["year"], row["month"])]
        if budget is None:
            return None
        require_same(step.key, budget, {"amount": row["amount"]})
        return budget["id"]
    if step.kind == "recurringRule":
        match = marker_matches(remote["recurring"]["rules"], "note", step.key)
        if match:
            require_same(step.key, match, {**row["payload"], "accountId": config["accountId"],
                                           "categoryId": config["categories"][row["category"]]})
            return match["id"]
        return None
    if step.kind == "recurringAction":
        rule_id = ids.get(row["rule"])
        if rule_id is None:
            return None  # the rule is created in this run: its occurrences are still unprocessed
        occurrence = next((o for o in remote["recurring"]["occurrences"] if o["ruleId"] == rule_id
                           and o["year"] == row["year"] and o["month"] == row["month"]), None)
        if occurrence is None:
            raise ImportFailure(f"Occurrence for {step.key} is missing remotely: the rule range differs")
        status = occurrence["status"]
        if status == "Due":
            return None
        if (status, row["action"]) == ("Confirmed", "confirm"):
            return occurrence["transactionId"]
        if (status, row["action"]) == ("Skipped", "skip"):
            return f"{rule_id}:{row['year']}-{row['month']}"
        raise ImportFailure(f"Occurrence {step.key} is {status} remotely; expected to {row['action']} it")
    if step.kind == "plannedExpense":
        match = marker_matches(remote["planned"], "note", step.key)
        if match:
            require_same(step.key, match, {"name": row["payload"]["name"], "expectedAmount": row["payload"]["expectedAmount"],
                                           "scheduledDate": row["payload"]["scheduledDate"], "accountId": config["accountId"],
                                           "categoryId": config["categories"][row["category"]]})
            return match["id"]
        return None
    if step.kind == "plannedAction":
        planned_id = ids.get(row["planned"])
        if planned_id is None:
            return None
        item = next((p for p in remote["planned"] if p["id"] == planned_id), None)
        status = item["status"] if item else "Due"
        if status == "Due":
            return None
        if (status, row["action"]) == ("Confirmed", "confirm"):
            return item["transactionId"]
        if (status, row["action"]) == ("Cancelled", "cancel"):
            return planned_id
        raise ImportFailure(f"Planned expense {step.key} is {status} remotely; expected to {row['action']} it")
    if step.kind == "transaction":
        match = marker_matches(remote["transactions"], "note", step.key)
        if match:
            p = row["payload"]
            # TransactionResponse names the transaction type "type".
            require_same(step.key, match, {"type": p["type"], "amount": p["amount"], "occurredAtUtc": p["occurredAtUtc"],
                                           "note": p["note"], "accountId": config["accountId"],
                                           "categoryId": config["categories"][row["category"]]})
            return match["id"]
        return None
    if step.kind == "meal":
        match = marker_matches(remote["meals"], "description", step.key)
        if match:
            p = row["payload"]
            require_same(step.key, match, {"description": p["description"], "mealType": p["mealType"],
                                           "date": p["date"], "time": p["time"]})
            if row["nutrition"] is None and match.get("nutrition"):
                warnings.append(f"{step.key}: meal planned as unanalyzed has nutrition ({match['nutrition'].get('source')}),"
                                " probably from the app's lazy close")
            return match["id"]
        return None
    if step.kind == "mealNutrition":
        meal_id = ids.get(row["key"])
        meal = next((m for m in remote["meals"] if m["id"] == meal_id), None) if meal_id else None
        if not meal or not meal.get("nutrition"):
            return None
        require_same(step.key, meal["nutrition"], row["nutrition"])
        return meal_id
    if step.kind == "journal":
        match = marker_matches(remote["journal"], "content", step.key)
        if match:
            require_same(step.key, match, row["payload"])
            return match["id"]
        return None
    raise ImportFailure("Unknown step kind")


def remote_id_from(step, response):
    if step.kind in ("budget", "mealNutrition"):
        return step.key  # PUT: 204 / the meal; the logical key identifies it
    if step.kind in ("recurringAction", "plannedAction"):
        return (response or {}).get("transactionId") or step.key
    if not response or not response.get("id"):
        raise ApiFailure()
    return response["id"]


def empty_report(manifest, mode):
    modules = {m: {"steps": 0, "alreadyPresent": 0, "created": 0, "pending": 0} for m in IMPORTABLE_MODULES}
    return {"mode": mode, "manifestSha256": manifest["sha256"], "outcome": "not-started",
            "startedAtUtc": datetime.now(timezone.utc).isoformat(), "modules": modules, "warnings": [], "failure": None,
            "gym": {"status": GYM_IMPORT_STATUS, "workouts": len(manifest["records"]["gym"]), "imported": 0}}


def import_dataset(manifest, config, approval, state_path, api, report=None, allow_remote=False):
    expected = check_approval(manifest, config, approval, allow_remote)
    if api.base_url != expected["baseUrl"]:
        raise ImportFailure("Transport target does not match approval")
    if manifest["endDate"] >= str(datetime.now(timezone.utc).date()):
        raise ImportFailure("Only datasets ending before today may be imported")
    report = report if report is not None else empty_report(manifest, "apply")
    with locked(state_path):
        state = json.loads(Path(state_path).read_text(encoding="utf-8")) if Path(state_path).exists() else {"binding": expected, "records": {}}
        if state.get("binding") != expected:
            raise ImportFailure("Resume state belongs to a different manifest, configuration or user")
        me = api.request("GET", "/api/me")
        if me["userId"] != config["userId"] or me["onboardingStatus"] != "Completed" or me["defaultCurrency"] != manifest["currency"]:
            raise ImportFailure("Authenticated user, onboarding or currency mismatch")
        accounts = api.request("GET", "/api/accounts")
        categories = api.request("GET", "/api/categories")
        if not any(a["id"] == config["accountId"] and a["currency"] == manifest["currency"] for a in accounts):
            raise ImportFailure("Configured account is not owned by this user or currency differs")
        for name, kind in CATEGORIES.items():
            if not any(c["id"] == config["categories"][name] and c["type"] == kind for c in categories):
                raise ImportFailure(f"Configured category '{name}' is not owned by this user or is not {kind}")
        remote = read_remote(api, manifest)
        steps, ids = steps_for(manifest), {}
        # Reconcile EVERY step before the first write: conflicts never cause a partial new import.
        plan = []
        for step in steps:
            report["modules"][step.module]["steps"] += 1
            remote_id = reconcile(step, remote, config, manifest, ids, report["warnings"])
            previous = state["records"].get(step.key)
            if remote_id:
                ids[step.row["key"] if step.kind == "mealNutrition" else step.key] = remote_id
                state["records"][step.key] = {"status": "done", "remoteId": remote_id}
                report["modules"][step.module]["alreadyPresent"] += 1
            elif previous and not step.idempotent:
                raise ImportFailure(f"Previously attempted create {step.key} is absent remotely; resolve manually "
                                    "(see README: uncertain records), no automatic POST retry")
            else:
                plan.append(step)
                report["modules"][step.module]["pending"] += 1
        save(state_path, state, pretty=False)
        for step in plan:
            method, path, payload = request_for(step, config, manifest, ids)
            state["records"][step.key] = {"status": "uncertain"}
            save(state_path, state, pretty=False)  # Durable intent BEFORE dispatch.
            try:
                response = api.request(method, path, payload)
                remote_id = remote_id_from(step, response)
            except ApiFailure as error:
                if error.status in NOT_APPLIED:
                    del state["records"][step.key]
                    save(state_path, state, pretty=False)
                report["failure"] = {"key": step.key, "kind": step.kind, "httpStatus": error.status,
                                     "guidance": STATUS_GUIDANCE.get(error.status, STATUS_GUIDANCE[422])}
                raise
            if step.kind != "mealNutrition":
                ids[step.key] = remote_id
            state["records"][step.key] = {"status": "done", "remoteId": remote_id}
            save(state_path, state, pretty=False)
            report["modules"][step.module]["created"] += 1
            report["modules"][step.module]["pending"] -= 1
        report["outcome"] = "complete"
        return report


def whoami(api):
    """Read-only identity check used to fill the local config. Never prints the email address."""
    me = api.request("GET", "/api/me")
    return {"userId": me["userId"], "onboardingStatus": me["onboardingStatus"], "defaultCurrency": me["defaultCurrency"],
            "accounts": [{"id": a["id"], "name": a["name"], "currency": a["currency"]} for a in api.request("GET", "/api/accounts")],
            "categories": [{"id": c["id"], "name": c["name"], "type": c["type"]} for c in api.request("GET", "/api/categories")]}
