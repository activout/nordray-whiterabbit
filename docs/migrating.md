# Moving a workflow to White Rabbit

The rabbit agent skill (`rabbit skill install`) knows these steps; you can ask your agent to do the move.

1. Find what the job does with Bunny: BunnyCLI, curl with `AccessKey`, or an action that takes the key. Map each call
   to its capability ([table](../README.md#capabilities)). An operation that is not supported stays where it is.
2. `rabbit admin bindings add` for that repository and workflow with only those capabilities. It prints the lines the
   workflow needs: the OIDC permission (GitHub `id-token: write`, Forgejo `enable-openid-connect: true`),
   `RABBIT_SERVER`, and installing BunnyCLI and a pinned rabbit.
3. Replace `bunny …` with `rabbit -- …` and curl calls with `rabbit -- api METHOD /path`. Run the workflow from the
   bound ref, check `rabbit admin audit --since 1h`, then delete the `BUNNYNET_API_KEY` secret from the repository.

Complete workflows: [`examples/ci`](../examples/ci).

## Deploy a new image

A deploy that only changes the image needs `mc.read` + `mc.deploy`, not `mc.manage`. Find the app, the registry and
the container, then patch only the image fields:

```sh
APP_ID=$(rabbit -- api GET /mc/apps --output json | jq -r --arg n "$APP_NAME" '.items[] | select(.name == $n) | .id')
REGISTRY_ID=$(rabbit -- api GET /mc/registries --output json | jq -r '.items[] | select(.hostName == "codebahn.net" and .isPublic == false) | .id')
CONTAINER_ID=$(rabbit -- api GET "/mc/apps/$APP_ID" --output json | jq -r '.containerTemplates[] | select(.name == "web") | .id')
rabbit -- api PATCH "/mc/apps/$APP_ID/containers/$CONTAINER_ID" --output json \
  --body "$(jq -n --arg tag "$TAG" --arg r "$REGISTRY_ID" '{imageTag: $tag, imageRegistryId: $r, imageNamespace: "acme", imageName: "app"}')"
```

A workflow that creates or replaces the whole app (`POST /mc/apps`, `PUT /mc/apps/{id}`) needs `mc.manage`, which
also lets it delete any app. When it sends environment values from CI secrets, prefer creating the app and its
environment once by hand and patching only the image.

## A short-lived database token

For example for a script that writes to a Bunny Database. Needs `db.read` + `db.token`:

```sh
echo "BUNNY_DATABASE_URL=$BUNNY_DATABASE_URL" > .env   # or pass the db_… ID as an argument
BUNNY_DATABASE_AUTH_TOKEN=$(rabbit -- db tokens create --expiry 1h --no-save --force --output json | jq -r .token)
echo "::add-mask::$BUNNY_DATABASE_AUTH_TOKEN"
export BUNNY_DATABASE_AUTH_TOKEN
```

- Always pass `--expiry`: without it the token is full access and never expires, so every run leaves one behind.
- rabbit removes `BUNNY_*` variables before it starts BunnyCLI, and BunnyCLI finds the database from a `.env` file
  (matched exactly against the URLs Bunny lists), not from the environment.
- With `--output json` only the JSON (`token`, `expires_at`, `db_id`, `authorization`) goes to stdout.
- `rabbit -- db migrations apply --force` without `--token` creates its own 30-minute token, with the same capabilities.
