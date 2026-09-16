# Observability

OpenTelemetry is disabled by default, including in tests. Enable it with
`OpenTelemetry__Enabled=true` and optionally set `OpenTelemetry__OtlpEndpoint` to
an OTLP collector endpoint. When enabled, Merconiq instruments HTTP requests,
outbound HTTP, EF Core database commands, runtime metrics, and the
`Merconiq.Inventory` meter.

The inventory meter emits counters for concurrency retries, forecast fallbacks,
low-stock alerts, and webhook delivery failures. Keep endpoint and exporter
configuration in deployment secrets/configuration rather than source control.
