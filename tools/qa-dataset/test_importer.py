import copy
import json
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch

from dataset import generate
from fake_api import CONFIG, FakeApi
from importer import (Api, ApiFailure, ImportFailure, NoRedirect, binding, empty_report, import_dataset, locked,
                      request_for, steps_for, target_url)

DATA = generate()
WRITES = len(steps_for(DATA))
EXPECTED = {"budgets": 3, "rules": 5, "states": 13, "planned": 6, "plannedStates": 4,
            "transactions": 120 + 12 + 3,  # direct + recurring confirmations + planned confirmations
            "meals": 90, "analyzedMeals": DATA["expected"]["nutrition"]["analyzed"], "journal": 52}


class ImportTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.state = Path(self.directory.name) / "state.json"
        self.data = DATA
        self.approval = {**binding(self.data, CONFIG), "approved": True, "dedicatedTestAccount": True,
                         "expiresAtUtc": (datetime.now(timezone.utc) + timedelta(hours=1)).isoformat()}
        self.api = FakeApi()
        # Durability (fsync per state write) is real-run behaviour; it only slows the tests down.
        fsync = patch("importer.os.fsync")
        fsync.start()
        self.addCleanup(fsync.stop)

    def run_import(self, report=None):
        return import_dataset(self.data, CONFIG, self.approval, self.state, self.api, report)

    def posts(self, since=0):
        return [c for c in self.api.calls[since:] if c[0] in ("POST", "PUT")]

    def test_complete_import_in_dependency_order(self):
        report = self.run_import()
        self.assertEqual(report["outcome"], "complete")
        self.assertEqual(self.api.created(), EXPECTED)
        self.assertEqual(sum(m["created"] for m in report["modules"].values()), WRITES)
        self.assertEqual(report["gym"], {"status": "not_importable_via_public_api", "workouts": 24, "imported": 0})
        order = [c[1].split("?")[0].split("/")[2] for c in self.posts()]
        first = {route: order.index(route) for route in dict.fromkeys(order)}
        self.assertLess(first["budgets"], first["recurring"])
        self.assertLess(first["recurring"], first["planned-expenses"])
        self.assertLess(first["planned-expenses"], first["transactions"])
        self.assertLess(first["transactions"], first["nutrition"])
        self.assertLess(first["nutrition"], first["journal"])
        self.assertFalse(any("/gym" in path for _, path in self.api.calls))

    def test_rerun_and_state_loss_never_duplicate(self):
        self.run_import()
        before = len(self.api.calls)
        report = self.run_import()
        self.assertEqual(sum(m["alreadyPresent"] for m in report["modules"].values()), WRITES)
        self.state.unlink()
        self.run_import()
        self.assertEqual(self.posts(before), [])
        self.assertEqual(self.api.created(), EXPECTED)

    def test_partial_validation_failure_resumes(self):
        self.api.fail_at = 40
        report = empty_report(self.data, "apply")
        with self.assertRaises(ApiFailure):
            self.run_import(report)
        self.assertEqual(report["failure"]["httpStatus"], 400)
        self.assertEqual(sum(m["created"] for m in report["modules"].values()), 39)
        self.api.fail_at = None
        resumed = self.run_import()
        self.assertEqual(sum(m["created"] for m in resumed["modules"].values()), WRITES - 39)
        self.assertEqual(self.api.created(), EXPECTED)

    def test_error_statuses_are_reported_and_resumable(self):
        for status in (401, 403, 409, 429):
            with self.subTest(status=status):
                self.setUp()
                self.api.fail_at, self.api.status = 7, status
                report = empty_report(self.data, "apply")
                with self.assertRaises(ApiFailure):
                    self.run_import(report)
                self.assertEqual(report["failure"]["httpStatus"], status)
                self.assertTrue(report["failure"]["guidance"])
                self.api.fail_at = None
                self.run_import()
                self.assertEqual(self.api.created(), EXPECTED)

    def test_timeout_after_commit_reconciles_create(self):
        self.api.fail_at, self.api.status, self.api.persist_failure = 60, None, True
        with self.assertRaises(ApiFailure):
            self.run_import()
        self.api.fail_at = None
        self.run_import()
        self.assertEqual(self.api.created(), EXPECTED)

    def test_uncertain_create_without_remote_result_never_reposts(self):
        self.api.fail_at, self.api.status = 60, None  # transport failure, nothing committed
        with self.assertRaises(ApiFailure):
            self.run_import()
        before = len(self.api.calls)
        self.api.fail_at = None
        with self.assertRaises(ImportFailure):
            self.run_import()
        self.assertEqual(self.posts(before), [])

    def test_uncertain_idempotent_actions_are_retried_safely(self):
        steps = steps_for(self.data)
        confirm = next(i for i, s in enumerate(steps) if s.kind == "recurringAction") + 1
        for persist in (False, True):
            with self.subTest(committed=persist):
                self.setUp()
                self.api.fail_at, self.api.status, self.api.persist_failure = confirm, None, persist
                with self.assertRaises(ApiFailure):
                    self.run_import()
                self.api.fail_at = None
                self.run_import()
                self.assertEqual(self.api.created(), EXPECTED)

    def test_uncertain_nutrition_put_is_retried(self):
        steps = steps_for(self.data)
        index = next(i for i, s in enumerate(steps) if s.kind == "mealNutrition") + 1
        self.api.fail_at, self.api.status = index, None
        with self.assertRaises(ApiFailure):
            self.run_import()
        self.api.fail_at = None
        self.run_import()
        self.assertEqual(self.api.created(), EXPECTED)

    def test_approval_mismatch_and_expiry_before_network(self):
        cases = (("approved", False), ("dedicatedTestAccount", False), ("userId", "wrong"),
                 ("expiresAtUtc", "2000-01-01T00:00:00Z"), ("expiresAtUtc", "2999-01-01T00:00:00"),
                 ("configSha256", "changed"), ("remote", True))
        for field, value in cases:
            with self.assertRaises(ImportFailure):
                import_dataset(self.data, CONFIG, {**self.approval, field: value}, self.state, self.api)
        self.assertEqual(self.api.calls, [])

    def test_config_must_map_every_category(self):
        config = copy.deepcopy(CONFIG)
        del config["categories"]["travel"]
        with self.assertRaises(ImportFailure):
            binding(self.data, config)

    def test_user_onboarding_account_and_category_guards(self):
        for field, value in (("userId", "wrong"), ("onboardingStatus", "PendingFirstAccount"), ("defaultCurrency", "USD")):
            self.api.user = {**self.api.user, field: value}
            with self.assertRaises(ImportFailure):
                self.run_import()
            self.api = FakeApi()
        config = copy.deepcopy(CONFIG)
        config["categories"]["income"] = CONFIG["categories"]["groceries"]  # an Expense category for Income
        approval = {**self.approval, **binding(self.data, config)}
        with self.assertRaises(ImportFailure):
            import_dataset(self.data, config, approval, self.state, self.api)
        self.assertEqual(self.posts(), [])

    def test_future_dataset_refused(self):
        future = generate(reference_date=str((datetime.now(timezone.utc) + timedelta(days=30)).date()))
        approval = {**self.approval, **binding(future, CONFIG)}
        with self.assertRaises(ImportFailure):
            import_dataset(future, CONFIG, approval, self.state, self.api)
        self.assertEqual(self.api.calls, [])

    def test_remote_conflicts_abort_before_any_write(self):
        conflicts = [
            lambda api: api.budgets.__setitem__((2026, 6), {"id": "b", "amount": 1.0}),
            lambda api: api.transactions.extend([{"note": self.data["records"]["finance"][-1]["payload"]["note"], "id": "x",
                                                  "type": "Expense", "amount": 1}]),
            lambda api: api.transactions.extend([{"note": self.data["records"]["finance"][-1]["payload"]["note"], "id": str(i)}
                                                 for i in range(2)]),
        ]
        for mutate in conflicts:
            self.api = FakeApi()
            mutate(self.api)
            with self.assertRaises(ImportFailure):
                self.run_import()
            self.assertEqual(self.posts(), [])

    def test_externally_processed_occurrence_conflicts(self):
        self.run_import()
        rule = self.api.rules[2]  # gym subscription: August is expected to be skipped
        self.api.states[(rule["id"], 2026, 8)] = ("Confirmed", "tx")
        before = len(self.api.calls)
        with self.assertRaises(ImportFailure):
            self.run_import()
        self.assertEqual(self.posts(before), [])

    def test_lazy_close_nutrition_is_a_warning_not_a_conflict(self):
        self.run_import()
        unanalyzed = next(m for m in self.api.meals if m["nutrition"] is None)
        unanalyzed["nutrition"] = {"caloriesKcal": 500, "proteinGrams": 20, "carbsGrams": 50, "fatGrams": 10, "source": "AiAutoClosed"}
        report = self.run_import()
        self.assertEqual(len(report["warnings"]), 1)

    def test_requests_never_carry_owner_and_use_contract_shapes(self):
        ids = {s.key: "11111111-1111-1111-1111-111111111111" for s in steps_for(self.data)}
        for step in steps_for(self.data):
            method, path, payload = request_for(step, CONFIG, self.data, ids)
            self.assertTrue(path.startswith("/api/"))
            self.assertNotIn("userId", json.dumps(payload or {}))
            if step.kind == "transaction":
                self.assertEqual(set(payload), {"type", "amount", "accountId", "sourceAccountId", "destinationAccountId",
                                                "categoryId", "occurredAtUtc", "note"})
            if step.kind == "mealNutrition":
                self.assertEqual(payload["source"], "UserAdjusted")

    def test_lock_and_wrong_resume_binding(self):
        with locked(self.state):
            with self.assertRaises(ImportFailure):
                self.run_import()
        self.state.write_text(json.dumps({"binding": {}, "records": {}}))
        with self.assertRaises(ImportFailure):
            self.run_import()
        self.assertEqual(self.api.calls, [])


