"""QA-001 reproducible, synthetic-only 90-day dataset. No network or database access.

Everything here is fictional. The generator depends only on (seed, reference date): the same pair
always produces the same manifest, byte for byte.
"""
import hashlib
import json
import random
from datetime import date, datetime, time, timedelta, timezone

SCHEMA_VERSION = 1
DAYS = 90
DEFAULT_SEED = 40
# The dataset covers the 90 days BEFORE the reference date: 2026-06-01 .. 2026-08-29.
DEFAULT_REFERENCE_DATE = "2026-08-30"
CURRENCY = "EUR"
# Fixed synthetic device offset (+02:00) used for local wall-clock times and API utcOffsetMinutes.
UTC_OFFSET_MINUTES = 120
GYM_IMPORT_STATUS = "not_importable_via_public_api"
GYM_IMPORT_REASON = ("ADR-008: workout session start/finish instants are assigned by the server clock; "
                     "no public API records historical sessions. Offline reference fixture only.")
TARGETS = {"finance": (140, 160), "nutrition": (85, 95), "journal": (40, 60), "gym": (24, 24)}
IMPORTABLE_MODULES = ("finance", "nutrition", "journal")
# Synthetic category keys. The operator maps each key to an existing category of the test account.
CATEGORIES = {"income": "Income", "groceries": "Expense", "dining": "Expense", "transport": "Expense",
              "home": "Expense", "subscriptions": "Expense", "travel": "Expense", "other": "Expense"}
FINANCE_KINDS = ("budget", "recurringRule", "recurringAction", "plannedExpense", "plannedAction", "transaction")
MEAL_TYPES = ("Breakfast", "Lunch", "Dinner", "Snack", "Other")
MONTHS_IT = ("gennaio", "febbraio", "marzo", "aprile", "maggio", "giugno", "luglio", "agosto",
             "settembre", "ottobre", "novembre", "dicembre")
WEEKDAYS_IT = ("lunedì", "martedì", "mercoledì", "giovedì", "venerdì", "sabato", "domenica")
FORBIDDEN_FIELDS = {"userid", "user_id", "ownerid", "owner"}

# Narrative timeline, as day offsets from the start date.
STRESS = range(14, 21)        # deadline of the fictional "Aurora" project: takeaway dinners
CAR_SERVICE = 40              # planned car service costs more than expected
TRIP = range(62, 69)          # 7-day trip to Trieste: travel spending, budget overrun
BREAK = range(62, 76)         # no training: the trip, then a cold
KNEE = 78                     # mild knee discomfort: squat load reduced afterwards
MEAL_PREP = 83                # Sunday meal prep starts
PHASES = ((0, "Adattamento"), (28, "Forza"), (76, "Ripresa"))
EXTRA_WORKOUTS = (47, 82)     # make-up sessions


def canonical(value):
    return json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(",", ":"))


def digest(value):
    return hashlib.sha256(canonical(value).encode()).hexdigest()


def money(cents):
    return round(cents / 100, 2)


def cents(amount):
    return round(amount * 100)


