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

## Security Best Practices for Deployments

When deploying this application:

- Use strong, unique passwords for the admin account
- Configure PostgreSQL with SSL/TLS
- Set `ASPNETCORE_ENVIRONMENT=Production`
- Use HTTPS in production
- Rotate secrets regularly
- Keep dependencies updated (`dotnet list package --vulnerable`)
- Do not commit `appsettings.Development.json` or `.env` files containing secrets

## Database Security

- The application uses PostgreSQL with Entity Framework Core
- Connection strings should use environment variables (`ConnectionStrings__DefaultConnection`) in production
- Default admin credentials in `appsettings.Development.json` are for local development only
- The seed data only runs in Development environment

## Supported Versions

| Version | Supported          |
| ------- | ------------------ |
| 1.x     | :white_check_mark: |
