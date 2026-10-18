# Specification implementation progress

Source: `messaging_backend_api_specification_v1.3_privacy_preferences.docx`, version 1.3. All business endpoints use C# Minimal APIs under `/api/v1`. Work starts with the account foundation; this is not yet a complete backend v1.

## Implemented first slice

| Area | Implemented behavior |
| --- | --- |
| Authentication (section 2) | Start and confirm phone verification through an explicitly enabled local OTP provider; single-use registration/login proof; register an account with initial public device keys; OTP login to an existing account-bound device; 10-minute signed access tokens; hashed 30-day refresh tokens with atomic rotation; logout and logout-all. |
| Profile (section 3) | Read and update the caller's own display name, about and avatar reference; ETag/If-Match concurrency. |
| Devices (sections 4, 29) | List the caller's active devices; update the current device's push capability; revoke an owned device and its sessions immediately. Separate accounts on a shared installation remain isolated. |
| Privacy (section 31) | Persist all account preference fields from section 31 with value validation and ETag/If-Match concurrency. This stores policy; communication enforcement awaits the corresponding APIs. |
| Persistence (section 19) | MongoDB single-node development replica set, unique phone lookup index, expiring verification documents and transactions for proof consumption, registration and refresh. |
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

Set independent `AUTH_SIGNING_KEY` and `AUTH_PHONE_HASH_KEY` secrets (at least 32 bytes each) in `.env`; generate each with `openssl rand -hex 48`. Existing local `.env` files from earlier setup need these new entries. Both Identity and Device use them. Keep the phone lookup secret stable; changing it requires a planned lookup migration. The local setup stores HMAC phone lookup hashes, not raw phone numbers.

```sh
docker compose up --build -d api identity device
python3 scripts/test-accounts.py
```

MongoDB is exposed on `127.0.0.1:27018` with a persistent `mongo-data` volume. It is an unauthenticated, single-node replica set for local development only. For direct local .NET execution use `Mongo__ConnectionString='mongodb://localhost:27018/?replicaSet=rs0&directConnection=true'`, the two `Auth__...` secrets and `ASPNETCORE_ENVIRONMENT=Development`. Identity also requires `Auth__EnableDevelopmentOtp=true` to use the test OTP provider.

A local verification example:

```sh
curl -X POST http://localhost:8080/api/v1/auth/verification/start \
  -H 'Content-Type: application/json' \
  -d '{"phoneNumber":"+12065550123","channel":"sms","locale":"en-US"}'
# Read the code directly from Identity, using verificationId from the response:
curl http://localhost:8081/dev/otp/<verificationId>
```

Confirmation returns a registration token, not an account token. Verification expires after five minutes, accepts at most five attempts, and has a per-phone cooldown plus per-process IP throttling. Codes are never logged or stored as plaintext in MongoDB. The development provider stores codes in expiring process memory and is exposed only when both Development and the explicit setting are enabled. Real SMS/voice delivery must be configured before enabling registration in a production environment; currently production verification start returns 503.

GET profile/privacy responses include `ETag`. Supply that value in `If-Match` when updating. Missing versions return 428; stale versions return 412. Ownership always comes from the signed token, never a body-supplied user or installation ID. Device lists omit public keys, push tokens and refresh-token hashes.

The integration script verifies registration proof replay protection, unauthorized requests, two accounts on one installation, cross-account device mutation denial, privacy and profile isolation, stale/concurrent version rejection, single-use refresh rotation, login proof isolation, persistence across service restarts, logout-all and immediate device revocation. It restarts Identity and Device to check persistence and creates test accounts in the local database and does not remove the database or its volume. CI runs the same script.

## Persistence boundary

For this first slice Identity and Device share the account database and one account aggregate with embedded device and session metadata. Public cryptographic prekey inventory is not implemented here. Keeping lifecycle state together provides atomic session revocation and refresh behavior; this is an initial implementation choice, not a final separate-database-per-service design. Login prunes expired/revoked sessions and bounds the account to 100 active sessions; trusted-device linking is follow-up work.

The existing Service Bus probe remains separate. Registration/device mutations are not yet published as business events. A transactional outbox must be introduced before downstream services depend on their lifecycle events.

## Remaining implementation order

1. Complete account lifecycle: production OTP provider, cryptographic device possession/link approval, recovery, account deletion, installation authentication, idempotency responses, durable distributed rate limits, contacts, blocks and privacy exceptions.
2. Public key inventory and atomic prekey claim APIs; key/version metadata and secure device-change events.
3. Direct conversations and encrypted message persistence with authorization, idempotency, sequencing, transactional outbox and durable sync.
4. Group membership, permissions, history boundaries and device-specific encrypted history-transfer packages.
5. Authenticated SignalR delivery, Redis presence/typing and shared communication-policy checks.
6. Account-specific APNs/FCM routing and privacy-preserving push handlers.
7. Encrypted object storage upload/download and retention enforcement.
8. Call state, signaling, LiveKit/SFU access and call policy.
9. Abuse/report administration, audit events, observability and the complete security test matrix.

## Azure prerequisites

Identity and Device now require a reachable transaction-capable MongoDB replica set and signing/phone-hash secrets. The Bicep template wires these through Container App secrets; the workflow requires GitHub environment secrets `MONGO_CONNECTION_STRING`, `AUTH_SIGNING_KEY` and `AUTH_PHONE_HASH_KEY` before deploying. MongoDB must be provisioned separately. Local MongoDB and the test OTP provider are not production infrastructure. Signing-secret distribution is a first-slice choice; asymmetric signing with verification keys and managed secret storage is follow-up work.

References: [Minimal API authentication](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/security?view=aspnetcore-10.0), [MongoDB transactions](https://www.mongodb.com/docs/manual/core/transactions/), [YARP configuration](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/config-files?view=aspnetcore-10.0).