def utc_instant(day, hour, minute=0):
    """Local wall-clock time at the fixed synthetic offset, as an explicit UTC instant."""
    local = datetime.combine(day, time(hour, minute), timezone(timedelta(minutes=UTC_OFFSET_MINUTES)))
    return local.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def parse_instant(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def local_date(instant):
    return (parse_instant(instant) + timedelta(minutes=UTC_OFFSET_MINUTES)).date()


def label(day):
    return f"{WEEKDAYS_IT[day.weekday()]} {day.day} {MONTHS_IT[day.month - 1]}"


def month_key(day):
    return f"{day.year:04d}-{day.month:02d}"


def months_between(start, end):
    result, cursor = [], date(start.year, start.month, 1)
    while cursor <= end:
        result.append((cursor.year, cursor.month))
        cursor = date(cursor.year + cursor.month // 12, cursor.month % 12 + 1, 1)
    return result


def scheduled(year, month, day_of_month):
    following = date(year + month // 12, month % 12 + 1, 1)
    return date(year, month, min(day_of_month, (following - timedelta(days=1)).day))


def workout_days():
    days = {week * 7 + offset for week in range(13) for offset in (1, 4)}
    return sorted(d for d in days | set(EXTRA_WORKOUTS) if d < DAYS and d not in BREAK)


def phase(d):
    return [name for first, name in PHASES if d >= first][-1]


def generate(seed=DEFAULT_SEED, reference_date=DEFAULT_REFERENCE_DATE):
    if isinstance(seed, bool) or not isinstance(seed, int) or not 0 <= seed <= 2**63:
        raise ValueError("Seed must be a non-negative integer")
    reference = date.fromisoformat(reference_date)
    if not date(2000, 4, 1) <= reference <= date(9998, 1, 1):
        raise ValueError("Reference date must be between 2000-04-01 and 9998-01-01")
    start, end = reference - timedelta(days=DAYS), reference - timedelta(days=1)
    rng = random.Random(seed)
    identity = digest({"version": SCHEMA_VERSION, "seed": seed, "reference": str(reference)})[:16]
    records = {"finance": [], "nutrition": [], "journal": [], "gym": []}

    def day_of(d):
        return start + timedelta(days=d)

    def add(module, day, record, marker_field=None):
        key = f"qa001-{identity}-{module}-{len(records[module]):03d}"
        if marker_field:
            record["payload"][marker_field] = record["payload"][marker_field] + f" [{key}]"
        records[module].append({"key": key, "date": str(day), **record})
        return key

    # ---- Finance: budgets, recurring rules + occurrence actions, planned expenses + actions, transactions.
    months = months_between(start, end)
    first_month, last_month = months[0], months[-1]
    streaming_end = (day_of(59).year, day_of(59).month)
    rules = [
        ("salary", "Stipendio sintetico", "Income", "income", 240000, 27, last_month),
        ("rent", "Affitto sintetico", "Expense", "home", 70000, 3, last_month),
        ("gym", "Abbonamento palestra", "Expense", "subscriptions", 3990, 5, last_month),
        ("streaming", "Abbonamento streaming", "Expense", "subscriptions", 1299, 15, streaming_end),
        ("phone", "Bolletta telefono", "Expense", "home", 1499, 20, last_month),
    ]
    trip_month = (day_of(TRIP[0]).year, day_of(TRIP[0]).month)
    rule_keys, finance_events = {}, []
    for rid, name, kind, category, amount, dom, (end_year, end_month) in rules:
        rule_keys[rid] = add("finance", start, {"kind": "recurringRule", "category": category, "payload": {
            "name": name, "type": kind, "amount": money(amount), "dayOfMonth": dom,
            "startYear": first_month[0], "startMonth": first_month[1], "endYear": end_year, "endMonth": end_month,
            "note": f"Regola ricorrente sintetica: {name.lower()}"}}, "note")
    occurrences = []
    for rid, name, kind, category, amount, dom, (end_year, end_month) in rules:
        for year, month in months:
            when = scheduled(year, month, dom)
            if start <= when <= end and year * 12 + month <= end_year * 12 + end_month:
                occurrences.append((when, rid, name, kind, category, amount, year, month))
    phone_dates = [o[0] for o in occurrences if o[1] == "phone"]
    expected_occurrences = {}
    for index, (when, rid, name, kind, category, amount, year, month) in enumerate(sorted(occurrences)):
        status_key = f"{rule_keys[rid]}:{year:04d}-{month:02d}"
        if rid == "gym" and (year, month) == trip_month:
            add("finance", when, {"kind": "recurringAction", "rule": rule_keys[rid], "year": year, "month": month,
                                  "action": "skip"})
            expected_occurrences[status_key] = "Skipped"
            continue
        if rid == "phone" and when == phone_dates[-1]:
            expected_occurrences[status_key] = "Due"  # deliberately left unconfirmed
            continue
        actual = amount + (150 if rid == "phone" and index % 2 else 0)
        add("finance", when, {"kind": "recurringAction", "rule": rule_keys[rid], "year": year, "month": month,
                              "action": "confirm", "payload": {
                                  "amount": money(actual), "occurredAtUtc": utc_instant(when, 9),
                                  "note": f"{name} di {MONTHS_IT[month - 1]}"}}, "note")
        expected_occurrences[status_key] = "Confirmed"
        finance_events.append((when, kind, category, actual))

    planned = [
        (19, "Regalo di compleanno per un'amica", "other", 6000, ("confirm", 5450)),
        (CAR_SERVICE, "Tagliando auto", "other", 25000, ("confirm", 31000)),
        (55, "Corso online di fotografia", "other", 8900, ("cancel", None)),
        (TRIP[0], "Treno per Trieste", "travel", 8900, ("confirm", 9240)),
        (86, "Visita dal dentista", "other", 12000, None),
        (88, "Rinnovo assicurazione bici", "other", 4500, None),
    ]
    expected_planned, planned_keys = {}, {}
    for d, name, category, amount, _ in planned:
        planned_keys[d] = add("finance", day_of(d), {"kind": "plannedExpense", "category": category, "payload": {
            "name": name, "expectedAmount": money(amount), "scheduledDate": str(day_of(d)),
            "note": f"Spesa pianificata sintetica: {name.lower()}"}}, "note")
    for d, name, category, amount, action in planned:
        if action is None:
            expected_planned[planned_keys[d]] = "Due"  # deliberately left unconfirmed
            continue
        verb, actual = action
        record = {"kind": "plannedAction", "planned": planned_keys[d], "action": verb}
        if verb == "confirm":
            note = "Tagliando: sostituite anche le pastiglie dei freni" if d == CAR_SERVICE else name
            record["payload"] = {"amount": money(actual), "occurredAtUtc": utc_instant(day_of(d), 11), "note": note}
            finance_events.append((day_of(d), "Expense", category, actual))
        add("finance", day_of(d), record, "note" if verb == "confirm" else None)
        expected_planned[planned_keys[d]] = "Confirmed" if verb == "confirm" else "Cancelled"

    def transaction(d, kind, category, low, high, hour, note, fixed=None):
        amount = fixed if fixed is not None else rng.randint(low, high)
        add("finance", day_of(d), {"kind": "transaction", "category": category, "payload": {
            "type": kind, "amount": money(amount), "occurredAtUtc": utc_instant(day_of(d), hour),
            "note": note}}, "note")
        finance_events.append((day_of(d), kind, category, amount))

    for d in range(DAYS):
        weekday = d % 7
        if d in TRIP:
            transaction(d, "Expense", "travel", 3200, 6400, 20, "Cena al ristorante a Trieste")
        elif d >= MEAL_PREP and weekday == 6:
            transaction(d, "Expense", "groceries", 5500, 8000, 10, "Spesa per il meal prep della settimana")
        elif weekday in (0, 3):
            transaction(d, "Expense", "groceries", 2500, 7500, 18, "Spesa alimentare con lista")
        elif d in STRESS:
            transaction(d, "Expense", "dining", 1800, 3200, 21, "Cena da asporto durante la scadenza Aurora")
        elif weekday in (5, 6):
            transaction(d, "Expense", rng.choice(("dining", "other")), 1500, 4000, 19, "Uscita del fine settimana")
        else:
            transaction(d, "Expense", "transport", 200, 1500, 8, "Trasporto urbano")
        if d % 3:
            continue
        if d == 45:
            transaction(d, "Income", "income", 0, 0, 12, "Rimborso spese di lavoro", fixed=6420)
        elif d == TRIP[1]:
            transaction(d, "Expense", "travel", 0, 0, 15, "Hotel a Trieste, sei notti", fixed=48600)
        elif d in TRIP:
            transaction(d, "Expense", "travel", 800, 3000, 16, "Museo e visite a Trieste")
        else:
            transaction(d, "Expense", "dining", 150, 600, 10, "Caffè e colazione al bar")

    budgets = {}
    for year, month in months:
        base = sum(a for when, kind, category, a in finance_events
                   if kind == "Expense" and category != "travel" and (when.year, when.month) == (year, month))
        budgets[(year, month)] = -(-base * 11 // 10 // 5000) * 5000  # +10% headroom, rounded up to 50 EUR
    # Budgets come first (dependency order) but are sized from the movements generated above.
    records["finance"][:0] = [{"key": f"qa001-{identity}-finance-budget-{year:04d}-{month:02d}",
                               "date": str(max(start, date(year, month, 1))), "kind": "budget",
                               "year": year, "month": month, "amount": money(budgets[(year, month)])}
                              for year, month in months]

    # ---- Gym: offline-only historical workouts across program phases.
    sessions, last_strength = workout_days(), {}
    exercises = (("Squat", 40), ("Panca piana", 30), ("Rematore con bilanciere", 25))
    for n, d in enumerate(sessions):
        name = "Full body A" if n % 2 == 0 else "Full body B"
        current = phase(d)
        sets = []
        for exercise, base in exercises:
            if current == "Adattamento":
                weight = base
            elif current == "Forza":
                weight = base + 2.5 * (1 + len([s for s in sessions[:n + 1] if PHASES[1][0] <= s < PHASES[2][0]]) // 4)
                last_strength[exercise] = weight
            else:
                weight = round(last_strength.get(exercise, base) * 0.9 / 2.5) * 2.5
                if exercise == "Squat" and d > KNEE:
                    weight = round(weight * 0.8 / 2.5) * 2.5
            reps = 12 if current == "Adattamento" else 6 if current == "Forza" else 10
            sets += [{"exercise": exercise, "setNumber": s, "reps": reps, "weightKg": weight} for s in (1, 2, 3)]
        add("gym", day_of(d), {"phase": current, "importStatus": GYM_IMPORT_STATUS, "payload": {
            "programName": "Programma sintetico in tre fasi", "workoutName": name,
            "startedAtUtc": utc_instant(day_of(d), 18), "completedAtUtc": utc_instant(day_of(d), 19), "sets": sets}})
    workout_set = set(sessions)

    # ---- Nutrition: one meal per diary day; some analyzed (UserAdjusted values), some deliberately not.
    foods = {
        "normal": [("Riso, pollo e verdure", (620, 42, 70, 14)), ("Pasta con ceci e pomodori", (680, 26, 105, 15)),
                   ("Insalata di lenticchie e pane", (540, 28, 72, 13)), ("Pesce, patate e zucchine", (590, 38, 55, 20))],
        "workout": [("Petto di pollo, riso basmati e spinaci; porzione post allenamento", (720, 55, 85, 14)),
                    ("Salmone, quinoa e broccoli; porzione post allenamento", (760, 48, 70, 28))],
        "stress": [("Pizza margherita da asporto", (900, 34, 120, 30)), ("Sushi misto da asporto", (720, 32, 105, 16))],
        "trip": [("Cena al ristorante a Trieste", (950, 40, 95, 40))],
        "prep": [("Meal prep: farro, tacchino e verdure grigliate", (610, 45, 68, 14)),
                 ("Meal prep: orzo, fagioli e zucchine", (570, 27, 88, 11))],
    }
    meal_days = {}
    for d in range(DAYS):
        day = day_of(d)
        if d in TRIP:
            group, kind, hour, minute = "trip", "Dinner", 20, 30
        elif d in STRESS:
            group, kind, hour, minute = "stress", "Dinner", 21, 15
        elif d in workout_set:
            group, kind, hour, minute = "workout", "Dinner", 20, 0
        elif d >= MEAL_PREP and d % 7 < 5:
            group, kind, hour, minute = "prep", "Lunch", 13, 0
        elif d % 7 < 5:
            group, kind, hour, minute = "normal", "Lunch", 13, 0
        else:
            group, kind, hour, minute = "normal", "Dinner", 20, 30
        description, base = foods[group][d % len(foods[group])]
        analyzed = group != "trip" and rng.random() < (0.4 if group == "stress" else 0.7)
        nutrition = None
        if analyzed:
            nutrition = {field: round(value * rng.uniform(0.92, 1.08), 1)
                         for field, value in zip(("caloriesKcal", "proteinGrams", "carbsGrams", "fatGrams"), base)}
        meal_days[d] = add("nutrition", day, {"group": group, "nutrition": nutrition, "payload": {
            "description": description, "mealType": kind, "date": str(day), "time": f"{hour:02d}:{minute:02d}:00",
            "utcOffsetMinutes": UTC_OFFSET_MINUTES}}, "description")

    # ---- Journal: 40-60 entries across topics, with known-answer facts on specific days.
    trip_month_name = MONTHS_IT[day_of(TRIP[0]).month - 1]
    facts = {
        0: (["obiettivi", "allenamento", "finanze"], "Inizio un periodo di novanta giorni con obiettivi chiari: "
            "voglio allenarmi due volte a settimana e restare sotto il budget mensile delle spese."),
        1: (["finanze", "alimentazione"], "Ho deciso di ridurre gli acquisti impulsivi usando una lista prima "
            "della spesa."),
        4: (["sonno", "allenamento"], "Ho dormito male; oggi ho mantenuto i carichi leggeri."),
        15: (["lavoro", "alimentazione"], "Settimana pesante per la scadenza del progetto Aurora: ceno tardi e ordino "
             "quasi sempre da asporto."),
        19: (["relazioni", "finanze"], "Ho comprato il regalo di compleanno per un'amica, spendendo un po' meno "
             "del previsto."),
        20: (["lavoro"], "Consegnato il progetto Aurora. Da domani torno a cucinare a casa."),
        CAR_SERVICE: (["finanze", "auto"], "Il tagliando dell'auto è costato 310 euro invece dei 250 previsti, "
                      "perché hanno sostituito anche le pastiglie dei freni."),
        41: (["finanze"], "Dopo il tagliando ho rivisto le spese della settimana per rientrare nel budget."),
        TRIP[0]: (["viaggio"], "Partenza in treno per Trieste: resterò sette giorni e mi prendo una pausa "
                  "dagli allenamenti."),
        63: (["viaggio", "alimentazione"], "A Trieste mangio sempre al ristorante e non registro i valori nutrizionali."),
        66: (["viaggio"], "Giornata in barca lungo la costa, molto rilassante."),
        67: (["viaggio", "finanze"], "Mi accorgo che le spese del viaggio stanno crescendo più del previsto."),
        TRIP[-1]: (["viaggio"], "Ultimo giorno a Trieste, domani si torna a casa."),
        70: (["finanze", "viaggio"], f"Facendo i conti ho superato il budget di {trip_month_name} a causa delle "
             "spese del viaggio a Trieste, soprattutto l'hotel."),
        72: (["salute", "allenamento"], "Ho preso un raffreddore: dopo il viaggio la pausa dagli allenamenti "
             "durerà due settimane in tutto."),
        76: (["allenamento"], "Da oggi la fase di ripresa: torno ad allenarmi con carichi ridotti."),
        KNEE: (["salute", "allenamento"], "Sento un leggero fastidio al ginocchio: nei prossimi allenamenti "
               "alleggerisco lo squat."),
        MEAL_PREP: (["alimentazione", "routine"], "Oggi ho iniziato a preparare i pasti della settimana la domenica."),
        86: (["salute", "finanze"], "Ho in programma la visita dal dentista, ancora da pagare."),
    }
    filler = [
        (["lavoro"], "Il progetto immaginario Aurora procede con piccoli passi."),
        (["routine"], "Una passeggiata serale mi aiuta a rilassarmi."),
        (["lettura"], "Ho letto qualche capitolo di un romanzo prima di dormire."),
        (["relazioni"], "Telefonata lunga con la famiglia, mi ha fatto piacere."),
        (["sonno"], "Ho dormito bene e oggi avevo più energia."),
        (["finanze"], "Controllato il conto: nessuna spesa imprevista."),
    ]
    entry_days = sorted(set(range(0, DAYS, 2)) | set(facts))
    entry_keys = {}
    for d in entry_days:
        day = day_of(d)
        topics, sentences = [], []
        if d in facts:
            topics += facts[d][0]
            sentences.append(facts[d][1])
        if d in workout_set and d not in facts:
            topics.append("allenamento")
            sentences.append(f"Allenamento completato alle 18, fase di {phase(d).lower()}.")
        if d not in facts or len(sentences) < 2:
            extra_topics, extra = filler[rng.randrange(len(filler))]
            topics += extra_topics
            sentences.append(extra)
        entry_keys[d] = add("journal", day, {"topics": sorted(set(topics)), "payload": {
            "occurredAtUtc": utc_instant(day, 22), "title": "Diario: " + label(day),
            "content": " ".join(sentences)}}, "content")

    gym_after_knee = [r["key"] for r in records["gym"] if date.fromisoformat(r["date"]) > day_of(KNEE)]
    questions = [
        {"id": "goal", "kind": "fact", "question": "Quale obiettivo di allenamento mi ero dato all'inizio del periodo?",
         "expectedAnswer": "Allenarmi due volte a settimana.", "evidenceKeys": [entry_keys[0]],
         "evidenceSnippets": ["allenarmi due volte a settimana"]},
        {"id": "shopping-list", "kind": "fact", "question": "Come ho deciso di ridurre gli acquisti impulsivi?",
         "expectedAnswer": "Usando una lista prima della spesa.", "evidenceKeys": [entry_keys[1]],
         "evidenceSnippets": ["lista prima della spesa"]},
        {"id": "takeaway", "kind": "cross-module",
         "question": f"Perché nella settimana di {label(day_of(STRESS[0]))} ordinavo spesso cena da asporto?",
         "expectedAnswer": "Per la scadenza del progetto Aurora.", "evidenceKeys": [entry_keys[15]],
         "evidenceSnippets": ["scadenza del progetto Aurora"]},
        {"id": "car-service", "kind": "cross-module", "question": "Quanto è costato il tagliando dell'auto e perché più del previsto?",
         "expectedAnswer": "310 euro invece di 250, perché sono state sostituite anche le pastiglie dei freni.",
         "evidenceKeys": [entry_keys[CAR_SERVICE]], "evidenceSnippets": ["310 euro", "pastiglie dei freni"]},
        {"id": "trip-destination", "kind": "temporal", "question": f"Dove ho fatto il viaggio di {trip_month_name}?",
         "expectedAnswer": "A Trieste.", "evidenceKeys": [entry_keys[TRIP[0]]], "evidenceSnippets": ["Trieste"]},
        {"id": "trip-length", "kind": "temporal", "question": "Quanti giorni è durato il viaggio a Trieste?",
         "expectedAnswer": f"Sette giorni, da {label(day_of(TRIP[0]))} a {label(day_of(TRIP[-1]))}.",
         "evidenceKeys": [entry_keys[TRIP[0]], entry_keys[TRIP[-1]]], "evidenceSnippets": ["sette giorni", "Ultimo giorno a Trieste"]},
        {"id": "budget-overrun", "kind": "cross-module", "question": "In quale mese ho superato il budget e perché?",
         "expectedAnswer": f"A {trip_month_name}, per le spese del viaggio a Trieste.", "evidenceKeys": [entry_keys[70]],
         "evidenceSnippets": [f"superato il budget di {trip_month_name}"]},
        {"id": "training-break", "kind": "temporal", "question": "Perché ho smesso di allenarmi per due settimane?",
         "expectedAnswer": "Prima per il viaggio a Trieste, poi per un raffreddore.",
         "evidenceKeys": [entry_keys[TRIP[0]], entry_keys[72]], "evidenceSnippets": ["pausa", "raffreddore"]},
        {"id": "knee", "kind": "cross-module", "question": "Quale esercizio ho alleggerito dopo il fastidio al ginocchio?",
         "expectedAnswer": "Lo squat.", "evidenceKeys": [entry_keys[KNEE]], "evidenceSnippets": ["alleggerisco lo squat"]},
        {"id": "meal-prep", "kind": "temporal", "question": "Da quando preparo i pasti della settimana la domenica?",
         "expectedAnswer": f"Da {label(day_of(MEAL_PREP))}.", "evidenceKeys": [entry_keys[MEAL_PREP]],
         "evidenceSnippets": ["preparare i pasti della settimana la domenica"]},
        {"id": "doctor-name", "kind": "abstain", "question": "Qual è il nome del mio medico di base?",
         "expectedAnswer": "Il diario non contiene questa informazione.", "evidenceKeys": [], "evidenceSnippets": [],
         "mustAbstain": True},
        {"id": "body-weight", "kind": "abstain", "question": "Quanto pesavo a metà del periodo?",
         "expectedAnswer": "Il diario non contiene questa informazione.", "evidenceKeys": [], "evidenceSnippets": [],
         "mustAbstain": True},
    ]
    in_days = lambda module, days: [r["key"] for r in records[module] if (date.fromisoformat(r["date"]) - start).days in days]
    scenarios = [
        {"id": "deadline-takeaway", "description": "Project deadline: takeaway dinners, dining spend, fewer analyzed meals.",
         "evidence": {"journal": [entry_keys[15], entry_keys[20]], "nutrition": in_days("nutrition", STRESS),
                      "finance": [r["key"] for r in records["finance"] if r.get("category") == "dining"
                                  and (date.fromisoformat(r["date"]) - start).days in STRESS]}},
        {"id": "car-service-overrun", "description": "Planned car service confirmed above its expected amount.",
         "evidence": {"journal": [entry_keys[CAR_SERVICE], entry_keys[41]],
                      "finance": [planned_keys[CAR_SERVICE]] + [r["key"] for r in records["finance"]
                                  if r.get("planned") == planned_keys[CAR_SERVICE]]}},
        {"id": "trip-budget-overrun", "description": "Trip month exceeds its budget; travel meals are not analyzed.",
         "evidence": {"journal": [entry_keys[d] for d in (TRIP[0], 67, TRIP[-1], 70)], "nutrition": in_days("nutrition", TRIP),
                      "finance": [r["key"] for r in records["finance"] if r.get("category") == "travel"
                                  or (r["kind"] == "budget" and (r["year"], r["month"]) == trip_month)]}},
        {"id": "training-break", "description": "No workouts during the trip and the following cold; gym fee skipped.",
         "evidence": {"journal": [entry_keys[TRIP[0]], entry_keys[72], entry_keys[76]],
                      "finance": [r["key"] for r in records["finance"] if r["kind"] == "recurringAction" and r["action"] == "skip"],
                      "gym": [r["key"] for r in records["gym"] if (date.fromisoformat(r["date"]) - start).days in (60, 78)]}},
        {"id": "knee-load-reduction", "description": "Knee discomfort: lighter squat in the following workouts.",
         "evidence": {"journal": [entry_keys[KNEE]], "gym": gym_after_knee}},
        {"id": "meal-prep", "description": "Sunday meal prep: dedicated groceries and meal-prep lunches.",
         "evidence": {"journal": [entry_keys[MEAL_PREP]], "nutrition": [meal_days[d] for d in range(MEAL_PREP, DAYS)
                                                                         if records["nutrition"][d]["group"] == "prep"],
                      "finance": [r["key"] for r in records["finance"] if r.get("payload", {}).get("note", "").startswith("Spesa per il meal prep")]}},
    ]
    body = {"schemaVersion": SCHEMA_VERSION, "synthetic": True, "generator": "QA-001", "seed": seed,
            "referenceDate": str(reference), "startDate": str(start), "endDate": str(end), "days": DAYS,
            "currency": CURRENCY, "utcOffsetMinutes": UTC_OFFSET_MINUTES, "categories": CATEGORIES,
            "counts": {module: len(rows) for module, rows in records.items()},
            "records": records, "scenarios": scenarios, "journalQuestions": questions,
            "gymPolicy": {"importStatus": GYM_IMPORT_STATUS, "reason": GYM_IMPORT_REASON},
            "expected": None}
    body["expected"] = expectations(body, expected_occurrences, expected_planned)
    body["sha256"] = digest(body)
    validate(body)
    return body


def finance_movements(manifest):
    """Every money movement the dataset creates: (local date, type, category key, cents)."""
    rows = manifest["records"]["finance"]
    by_key = {r["key"]: r for r in rows}
    for row in rows:
        if row["kind"] == "transaction":
            yield date.fromisoformat(row["date"]), row["payload"]["type"], row["category"], cents(row["payload"]["amount"])
        elif row["kind"] in ("recurringAction", "plannedAction") and row["action"] == "confirm":
            parent = by_key[row.get("rule") or row.get("planned")]
            kind = parent["payload"].get("type", "Expense")
            yield date.fromisoformat(row["date"]), kind, parent["category"], cents(row["payload"]["amount"])


def expectations(manifest, occurrences=None, planned=None):
    rows = manifest["records"]["finance"]
    if occurrences is None:
        occurrences, planned = manifest["expected"]["finance"]["occurrences"], manifest["expected"]["finance"]["plannedExpenses"]
    months = {}
    for row in rows:
        if row["kind"] == "budget":
            months[f"{row['year']:04d}-{row['month']:02d}"] = {"budget": row["amount"], "expenses": 0, "income": 0}
    for when, kind, _, amount in finance_movements(manifest):
        bucket = months[month_key(when)]
        bucket["expenses" if kind == "Expense" else "income"] += amount
    for bucket in months.values():
        bucket["expenses"], bucket["income"] = money(bucket["expenses"]), money(bucket["income"])
        bucket["overBudget"] = bucket["expenses"] > bucket["budget"]
    meals = manifest["records"]["nutrition"]
    return {
        "finance": {"kinds": {k: sum(r["kind"] == k for r in rows) for k in FINANCE_KINDS}, "months": months,
                    "occurrences": dict(sorted(occurrences.items())), "plannedExpenses": dict(sorted(planned.items()))},
        "nutrition": {"analyzed": sum(m["nutrition"] is not None for m in meals),
                      "unanalyzed": sum(m["nutrition"] is None for m in meals)},
        "journal": {"entries": len(manifest["records"]["journal"]), "questions": len(manifest["journalQuestions"])},
        "gym": {"workouts": len(manifest["records"]["gym"]), "importable": 0},
    }


def _fail(message):
    raise ValueError(message)


def _scan_forbidden(value, path="manifest"):
    if isinstance(value, dict):
        for key, item in value.items():
            if key.lower() in FORBIDDEN_FIELDS:
                _fail(f"Ownership field '{key}' must never be sent: the API derives the owner from the token ({path})")
            _scan_forbidden(item, f"{path}.{key}")
    elif isinstance(value, list):
        for item in value:
            _scan_forbidden(item, path)


def _amount(value, field):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not 0 < value <= 100000 or round(value, 2) != value:
        _fail(f"Invalid amount in {field}")


def validate(manifest):
    """Structural, chronological, contract and cross-module consistency checks. Raises ValueError."""
    body = {k: v for k, v in manifest.items() if k != "sha256"}
    if digest(body) != manifest.get("sha256"):
        _fail("Manifest checksum mismatch")
    if manifest.get("schemaVersion") != SCHEMA_VERSION or manifest.get("synthetic") is not True:
        _fail("Unsupported or non-synthetic manifest")
    _scan_forbidden(manifest["records"])
    start, end = date.fromisoformat(manifest["startDate"]), date.fromisoformat(manifest["endDate"])
    reference = date.fromisoformat(manifest["referenceDate"])
    if (end - start).days != DAYS - 1 or manifest["days"] != DAYS or reference != end + timedelta(days=1):
        _fail("Expected a 90-day dataset ending the day before the reference date")
    if manifest["currency"] != CURRENCY or manifest["utcOffsetMinutes"] != UTC_OFFSET_MINUTES or manifest["categories"] != CATEGORIES:
        _fail("Unexpected currency, offset or category map")
    records = manifest["records"]
    if set(records) != set(TARGETS) or manifest["counts"] != {m: len(r) for m, r in records.items()}:
        _fail("Declared counts do not match the records")
    for module, (low, high) in TARGETS.items():
        if not low <= len(records[module]) <= high:
            _fail(f"{module} count outside the QA-001 target range")
    keys = {}
    for module, rows in records.items():
        previous = None
        for row in rows:
            key = row["key"]
            if key in keys or not key.startswith("qa001-"):
                _fail("Invalid or duplicate record key")
            keys[key] = (module, row)
            day = date.fromisoformat(row["date"])
            if not start <= day <= end:
                _fail(f"Date outside dataset: {key}")
            if module != "finance":
                if previous and day < previous:
                    _fail(f"{module} records are not chronological")
                previous = day
    _validate_finance(manifest, keys, start, end)
    _validate_nutrition(records["nutrition"], start)
    _validate_journal(manifest, keys)
    _validate_gym(records["gym"])
    if manifest["expected"] != expectations(manifest):
        _fail("Expected assertions do not match the records")
    _validate_narrative(manifest, start)


def _marker(row, field, maximum):
    text = row["payload"].get(field)
    if not isinstance(text, str) or not text.strip() or len(text) > maximum or f"[{row['key']}]" not in text:
        _fail(f"Missing marker or invalid text: {row['key']}")


def _validate_finance(manifest, keys, start, end):
    rows = manifest["records"]["finance"]
    budget_months = []
    seen_actions = set()
    for row in rows:
        kind, p = row["kind"], row.get("payload", {})
        if kind not in FINANCE_KINDS:
            _fail("Unknown finance kind")
        if "category" in row and row["category"] not in CATEGORIES:
            _fail("Unknown category key")
        if kind == "budget":
            _amount(row["amount"], row["key"])
            budget_months.append((row["year"], row["month"]))
        elif kind == "recurringRule":
            if p["type"] not in ("Income", "Expense") or CATEGORIES[row["category"]] != p["type"] or not 1 <= p["dayOfMonth"] <= 31:
                _fail("Invalid recurring rule")
            if (p["endYear"], p["endMonth"]) < (p["startYear"], p["startMonth"]) or len(p["name"]) > 200:
                _fail("Invalid recurring rule range")
            _amount(p["amount"], row["key"])
            _marker(row, "note", 1000)
        elif kind == "plannedExpense":
            if CATEGORIES[row["category"]] != "Expense" or p["scheduledDate"] != row["date"]:
                _fail("Invalid planned expense")
            _amount(p["expectedAmount"], row["key"])
            _marker(row, "note", 1000)
        elif kind == "transaction":
            if p["type"] not in ("Income", "Expense") or CATEGORIES[row["category"]] != p["type"]:
                _fail("Transaction type and category type differ")
            _amount(p["amount"], row["key"])
            _marker(row, "note", 1000)
            if local_date(p["occurredAtUtc"]).isoformat() != row["date"] or not p["occurredAtUtc"].endswith("Z"):
                _fail("Invalid UTC occurrence")
        else:
            parent_field = "rule" if kind == "recurringAction" else "planned"
            parent = keys.get(row.get(parent_field), (None, {}))[1]
            expected_kind = "recurringRule" if kind == "recurringAction" else "plannedExpense"
            if parent.get("kind") != expected_kind or rows.index(parent) > rows.index(row):
                _fail("Action references a missing or later parent")
            identity = (row[parent_field], row.get("year"), row.get("month"))
            if identity in seen_actions:
                _fail("Duplicate action for the same occurrence")
            seen_actions.add(identity)
            allowed = ("confirm", "skip") if kind == "recurringAction" else ("confirm", "cancel")
            if row["action"] not in allowed:
                _fail("Unsupported action")
            if kind == "recurringAction":
                rp = parent["payload"]
                month = row["year"] * 12 + row["month"]
                if not rp["startYear"] * 12 + rp["startMonth"] <= month <= rp["endYear"] * 12 + rp["endMonth"]:
                    _fail("Occurrence outside the rule range")
                if row["date"] != str(scheduled(row["year"], row["month"], rp["dayOfMonth"])):
                    _fail("Occurrence date differs from the rule schedule")
            elif row["date"] != parent["payload"]["scheduledDate"]:
                _fail("Planned action date differs from its schedule")
            if row["action"] == "confirm":
                _amount(p["amount"], row["key"])
                _marker(row, "note", 1000)
                if local_date(p["occurredAtUtc"]).isoformat() != row["date"]:
                    _fail("Confirmation must occur on its scheduled date")
            elif "payload" in row:
                _fail("Only confirmations carry a payload")
    if budget_months != months_between(start, end):
        _fail("Expected exactly one budget per calendar month of the window, first")


def _validate_nutrition(rows, start):
    days = set()
    for row in rows:
        p, n = row["payload"], row["nutrition"]
        if p["date"] != row["date"] or p["mealType"] not in MEAL_TYPES or p["utcOffsetMinutes"] != UTC_OFFSET_MINUTES:
            _fail("Invalid meal contract")
        if time.fromisoformat(p["time"]).second or time.fromisoformat(p["time"]).microsecond:
            _fail("Meal time must be a whole minute")
        _marker(row, "description", 2000)
        if n is not None:
            if set(n) != {"caloriesKcal", "proteinGrams", "carbsGrams", "fatGrams"}:
                _fail("Nutrition values must be complete")
            for field, value in n.items():
                if not 0 <= value <= (10000 if field == "caloriesKcal" else 1000) or round(value, 1) != value:
                    _fail("Invalid nutrition value")
        days.add(row["date"])
    if len(days) != len(rows):
        _fail("Expected one meal per diary day")
    if not any(r["nutrition"] for r in rows) or all(r["nutrition"] for r in rows):
        _fail("Nutrition must include both analyzed and unanalyzed meals")


def _validate_journal(manifest, keys):
    for row in manifest["records"]["journal"]:
        p = row["payload"]
        _marker(row, "content", 20000)
        if not p["title"] or len(p["title"]) > 200 or local_date(p["occurredAtUtc"]).isoformat() != row["date"] or not row["topics"]:
            _fail("Invalid journal entry")
    topics = {t for r in manifest["records"]["journal"] for t in r["topics"]}
    if len(topics) < 6:
        _fail("Journal must span multiple topics")
    questions = manifest["journalQuestions"]
    if len({q["id"] for q in questions}) != len(questions) or not any(q.get("mustAbstain") for q in questions):
        _fail("Journal questions need unique ids and at least one abstention case")
    for q in questions:
        if not q["question"] or not q["expectedAnswer"]:
            _fail("Invalid Journal question")
        if q.get("mustAbstain"):
            if q["evidenceKeys"]:
                _fail("Abstention questions have no evidence")
            continue
        evidence = [keys.get(k, ("", {})) for k in q["evidenceKeys"]]
        if not evidence or any(module != "journal" for module, _ in evidence):
            _fail(f"Question {q['id']} must cite Journal evidence")
        text = " ".join(row["payload"]["content"] for _, row in evidence)
        if not q["evidenceSnippets"] or any(s not in text for s in q["evidenceSnippets"]):
            _fail(f"Question {q['id']} answer is not verifiable from its evidence")
    for scenario in manifest["scenarios"]:
        for module, scenario_keys in scenario["evidence"].items():
            if not scenario_keys or any(keys.get(k, ("",))[0] != module for k in scenario_keys):
                _fail(f"Scenario {scenario['id']} references missing {module} records")


def _validate_gym(rows):
    order = [name for _, name in PHASES]
    previous = 0
    for row in rows:
        p = row["payload"]
        if row.get("importStatus") != GYM_IMPORT_STATUS:
            _fail("Gym workouts must be marked not importable")
        a, b = parse_instant(p["startedAtUtc"]), parse_instant(p["completedAtUtc"])
        if a.utcoffset() != timedelta(0) or not timedelta(0) < b - a <= timedelta(hours=3) or local_date(p["startedAtUtc"]).isoformat() != row["date"]:
            _fail("Invalid offline workout timing")
        if order.index(row["phase"]) < previous:
            _fail("Gym phases are not chronological")
        previous = order.index(row["phase"])
        if not p["sets"] or any(s["reps"] <= 0 or s["weightKg"] <= 0 for s in p["sets"]):
            _fail("Invalid workout sets")


def _validate_narrative(manifest, start):
    records = manifest["records"]
    offset = lambda row: (date.fromisoformat(row["date"]) - start).days
    if any(offset(r) in BREAK for r in records["gym"]):
        _fail("No workout may fall inside the training break")
    months = manifest["expected"]["finance"]["months"]
    trip_month = month_key(start + timedelta(days=TRIP[0]))
    if not months[trip_month]["overBudget"] or any(v["overBudget"] for k, v in months.items() if k != trip_month):
        _fail("Only the trip month may exceed its budget")
    knee = [r for r in records["gym"] if offset(r) > KNEE]
    before = [r for r in records["gym"] if PHASES[2][0] <= offset(r) <= KNEE]
    squat = lambda row: max(s["weightKg"] for s in row["payload"]["sets"] if s["exercise"] == "Squat")
    if knee and before and not all(squat(r) < squat(before[-1]) for r in knee):
        _fail("Squat load must drop after the knee discomfort")
    if any(r["nutrition"] for r in records["nutrition"] if offset(r) in TRIP):
        _fail("Trip meals stay unanalyzed")
