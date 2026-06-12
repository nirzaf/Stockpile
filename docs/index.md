---
layout: default
title: Inventory Management System — Documentation
description: End-user guide and documentation for the Inventory Management System.
---

# 📦 Stockpile — Documentation

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/nirzaf/Stockpile/blob/master/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-purple.svg)](https://dotnet.microsoft.com/)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-blue.svg)](https://www.postgresql.org/)
[![Docker](https://img.shields.io/badge/Docker-ready-2496ED.svg)](https://www.docker.com/)

Welcome to the documentation site for **Stockpile** — a modern inventory management web app for tracking items, stock levels, purchase orders, suppliers, and locations.

---

## 📖 Available guides

| Guide | Audience | Description |
|-------|----------|-------------|
| **[User Guide](USER_GUIDE.md)** | End users (Admin, Manager, Staff) | How to use the app day-to-day: login, items, stock operations, purchase orders, troubleshooting. |
| **[README](https://github.com/nirzaf/InventoryManagementSystem/blob/master/README.md)** | Developers & operators | Installation, architecture, API reference, deployment. |
| **[CHANGELOG](https://github.com/nirzaf/InventoryManagementSystem/blob/master/CHANGELOG.md)** | Everyone | Version history and release notes. |
| **[Contributing](https://github.com/nirzaf/InventoryManagementSystem/blob/master/CONTRIBUTING.md)** | Contributors | How to file issues, open PRs, and follow the project's coding standards. |
| **[Security](https://github.com/nirzaf/InventoryManagementSystem/blob/master/SECURITY.md)** | Operators | Security policy and how to report vulnerabilities. |

---

## 🚀 Quick start

The fastest way to try IMS is with Docker:

```bash
git clone https://github.com/nirzaf/InventoryManagementSystem.git
cd InventoryManagementSystem
cp .env.example .env        # edit credentials if desired
docker compose up -d        # starts app + PostgreSQL
```

The app will be available at **http://localhost:8080**.

Default admin: `admin@inventory.com` / `Admin@123` (change in `.env`).

> For full installation, configuration, and deployment instructions, see the [README on GitHub](https://github.com/nirzaf/InventoryManagementSystem/blob/master/README.md).

---

## ✨ What can IMS do?

- **Items** — full CRUD with codes, barcodes, prices, and suppliers.
- **Stock** — receive, transfer, and sell with a complete transaction history.
- **Purchase orders** — Pending → Approved → Received lifecycle, with status chips.
- **Suppliers & locations** — manage who you buy from and where you store stock.
- **Mobile-first UI** — works on desktop, tablet, and phone.
- **REST API** — versioned endpoints under `/api/v1` for integrations.
- **AI insights** — on-device demand forecasting and anomaly detection (ML.NET).
- **PDF & CSV reports** — generate purchase orders and export item lists.

---

## 🛠 Enabling this site

This site is a **GitHub Pages** site served from the `/docs` folder of the repository. To publish it on your fork:

1. Push the `docs/` folder to your repository (already present in this repo).
2. In your GitHub repository, go to **Settings → Pages**.
3. Under **Source**, choose **Deploy from a branch**.
4. Select the branch (`master` or `main`) and the **`/docs`** folder.
5. Click **Save**. Your site will be live at:
   ```
   https://<your-username>.github.io/InventoryManagementSystem/
   ```
   within a minute or two.

For a custom domain, see [the GitHub Pages docs](https://docs.github.com/en/pages/configuring-a-custom-domain-for-your-github-pages-site).

---

## 🧭 Where to next?

- **I'm using the app for the first time** → start with the [User Guide](USER_GUIDE.md).
- **I'm installing or operating the app** → read the [README](https://github.com/nirzaf/InventoryManagementSystem/blob/master/README.md).
- **I want to integrate with the API** → see the [API Reference](https://github.com/nirzaf/InventoryManagementSystem/blob/master/README.md#api-reference).
- **I want to report a bug or suggest a feature** → open an [issue](https://github.com/nirzaf/InventoryManagementSystem/issues/new).
- **I want to contribute code** → read [CONTRIBUTING.md](https://github.com/nirzaf/InventoryManagementSystem/blob/master/CONTRIBUTING.md).

---

*Built with .NET 10, ASP.NET Core, MudBlazor, PostgreSQL, MediatR, and ML.NET. Released under the [MIT License](https://github.com/nirzaf/InventoryManagementSystem/blob/master/LICENSE).*
