---
name: rabbit
description: Run bunny.net's BunnyCLI through a Nordray White Rabbit server with the `rabbit` CLI instead of holding the bunny.net API key, and manage CI workload bindings (`rabbit admin bindings`). Use when the user works with bunny.net through White Rabbit or rabbit, signs in to a White Rabbit environment, asks which capabilities a command needs, sets up a GitHub Actions or Forgejo Actions job to use rabbit, moves an existing workflow off its bunny.net API key, deploys a White Rabbit server on bunny.net, or adds, lists or removes a binding.
---

# rabbit

`rabbit -- <bunny arguments>` runs the official BunnyCLI (`bunny`) through a White Rabbit server. Neither the agent
nor a CI job ever holds the real bunny.net API key. White Rabbit allows only the operations in its registry, and only
with the capabilities of the credential (`--capabilities`) or CI binding. Details: `cli/README.md` in github.com/activout/nordray-whiterabbit.
This file comes with rabbit: `rabbit skill install` updates it to match the installed version.

## Rules for the agent

- **Use `rabbit -- …`, not `bunny …`**, whenever the user works through White Rabbit. Never ask for, read or pass
  the bunny.net API key, and never use `--api-key` (rabbit refuses it).
- **Sign-in is the user's.** `rabbit auth login` opens a browser for an emailed one-time code. Suggest the exact
  command with the `!` prefix so the user runs it. The session may use every capability, so run BunnyCLI with
  `--capabilities` (or `RABBIT_CAPABILITIES`) set to the fewest the task needs.
- **The bunny.net API key is the user's too.** Only the user runs `rabbit admin key set`; it reads the key from a
  hidden prompt or stdin. Never put the key in a command, file or message.
- **Bindings change who can act on the user's Bunny account.** Show the full `rabbit admin bindings add` or `rm`
  command and get the user's confirmation before running it. Admin commands work only in an interactive session.
- **Never print a credential.** `RABBIT_DEBUG=1` logs only method, path and status. It is safe to use.
- **Check the environment first:** `rabbit auth status`. Interactive commands print the server they use. With
  several environments and none selected, rabbit refuses to guess: use `--env NAME`.

## Everyday use

```sh
rabbit env add test https://whiterabbit.example.com   # once per server; URLs only, never credentials
rabbit env use test
! rabbit auth login                                    # the user runs this: browser + emailed code, session for 8 h
! rabbit admin key set                                 # the user runs this once: stores their bunny.net API key
rabbit auth whoami                                     # who, which server, whether a key is stored, until when
rabbit --capabilities mc.read -- apps list             # everything after -- goes to BunnyCLI unchanged
rabbit auth logout
```

There are no refresh tokens: sign in again when the session expires.

## Capabilities

| Capability | Allows | Typical commands |
|---|---|---|
| (sign-in only) | List regions | `rabbit -- api GET /region` |
| `account.read` | Account ID, owner, billing and address | `bunny whoami`, `bunny apps deploy` (account ID = registry namespace) |
| `statistics.read` | Account traffic and billing statistics | `api GET /statistics` |
| `pullzone.read` / `pullzone.write` | Read / create, change, delete pull zones | `api GET /pullzone` |
| `dns.read` / `dns.write` | Read / create, change, delete DNS zones | `api GET /dnszone` |
| `shield.read` / `shield.write` | Read / change Shield zones | |
| `mc.read` | Read apps, containers, regions, registries. Environment values are **redacted** | `apps list`, `apps show` |
| `mc.deploy` | Change a container's image, deploy, restart | the deploy step of a pipeline |
| `mc.env` | Replace environment variables (current values are never shown) | `apps env set/remove` |
| `mc.manage` | Create, replace (`PUT /mc/apps/{id}`) and delete apps | preview deployments, a deploy that sends the whole app |
| `db.read` | Read databases, limits, regions, status | `db list`, `db show` |
| `db.manage` | Create and delete databases | |
| `db.token` | Create database tokens | `db tokens create`, `db shell` |
| `sites.read` | Read your Bunny Sites, their zones, deploys and files | `sites show`, `sites list`, `sites deployments list` |
| `sites.deploy` | Upload and delete site files, publish a deploy, purge | `sites deploy`, `sites deployments prune/publish` |
| `sites.manage` | Create and delete sites | `sites create`, `sites delete` (per-PR previews) |

