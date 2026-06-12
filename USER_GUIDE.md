# User Guide — Inventory Management System

Welcome to the **Inventory Management System (IMS)**. This guide is for the people who will actually **use** the application day-to-day to track items, stock levels, suppliers, locations, and purchase orders. It is written in plain language and assumes no technical background.

If you are a developer looking for installation, API, or architecture information, please see the [README.md](README.md) instead.

---

## Table of Contents

1. [What is this app?](#1-what-is-this-app)
2. [Getting started](#2-getting-started)
   - [Logging in](#logging-in)
   - [The dashboard](#the-dashboard)
   - [Navigation menu](#navigation-menu)
3. [Managing suppliers](#3-managing-suppliers)
4. [Managing locations](#4-managing-locations)
5. [Managing items](#5-managing-items)
6. [Working with stock](#6-working-with-stock)
   - [Viewing stock on hand](#viewing-stock-on-hand)
   - [Receiving stock](#receiving-stock)
   - [Transferring stock between locations](#transferring-stock-between-locations)
   - [Selling stock](#selling-stock)
   - [Viewing stock transactions](#viewing-stock-transactions)
7. [Purchase orders](#7-purchase-orders)
   - [Creating a purchase order](#creating-a-purchase-order)
   - [Approving, receiving, or cancelling a PO](#approving-receiving-or-cancelling-a-po)
8. [AI-powered insights (Forecasts & Anomalies)](#8-ai-powered-insights-forecasts--anomalies)
9. [PDF reports & CSV exports](#9-pdf-reports--csv-exports)
10. [Roles & permissions](#10-roles--permissions)
11. [Tips, FAQ, and troubleshooting](#11-tips-faq-and-troubleshooting)
12. [Glossary](#12-glossary)

---

## 1. What is this app?

The Inventory Management System is a web application that helps you keep an accurate, real-time picture of:

- **Items** — the products you sell or use (with codes, barcodes, prices, and suppliers).
- **Stock** — how many units of each item you have, and **where** they are physically located.
- **Suppliers** — the companies you buy from.
- **Locations** — the physical places where you store stock (warehouses, stores, rooms, trucks, etc.).
- **Purchase orders (POs)** — orders placed to suppliers for restocking.
- **Stock transactions** — a complete, filterable history of every receive, transfer, and sale.

The app runs in any modern browser on a desktop, tablet, or phone, and uses a clean Material-style interface.

---

## 2. Getting started

### Logging in

1. Open the application URL provided by your administrator (for example, `http://localhost:8080` or your company's domain).
2. On the **Login** page, enter the **email** and **password** you were given.
3. Click **Log in**.

> **Default admin (development only):** `admin@inventory.com` / `Admin@123` — change this immediately in any non-test environment.

If you forget your password, contact your administrator — they can reset it for you.

### The dashboard

After logging in, you land on the **Dashboard**. It gives you a quick snapshot of the business:

| Card | What it shows |
|------|---------------|
| **Total Items** | The number of distinct items in your catalog. |
| **Purchase Orders** | The total number of POs that have been created. |
| **Units in Stock** | The total quantity of all items, summed across every location. |

The dashboard also has two shortcut panels:

- **Quick Actions** — one-click buttons to add an item, receive stock, or add a supplier.
- **Navigation** — buttons that jump to the most-used screens.

### Navigation menu

The left-hand (or top, on mobile) menu is your main way to move around the app:

- **Dashboard** — home screen.
- **Items** — manage the items you stock.
- **Stock** *(expandable group)*:
  - View Stock
  - Receive Stock
  - Transfer Stock
  - Sell Stock
  - Transactions
- **Purchase Orders**
- **Suppliers**
- **Locations**

On small screens the menu collapses into a hamburger button in the top-left corner.

---

## 3. Managing suppliers

Suppliers are the vendors you purchase items from. Each item can be linked to one supplier so that you always know who to reorder from.

### Add a supplier

1. Click **Suppliers** in the menu.
2. Click **Add New Supplier**.
3. Fill in the form:
   - **Name** *(required)* — the company name.
   - **Contact Person** — the main person you deal with.
   - **Phone**, **Email**, **Address** — typical contact details.
4. Click **Create**.

### Edit or delete a supplier

Open **Suppliers**, then use the **Edit** or **Delete** button next to the row you want to change. Deleting a supplier is permanent and may be blocked if items are still linked to it.

### Search

Use the **Search** box at the top-right of the table to filter by name, contact person, or email.

---

## 4. Managing locations

Locations are the physical places where you store stock. Examples: `Main Warehouse`, `Store #1 — Downtown`, `Van A`, `Returns Cage`.

### Add a location

1. Click **Locations** in the menu.
2. Click **Add New Location**.
3. Enter the **Name** *(required)* and an optional **Address**.
4. Click **Create**.

You can also **Edit** or **Delete** any existing location from the list. You will not be able to delete a location that still has stock or appears in any transaction history.

---

## 5. Managing items

An **item** is a distinct product (or material) you track in inventory. Each item has a code, a description, a rate (price), and an optional supplier.

### Add an item

1. Click **Items** in the menu.
2. Click **Add New Item**.
3. Fill in:
   - **Item Code** *(required)* — your unique code, e.g. `WIDGET-001`. Must be unique.
   - **Barcode (SKU/EAN)** *(optional)* — the scannable barcode, if any.
   - **Description** *(required)* — a human-readable name, e.g. `Blue widget, 10mm`.
   - **Rate** *(required)* — the unit price (used for sales and PO line items).
   - **Supplier** *(optional)* — link to a supplier if one already exists.
4. Click **Create**.

### View or edit an item

From the **Items** list, use the row buttons:

- **Details** — view the item's fields.
- **Edit** — change any field.
- **Delete** — permanently remove the item (blocked if the item has stock or transactions).

### Search and sort

- Use the **Search** box to find items by code, barcode, description, or supplier.
- Click any column header to sort by that column. Click again to reverse the sort.

### Export to CSV

Click **Export to CSV** in the top-right of the items list to download a spreadsheet of all your items. This is useful for reporting, audits, or bulk editing in Excel.

---

## 6. Working with stock

Stock is the **quantity of an item currently held at a particular location**. Every change to stock — receiving from a supplier, moving between locations, or selling to a customer — is recorded as a **stock transaction**.

### Viewing stock on hand

1. Go to **Stock → View Stock**.
2. You will see a table with one row per **(item, location)** pair, showing the current quantity.
3. Use the search box to filter by item code, description, or location.
4. Use the buttons at the top to **Receive**, **Transfer**, or **Sell** stock.

### Receiving stock

Use **Receive Stock** when new stock arrives from a supplier — typically as the result of a purchase order that has been delivered.

1. Go to **Stock → Receive Stock**.
2. Select the **Item**, the **Location** it should be placed in, and the **Quantity**.
3. Optionally add a **Note** (for example: *"PO #1234, supplier XYZ"*).
4. Click **Receive Stock**.

The quantity on hand for that item at that location will increase, and a `Receive` transaction will be added to the history.

### Transferring stock between locations

Use **Transfer Stock** when you move existing stock from one location to another (e.g. restocking a store from the warehouse).

1. Go to **Stock → Transfer Stock**.
2. Select the **Item**, the **From Location**, the **To Location**, and the **Quantity**.
3. The source and destination must be different.
4. Optionally add a **Note**.
5. Click **Transfer Stock**.

> The transfer will be blocked if the source location does not have enough quantity. Check the **View Stock** screen first.

### Selling stock

Use **Sell Stock** to record a sale or outbound issue. This **reduces** the quantity on hand at a location.

1. Go to **Stock → Sell Stock**.
2. Select the **Item**, the **Location** you are selling from, and the **Quantity**.
3. Optionally add a **Note** (e.g. *"Invoice #5678"* or *"Walk-in customer"*).
4. Click **Sell Stock**.

The system will block the sale if there is not enough stock at that location.

### Viewing stock transactions

Every receive, transfer, and sale is recorded.

1. Go to **Stock → Transactions**.
2. Use the **From Date** and **To Date** pickers to filter the period you want.
3. Click **Filter**.
4. The table shows: **Date, Type, Item, From, To, Quantity, Notes**.
5. Transaction types are color-coded:
   - 🟢 **Receive** — stock came in.
   - 🔵 **Transfer** — stock moved between locations.
   - 🟡 **Sell** — stock went out.

The transaction log is the audit trail for all stock movement in the system.

---

## 7. Purchase orders

A **purchase order (PO)** is a document you create to buy items from a supplier. POs move through a simple lifecycle:

```
Pending  →  Approved  →  Received
   ↘
     Cancelled
```

- **Pending** — the PO is drafted but not yet sent/approved.
- **Approved** — the PO is approved and can be sent to the supplier.
- **Received** — the stock has arrived and been received (use **Receive Stock** to actually add the units to inventory).
- **Cancelled** — the PO was cancelled and will not be fulfilled.

### Creating a purchase order

1. Click **Purchase Orders** in the menu.
2. Click **Create Purchase Order**.
3. Enter:
   - **PO Number** *(required)* — your internal reference, e.g. `PO-2026-0042`. Must be unique.
   - **Supplier** *(required)* — choose from your existing suppliers.
4. Under **Line Items**:
   - Pick the **Item** and enter the **Quantity** and **Unit Price**.
   - Click **Add Item** to add another line.
   - Use the red trash icon to remove a line (at least one is required).
5. Click **Create Purchase Order**.

The PO is saved with status `Pending`.

### Approving, receiving, or cancelling a PO

1. Open the **Purchase Orders** list and click **Details** on a PO.
2. Depending on the current status, the action buttons are:
   - **Pending** → `Approve` or `Cancel PO`.
   - **Approved** → `Mark as Received`.
3. Click the action you want.

> **Important:** Marking a PO as `Received` does **not** automatically add stock to a location. You still need to go to **Stock → Receive Stock** to record the actual quantities received. This is by design, so partial deliveries are supported.

---

## 8. AI-powered insights (Forecasts & Anomalies)

The system includes an optional AI module (powered by **ML.NET**) that runs entirely on the server — no data is sent to the cloud. It exposes two features:

- **Demand forecasting** — predicts the quantity of an item that will be needed in the next 30 days, based on historical receive/transfer/sell patterns.
- **Anomaly detection** — flags unusual stock movements (sudden spikes or drops) that may indicate theft, mis-entry, or supply issues.

These are exposed as a **headless API** (and the system pre-trains its models in the background every few hours). If your administrator has enabled them, you can ask for forecasts and anomaly reports via the API. Swagger documentation is available at `/swagger` in the development environment.

> The web UI surfaces high-level metrics on the dashboard; the detailed per-item forecasts and anomalies are currently consumed via the API and built-in reports. Speak to your administrator about integration into other dashboards.

---

## 9. PDF reports & CSV exports

The application can generate professional PDF reports and CSV exports of your data:

- **CSV export of items** — click **Export to CSV** on the Items page.
- **PDF reports** — generated server-side using the **QuestPDF** library for purchase orders and stock reports. Available via the API at `/api/v1/...`; contact your administrator for the exact endpoints and authentication.

CSV files open directly in Excel, Google Sheets, or any spreadsheet program.

---

## 10. Roles & permissions

The system uses **role-based access control (RBAC)** with three built-in roles. Your administrator decides which role you have.

| Role | Can do |
|------|--------|
| **Admin** | Everything: manage users, suppliers, locations, items, stock, and POs. |
| **Manager** | Approve/cancel POs, receive/transfer/sell stock, manage items, suppliers, locations. |
| **Staff** | Day-to-day operations: view stock, receive/transfer/sell stock, create POs, view reports. |

> All areas of the app currently require authentication. If you try to access a feature you don't have permission for, you'll be redirected to a friendly **Access Denied** page. Speak to your admin if you believe you should have access.

---

## 11. Tips, FAQ, and troubleshooting

### General tips

- **Search first.** Most lists have a search box in the top-right.
- **Sort tables** by clicking the column header.
- **Use the dashboard's Quick Actions** to perform common tasks without navigating the menu.
- **Use notes.** Add notes to every stock movement so future you (and your auditor) understands the *why*.
- **Check transactions** before reporting "missing" stock — transfers and sales are the usual culprit.

### Frequently asked questions

**Q: I received stock but the dashboard still shows the old count.**
A: The dashboard counts all units across all locations. Use **Stock → View Stock** to verify, and check that the location you received into is the one you expect.

**Q: I can't delete an item/supplier/location.**
A: The system blocks deletion when there is still stock, transactions, or other records linked to the entity. Remove or reassign those first.

**Q: The transfer failed because of insufficient stock.**
A: Go to **Stock → View Stock** and confirm the source location actually has the quantity you are trying to move. You may have already transferred or sold it.

**Q: I marked a PO as received — why isn't the stock updated?**
A: The PO lifecycle is intentionally separate from stock movements, to support partial deliveries. After marking a PO as received, go to **Stock → Receive Stock** and record the quantities that actually arrived.

**Q: My forecast numbers look off.**
A: Forecasting needs at least a few weeks of clean transaction history. The background service retrains models periodically — give it time, and ensure every receive/transfer/sell is recorded with accurate quantities and dates.

### Common error messages

| Message | What it means | What to do |
|---------|---------------|------------|
| *"Please select an item / location"* | You didn't pick from the dropdown. | Pick a value in the dropdown. |
| *"Insufficient stock at source location"* | Trying to transfer or sell more than is available. | Check **View Stock** and adjust the quantity. |
| *"Source and destination locations must be different"* | You picked the same location twice in a transfer. | Pick two different locations. |
| *"A purchase order must have at least one line item"* | You tried to remove the last line. | At least one line item is required. |
| *"Item Code must be unique"* | The code you entered is already used by another item. | Use a different code. |
| *"Account locked. Try again later."* | Too many failed login attempts. | Wait a few minutes or ask an admin to unlock your account. |

### Still stuck?

If something is broken, unexpected, or unclear:

1. Note the **page** you were on and the **action** you tried.
2. Copy any **error message** verbatim.
3. Send it to your system administrator along with the approximate time it happened.

---

## 12. Glossary

| Term | Meaning |
|------|---------|
| **Item** | A distinct product (e.g. `WIDGET-001 — Blue widget, 10mm`). |
| **Stock on hand** | The number of units of an item currently at a specific location. |
| **Location** | A physical place where stock is stored (warehouse, store, van, etc.). |
| **Supplier** | A company you purchase items from. |
| **Purchase Order (PO)** | A document ordering items from a supplier. |
| **Receive** | Adding new stock to a location (typically from a supplier). |
| **Transfer** | Moving stock from one location to another. |
| **Sell** | Removing stock from a location due to a sale or issue. |
| **Transaction** | A single record of a receive, transfer, or sell. |
| **Forecast** | A predicted future quantity needed for an item, based on history. |
| **Anomaly** | An unusual stock movement flagged by the system as worth reviewing. |

---

*This guide covers the user-facing functionality of the Inventory Management System. For installation, configuration, and developer documentation, see [README.md](README.md).*
