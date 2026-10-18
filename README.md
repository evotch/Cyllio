# Cyllio

.NET 10 messaging backend using C# Minimal APIs, with one shared solution and AWS infrastructure managed with Terraform. The first account foundation is implemented; see [implementation progress](docs/implementation.md) for endpoints, setup, tests and remaining work. See [service boundaries](docs/services.md) for the ten specification services.

## Projects

All twelve projects belong to `Cyllio.sln`. The eleven service projects are independently runnable hosts with health endpoints. `Cyllio.Common` is a shared class library. The table describes their intended responsibilities. Identity and Device now implement the first account APIs; most other business APIs remain scaffolds.

| Project | Purpose |
| --- | --- |
| `Cyllio.Common` | Shared classes, DTOs and event contracts used by all service projects. Contains the Service Bus entity names and infrastructure probe event contract; it has no container or HTTP endpoints. |
| `Cyllio.Api` | Public REST entry point for mobile clients. Routes authentication, own-profile/privacy and device requests to their backend services. |
| `Cyllio.Identity` | Phone-number verification, registration and login, sessions, refresh tokens, account lifecycle, profiles, contacts and account privacy settings. |
| `Cyllio.Device` | Device registration, linked devices, account associations on shared installations, push tokens and device revocation. |
| `Cyllio.Messaging` | Conversations, encrypted message storage, message sequencing, edits and deletion, delivery/read receipts, offline synchronization and message requests. |
| `Cyllio.Realtime.Gateway` | SignalR/WebSocket connections for presence, typing indicators, message delivery events and call signaling. |
| `Cyllio.Group` | Group membership, roles, invitations, settings, membership versions and permissions for sharing encrypted message history with new members. |
| `Cyllio.Media` | Upload authorization, encrypted attachment lifecycle, media metadata, retention and thumbnails. |
| `Cyllio.Key` | Distribution of public identity keys, signed prekeys, one-time prekeys and group sender-key metadata. Must never receive plaintext message keys. |
| `Cyllio.Call` | Voice/video call state, participants, call permissions and access tokens for the WebRTC media server (SFU). |
| `Cyllio.Notification` | Internal Apple Push Notification service (APNs) and Firebase Cloud Messaging (FCM) delivery, with account-specific routing and privacy-preserving payloads. |
| `Cyllio.TrustSafety` | User blocks, abuse reports, rate limits, spam detection signals and administrative review. |

Shared code lives under `src/Cyllio.Common` and is available through project references in every service. Keep service-specific business logic in its owning project. The common library is compiled into each service image, so it needs no entry in `services.json` or Compose.

## Local development

```sh
dotnet restore Cyllio.sln
dotnet build Cyllio.sln -c Release
dotnet run --project src/Cyllio.Api --urls http://localhost:8080
```

The standalone `dotnet run` command starts the gateway only; account APIs need Identity, Device and MongoDB running with the configuration described in [implementation progress](docs/implementation.md).

Endpoints: `/`, `/health/live`, `/health/ready`. Identity and Device readiness checks include MongoDB availability. Other hosts currently check process health only.

```sh
docker compose up --build -d api identity device
python3 scripts/test-accounts.py
```

## Run all services with Docker Compose

