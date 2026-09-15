# Analyzer baseline

The web project treats the security-sensitive API versioning analyzer (AV0021) and
MudBlazor component contract analyzer (MUD0002) as errors. This keeps the selected
baseline enforced in both local Release builds and CI without turning unrelated
legacy warnings into a broad, noisy build failure.

When a new warning appears, fix the component or API contract rather than suppressing
the analyzer. The existing CS1591 setting remains visible in CI but is not part of
this selected fail-the-build baseline.