Every capability applies to the whole account (`mc.deploy` can change any app), except `sites.*`: a binding's
`sites.*` reach only the sites it names with `--site`, or one preview site per PR with `--site-prefix`. A human
session reaches every site. A site's zones are `sites-<site>-<suffix>`; a site imported with `--from-zone` keeps
other names and is out of reach.

Not possible through rabbit:
- `storage files` on a zone that is not a Bunny Site's (it needs the zone password);
- `sites domains` and DNS for a site (they need `pullzone.write` and `dns.write` on the whole account);
- reading environment values (shown as `[redacted by White Rabbit]`);
- log forwarding;
- any operation missing from the registry.

## When something fails

| Symptom | Meaning |
|---|---|
| `✖ Not found.` / 404 | The operation is not in White Rabbit's registry. Run with `RABBIT_DEBUG=1` to see the path, then report it; don't work around it with the real key |
| 403 `capability_not_granted` (`required: X`) | Add `X` to `--capabilities`, or to the CI binding |
| No session / expired | `! rabbit auth login` again |
| 403 `bunny_api_key_not_configured` | The user stores their key: `! rabbit admin key set` |
| CI: 401 `no_matching_binding` | No binding matches the job's repository, ref, workflow or event |
| CI: 401 `event_not_allowed` | A `pull_request` job without a preview binding (`--site-prefix`), or from another workflow |
| 404 on a site, zone or site file | Outside the session's sites: check the binding's `--site` / `--site-prefix` |

`rabbit admin audit --since 1h` (interactive session only) shows what was allowed or denied, with the reason, for the
user and their CI bindings. A CI exchange denied as `no_matching_binding` has no owner yet, so it is not listed.

BunnyCLI 0.22.0 drops the `/mc` and `/database` prefixes under `BUNNYNET_API_URL`. rabbit's forwarder restores them,
so this is not an error, but `bunny api` calls must include the prefix (`api GET /mc/apps`).

## CI: GitHub Actions and Forgejo Actions

A CI job does not request capabilities. It gets exactly those of the binding that matches it.

1. The job needs an OIDC token:
   - GitHub: `permissions: id-token: write`;
   - Forgejo: `enable-openid-connect: true` on the job (`permissions` has no effect).
2. Set `RABBIT_SERVER` to the server URL. The token audience comes from the server.
3. Install BunnyCLI and a pinned rabbit (`scripts/install-rabbit.sh <version>`), then run `rabbit -- …`.

Complete workflows: `examples/ci/github-actions.yml` and `examples/ci/forgejo-actions.yml`. No secret goes into the repository.
`rabbit admin bindings add` prints these lines for the new binding's workflow; copy them rather than writing your own.

### Moving an existing workflow to rabbit

1. Read the workflow and list every Bunny call: `bunny …`, curl or other HTTP with `AccessKey`, actions that take
   the key (e.g. `BunnyWay/actions/*`). Map each to a capability from the table. If one has no capability (e.g.
   `storage files` on a non-site zone, or a 404 with `RABBIT_DEBUG=1`), tell the user and leave that step on its key; don't widen anything.
2. Propose one binding with exactly those capabilities and the workflow's ref and trigger events; get the user's
   confirmation, then run `rabbit admin bindings add`.
3. Edit the workflow:
   - add the OIDC permission and `RABBIT_SERVER` from the printed lines, and install BunnyCLI and a pinned rabbit;
   - `bunny X` becomes `rabbit -- X`; curl calls become `rabbit -- api METHOD /path` (with `/mc` or `/database`);
   - remove `BUNNYNET_API_KEY` / `--api-key` / `AccessKey` from the workflow.
4. Open the change as a PR. It runs only from the bound ref, so the user merges and runs it, then checks
   `rabbit admin audit --since 1h`. Only after a green run does the user delete the repository's Bunny key secret.

