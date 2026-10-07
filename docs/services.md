# Service project boundaries

Source: `messaging_backend_api_specification_v1.3_privacy_preferences.docx`, version 1.3, section 1 recommended services. These projects are runnable scaffolds with health checks; specification endpoints, authentication, persistence, event consumers, SignalR hubs and SFU integration are not implemented.

| Project | Responsibility |
| --- | --- |
| `Cyllio.Identity` | Phone verification, account lifecycle, sessions, profiles and contacts. |
| `Cyllio.Device` | Account-bound devices, installations, linked accounts, push registration and revocation. |
| `Cyllio.Messaging` | Conversations, encrypted envelopes, sequencing, receipts, sync and message requests. |
| `Cyllio.Realtime.Gateway` | SignalR/WebSocket connections, presence, typing, delivery and call signaling. |
| `Cyllio.Group` | Membership, roles, invitations, versions and authorized E2EE history transfers. |
| `Cyllio.Media` | Upload authorization, encrypted attachment metadata, retention and thumbnails. |
| `Cyllio.Key` | Public identity keys, signed/one-time prekeys and group key metadata; never plaintext keys. |
| `Cyllio.Call` | Call state, participants, permissions and SFU access tokens. |
| `Cyllio.Notification` | Internal APNs/FCM delivery with account-specific routing and privacy-preserving payloads. |
| `Cyllio.TrustSafety` | Blocks, reports, rate limits, spam signals and administrative review. |

The existing `Cyllio.Api` is retained as the public REST entry project. Reverse-proxy routing and authentication are future implementation work. The realtime project is a gateway host scaffold without a mapped hub yet. The notification project uses an HTTP host for Container Apps probes; push processing will remain internal.

Sections 29–31 extend these boundaries rather than explicitly defining additional services. Account/device isolation spans Identity, Device, Notification and Realtime. Group history grants belong to Group, with Messaging/Media enforcing retrieval boundaries and Key supporting public key distribution. Privacy settings and exceptions can start in Identity, blocks in TrustSafety, with a consistent policy evaluator used across REST, realtime and push. These ownership decisions are scaffolding choices, not implemented policy enforcement.

`services.json` is the shared deployment manifest: project, image name, Container App suffix and ingress visibility. CI builds and smoke tests every listed image. Azure deploy pushes every image before deploying all apps. Only the API and realtime hosts have external ingress; remaining services have environment-internal ingress. Add a project to the solution and this manifest to include it in deployment.

Run any service locally:

```sh
dotnet run --project src/Cyllio.Identity --urls http://localhost:8081
docker build --build-arg SERVICE_PROJECT=Cyllio.Identity -t cyllio-identity:local .
docker run --rm -p 8081:8080 cyllio-identity:local
```

Production now runs at least one replica per listed host. MongoDB, Redis, object storage, a durable broker and LiveKit-compatible SFU remain to be provisioned when their integrations are implemented.
