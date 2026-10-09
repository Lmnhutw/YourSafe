# PasswordTool.Api

Optional ASP.NET Core Minimal API over the password-hashing capabilities in `PasswordTool.Core`. It does **not** expose the encrypted vault.

## Current endpoints

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/api/password/hash` | Hashes `Password` using `AlgorithmName`. |
| `POST` | `/api/password/verify` | Checks `Password` against `StoredHash`. |
| `POST` | `/api/password/inspect` | Parses supported metadata from `StoredHash`. |
| `GET` | `/api/password/algorithms` | Lists supported algorithms and their security category. |

Run from the repository root with the .NET 10 SDK:

```powershell
dotnet run --project src\PasswordTool.Api\PasswordTool.Api.csproj --launch-profile https
```

The HTTPS launch profile listens at `https://localhost:7072` and `http://localhost:5030`; HTTP requests are redirected to HTTPS. In Development, the OpenAPI document is at `https://localhost:7072/openapi/v1.json`. No Swagger UI is configured. [PasswordTool.Api.http](PasswordTool.Api.http) contains example requests. JSON request fields use `password`, `algorithmName`, and `storedHash`; `/algorithms` reports the available registry descriptors, including educational-only algorithms.

This process is separate from the desktop and NativeHost. Browser autofill uses local named pipes, not this HTTP API.

## Security status

This process accepts only a loopback peer and a localhost/loopback Host header. The supplied launch profiles listen on localhost; overriding listener configuration does not bypass the peer/Host gate. It has no vault endpoint and is intended for local development. Its hash and verify routes receive raw passwords in memory; local callers are not authenticated.

Requests are limited to 32 KiB, passwords to 4,096 characters and stored hashes to 2,048 characters. A process-wide limit permits 60 requests per minute; expensive hash/verify work is limited to two concurrent requests with no queue. Rate and concurrency rejection returns HTTP 429. Responses disable caching. Educational-only algorithms remain listed for inspection, but `/hash` rejects generating them.

Core rejects malformed/duplicate/version-invalid hash formats and bounds work factors, output and salt lengths before expensive derivation. bcrypt passwords exceeding 72 UTF-8 bytes are rejected rather than silently truncated. Compatibility hash defaults remain unchanged; inspection now distinguishes supported formats from adequate password-storage strength.

Public deployment still requires a separate authentication/authorization, HTTPS/proxy and operational security design. Keep Core as the sole source of hashing behavior and never enable password/hash payload logging. The security hardening pass has deferred final build and runtime qualification; see [the requirements](../../docs/security-hardening-requirements.md).