Things that trip up a move (the README's "Moving a workflow to White Rabbit" has the full recipes):
- **Bunny Sites.** Replace `BunnyWay/actions/deploy-site` (it takes the key) with
  `rabbit -- sites deploy DIR --site NAME --force --output json` (`.production` is the URL, `.id` the deploy ID).
  Production: a binding with `--site NAME` and `sites.read` + `sites.deploy`. Previews and their cleanup: one
  binding per workflow with `--site-prefix NAME-pr-`; previews need `sites.manage` to create, cleanup to delete.
- **Deploys.** A "create or update the app" step (`POST /mc/apps`, `PUT /mc/apps/{id}` with the whole app, often with
  environment values from CI secrets) needs `mc.manage`. Usually better: `mc.read` + `mc.deploy`, and patch only the
  image (`PATCH /mc/apps/{id}/containers/{cid}` with `imageTag`, `imageRegistryId`, `imageNamespace`, `imageName`);
  the app and its environment are set once by hand. Say which you chose and why.
- **Database tokens.** A step that uses a stored database token can mint one per run with `db.read` + `db.token`:
  `rabbit -- db tokens create --expiry 1h --no-save --force --output json | jq -r .token`, then `::add-mask::` it.
  Always pass `--expiry` (the default never expires). The database comes from the `db_…` ID or from
  `BUNNY_DATABASE_URL` in a `.env` file; rabbit removes `BUNNY_*` environment variables before BunnyCLI starts.
  `rabbit -- db migrations apply --force` without `--token` mints its own 30-minute token.
- **The workflow path.** `--workflow` is the path the forge runs: Codebahn and other Forgejo servers run
  `.github/workflows/` when there is no `.forgejo/workflows/`.
- **Tags.** A deploy on `v*` tags needs a binding without `--ref`; the repository's tag protection then decides.

## Bindings

A binding lets matching CI jobs act **on behalf of the user who created it**, with that user's stored Bunny key and
only the listed capabilities.

```sh
rabbit admin bindings list

# GitHub: the numeric IDs make the binding survive renames. Get them with
#   gh api repos/OWNER/REPO --jq '.id, .owner.id'
rabbit admin bindings add --issuer github --repository OWNER/REPO \
  --repository-id 123456789 --repository-owner-id 987654 \
  --ref refs/heads/main --workflow .github/workflows/deploy.yml \
  --capability mc.read --capability mc.deploy --description "Deploy to test"

# Forgejo: matched by name (Forgejo tokens carry no repository ID).
rabbit admin bindings add --issuer codebahn --repository OWNER/REPO \
  --ref refs/heads/main --workflow .forgejo/workflows/deploy.yml \
  --capability mc.read --capability mc.deploy --description "Deploy to test"

rabbit admin bindings rm 3
```

- `--issuer` is a trusted issuer name configured on the server (e.g. `github`, `codebahn`).
- `--ref` is optional and exact (`refs/heads/…` or `refs/tags/…`).
  - With it, only that branch or tag matches.
  - Without it, the workflow matches from any branch or tag, e.g. for release tags.
  - **Before leaving it out**, ask the user how the repository protects the refs that can run the workflow: who may
    push branches and tags, rulesets, or on GitHub a protected environment (add `--environment`). Anyone who can push
    any branch can otherwise change the workflow there and use the binding.
- `--workflow` takes the workflow file path. The binding stores `<repository>/<path>@<ref>`, and the job's
  `workflow_ref` must match it exactly.
- `--event` defaults to `push` and `workflow_dispatch`. `pull_request` is refused except on a preview binding.
- `--site NAME` (repeatable) names the Bunny Sites the binding's `sites.*` capabilities reach; required with `sites.*`.
- `--site-prefix PREFIX` makes a preview binding: only `pull_request` jobs (the default event then), no `--ref`, only
  `sites.*`, and each job reaches exactly the site `<prefix><PR number>`. Fork PRs get no OIDC token at all.
- GitHub only: `--environment` and `--job-workflow-ref` narrow the binding further.
- **There is no edit.** To change a binding, `rm` the old one and add the new one; two bindings that both match a job
  are refused as ambiguous.

Good bindings:
- one per repository and workflow, with a ref unless the workflow must run from several refs;
- only the capabilities that workflow uses, typically `mc.read` + `mc.deploy` for a container deploy;
- `mc.manage`, `db.manage` and `db.token` only when the workflow really creates or deletes things or issues tokens;
- removed when the workflow is retired.

## Deploying a White Rabbit server on bunny.net

Follow "Deploy on bunny.net, step by step" in the repository README (github.com/activout/nordray-whiterabbit). In
short: a Bunny Database and its token, an encryption key, a Magic Containers app with one instance running a pinned
digest of `ghcr.io/activout/nordray-whiterabbit` on port 8080, the `Federation__*` settings for the CI issuers, a
hostname with HTTPS, then `/ready` returns 200.

- This is the one step that needs the bunny.net API key itself, so the user runs BunnyCLI with their own key. Prefer
  the SE region unless the user says otherwise.
- Secrets (encryption key, database token, Mailjet keys) never go into a command line, a file in the repository or
  a message: write them through files in `mktemp -d` and post them with curl and a header file, or use the user's
  secrets loader.
- Remind the user to store the encryption key safely: without it the stored bunny.net keys are lost.
