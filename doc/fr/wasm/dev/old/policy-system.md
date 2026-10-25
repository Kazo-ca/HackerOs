# Capability Policy System

## Purpose

Provide deny-by-default, exact capability grants and structured resource
constraints without wildcard permissions or app-supplied authority.

## Implemented grant contracts

`CapabilityGrant` is immutable and keyed by:

- opaque grant ID;
- exact app ID and user ID;
- one exact known capability;
- positive policy revision; and
- trusted source: build profile, user approval, Administrator approval, or
  explicit System policy.

Constraints use closed typed records rather than string dictionaries:

- `VirtualPathCapabilityConstraint` allows one exact path or segment-boundary
  subtree;
- `NetworkHostCapabilityConstraint` allows one normalized exact DNS name or IP
  address and rejects wildcards; and
- `NetworkPortCapabilityConstraint` allows one inclusive range from 1 to 65535.

A grant may contain at most one constraint of each kind. Constraint collections
are copied before storage. Empty IDs, unknown capabilities, wildcard capability
names, duplicate constraint kinds, wildcard hosts, and invalid port ranges are
rejected at construction.

## Policy evaluation

`CapabilityPolicyEvaluation` represents every policy decision with:

- a granted flag;
- a closed stable reason;
- the evaluated policy revision; and
- the matching grant ID when a grant participated in the decision.

The default value denies with the `Missing` reason. Explicit factories produce
only coherent states: `Permit` requires a matching grant, `DenyMissing` records
the current policy revision without a grant, and grant-based denial accepts only
`Revoked`, `Constrained`, or `AuthorityDenied`. A matching grant therefore does
not imply access when resource constraints or acting authority fail.

## Security boundary

Grants are created by trusted policy code. App manifests request known exact
capabilities but do not create grants, choose authority, or supply trusted
constraints. Client policy remains an architecture boundary for reviewed apps,
not a sandbox against malicious managed assemblies.

## Grant repository and audit

`CapabilityGrantRepository` (`HackerOs.Platform.Core.Policy`) stores active and
revoked grants in memory, evaluates `CapabilityPolicyEvaluation` against a
candidate resource, and records a chronological `CapabilityGrantAuditEntry` log.

- `Grant`/`Revoke` both require Administrator or System acting authority; a
  lower authority returns `AuthorityDenied` without mutating state.
- `Evaluate` matches on exact app ID, user ID, and capability; a resource
  candidate (`VirtualPathResourceCandidate`, `NetworkHostResourceCandidate`,
  `NetworkPortResourceCandidate`) is checked against the grant's structured
  constraints before returning `Granted`. A matching grant whose constraints
  reject the candidate returns `Constrained`; a revoked matching grant returns
  `Revoked`; no matching grant at all returns `Missing`.
- Every mutation bumps a monotonic policy revision returned with the result.
- Re-granting the same app/user/capability tuple with constraints that allow
  everything a prior active grant allowed (and more) is recorded as `Expanded`
  rather than a plain `Granted`, and the audit entry records which action
  occurred, the affected grant, the resulting policy revision, and the acting
  authority.

## Settings scopes and protected policy document

`SettingsScope` (`HackerOs.Simulation.Abstractions.Settings`) defines the four
ADR 0011 scopes: `AppUser`, `AppDevice`, `AppRoamingUser`, and `OsAdmin`.
`SettingsDocumentKey` is the structured repository identity for a canonical
document; `SettingsDocumentPathFactory` derives its deterministic virtual
filesystem projection path. `SettingsScopePolicy.Authorize` enforces:

- a scope must be declared by the app's manifest before it can be used at all;
- `AppUser`/`AppDevice` need only that declaration;
- `AppRoamingUser` additionally requires a granted roaming sync-eligibility
  capability; and
- `OsAdmin` additionally requires the system-settings capability and
  Administrator/System effective authority.

App kind alone never elevates scope, and a manifest-declared "system" app
launched by a normal user keeps User authority unless the platform explicitly
marks the operation `IsSystemOperation`.

Capability grant and other OS policy changes are themselves recorded as an
ordinary protected canonical settings document,
`PolicySettingsDocuments` (`/etc/hackeros/policy.config`), requiring
Administrator write authority and `settings.system.write`. This reuses the
existing settings revision, validation, and audit path rather than adding a
parallel storage mechanism.

## Capability catalog and manifest compatibility

`AppCapabilities` (`HackerOs.App.Abstractions`) also defines process
(`process.list`, `process.manage`), notification (`notifications.post`),
window (`windows.manage`), clipboard (`clipboard.read`, `clipboard.write`), and
service (`services.manage`) capabilities alongside the existing filesystem,
settings, dialog, and file-association set. `AppManifestValidator` rejects any
unknown capability and, separately, rejects the window-only dialog
capabilities (`dialogs.file-open`, `dialogs.file-save`,
`dialogs.folder-select`) when declared by a non-`Window` app, since only a
window-hosting app can own a modal dialog.

## Clean-profile default grants

`CleanProfileCapabilityGrantSeeder` (`HackerOs.Platform.Core.Policy`) seeds a
new profile by granting exactly the capabilities one validated manifest
declares, sourced as `CapabilityGrantSource.BuildProfile`. Seeding never widens
access beyond the manifest: an undeclared capability remains `Missing` on
evaluation even when the acting authority is System, proving System authority
never substitutes for an exact capability grant.

### `.config` schema and format

`SettingsSchema` declares typed fields (`String`, `Integer`, `Boolean`, `Enum`),
default values, optional `[Group]` membership, and a sensitivity class
(`Public`, `Private`, `SecretReference`, `Restricted`). `ConfigDocumentFormat`
parses and serializes the ADR 0011 `.config` grammar: `#` comments,
`[GroupName]` sections, `key=value` pairs, and `\#`, `\=`, `\\`, `\n`, `\r`,
`\t` value escapes. `SchemaConfigSettingsDocumentValidator` combines both to
implement `ISettingsDocumentValidator` for schema-driven documents.




## Exclusions

- Wildcard capabilities or hosts.
- UI permission prompts.
- Server authorization.
- Malicious-code isolation.

## Task list

- [x] Define immutable exact grants and structured path/host/port constraints.
- [x] Define deny-by-default evaluation and stable reasons.
- [x] Define policy changes as a protected revisioned settings document under
  `/etc/hackeros/` requiring Administrator/System write authority.
- [x] Implement the in-memory grant repository with revocation, update-expansion
  detection, and audit records.
- [x] Define app/user, app/device, roaming-user, and OS/admin settings document
  keys and a deterministic projection path factory.
- [x] Define which settings scopes a manifest may request and which trusted
  policies may grant them; app kind alone never elevates scope.
- [x] Implement schema-driven setting declarations, defaults, sensitivity, and
  the `.config` parser/serializer.
- [x] Prove a system app operated by a User does not gain System authority
  without an explicit audited system context.
- [x] Add tests for scope isolation, exact matching, revocation, constrained
  resources, privilege boundaries, expansion, audit, and revision conflict.
- [x] Define clean-profile default grants per app/user/policy (`P1-CAP-002`).
- [ ] Audit Phase 2 manifests for missing capabilities (`P1-CAP-001`) — blocked
  until Phase 2 app manifests exist; the capability catalog itself is current.
- [x] Reject unknown/incompatible capabilities in manifest validation
  (`P1-CAP-003`).