class TransportTests(unittest.TestCase):
    def test_targets(self):
        for url in ("https://lifeos.example.com", "http://localhost.evil", "http://user:secret@localhost",
                    "http://127.0.0.1/path", "file:///tmp", "http://127.0.0.1?target=prod", "ftp://localhost"):
            with self.assertRaises(ImportFailure):
                target_url(url)
        for url in ("http://localhost:5050", "https://127.0.0.1", "http://[::1]:5050"):
            self.assertEqual(target_url(url), url)
        self.assertEqual(target_url("https://qa.example.com", allow_remote=True), "https://qa.example.com")
        for url in ("http://qa.example.com", "https://qa.example.com/api", "https://u:p@qa.example.com"):
            with self.assertRaises(ImportFailure):
                target_url(url, allow_remote=True)

    def test_remote_binding_needs_matching_flag(self):
        config = {**CONFIG, "baseUrl": "https://qa.example.com"}
        with self.assertRaises(ImportFailure):
            binding(DATA, config)
        self.assertTrue(binding(DATA, config, allow_remote=True)["remote"])

    def test_redirect_is_not_followed_and_no_proxy(self):
        self.assertIsNone(NoRedirect().redirect_request(None, None, 302, "", {}, "https://production.example"))
        api = Api(CONFIG["baseUrl"], "synthetic-test-token")
        self.assertFalse(any(getattr(h, "proxies", {}) for h in api.opener.handlers))
        for token in ("", "two words"):
            with self.assertRaises(ImportFailure):
                Api(CONFIG["baseUrl"], token)

    def test_unreachable_loopback_is_a_transport_failure(self):
        api = Api("http://127.0.0.1:9", "synthetic-test-token")
        with self.assertRaises(ApiFailure) as raised:
            api.request("GET", "/api/me")
        self.assertIsNone(raised.exception.status)
        with self.assertRaises(ImportFailure):
            api.request("GET", "https://elsewhere.example/api/me")


if __name__ == "__main__":
    unittest.main()
