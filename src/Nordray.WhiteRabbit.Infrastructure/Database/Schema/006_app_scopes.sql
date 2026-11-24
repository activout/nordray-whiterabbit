-- Magic Containers apps: a binding may name the apps its mc.* capabilities reach (without any, they stay
-- account-wide), and a pull request binding may give each PR one app, <prefix><PR number>.
ALTER TABLE WorkloadBindings ADD COLUMN Apps TEXT;       -- space-separated exact app names, or NULL
ALTER TABLE WorkloadBindings ADD COLUMN AppPrefix TEXT;  -- e.g. "myapp-pr-", or NULL
ALTER TABLE CliSessions ADD COLUMN Apps TEXT;            -- space-separated apps the session may touch, or NULL
