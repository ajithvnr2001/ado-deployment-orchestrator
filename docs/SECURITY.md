# Security

## 1. Identity & access model

| Path | Implementation |
|---|---|
| API → Azure SQL | SQL auth via connection string stored as a **Container Apps secret** (never in code/image). Hardening path: enable Entra-only auth + managed identity (the server already has `azuread_administrator` set; add `Authentication="Active Directory Managed Identity"` to the connection string and drop the SQL password entirely) |
| API → ADO pipelines | `IPipelineClient` uses a PAT via Basic auth when configured. Hardening path: ADO **service connection with workload identity federation** and remove PATs (see DEPLOYMENT.md §4) |
| API → Key Vault | System-assigned managed identity + RBAC role `Key Vault Secrets User` |
| Terraform apply | ADO service connection (ARM, workload identity) — never long-lived secrets |
| Container image pull | ACR admin creds as CA secret (hardening path: ACR pull with managed identity) |

## 2. Approval gates (defense in depth)

1. **Validation gate** — required fields per resource type; malformed/missing requests can't
   proceed past `Validation`
2. **Security approval gate** — human approval recorded with approver + comment
3. **Review team approval gate** — second human approval; only this triggers the pipeline
4. **ADO environment gate** — the pipeline's apply stage targets ADO environment `state`
   (e.g. `prod`), which carries its own manual-approval + branch-control checks
5. **Branch protection** on GitHub (protected branches in the reference diagram) — tfvars
   commits go through the agent identity; enable branch policies for `main`

## 3. Audit trail

Every workflow decision is appended to Azure SQL (`AuditEntryRows`): start, inference,
validation results, missing-input requests, approvals (with approver identity + comment),
pipeline queue events, completion results. Append-only by design; consider an immutability
policy (WORM) for regulated environments.

## 4. Secrets handling in this repo

- **No secrets are committed.** SQL passwords are generated at deploy time
  (`deploy.sh`), passed only into Terraform variables and CA/KV runtime secrets.
- The `terraform.tfstate` file (which contains the SQL password) is **gitignored**.
- The GitHub token used to publish this repo was used transiently in the push URL only
  and is not stored in any file. **Rotate it immediately after publishing** — it was
  shared in a chat message.

## 5. Network

- API: HTTPS-only public ingress (Container Apps), websockets via SignalR
- SQL: firewall = Azure services (`0.0.0.0`) + deployer IP only
- Key Vault: public, RBAC authorized (hardening: private endpoints)
- Hardening path: lock the Container Apps environment into a custom VNet with private
  endpoints for SQL/KV/ACR, and put the frontend behind Front Door/CDN with WAF

## 6. Input validation

- `requestedBy` required; empty field values rejected (`ArgumentException` → 400)
- Unknown resource types rejected by the catalogue; inference is deterministic
- EF Core parameterized queries throughout (no raw SQL)
- Rate limiting / auth on the public API is **not enabled in this demo** — see §7

## 7. Production hardening checklist

- [ ] Entra ID JWT bearer auth on the API (package already referenced) + role-based
      authorization (`DeploymentRequester`, `SecurityApprover`, `ReviewApprover`)
- [ ] Rate limiting middleware + WAF in front of the API and frontend
- [ ] EF Core **migrations** instead of `EnsureCreated()`
- [ ] App Insights (add the NuGet + connection string; hooks already stubbed in Program.cs)
- [ ] Bring-your-own ADO service connection (workload identity) and remove PATs
- [ ] Managed-identity SQL auth (drop password auth entirely)
- [ ] Private networking (VNet-integrated CAE + private endpoints)
- [ ] Remote Terraform state with state locking (azurerm backend, documented)
- [ ] Immutability/retention policy on the audit table
- [ ] Branch protection + PR validation builds on GitHub
- [ ] Alert rules on CompletedFailed / monitoring timeouts (action group exists in TF)
