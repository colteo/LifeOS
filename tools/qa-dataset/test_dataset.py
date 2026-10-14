import copy
import json
import unittest
from datetime import date, timedelta
from pathlib import Path

from dataset import (BREAK, CATEGORIES, GYM_IMPORT_STATUS, STRESS, TARGETS, TRIP, digest, finance_movements, generate,
                     local_date, validate)

FIXTURE = Path(__file__).parent / "fixtures" / "manifest-v1.json"


def resign(manifest):
    manifest["sha256"] = digest({k: v for k, v in manifest.items() if k != "sha256"})
    return manifest


class DeterminismTests(unittest.TestCase):
    def test_reproducible_and_seed_or_reference_sensitive(self):
        self.assertEqual(generate(), generate())
        self.assertNotEqual(generate()["sha256"], generate(41)["sha256"])
        self.assertNotEqual(generate()["sha256"], generate(reference_date="2026-07-15")["sha256"])

    def test_reference_fixture_is_regenerable(self):
        self.assertEqual(json.loads(FIXTURE.read_text(encoding="utf-8")), generate())

    def test_other_seeds_and_reference_dates_stay_valid(self):
        for seed, reference in ((1, "2026-08-30"), (7, "2025-03-15"), (99, "2026-12-31"), (40, "2027-02-01")):
            manifest = generate(seed, reference)
            self.assertEqual(manifest["endDate"], str(date.fromisoformat(reference) - timedelta(days=1)))

    def test_invalid_parameters_rejected(self):
        for seed, reference in ((-1, "2026-08-30"), (True, "2026-08-30"), (40, "1999-01-01"), (40, "not-a-date")):
            with self.assertRaises(ValueError):
                generate(seed, reference)


class ContentTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.data = generate()

    def test_counts_within_targets(self):
        counts = self.data["counts"]
        for module, (low, high) in TARGETS.items():
            self.assertTrue(low <= counts[module] <= high, module)
        self.assertEqual(counts["gym"], 24)

    def test_finance_covers_planning_features_and_states(self):
        kinds = self.data["expected"]["finance"]["kinds"]
        self.assertTrue(all(kinds[k] > 0 for k in kinds))
        statuses = set(self.data["expected"]["finance"]["occurrences"].values())
        self.assertEqual(statuses, {"Confirmed", "Skipped", "Due"})
        self.assertEqual(set(self.data["expected"]["finance"]["plannedExpenses"].values()), {"Confirmed", "Cancelled", "Due"})

    def test_budgets_one_month_over(self):
        months = self.data["expected"]["finance"]["months"]
        self.assertEqual([k for k, v in months.items() if v["overBudget"]], ["2026-08"])
        movements = list(finance_movements(self.data))
        august = sum(a for d, kind, _, a in movements if kind == "Expense" and (d.year, d.month) == (2026, 8))
        self.assertEqual(round(august / 100, 2), months["2026-08"]["expenses"])

    def test_nutrition_mixes_analyzed_and_missing(self):
        expected = self.data["expected"]["nutrition"]
        self.assertGreater(expected["analyzed"], 30)
        self.assertGreater(expected["unanalyzed"], 15)

    def test_gym_is_offline_and_respects_break(self):
        start = date.fromisoformat(self.data["startDate"])
        for row in self.data["records"]["gym"]:
            self.assertEqual(row["importStatus"], GYM_IMPORT_STATUS)
            self.assertNotIn((date.fromisoformat(row["date"]) - start).days, BREAK)
        self.assertEqual([r["phase"] for r in self.data["records"]["gym"]][::8], ["Adattamento", "Forza", "Forza"])
        self.assertEqual(self.data["records"]["gym"][-1]["phase"], "Ripresa")

    def test_cross_module_relations(self):
        start = date.fromisoformat(self.data["startDate"])
        meals = {m["date"]: m for m in self.data["records"]["nutrition"]}
        for row in self.data["records"]["gym"]:
            if (date.fromisoformat(row["date"]) - start).days not in STRESS:  # deadline week: takeaway wins
                self.assertIn("post allenamento", meals[row["date"]]["payload"]["description"])
        for d in TRIP:
            self.assertIsNone(meals[str(start + timedelta(days=d))]["nutrition"])
        journal_days = {j["date"] for j in self.data["records"]["journal"]}
        self.assertTrue({str(start + timedelta(days=d)) for d in (TRIP[0], TRIP[-1])} <= journal_days)

    def test_journal_questions_are_verifiable(self):
        journal = {j["key"]: j for j in self.data["records"]["journal"]}
        kinds = {q["kind"] for q in self.data["journalQuestions"]}
        self.assertTrue({"fact", "temporal", "cross-module", "abstain"} <= kinds)
        for q in self.data["journalQuestions"]:
            text = " ".join(journal[k]["payload"]["content"] for k in q["evidenceKeys"])
            self.assertTrue(all(s in text for s in q["evidenceSnippets"]), q["id"])
        topics = {t for j in journal.values() for t in j["topics"]}
        self.assertGreaterEqual(len(topics), 6)

    def test_every_writable_record_carries_its_marker_and_no_owner(self):
        text = json.dumps(self.data["records"])
        self.assertNotIn("userId", text)
        for row in self.data["records"]["finance"]:
            if "payload" in row and "note" in row["payload"]:
                self.assertIn(f"[{row['key']}]", row["payload"]["note"])
        self.assertTrue(all(local_date(j["payload"]["occurredAtUtc"]).isoformat() == j["date"] for j in self.data["records"]["journal"]))

    def test_category_keys_are_fixed(self):
        used = {r["category"] for r in self.data["records"]["finance"] if "category" in r}
        self.assertTrue(used <= set(CATEGORIES))


