# Specification implementation progress

Source: `messaging_backend_api_specification_v1.3_privacy_preferences.docx`, version 1.3. All business endpoints use C# Minimal APIs under `/api/v1`. Work starts with the account foundation; this is not yet a complete backend v1.

## Implemented first slice

| Area | Implemented behavior |
| --- | --- |
| Authentication (section 2) | Start and confirm phone verification through AWS End User Messaging SMS or an explicitly enabled local OTP provider; single-use registration/login proof; register an account with initial public device keys; OTP login to an existing account-bound device; 10-minute signed access tokens; hashed 30-day refresh tokens with atomic rotation; logout and logout-all. |
| Profile (section 3) | Read and update the caller's own display name, about and avatar reference; ETag/If-Match concurrency. |
| Devices (sections 4, 29) | List the caller's active devices; update the current device's push capability; revoke an owned device and its sessions immediately. Separate accounts on a shared installation remain isolated. |
| Privacy (section 31) | Persist all account preference fields from section 31 with value validation and ETag/If-Match concurrency. This stores policy; communication enforcement awaits the corresponding APIs. |
| Persistence (section 19) | DynamoDB Local in development and the AWS DynamoDB entity table in staging/production; consistent key lookups, unique phone reservations, TTL and conditional transactions for proof consumption, registration and refresh. |
| API entry | YARP routes auth/profile/privacy to Identity and device APIs to Device. Every backend performs JWT and live account/session/device validation. Development OTP endpoints are not forwarded. |

Endpoints:

```text
POST   /api/v1/auth/verification/start
POST   /api/v1/auth/verification/confirm
POST   /api/v1/auth/register
POST   /api/v1/auth/login
POST   /api/v1/auth/refresh
POST   /api/v1/auth/logout
POST   /api/v1/auth/logout-all
GET    /api/v1/users/me
PATCH  /api/v1/users/me
GET    /api/v1/privacy/settings
PUT    /api/v1/privacy/settings
GET    /api/v1/devices
PUT    /api/v1/devices/{deviceId}/push-token
DELETE /api/v1/devices/{deviceId}
```

The request/response contracts live in `Cyllio.Common/Accounts`. Registration takes `displayName`, device `name`, `identityKey`, `signedPreKey` and optional `push`; its short-lived verification token is supplied as `Authorization: Bearer <registrationToken>`. Confirmation binds the proof to `device.installationId` and `device.platform`. Registration returns an account access/refresh token pair only after those public key fields are stored. Login takes `deviceId` and a fresh verification proof bound to the same installation; it cannot add an unknown device.

Public keys and signature lengths are checked, but cryptographic signature verification, private-key possession proofs and audited protocol integration remain pending. The test uses random bytes as public key fixtures and makes no E2EE claim.

## Run and verify

Set independent `AUTH_SIGNING_KEY` and `AUTH_PHONE_HASH_KEY` secrets (at least 32 bytes each) in `.env.local`; generate each with `openssl rand -hex 48`. Existing local `.env` files from earlier setup need these new entries. Both Identity and Device use them. Keep the phone lookup secret stable; changing it requires a planned lookup migration. The local setup stores HMAC phone lookup hashes, not raw phone numbers.

```sh
docker compose --env-file .env.local up --build -d api identity device
COMPOSE_ENV_FILES=.env.local python3 scripts/test-accounts.py
```

DynamoDB Local is exposed on `127.0.0.1:8000` with a persistent `dynamodb-data` volume. Identity and Device create the local table automatically, tolerating concurrent startup. For direct local .NET execution set `DynamoDb__ServiceUrl=http://localhost:8000`, `DynamoDb__TableName=cyllio-local-entities`, both `Auth__...` secrets and `ASPNETCORE_ENVIRONMENT=Development`. Identity also needs `Auth__EnableDevelopmentOtp=true` for test OTPs. Compose sets the database endpoint and table automatically; `DYNAMODB_TABLE_NAME` can override the local table name. Local database credentials are dummy values used only inside the database client, not SMS credentials. Existing MongoDB volumes are preserved but their data is not imported.

A local verification example:

```sh
curl -X POST http://localhost:8080/api/v1/auth/verification/start \
  -H 'Content-Type: application/json' \
  -d '{"phoneNumber":"+12065550123","channel":"sms","locale":"en-US"}'
# Read the code directly from Identity, using verificationId from the response:
curl http://localhost:8081/dev/otp/<verificationId>
```

