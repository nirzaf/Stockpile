# Public API conventions

Stockpile exposes its public resource HTTP API through versioned MVC controllers at
`/api/v1`. Resource routes, authentication, validation, response envelopes, rate
limits, and OpenAPI metadata are defined on the controllers. Auth, webhook, and AI
operations remain minimal endpoints because they are not duplicate resource routes.

The versioning package is configured with API Explorer support so generated OpenAPI
documents can describe versioned endpoints and substitute the URL version segment.
New resource endpoints should be added to the appropriate versioned controller instead
of introducing a second route implementation for the same resource.
