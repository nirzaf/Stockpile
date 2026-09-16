# Forecasting runtime

The production Docker image targets both `linux/amd64` and `linux/arm64`. Its
default forecasting implementation is `managed-moving-average`, a deterministic
and platform-independent model that does not load ML.NET native libraries. The
implementation is explicit in `Forecasting:Implementation`, is validated at
startup, and is returned as `ForecastingImplementation` in each forecast result.
Responses also include `ForecastingImplementationVersion`, the actual observed
`DataWindowStartDate`/`DataWindowEndDate`, configured limits, and `KnownLimitations`.
The managed algorithm reports version `1.0.0`; SSA reports the loaded
`Microsoft.ML.TimeSeries` assembly version.

Forecast requests are bounded by these settings:

| Setting | Default | Supported range | Behavior |
|---|---:|---:|---|
| `Forecasting:MaxForecastHorizonDays` | 90 days | 1–365 days | Requests outside the configured limit return an API validation error; they are not silently clamped. |
| `Forecasting:MaxHistoricalDays` | 365 days | 5–3,650 days | Only sell movements from the inclusive UTC calendar window ending today are queried and used. |
| `Forecasting:MaxHistoricalTransactionsPerForecast` | 50,000 rows | 1–250,000 rows | A database-side ordered query reads at most the configured limit plus one matching sell row. If the extra row exists, the request fails with HTTP 400; no partial forecast is returned. |
| `Forecasting:MaxItemsPerAllItemsForecast` | 250 items | 1–2,500 items | A database-side tenant-filtered ordered query reads at most the configured limit plus one item. If the extra item exists, the request fails with HTTP 400; no partial list is returned. |

The two row-count settings are hard work limits as well as configuration bounds:
startup rejects values outside their supported ranges, and an individual request
is rejected instead of silently truncating its input. All-item item selection keeps
the repository's tenant and soft-delete filters; per-item movement selection keeps
the historical date, sell-type, and optional company-scope predicates. Matching rows
are ordered deterministically before the database-side limit is applied. Limits apply
to one forecast request; company-scoped all-item calls count only items with sell
movements in the requested company set. No latency or aggregate tenant-wide workload
SLA is claimed.

The daily series fills missing dates between the first and latest recorded sale
with zero. It does not append dates after the latest sale. Receipts and transfers
are not demand. The current transaction model has no separate return movement,
and it does not record stockout/lost-sales observations; therefore returns cannot
be netted out and a zero-sales day cannot be interpreted as proof of zero unmet
demand. The managed moving average repeats the historical mean and does not model
trend or seasonality.

The historical-day limit bounds the calendar window and the horizon limit bounds
returned values. The new row and item limits separately cap input records and
all-item catalog size. All-item processing remains sequential within those limits.
No fixed latency or memory budget is claimed. No external AI provider is called
and forecasts do not create orders or journals.

ML.NET SSA is still available for deployments that install and verify its native
runtime dependencies. Select it explicitly with:

```text
Forecasting__Implementation=ssa
```

SSA failures are logged and returned as failures; the service does not silently
change models. This keeps the model name in the API response truthful and avoids
architecture-dependent behavior in the published image.
