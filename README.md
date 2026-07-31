# LLMOps bootcamp — SOLUTIONS

Еталонні рішення capstone. По одному тижню на бранч, **кумулятивно** (кожен бранч =
попередній + новий тиждень). На `main` — цей лендинг.

Стартер для студентів: `TarasFedorenkoFTV/llmops-bootcamp`.

| Бранч | Тиждень | Що додано |
|---|---|---|
| [`w1`](../../tree/w1) | W1 | prompt registry: активний промпт з БД, версія в лозі, `/prompts`, promote/rollback |
| [`w2`](../../tree/w2) | W2 | routing (intent → модель) + cost attribution + `/cost` |
| [`w3`](../../tree/w3) | W3 | in-memory cache + виконання інструментів |
| [`w4`](../../tree/w4) | W4 | fallback + graceful degradation, HITL-approval, PII-guardrail |
| [`w5`](../../tree/w5) | W5 | observability-агрегати (`/observability`, `/providers`) |
| [`w6`](../../tree/w6) | W6 | incident runbook + CI eval gate (фінал) |

Кожен бранч запускається `docker compose up --build` і проходить evals на mock (без ключа).
Перевірено наживо: на `w6` — evals **6/6**, а кеш, HITL, fallback, cost і observability
наповнюють консоль реальними даними.

Деталі: [SOLUTION_W1.md](SOLUTION_W1.md), [INCIDENT_RUNBOOK.md](INCIDENT_RUNBOOK.md) (на `w6`).
