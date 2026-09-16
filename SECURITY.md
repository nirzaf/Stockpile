# Security Policy

## Reporting a Vulnerability

We take the security of this project seriously. If you discover a security vulnerability, please **do not** open a public issue.

Instead, report it privately by:

1. **Email the maintainers** at `nirzaf@users.noreply.github.com`
2. Use GitHub's private vulnerability reporting (when enabled for the repo)

Please include:

- A clear description of the vulnerability
- Steps to reproduce
- Affected versions
- Any potential impact assessment

## Response Timeline

- We will acknowledge receipt within **48 hours**
- We aim to provide a fix or mitigation within **7 days** for critical issues
- We'll coordinate disclosure with you

## Authentication abuse controls

The browser login and anonymous token endpoint use two complementary controls:

- Each Identity user is locked for five minutes after five failed password
  attempts. The token endpoint uses the same failed-attempt accounting as the
  browser login, so a locked account cannot obtain a token through that path.
- Anonymous login requests use a fixed window of 20 requests per minute,
  partitioned by the resolved tenant and remote IP address. This limits unknown
  account attempts without creating one global budget for all tenants. The
  limiter is an abuse boundary, not a guarantee against distributed attacks.

Failure responses remain generic for unknown users, wrong passwords and
wrong-tenant credentials. Operators should provide an account recovery path
before enabling a policy that can temporarily lock a legitimate user.

## Security Best Practices for Deployments

When deploying this application:

- Use strong, unique passwords for the admin account
- Configure PostgreSQL with SSL/TLS
- Set `ASPNETCORE_ENVIRONMENT=Production`
- Use HTTPS in production
- Rotate secrets regularly
- Keep dependencies updated (`dotnet list package --vulnerable`)
- Do not commit `appsettings.Development.json` or `.env` files containing secrets
- If a credential was ever committed, rotate it even after removing the file from the current branch

## Database Security

- The application uses PostgreSQL with Entity Framework Core
- Connection strings should use environment variables (`ConnectionStrings__DefaultConnection`) in production
- Development seed credentials must be supplied through user secrets or environment variables; none are committed
- The seed data only runs in Development environment

## Supported Versions

| Version | Supported          |
| ------- | ------------------ |
| 1.x     | :white_check_mark: |
