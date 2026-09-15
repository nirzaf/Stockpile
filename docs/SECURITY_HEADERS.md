# HTTP security headers

The application emits a baseline Content-Security-Policy and related browser
security headers. The policy permits only same-origin assets, inline styles and
scripts required by the current Blazor/MudBlazor rendering model, and WebSocket
connections for interactive server components.

Forwarded headers are processed only from `Security:TrustedProxies` and
`Security:TrustedNetworks`. Configure those values to match the actual reverse
proxy or load-balancer network; an empty configuration deliberately trusts no
forwarded sender.