Confirmation returns a registration token, not an account token. Verification expires after five minutes, accepts at most five attempts, and has a per-phone cooldown plus per-process IP throttling. Codes are never logged or stored as plaintext in DynamoDB. The development provider stores codes in expiring process memory and is exposed only when both Development and the explicit setting are enabled. Real SMS uses AWS End User Messaging SMS with region, configuration set and approved `Auth:Sms:OriginationIdentities:US` / `:BG` ARNs. Without a configured identity, production verification start returns 503. Voice delivery is not implemented. AWS SMS configuration, including partial configuration, disables development OTP retrieval. See [deployment setup](deployment.md#aws-otp-sms-and-dedicated-short-codes-us-and-bulgaria).

GET profile/privacy responses include `ETag`. Supply that value in `If-Match` when updating. Missing versions return 428; stale versions return 412. Ownership always comes from the signed token, never a body-supplied user or installation ID. Device lists omit public keys, push tokens and refresh-token hashes.

The integration script verifies registration proof replay protection, unauthorized requests, two accounts on one installation, cross-account device mutation denial, privacy and profile isolation, stale/concurrent version rejection, single-use refresh rotation, login proof isolation, persistence across service restarts, logout-all and immediate device revocation. It restarts DynamoDB Local, Identity and Device to check disk persistence and creates test accounts in the local database and does not remove the database or its volume. CI runs the same script.

## Persistence boundary

For this first slice Identity and Device share the account database and one account aggregate with embedded device and session metadata. Public cryptographic prekey inventory is not implemented here. Keeping lifecycle state together provides atomic session revocation and refresh behavior; this is an initial implementation choice, not a final separate-database-per-service design. Login prunes expired/revoked sessions and bounds the account to 100 active sessions; trusted-device linking is follow-up work.

DynamoDB uses `PK` plus constant `SK=DATA`. Account aggregates use `ACCOUNT#<id>`; unique `PHONE#<hash>` reservations map to accounts. `REFRESH#<hash>` and `PROOF#<hash>` lookups map to accounts/challenges without scans or eventually consistent indexes. `CHALLENGE#<id>` stores hashed verification state, and `COOLDOWN#<phone-hash>` serializes sends across service instances. JSON payloads and numeric revisions are committed together. Challenge/proof/refresh/cooldown items carry Unix-second `expiresAt` TTL values; account state remains durable. Stale refresh lookups cannot authorize a request because the current account session is always checked. Existing MongoDB records are not imported automatically.

The existing Service Bus probe remains separate. Registration/device mutations are not yet published as business events. A transactional outbox must be introduced before downstream services depend on their lifecycle events.

## Remaining implementation order

1. Complete account lifecycle: cryptographic device possession/link approval, recovery, account deletion, installation authentication, idempotency responses, durable distributed rate limits, contacts, blocks and privacy exceptions.
2. Public key inventory and atomic prekey claim APIs; key/version metadata and secure device-change events.
3. Direct conversations and encrypted message persistence with authorization, idempotency, sequencing, transactional outbox and durable sync.
4. Group membership, permissions, history boundaries and device-specific encrypted history-transfer packages.
5. Authenticated SignalR delivery, Redis presence/typing and shared communication-policy checks.
6. Account-specific APNs/FCM routing and privacy-preserving push handlers.
7. Encrypted object storage upload/download and retention enforcement.
8. Call state, signaling, LiveKit/SFU access and call policy.
9. Abuse/report administration, audit events, observability and the complete security test matrix.

## AWS prerequisites

Identity and Device require the environment's DynamoDB table plus signing/phone-hash secrets. Terraform injects the table name and grants scoped task-role access. `ACCOUNT_SECRET_ARN` references a Secrets Manager JSON object containing only `authSigningKey` and `authPhoneHashKey`; values do not enter Terraform state. Table creation on startup is limited to an explicitly configured local endpoint. AWS tables are created by Terraform.

## AWS v2 infrastructure migration

The active cloud deployment uses `infra/aws` Terraform and GitHub **Deploy AWS**, with independent `stg` and `prd` state. The previous Azure Bicep templates are archived under `infra/legacy-azure` and no longer run in CI/deploy. Terraform provisions the AWS foundation described in [deployment setup](deployment.md).

Account persistence and OTP transport now use DynamoDB and AWS End User Messaging SMS respectively. Event probes still use Azure Service Bus. SNS/SQS consumers/outbox, authenticated WebSocket handlers, CloudFront authorization and Chime SDK remain pending. Application deployment defaults off; enable it after configuring the authentication secret and images. Approved US/Bulgarian short codes and SMS account access are required for real delivery. Local development codes remain available when AWS SMS is unset.
