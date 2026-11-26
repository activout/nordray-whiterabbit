# Contributing to Nordray White Rabbit

White Rabbit is maintained by [Nordray](https://nordray.eu) and [Activout](https://activout.se). Issues and pull
requests are welcome. [`AGENTS.md`](AGENTS.md) holds the full rules for the code base; this file explains how it
works.

## How it works

```
GitHub / Forgejo Actions job            you, at a terminal
   │ job OIDC token                       │ rabbit auth login (browser + emailed code)
   ▼                                      ▼
┌──────────────────────────────────────────────────┐
│              Nordray White Rabbit                │
│  • token exchange      CI token / login code → session (wrs_…)
│  • workload bindings   repository + ref + workflow → capabilities
│  • virtual credentials wrk_…, 5 minutes          │
│  • operation registry  deny by default (404/403) │
│  • key injection       your bunny.net key, never returned
│  • audit log           every decision            │
└─────────────────────────┬────────────────────────┘
                          ▼
                   https://api.bunny.net
```

Each BunnyCLI call goes through White Rabbit's proxy with a virtual credential. The operation must be in the
hard-coded registry (`404` otherwise) and its capability must be granted (`403` otherwise). Then the owner's key is
injected and the request is forwarded to `https://api.bunny.net`.

## Operation registry

- Every proxied operation is an entry in `BunnyOperationRegistry`. New endpoints need a code review and an explicit
  entry. Bunny's OpenAPI specs are for reference only: presence in the spec does not make an endpoint available.
- **Each capability has one description**, in `BunnyOperationRegistry`, covering every operation it allows. `rabbit`
  and the README's capability table show that text.
- **Left out on purpose:** log forwarding (its responses carry tokens), registry changes and single-registry reads,
  separate volume and endpoint changes (replacing the whole app with `mc.manage` covers them), and database groups and
  their tokens.

### Redaction

Some responses hold secrets the capability does not cover. White Rabbit replaces those fields with
`[redacted by White Rabbit]`:
- container environment variable values, in the app, app-create, container, container-update and set-environment
  responses;
- registry password fragments, in the registry list;
- pull zone secrets (`ZoneSecurityKey`, `AWSSigningKey`, `AWSSigningSecret`, `LogForwardingToken`, custom
  certificates' `CertificateKey`), in every pull zone response;
- storage zone passwords (`Password`, `ReadOnlyPassword`), in every storage zone response.

Everything else is returned unchanged. A successful response that cannot be parsed for redaction is refused (502), not
passed through.

**Setting environment variables keeps redacted values.** Setting a container's environment replaces all of its
variables, and tools such as `bunny apps env set` send back every variable they read. Before forwarding, each value
equal to the placeholder is replaced with that variable's current value, read with the owner's key. A placeholder for a
variable the container does not have is refused (400). If the current values cannot be read, nothing is written (502).

**Replacing an app keeps redacted values too.** `PUT /mc/apps/{id}` replaces the whole app. Each placeholder in a
container's `environmentVariables` gets that variable's current value from the current app's container with the same
`id` (or, without one, the same `name`). A placeholder that matches no current variable, or one anywhere else in the
body, is refused (400). If the current app cannot be read, nothing is written (502).

**Bunny Sites are scoped by site name.** A site's storage zone and pull zone are named `sites-<site>-<6 characters>`
by BunnyCLI. A binding's `sites.*` capabilities reach only the sites it names (`--site`); a human session reaches every
site. Lists are filtered, anything else is "not found", and a new zone must belong to one of those sites. The
`sites.*` capabilities never reach a storage zone that is not a site's. Pull zone operations granted through
`sites.*` accept only the fields a site deploy sends; `pullzone.*` stays account-wide.

**Site files without the zone password.** `bunny sites deploy` uploads straight to `<region>.storage.bunnycdn.com`
with the zone password, and BunnyCLI has no setting to point it elsewhere. `rabbit` is BunnyCLI's HTTPS proxy: it
terminates TLS for the Edge Storage hosts only, with a certificate authority that exists for that one run
(`NODE_EXTRA_CA_CERTS`, valid only for `storage.bunnycdn.com` names), and sends those requests to White Rabbit, which
injects the zone's password. Everything else is tunnelled unchanged. BunnyCLI only ever sees the redaction placeholder.

**`mc.deploy` changes only the image.** Bunny's container update also accepts environment variables, endpoints,
volumes, probes and the entry point. White Rabbit forwards only the image fields and refuses any other field (400),
even when it is null or empty.

## CLI API

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /cli/login?port=&state=&code_challenge=` | browser sign-in (email code) | After an explicit confirmation, redirects to `http://127.0.0.1:{port}/callback` with a one-time code `wrc_…` (2 minutes) bound to the PKCE S256 challenge |
| `POST /api/cli/exchange` `{"code": "…", "code_verifier": "…"}` or `{"subject_token": "…"}` | none (rate limited) | Exchanges a login code with its verifier, or a GitHub Actions or Forgejo Actions token, for a session token `wrs_…`. Humans get 8 hours with every capability, CI workloads 60 minutes with their binding's |
| `POST /api/cli/credentials` `{"capabilities": […]}` | `Authorization: Bearer wrs_…` | Issues a virtual credential `wrk_…` (5 minutes), never broader than the session. Send it as `AccessKey` to `/proxy/api.bunny.net/…`, or to `/proxy/storage/<host>/<zone>/…` for a site's files |
| `GET /api/cli/whoami` | session | The authenticated human or workload and its current capabilities |
| `POST /api/cli/revoke` `{"credential_id": "…"?}` | session | Revokes one credential, or the session and all its credentials |
| `GET/POST /api/admin/bindings`, `DELETE /api/admin/bindings/{id}` | human session only | Lists, creates and revokes the caller's own CI workload bindings |
| `GET /api/admin/audit?since=&binding_id=&limit=` | human session only | The caller's own security events, newest first (default: last 24 hours, 50 events, at most 500) |
| `PUT /api/admin/bunny-key` `{"api_key": "…"}`, `DELETE /api/admin/bunny-key` | human session only | Stores (encrypted) or removes the caller's bunny.net API key. Never returned; `whoami` only says `has_bunny_key` |

## Security model

- **Sessions and virtual credentials** are opaque random tokens; only their SHA-256 hashes are stored. They are checked
  on every use against the sign-in allow-list and, for CI, the workload binding, so revoking either takes effect
  immediately.
- **Email codes** are SHA-256 hashed, expire in 10 minutes, are invalidated after first use, and are rate limited per
  email and per IP (requests and verification attempts).
- **CLI login codes** are opaque, stored as SHA-256 hashes, valid for 2 minutes and consumed on first use, even when the
  verifier is wrong. The redirect always goes to `127.0.0.1`, never to a caller-supplied URL.
- **The proxy** accepts only a virtual credential in `AccessKey`. Anything else, including a real bunny.net key, is
  refused and never forwarded. Inbound `AccessKey`, `Authorization` and `X-Remote-*` headers are stripped; the owner's
  stored key is injected server side.
- **CI tokens** are validated per configured issuer: exact `iss`, audience `Federation:Audience`, RS256 only (checked
  before any key lookup), expiry and not-before, and a lifetime of at most `Federation:MaxTokenLifetime`. Signing keys
  come from the issuer's discovery document; only configured issuers are ever fetched.
- **Workload bindings** decide what a CI identity may do. They are created only through the admin API by a human,
  never from repository content.
  - Every field is matched exactly: issuer, repository, ref (`refs/heads/…` or `refs/tags/…` only) and
    `workflow_ref`, whose `@ref` must equal the ref.
  - The ref is optional. A binding without one accepts the workflow from any branch or tag, so the repository's own
    protections decide which refs are trusted: who may push which branches and tags, rulesets, and on GitHub a
    protected `environment` on the binding. The workflow must still come from the ref being built.
  - The event must be on the binding's allow-list. `pull_request_target`, `workflow_run` and similar events are never
    allowed, and `pull_request` only on a pull request binding.
  - **Pull request bindings** (`--site-prefix example-site-pr-`) accept exactly the `pull_request` event, have no ref
    and hold only `sites.*` capabilities. The job reaches one site, `<prefix><PR number>`, with the number from the
    token's ref (`refs/pull/N/head` on Forgejo, `/merge` on GitHub). A PR from a fork gets no OIDC token on either
    forge. A PR from a branch may have changed its own workflow, which is why it can never reach anything but its own
    preview.
  - **GitHub** bindings also require the immutable `repository_id`, and optionally `repository_owner_id`,
    `environment` and `job_workflow_ref`.
  - **Forgejo** (tested on 15.0.12) does not issue IDs or environments. Its bindings match on names, so renaming or
    transferring a repository is a known risk. Forgejo's `ref_protected` claim is always `"false"` and is not used.
    Forgejo jobs must set `enable-openid-connect: true`.
  - Two matching bindings are refused as ambiguous.

### Audit

Security events (`SecurityEvents` table and the structured log) record CLI sign-ins, token exchanges, credential
issuance, bunny.net key changes, revocations, binding changes and every proxy decision. Each event records:
- principal type, subject and issuer;
- binding and session;
- repository, workflow, ref and run;
- credential ID;
- operation, method and path (never the query string);
- status, upstream status and correlation ID.

Tokens, keys, codes and request bodies are never recorded. Send `X-Correlation-ID` to correlate a request; it is
echoed in the response.

`rabbit admin audit` shows everything recorded with the caller as the owner, including their CI workloads' exchanges
and proxy calls. Denials where no owner is known yet, such as a token from an untrusted issuer or for a repository
without a binding, are only in the table and the log.

## Technology stack

| Concern | Choice |
|---|---|
| Web host | ASP.NET Core 11 |
| Token validation | `Microsoft.IdentityModel` (GitHub and Forgejo Actions OIDC tokens) |
| Proxy forwarding | YARP (reverse proxy pipeline) |
| Database | libSQL (a local file, or [Bunny Database](https://bunny.net/database/?ref=ch5ir79hyc) when deployed; affiliate link) via [Activout.DatabaseClient](https://github.com/twogood/Activout.DatabaseClient) and its `LibSqlGateway` |
| Email | Mailjet (SMTP/Mailpit fallback for local dev) |
| Tests | xUnit, RichardSzalay.MockHttp |
| CLI | Go ([`cli/`](cli/README.md)) |

## Project layout

```
src/
  Nordray.WhiteRabbit.Web            ASP.NET Core host, CLI API, sign-in pages, proxy pipeline
  Nordray.WhiteRabbit.Core           Domain types — BunnyOperation, capabilities, models
  Nordray.WhiteRabbit.Infrastructure libSQL DAOs and migrations, Mailjet/SMTP email service
  Nordray.WhiteRabbit.Proxy          BunnyHttpClientFactory, ISRG Root X1 certificate pinning
  Nordray.WhiteRabbit.Bunny          BunnyOperationRegistry (the hard-coded allow-list)
tests/
  Nordray.WhiteRabbit.Tests.Unit
  Nordray.WhiteRabbit.Tests.Integration
cli/                                 rabbit, the Go command-line client
examples/ci/                         GitHub Actions and Forgejo Actions workflows
docs/                                Deployment and migration guides
```

## Running locally

Prerequisites: Docker, .NET 11 SDK.

```bash
# Start Mailpit (catches the login emails)
docker compose up mailpit

# The two required settings
export WhiteRabbit_DataProtection__EncryptionKey=$(openssl rand -base64 32)
export WhiteRabbit_Access__AllowedEmails__0=you@example.com

# Run White Rabbit with hot reload
dotnet watch --project src/Nordray.WhiteRabbit.Web
```

Then `rabbit --server http://localhost:8080 auth login`, and look in [http://localhost:8025](http://localhost:8025)
(Mailpit) for the code.

To run everything in Docker:

```bash
cp .env.example .env   # fill in secrets if needed
docker compose up
```

Configuration is described in [docs/deploy.md](docs/deploy.md#configuration).

## Database

The same libSQL provider serves a local file, an in-memory database in tests and Bunny Database in production. The
schema lives in numbered migrations (`src/Nordray.WhiteRabbit.Infrastructure/Database/Schema/NNN_name.sql`), applied
once each at start-up and recorded in `SchemaMigrations`. Never edit an applied migration; add a new numbered file. The
native libSQL library ships for linux-x64, linux-arm64, macOS arm64 and Windows x64; Intel Macs are not supported for
local development.

## What v1 deliberately does not include

- OpenFGA or attribute-based policies
- Resource-level scoping, except Bunny Sites by site name (no per pull zone or per DNS zone grants)
- Refresh tokens: sign in again when the session ends
- Third-party OIDC clients: White Rabbit is for CI/CD and the `rabbit` CLI only
- Automatic endpoint discovery from bunny's OpenAPI specs
- Multi-tenant isolation
- Passwords or social login
