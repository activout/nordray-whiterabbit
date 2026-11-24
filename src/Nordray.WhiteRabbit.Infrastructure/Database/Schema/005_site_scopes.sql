-- Bunny Sites: a binding names the sites its sites.* capabilities reach, and a session records the sites it may
-- touch. A preview binding (SitePrefix) accepts pull_request jobs; its session reaches only <prefix><PR number>.
ALTER TABLE WorkloadBindings ADD COLUMN Sites TEXT;       -- space-separated exact site names, or NULL
ALTER TABLE WorkloadBindings ADD COLUMN SitePrefix TEXT;  -- e.g. "example-site-pr-", or NULL
ALTER TABLE CliSessions ADD COLUMN Sites TEXT;            -- space-separated; NULL = every site (human sessions)
