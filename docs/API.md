# Public API conventions

Stockpile exposes its public HTTP API through the versioned minimal-API group at
`/api/v1`. Resource routes, authentication, validation, response envelopes, rate
limits, and endpoint metadata are registered together in `Program.cs`.

The versioning package is configured with API Explorer support so generated OpenAPI
documents can describe versioned endpoints and substitute the URL version segment.
New public endpoints should be added to the versioned group instead of introducing
a second controller route for the same resource.