With Docker running, configure the local Service Bus emulator before starting the stack. Use your private `.env.local` file with `docker compose --env-file .env.local`, set a strong `MSSQL_SA_PASSWORD` and independent `AUTH_SIGNING_KEY` / `AUTH_PHONE_HASH_KEY` secrets of at least 32 bytes, and set `ACCEPT_EULA=Y` after accepting the [Service Bus emulator license](https://github.com/Azure/azure-service-bus-emulator-installer/blob/main/EMULATOR_EULA.txt) and [SQL Server Linux license](https://go.microsoft.com/fwlink/?LinkId=746388). `.env` is ignored by Git and excluded from Docker builds.

Start all eleven projects, MongoDB and the two emulator containers from the repository root:

```sh
docker compose up --build -d
docker compose ps
docker compose logs -f
```

Each service uses the shared Dockerfile with its own `SERVICE_PROJECT` build argument. Compose connects them on its default network; containers can reach each other at `http://<service-name>:8080`, for example `http://identity:8080`. Local ports bind to `127.0.0.1`.

| Service | Local URL |
| --- | --- |
| `api` | http://localhost:8080 |
| `identity` | http://localhost:8081 |
| `device` | http://localhost:8082 |
| `messaging` | http://localhost:8083 |
| `realtime` | http://localhost:8084 |
| `group` | http://localhost:8085 |
| `media` | http://localhost:8086 |
| `key` | http://localhost:8087 |
| `call` | http://localhost:8088 |
| `notification` | http://localhost:8089 |
| `trust-safety` | http://localhost:8090 |

Each URL exposes `/`, `/health/live` and `/health/ready`. For example:

```sh
curl --fail http://localhost:8080/health/ready
curl --fail http://localhost:8081/health/ready
```

Stop and remove the containers and network:

```sh
docker compose down
```

To rebuild one service after editing its code, run `docker compose up --build -d identity` (replace `identity` with its Compose service name). If a local port is already in use, change that service's host port in `compose.yaml`.

### Local Service Bus

Compose includes the Azure Service Bus emulator and its SQL Server dependency. SQL Server is used only by the emulator. They run as `linux/amd64` containers, so Apple Silicon uses Docker's architecture emulation.

The emulator configuration in `docker/servicebus/config.json` creates the `message-events` topic and independent `notification` and `realtime` subscriptions. Messaging publishes a development-only `InfrastructureProbe`; Notification and Realtime consume and acknowledge their own copies. Failed processing leaves messages available for redelivery; unsupported event types are dead-lettered rather than silently acknowledged.

```sh
bash scripts/test-servicebus.sh
# Or publish manually and inspect the consumers:
curl --fail -X POST http://localhost:8083/dev/servicebus/probe
docker compose logs notification realtime
```

The probe endpoint returns an event ID after Service Bus accepts the event. Both consumer logs should show that ID. It accepts no message content and is available only in Development with a configured broker. It does not persist a chat message or send a real push/SignalR event.

Emulator health: `http://localhost:5300/health`. AMQP: `localhost:5672`. Compose supplies `ServiceBus__ConnectionString` to Messaging, Notification and Realtime; when running a project directly, use:

```sh
ServiceBus__ConnectionString='Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;' ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Cyllio.Messaging
```

Without a connection string, these hosts continue to run with broker integration disabled. The AWS deployment provisions SNS/SQS, but the current Service Bus application adapter is still used only by the existing local stack. This emulator is for development, not production durability; do not rely on message retention across emulator restarts. SQL data uses a named volume, and `docker compose down` retains it.

Identity and Device implement the initial authenticated account APIs. Message persistence, transactional outbox/inbox handling, business event handlers, push delivery and realtime hubs remain to be implemented. Run `python3 scripts/test-accounts.py` to verify the account slice; see [implementation progress](docs/implementation.md) for details. See [Microsoft's emulator documentation](https://learn.microsoft.com/en-us/azure/service-bus-messaging/test-locally-with-service-bus-emulator).

## AWS deployment

See [deployment setup](docs/deployment.md) for Terraform, AWS OIDC, staging/production state, and the application migration boundary.

- `Cyllio.sln`: all service projects belong here.
- `src`: API entry host and ten independent service projects.
- `Dockerfile`: shared non-root service image build.
- `compose.yaml`: local Docker setup for all eleven projects.
- `services.json`: shared manifest for CI, ECR repositories, and ECS services.
- `infra/bootstrap`: separate protected staging/production state buckets and optional GitHub OIDC roles.
- `infra/aws`: VPC, private ECS Fargate, ECR, API Gateway HTTP API, internal ALB, DynamoDB, S3, SNS/SQS and optional Valkey cache.
- `infra/aws/environments`: staging and production Terraform inputs.
- `infra/legacy-azure`: historical Bicep templates retained for reference.
- `.github/workflows/ci.yml`: build, formatting, Terraform validation and mocked infrastructure tests, container smoke test.
- `.github/workflows/deploy.yml`: automatic staging Terraform deployment on main pushes; manual staging/production plan or apply.

To add a service, create its project under `src`, add it with `dotnet sln Cyllio.sln add ...`, add it to `services.json`, and build it with the shared Dockerfile.
