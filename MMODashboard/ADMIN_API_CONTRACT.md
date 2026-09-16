# MMO Dashboard remote admin document contract (future server endpoint)

The dashboard UI is local-first, but all authoring now goes through `IServerAdminWorkspace`.
`LocalServerAdminWorkspace` edits the canonical files directly. `HttpServerAdminWorkspace`
implements the same calls for a future authenticated server endpoint.

## Endpoints

- `GET /v1/admin/documents/runtime-config`
- `PUT /v1/admin/documents/runtime-config`
- `GET /v1/admin/documents/gateway-settings`
- `PUT /v1/admin/documents/gateway-settings`
- `GET /v1/admin/documents/gameplay-content`
- `PUT /v1/admin/documents/gameplay-content`

Authentication is expected to use `Authorization: Bearer <admin-token>` over HTTPS.
This API must never be exposed without strong authentication/authorization.

### GET response

```json
{
  "displayPath": "Content/GameplayContent.json",
  "content": "{ ... full canonical document ... }",
  "version": "opaque-concurrency-token"
}
```

### PUT request

```json
{
  "content": "{ ... full canonical document ... }",
  "expectedVersion": "opaque-concurrency-token-from-last-read"
}
```

### PUT response

Return the same shape as GET with the newly stored version.

The server should reject stale `expectedVersion` values (HTTP 409 is recommended), validate
content before replacing canonical data, write atomically, and retain a last-known-good/backup.
The dashboard already handles the same optimistic-concurrency behavior for local files.
