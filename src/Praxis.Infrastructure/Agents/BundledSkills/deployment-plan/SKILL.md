---
name: deployment-plan
description: "Prepare a hosting, database, auth, domain, caching or deployment plan from verified environment details."
---

Identify the current environment, owning service and repository, scope of credentials, and target. Check current official docs for provider-dependent behavior. Cover config, migrations, access controls, validation and rollback in execution order. Never paste credentials or assume production access. If platform tools are missing, deliver a reviewable plan with read-only checks and mark which claims need verification.
