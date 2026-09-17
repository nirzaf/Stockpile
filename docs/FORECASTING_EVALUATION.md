# Forecasting evaluation

Merconiq reports the configured forecasting implementation in every forecast
result. The default is `managed-moving-average`: it repeats the mean daily sell
quantity across the requested horizon. ML.NET SSA is an explicit `ssa` opt-in and
must not be described as the default or as validated business-impact evidence.
See [Forecasting runtime](FORECASTING_RUNTIME.md) for deployment prerequisites.

Forecasting is enabled by default and can be disabled with
`Forecasting:Enabled=false` (or `Forecasting__Enabled=false` in the environment).
While disabled, forecast API calls report service unavailability and the
background scheduler performs no tenant forecast work; it does not return a
successful empty forecast or fall back to a different model. Forecast inputs are
processed locally against the configured application database, with no cloud AI
provider required or called.

## Reproducible chronological fixture

The test
`ForecastingEvaluation_UsesChronologicalHoldoutAndReportsBaseline` in
`tests/Merconiq.Tests/Core/Services/DemandForecastServiceTests.cs`
uses eight daily observations:

| Split | Values | Count |
|---|---|---:|
| Training | 4, 6, 8, 10, 12 | 5 |
| Holdout | 14, 16, 18 | 3 |

The managed moving-average prediction is the training mean, `8`, and the
last-value naive baseline is `12`. Mean absolute error on the future-only
holdout is `8` for the managed method and `4` for the naive baseline. The fixture
therefore demonstrates method behavior and does not claim that the managed
method is superior. It also records the input window, metric, sample sizes, and
the fact that no holdout values are used to produce the prediction.

Run the focused evaluation with the pinned SDK:

```bash
dotnet test tests/Merconiq.Tests/Merconiq.Tests.csproj \
  --configuration Release --filter "FullyQualifiedName~ForecastingEvaluation_UsesChronologicalHoldoutAndReportsBaseline"
```

No real-shop adoption, waste reduction, stockout reduction, or production
forecast accuracy is inferred from this synthetic fixture. A future evaluation
must record the data source and date, item/day window, missing-day policy,
forecast horizon, metric, sample count, failed cases, and limitations. If the
SSA native runtime is unavailable on a target platform, the result remains
unavailable rather than being represented as a successful SSA evaluation.

## Bounded window and data-quality fixture

`ForecastDemandAsync_UsesBoundedUtcHistoryAndReportsLimitMetadata` freezes the
as-of time at 2026-01-10 UTC and configures a five-day history window (2026-01-06
through 2026-01-10) and a seven-day maximum horizon. A sale of 1,000 units on
2026-01-05 and a future sale of 2,000 units on 2026-01-11 are excluded. Inside
the window, the synthetic sales are 4 units on Jan 6, and 8 units on each of Jan
9 and Jan 10. A receipt and an internal transfer on Jan 8 are not demand. The
chronological daily series is `[4, 0, 0, 8, 8]`, so the independently calculated
managed mean is 4 units/day and a three-day forecast is `[4, 4, 4]`. The response
reports the configured limits, observed date window, managed implementation
version, and known limitations. The associated preparation test proves missing
calendar days are zero-filled and receipts/transfers do not inflate sales.

This fixture cannot evaluate return adjustments or stockout correction: the
current movement model has no distinct return type and records fulfilled sales,
not unmet demand or stock availability. A zero in the fixture is a missing
recorded sale, not evidence that demand was satisfied. Those cases remain explicit
known limitations rather than fabricated evaluation results. The same default
managed implementation runs without native SSA dependencies. SSA remains an
explicit opt-in; its native-runtime error path rethrows and never silently
substitutes the managed model.
