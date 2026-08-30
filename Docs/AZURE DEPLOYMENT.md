# Azure Deployment: JWT Signing Key and Key Vault

This document explains how to supply the JWT signing key when deploying
`GameTrackerBlazorServerApp` to Azure, and why no application code changes are required to
do so.

It complements step 4 of the [README](../README.md), which covers the equivalent local
development setup using `dotnet user-secrets`.

---

## Summary

**No code changes are needed**, provided the key is supplied through an Azure Key Vault
*reference* rather than by adding a Key Vault configuration provider to the application.

The server reads the key from `builder.Configuration`:

```csharp
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
```

`builder.Configuration` is provider-agnostic. `WebApplication.CreateBuilder` already registers
the environment variable provider, and Azure App Service injects application settings into the
process as environment variables. The key therefore reaches the application without it knowing
where the value came from.

The only requirement is that the setting resolves to the configuration key `Jwt:Key`.

---

## Naming: three spellings of the same key

This is the single most common source of confusion, because the same value is written three
different ways depending on where it appears.

| Context | Name | Reason |
|---|---|---|
| C# / `appsettings.json` | `Jwt:Key` | `:` is the .NET configuration section separator |
| App Service setting / environment variable | `Jwt__Key` | `:` is not portable in environment variable names on Linux; .NET maps `__` to `:` |
| Key Vault secret name | `JwtKey` | Key Vault permits only alphanumerics and hyphens |

The Key Vault secret name is arbitrary — the mapping back to `Jwt:Key` is performed by the App
Service setting name, not by the secret name.

---

## Option A — Key Vault references (recommended)

Requires no code changes and stores no credential anywhere.

### 1. Create the vault and store the key

```powershell
az keyvault create --name gametracker-kv --resource-group <rg> --location <region>

# Generate a cryptographically secure 64-byte key.
# Get-Random is NOT suitable here: it is not a cryptographic random source.
$bytes = New-Object byte[] 64
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)

az keyvault secret set `
  --vault-name gametracker-kv `
  --name JwtKey `
  --value ([Convert]::ToBase64String($bytes))
```

The application refuses to start with a key shorter than 32 bytes, because a short key can be
brute-forced to forge tokens. 64 bytes is comfortably above that floor.

### 2. Grant the web app a managed identity

```powershell
az webapp identity assign --name <app-name> --resource-group <rg>

$principalId = az webapp identity show `
  --name <app-name> --resource-group <rg> --query principalId -o tsv

az role assignment create `
  --assignee $principalId `
  --role "Key Vault Secrets User" `
  --scope $(az keyvault show --name gametracker-kv --query id -o tsv)
```

A managed identity is what makes this approach worthwhile: the application authenticates to Key
Vault using an identity Azure manages, so there is no bootstrap credential to store, rotate, or
leak. Storing a vault client secret in configuration would simply move the original problem.

> If the vault uses access policies rather than Azure RBAC, grant `get` on secrets with
> `az keyvault set-policy --name gametracker-kv --object-id $principalId --secret-permissions get`
> instead of the role assignment above.

### 3. Reference the secret from an application setting

```powershell
az webapp config appsettings set --name <app-name> --resource-group <rg> --settings `
  "Jwt__Key=@Microsoft.KeyVault(VaultName=gametracker-kv;SecretName=JwtKey)"
```

App Service resolves the reference at startup and passes the plain value to the application.

Apply the same pattern to the database connection string:

```powershell
az webapp config appsettings set --name <app-name> --resource-group <rg> --settings `
  "ConnectionStrings__DefaultConnection=@Microsoft.KeyVault(VaultName=gametracker-kv;SecretName=SqlConnectionString)"
```

### 4. Verify

If the reference fails to resolve, App Service passes the literal `@Microsoft.KeyVault(...)`
string through as the value. The startup guard catches this: the string is longer than 32 bytes,
so it passes the length check and the application starts with a wrong key, and every token then
fails validation.

Confirm the reference resolved before trusting the deployment:

```powershell
az webapp config appsettings list --name <app-name> --resource-group <rg> `
  --query "[?name=='Jwt__Key']" -o json
```

