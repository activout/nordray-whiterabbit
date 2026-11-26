# Nordray White Rabbit

**Keep your bunny.net API key out of CI/CD.**

An open source project by [**Nordray**](https://nordray.eu) and [**Activout**](https://activout.se).

bunny.net has one all-powerful account API key and no OIDC or scoped keys. Put it in a CI secret and every workflow,
and anyone who can edit one, controls your whole account. White Rabbit keeps the key on its server. GitHub Actions and
Forgejo Actions jobs sign in with their own OIDC token and get a five-minute credential that can do only what you
allowed that workflow, for example `mc.read` + `mc.deploy` and nothing else.

The same works at your terminal, so an AI agent can run BunnyCLI without ever seeing the key.

No bunny.net account yet? Sign up at [bunny.net](https://bunny.net?ref=ch5ir79hyc) (affiliate link: Activout gets a commission, you pay the same price).

## Get started

**1. Install `rabbit`** and [BunnyCLI](https://www.npmjs.com/package/@bunny.net/cli), which rabbit runs for you:

```sh
curl -fsSL https://raw.githubusercontent.com/activout/nordray-whiterabbit/main/scripts/install-rabbit.sh | sh
npm i -g @bunny.net/cli
```

**2. Install the agent skill, before anything else.** It teaches Claude Code, Codex, Gemini CLI, Copilot and OpenCode
how to use rabbit: which capabilities a command needs, how to set up bindings and how to move a workflow off its API
key. With it, your agent can do the rest of this list with you.

```sh
rabbit skill install    # rerun after upgrading rabbit
```

**3. Sign in and store your bunny.net key, once:**

```sh
rabbit env add prod https://whiterabbit.example.com   # your White Rabbit server
rabbit auth login                                      # browser + emailed one-time code
rabbit admin key set                                   # hidden prompt; never shown again
```

**4. Run BunnyCLI through rabbit.** Everything after `--` goes to `bunny` unchanged:

```sh
rabbit -- whoami
rabbit -- api GET /pullzone
rabbit --capabilities dns.read -- api GET /dnszone   # limit what one command, or agent, may do
```

**5. Let a CI workflow in.** A binding allows one workflow of one repository a set of capabilities:

```sh
rabbit admin bindings add --issuer github --repository OWNER/REPO --repository-id 123456789 \
  --ref refs/heads/main --workflow .github/workflows/deploy.yml \
  --capability mc.read --capability mc.deploy --description "Deploy"
```

It prints the lines the workflow needs. Replace `bunny …` with `rabbit -- …`, check the run with
`rabbit admin audit --since 1h`, then delete the `BUNNYNET_API_KEY` secret. Examples: [`examples/ci`](examples/ci) and
[Moving a workflow](docs/migrating.md).

All commands and options: [`cli/README.md`](cli/README.md).

## Capabilities

Only these operations are allowed; everything else is refused.

| Area | Operations | Capabilities |
|---|---|---|
| Regions | List regions | (sign-in only) |
| Account | Account details (`bunny whoami`) | `account.read` |
| Statistics | Account statistics | `statistics.read` |
| Pull Zones | List, get, create, update, delete | `pullzone.read`, `pullzone.write` |
| DNS Zones | List, get, create, update, delete | `dns.read`, `dns.write` |
| Shield | List, get, update zones | `shield.read`, `shield.write` |
| Magic Containers | List and get apps, containers, regions, registries | `mc.read` |
| Magic Containers | Change a container's image, deploy, restart | `mc.deploy` |
| Magic Containers | Set a container's environment variables | `mc.env` |
| Magic Containers | Create, replace and delete apps (e.g. preview deployments) | `mc.manage` |
| Bunny Database | List, get and inspect databases | `db.read` |
| Bunny Database | Create and delete databases | `db.manage` |
| Bunny Database | Create a database access token | `db.token` |

- **Secrets stay hidden.** Environment variable values and registry passwords come back as
  `[redacted by White Rabbit]`. `mc.env` can still add, change and remove variables without reading them.
- **`mc.deploy` changes only the image**, nothing else on the container.
- **`mc.manage` and `db.manage` can delete anything** on the account, not just what they created.
- **Not supported:** Edge Storage files, Stream, log forwarding and registry changes.

## Why you can trust it

- **Your key never leaves the server.** It is stored encrypted, injected into requests to `api.bunny.net`, and never
  returned or logged.
- **Deny by default.** Unknown operations get 404, missing capabilities 403.
- **Exact matching for CI.** A job gets in only if issuer, repository, workflow and event match a binding you created.
  Pull request events are never allowed.
- **Short-lived.** Credentials last 5 minutes, CI sessions 1 hour, your own sessions 8 hours.
- **No passwords.** Sign-in is by emailed one-time code, rate limited.
- **Audited.** Every sign-in, credential and proxied call is recorded: `rabbit admin audit`.

## Run your own server

White Rabbit is one container (`ghcr.io/activout/nordray-whiterabbit`), made for
[bunny.net Magic Containers](https://bunny.net/magic-containers/?ref=ch5ir79hyc) (affiliate link) and Bunny Database. See
[Deploying White Rabbit](docs/deploy.md).

## Contributing

Architecture, API, security details and local development: [CONTRIBUTING.md](CONTRIBUTING.md).

## License

MIT. Made by [Nordray](https://nordray.eu) and [Activout](https://activout.se).
