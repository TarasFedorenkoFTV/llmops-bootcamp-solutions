# Incident runbook (W6)

Демонстрація «інцидент → fallback → відновлення» на mock. Усе вже підтримано кодом:
fallback + graceful degradation (W4) і лічильники в /observability (W5).

## Сценарій outage

1. Норма: у чат «Як скинути пароль?» → нормальна відповідь.
2. Інцидент: у чат `__fail_503` → усі спроби провайдера падають → **graceful degradation**
   («тимчасові проблеми, спробуйте згодом»), а `/observability` показує зростання `fallback_events`.
3. Відновлення: знову «Як скинути пароль?» → нормальна відповідь. Інцидент минув.

```bash
# усе всередині docker-мережі (див. GETTING_STARTED §6)
curl -s -X POST .../chat -d '{"message":"__fail_503"}'      # graceful degradation
curl -s .../observability                                    # fallback_events > 0
curl -s -X POST .../chat -d '{"message":"Як скинути пароль?"}'  # recovery
```

## CI (eval gate)

`.github/workflows/eval-gate.yml` на кожен PR: піднімає стек, ганяє evals, блокує merge
на регресії. Зламаний промпт або збій поведінки → гейт червоний.

## Що зараховуємо на W6

Демо інциденту відтворюється (fallback + degradation видно в observability),
CI-гейт блокує регресію. Це фінальний тиждень — решта capstone уже зібрана.