In the portal, **Configuration → Application settings** shows a green tick beside a resolved
Key Vault reference and an error against a broken one.

---

## Option B — Key Vault configuration provider (requires code changes)

Only necessary if secrets must be re-read at runtime without restarting the app, or when
deploying somewhere that does not support Key Vault references.

Packages:

```xml
<PackageReference Include="Azure.Extensions.AspNetCore.Configuration.Secrets" Version="..." />
<PackageReference Include="Azure.Identity" Version="..." />
```

Registration, before the configuration is read:

```csharp
if (!builder.Environment.IsDevelopment())
{
	builder.Configuration.AddAzureKeyVault(
		new Uri($"https://{vaultName}.vault.azure.net/"),
		new DefaultAzureCredential());
}
```

Note that this provider uses a different naming convention again: nesting is expressed with a
double hyphen, so the secret must be named `Jwt--Key` rather than `JwtKey`.

**Recommendation:** prefer Option A. Option B adds two dependencies, a startup dependency on
vault availability, and a fourth spelling of the same key name, in exchange for rotation
behaviour this application does not currently need.

---

## Operational considerations

### Scale-out requires one shared key

Every instance must sign and validate with the same key. If instances disagree, a token issued
by one is rejected by another, presenting as intermittent 401s that appear to depend on which
instance served the request. A single application setting satisfies this automatically; per
instance configuration does not.

### Deployment slots

If staging and production reference different vaults, mark the setting as a **deployment slot
setting**. Otherwise a slot swap carries the staging key into production and silently signs
every client out.

### Key rotation is a breaking operation

Every issued token is validated against the current key, so replacing the secret immediately
invalidates every outstanding token and forces all clients to sign in again. There is no
overlap period: the application trusts exactly one key.

Rotate deliberately, and expect a re-login. Supporting overlapping keys would require validating
against multiple `IssuerSigningKeys`, which is not currently implemented.

### The admin seeder also needs configuration

`DbSeeder` runs at application startup in production as well as development. It creates the
admin account only when `Seed:AdminPassword` is configured, and it does **not** update an
account that already exists. Add it as a second reference if an admin account is required:

```powershell
az webapp config appsettings set --name <app-name> --resource-group <rg> --settings `
  "Seed__AdminPassword=@Microsoft.KeyVault(VaultName=gametracker-kv;SecretName=SeedAdminPassword)"
```

### The WPF client still points at localhost

`GameTrackerWpfClientApp/appsettings.json` sets:

```json
"Server": { "BaseAddress": "https://localhost:7157/" }
```

This is unrelated to Key Vault, but it will stop the deployed system from working end to end:
the client will keep calling localhost and fail to sync or upload. Update it to the deployed
URL when distributing the client.

---

## What is deliberately *not* in Key Vault

`Jwt:Issuer` and `Jwt:Audience` are not secrets. They are validation parameters, not credentials,
and belong in `appsettings.json` where they are reviewable. Only `Jwt:Key` is sensitive: it is
the value that permits minting a token, and anyone holding it can issue a valid Admin token.

---

## Checklist

- [ ] Key Vault created
- [ ] 64-byte key generated from a cryptographic source and stored as `JwtKey`
- [ ] Managed identity assigned to the web app
- [ ] `Key Vault Secrets User` role (or `get` secret policy) granted to that identity
- [ ] `Jwt__Key` app setting created as a Key Vault reference
- [ ] Connection string supplied the same way
- [ ] `Seed__AdminPassword` configured, if an admin account is needed
- [ ] Reference confirmed as resolved, not passed through literally
- [ ] Setting marked as a slot setting, if slots are in use
- [ ] Client `Server:BaseAddress` updated to the deployed URL
