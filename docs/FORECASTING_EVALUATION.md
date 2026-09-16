# Forecasting evaluation

Stockpile reports the configured forecasting implementation in every forecast
result. The default is `managed-moving-average`: it repeats the mean daily sell
quantity across the requested horizon. ML.NET SSA is an explicit `ssa` opt-in and
must not be described as the default or as validated business-impact evidence.
See [Forecasting runtime](FORECASTING_RUNTIME.md) for deployment prerequisites.

## Reproducible chronological fixture

The test
`ForecastingEvaluation_UsesChronologicalHoldoutAndReportsBaseline` in
`InventoryManagementSystem.Tests/Core/Services/DemandForecastServiceTests.cs`
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
dotnet test InventoryManagementSystem.Tests/InventoryManagementSystem.Tests.csproj \
  --configuration Release --filter "FullyQualifiedName~ForecastingEvaluation_UsesChronologicalHoldoutAndReportsBaseline"
```

No real-shop adoption, waste reduction, stockout reduction, or production
forecast accuracy is inferred from this synthetic fixture. A future evaluation
must record the data source and date, item/day window, missing-day policy,
forecast horizon, metric, sample count, failed cases, and limitations. If the
SSA native runtime is unavailable on a target platform, the result remains
unavailable rather than being represented as a successful SSA evaluation.
