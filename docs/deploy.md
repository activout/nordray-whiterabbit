# Deploying White Rabbit

White Rabbit runs as a single container (`white-rabbit` on port `8080`) on
[bunny.net Magic Containers](https://bunny.net/magic-containers/). All its data, including the encrypted Data
Protection key ring, lives in its database (Bunny Database when deployed), so the container needs no volume.

## Image

`.github/workflows/image.yml` publishes the server image to `ghcr.io/activout/nordray-whiterabbit`:
- every push to `main` gets `:sha-<commit>` and `:main`;
- every `v*` tag gets `:<tag>` and `:latest`.

The run summary shows the digest. Deploy a pinned digest, not a moving tag. The image is generic: it holds no
configuration, hostnames or secrets. Supply those as environment variables, for example from a secrets loader that you
add as the entrypoint in your own image layer.

## Configuration

Secrets are **never** stored in `appsettings` files. Supply them as `WhiteRabbit_`-prefixed environment variables
(`__` maps to `:` in the config hierarchy):

| Variable | Purpose |
|---|---|
| `WhiteRabbit_DataProtection__EncryptionKey` | **Required.** Base64 of 32 random bytes (`openssl rand -base64 32`). Encrypts the Data Protection key ring stored in the database, which in turn protects every stored bunny.net API key. Keep it with the database backup, separately protected: without it the stored keys cannot be read. White Rabbit refuses to start without it |
| `WhiteRabbit_Database__ConnectionString` | `Data Source=<file>` locally, or `Data Source=https://<database>.lite.bunnydb.net;Auth Token=<token>` for Bunny Database |
| `WhiteRabbit_Mailjet__ApiKey` | Mailjet API key (omit to use SMTP fallback) |
| `WhiteRabbit_Mailjet__SecretKey` | Mailjet secret key |
| `WhiteRabbit_Mailjet__FromEmail` | Sender address |

Non-secret settings:

| Setting | Purpose |
|---|---|
| `Access:AllowedEmails`, `Access:AllowedDomains` | Who may sign in (e.g. `WhiteRabbit_Access__AllowedEmails__0=you@example.com`). **Empty means nobody**, so an instance never collects strangers' bunny.net keys by accident. Removing an address ends its sessions and proxy access |
| `Federation:Audience` | Token audience for CI, distinct per environment (e.g. `whiterabbit:prod`). Empty disables CI federation |
| `Federation:Issuers` | Trusted CI issuers, each `Name`, `Kind` (`GitHub` or `Forgejo`) and exact `Url`. Empty disables CI federation |
| `Federation:MaxTokenLifetime` | Longest accepted CI token lifetime (default 2 hours) |
| `Cli:*Lifetime` | Login code, human session, workload session and credential lifetimes |

See [`.env.example`](../.env.example) for a full template.

## Deploy on bunny.net, step by step

A one-time job, done by the owner on their own machine with [BunnyCLI](https://www.npmjs.com/package/@bunny.net/cli)
and the bunny.net API key (the only time White Rabbit's setup needs it outside the server). Region SE is a fine
default; pick one region and keep the app and database together.

1. **Database:** `bunny db create` (one primary region), then a full-access token with
   `bunny db tokens create <id> --no-save`. The connection string is
   `Data Source=https://<database>.lite.bunnydb.net;Auth Token=<token>` (`https://`, not `libsql://`).
   `bunny db create --output json` may return `id: null`; take the ID from `bunny db list`.
2. **Secrets:** `openssl rand -base64 32` for `WhiteRabbit_DataProtection__EncryptionKey`. Store it with the database
   backup: **losing it makes every stored bunny.net key unreadable.** Add the connection string, Mailjet keys and
   sender, and `WhiteRabbit_Access__AllowedEmails__0` (who may sign in). Keep them out of shell history and
   command lines: write them through files in `mktemp -d` or a secrets loader.
3. **App:** a Magic Containers app (`POST /mc/apps`) with exactly one instance (`autoScaling {min: 1, max: 1}`),
   one region, one container from a pinned digest of `ghcr.io/activout/nordray-whiterabbit` and an endpoint on
   port 8080 through the CDN. Its environment holds the secrets above and these settings:
   ```
   ASPNETCORE_FORWARDEDHEADERS_ENABLED=true        # Bunny's CDN is the only way in
   WhiteRabbit_Federation__Audience=whiterabbit:prod
   WhiteRabbit_Federation__Issuers__0__Name=github
   WhiteRabbit_Federation__Issuers__0__Kind=GitHub
   WhiteRabbit_Federation__Issuers__0__Url=https://token.actions.githubusercontent.com
   # Forgejo, e.g. Codebahn: Kind=Forgejo, Url=https://<forgejo host>/api/actions
   ```
   `bunny api` takes a body only on the command line, so post a body that carries secrets with curl and a header file
   instead.
4. **Hostname:** add your hostname to the endpoint's pull zone, point DNS at the pull zone, load a free certificate
   and force HTTPS. The hostname is the server URL for `rabbit env add` and `RABBIT_SERVER`.
5. **Check:** `curl https://<hostname>/ready` returns 200. Then `rabbit env add prod https://<hostname>`,
   `rabbit auth login` and `rabbit admin key set`. Then delete the copies of the key in CI secrets as each workflow
   moves ([Moving a workflow](migrating.md)).

## Health

- `GET /health`: the process is running.
- `GET /ready`: the database answers and every migration shipped with this build has been applied. Returns 503
  otherwise, without connection details.
