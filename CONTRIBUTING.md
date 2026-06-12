# Contributing to Inventory Management System

Thank you for your interest in contributing! This document outlines the process for contributing to this open-source project.

## Code of Conduct

Please read and follow our [Code of Conduct](CODE_OF_CONDUCT.md).

## How to Contribute

### Reporting Bugs

- Search existing [issues](https://github.com/nirzaf/InventoryManagementSystem/issues) to avoid duplicates
- Use the Bug Report template when creating a new issue
- Include clear steps to reproduce, expected vs actual behavior, and environment details

### Suggesting Features

- Use the Feature Request template
- Describe the problem the feature solves and how it benefits users
- Be open to discussion and alternative approaches

### Pull Requests

1. **Fork** the repository
2. **Create a branch** from `main`:
   ```bash
   git checkout -b feature/your-feature-name
   ```
3. **Make changes** following our coding conventions
4. **Add tests** for new functionality
5. **Run all tests** to verify nothing is broken:
   ```bash
   dotnet test
   ```
6. **Build** the solution:
   ```bash
   dotnet build
   ```
7. **Commit** with descriptive messages
8. **Push** and open a Pull Request against `main`

### PR Guidelines

- Keep PRs focused on a single change
- Reference related issues (e.g., "Closes #123")
- Update documentation if applicable
- Ensure CI passes (build + tests)

## Development Setup

See the [README](README.md#getting-started) for setup instructions.

## Coding Conventions

- Follow standard C# naming conventions (PascalCase for types, camelCase for locals)
- Use async/await for I/O-bound operations
- Prefer constructor injection for dependencies
- Add XML doc comments on public APIs
- Keep methods small and focused
- Use `var` when the type is obvious, explicit types otherwise

## Commit Messages

Follow conventional commits format:

```
type(scope): description

- feat: new feature
- fix: bug fix
- docs: documentation
- refactor: code restructuring
- test: adding tests
- chore: maintenance tasks
```

## Project Structure

```
├── InventoryManagementSystem.Core/         Domain layer
│   ├── Entities/                           Domain models
│   ├── Interfaces/                         Service contracts
│   └── Services/                           Business logic
├── InventoryManagementSystem.Infrastructure/ Data access
│   ├── Data/                               DbContext, migrations, seed
│   └── Repositories/                       Repository implementations
├── InventoryManagementSystem.Web/          ASP.NET Core MVC
│   ├── Controllers/                        MVC controllers
│   └── Views/                              Razor views
└── InventoryManagementSystem.Tests/        Test projects
```

## Questions?

Open a [Discussion](https://github.com/nirzaf/InventoryManagementSystem/discussions) or ask in an issue.
