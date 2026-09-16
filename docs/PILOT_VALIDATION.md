# Merconiq pilot validation kit

This is a consent-aware synthetic walkthrough for an operator or first-time
contributor. It is a test script and feedback form, not evidence that a real
business completed a pilot. Do not request passwords, tokens, customer names,
sales records, or other private business data.

## Definitions

- **Demonstration:** a maintainer or contributor follows this checklist with
  synthetic data to show the workflow.
- **Evaluation deployment:** an explicitly approved disposable or sandbox
  instance used to collect structured feedback.
- **Sustained production use:** an owner-approved business uses Merconiq in
  day-to-day operations and independently decides whether to continue.

These are different outcomes. A demonstration or evaluation must not be called
production adoption.

## Synthetic operator scenario

Use a disposable instance and values such as `PILOT-001`, `Main Warehouse`, and
`Shop Counter`; never copy real customer or supplier data.

1. Install the application using the current [first-run instructions](../README.md)
   and record the host/runtime assumptions.
2. Authenticate with an administrator account supplied through environment
   variables or user secrets. Do not put credentials in this form.
3. Create one supplier, two locations, and one catalog item with an explicit
   reorder level.
4. Receive 100 units at `Main Warehouse`; verify the response, balance, and
   transaction history.
5. Transfer 30 units to `Shop Counter`; verify source 70, destination 30, total
   100, and both history locations.
6. Sell 20 units from `Main Warehouse`; verify source 50, destination 30, total
   80, and the sale history row.
7. Try an oversell from either location; verify the documented conflict/error
   response and unchanged persisted balances.
8. Read balances and history from a fresh page/session, then restart the
   disposable application and verify the synthetic records remain available.

Record which steps were completed, which required help, and any discrepancy.
Do not present a successful walkthrough as a user testimonial or an independent
pilot.

## Consent-aware feedback form

```markdown
### Validation record — [YYYY-MM-DD]

- Validation type: `demonstration` | `evaluation deployment` | `sustained production use`
- Instance/environment: <disposable/sandbox description; no secrets>
- Participant: `not recorded` unless the owner has consent to retain an identifier
- Consent to retain anonymized findings: `yes` | `no` | `not requested`
- Steps completed: <1-8>
- Help required: <none or step numbers and non-sensitive description>
- Discrepancies: <expected vs observed behavior; no customer data>
- Continued-use decision: `not applicable` | `not recorded` | <consented summary>
- Reproducible issue links: <GitHub issue URLs or none>
- Publication approval: `not approved` | `approved by owner on YYYY-MM-DD`
```

Only owner-approved, consented, anonymized findings may be published. Real-user
recruitment, business contact, testimonials, and any claim about three
independent pilots remain owner actions and stay `not recorded` until real
evidence exists. Contributor issues should describe a reproducible failure and
expected behavior; do not label speculative work as “good first issue.”