class ValidationTests(unittest.TestCase):
    def setUp(self):
        self.data = generate()

    def assertInvalid(self, mutate):
        data = copy.deepcopy(self.data)
        mutate(data)
        with self.assertRaises(ValueError):
            validate(resign(data))

    def test_tamper_detected_without_resign(self):
        self.data["records"]["finance"][10]["payload"]["amount"] = 1
        with self.assertRaises(ValueError):
            validate(self.data)

    def test_contract_and_consistency_violations(self):
        finance = lambda kind: next(i for i, r in enumerate(self.data["records"]["finance"]) if r["kind"] == kind)
        tx, action, rule = finance("transaction"), finance("recurringAction"), finance("recurringRule")
        cases = [
            lambda d: d["records"]["finance"][tx]["payload"].__setitem__("amount", -1),
            lambda d: d["records"]["finance"][tx]["payload"].__setitem__("amount", 1.234),
            lambda d: d["records"]["finance"][tx]["payload"].__setitem__("userId", "x"),
            lambda d: d["records"]["finance"][tx]["payload"].__setitem__("note", "no marker"),
            lambda d: d["records"]["finance"][tx].__setitem__("category", "income"),
            lambda d: d["records"]["finance"][action].__setitem__("rule", "qa001-missing"),
            lambda d: d["records"]["finance"][action].__setitem__("month", 12),
            lambda d: d["records"]["finance"][rule]["payload"].__setitem__("endMonth", 1),
            lambda d: d["records"]["finance"].pop(0),
            lambda d: d["records"]["nutrition"][0]["payload"].__setitem__("time", "13:00:30"),
            lambda d: d["records"]["nutrition"][1].__setitem__("date", d["records"]["nutrition"][0]["date"]),
            lambda d: d["records"]["nutrition"][0].__setitem__("nutrition", {"caloriesKcal": 1}),
            lambda d: d["records"]["journal"][0]["payload"].__setitem__("title", "x" * 201),
            lambda d: d["records"]["journal"].reverse(),
            lambda d: d["journalQuestions"][0].__setitem__("evidenceSnippets", ["frase inesistente"]),
            lambda d: d["journalQuestions"][0].__setitem__("evidenceKeys", [d["records"]["gym"][0]["key"]]),
            lambda d: d["records"]["gym"][0].__setitem__("importStatus", "importable"),
            lambda d: d["records"]["gym"][0]["payload"]["sets"][0].__setitem__("reps", 0),
            lambda d: d["records"]["gym"][-1]["payload"]["sets"][0].__setitem__("weightKg", 500),
            lambda d: d["records"]["finance"][0].__setitem__("amount", 99999),
            lambda d: d["expected"]["nutrition"].__setitem__("analyzed", 0),
            lambda d: d["scenarios"][0]["evidence"]["journal"].append("qa001-missing"),
            lambda d: d.__setitem__("synthetic", False),
            lambda d: d["counts"].__setitem__("journal", 1),
        ]
        for index, mutate in enumerate(cases):
            with self.subTest(case=index):
                self.assertInvalid(mutate)


if __name__ == "__main__":
    unittest.main()
