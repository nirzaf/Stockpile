# Forecasting runtime

The production Docker image targets both `linux/amd64` and `linux/arm64`. Its
default forecasting implementation is `managed-moving-average`, a deterministic
and platform-independent model that does not load ML.NET native libraries. The
implementation is explicit in `Forecasting:Implementation`, is validated at
startup, and is returned as `ForecastingImplementation` in each forecast result.

ML.NET SSA is still available for deployments that install and verify its native
runtime dependencies. Select it explicitly with:

```text
Forecasting__Implementation=ssa
```

SSA failures are logged and returned as failures; the service does not silently
change models. This keeps the model name in the API response truthful and avoids
architecture-dependent behavior in the published image.
