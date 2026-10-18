# AWS deployment with Terraform

The target is `messaging_backend_api_specification_v2.0_aws_native.docx`, especially sections 32–39. Terraform replaces Bicep for cloud infrastructure. The old templates are retained in `infra/legacy-azure` as historical reference; this change does not delete existing Azure resources. Local Compose and your private `.env.local`, `.env.stg`, `.env.prd` files remain unchanged.

## What is provisioned

- VPC with two availability zones, private ECS subnets, public NAT subnets, and S3/DynamoDB gateway endpoints. Staging has one NAT; production has one per AZ.
- Eleven immutable ECR repositories from `services.json`, an ECS cluster, separate execution/task IAM roles, Cloud Map private service discovery, and CloudWatch logs.
- HTTPS API Gateway HTTP API through a VPC Link to an internal ALB. Only `/api/v1/*` and `/health/ready` are routed. Development OTP routes are not exposed. Existing Identity/Device bearer/session checks remain the authorization boundary; API Gateway is not configured to validate the app's custom JWT itself.
- On-demand DynamoDB entity table with PK/SK, sparse GSI1, streams, TTL, KMS encryption, and point-in-time recovery. Production enables deletion protection.
- Private, versioned, KMS-encrypted S3 media bucket with public access blocked and TLS required.
- SNS events topic plus dedicated SQS queues and DLQs for notification, realtime and trust-safety consumers. Queue policies accept only this SNS topic. Queue age and DLQ depth alarms can notify an existing `alarm_topic_arn`.
- Optional private ElastiCache Serverless Valkey (`enable_cache=true`), disabled until needed.

The public API URL is an AWS-provided HTTPS endpoint, so an initial deploy does not require a custom domain or certificate. Custom domains/DNS, CloudFront signed media delivery, API Gateway WebSocket authorization/integrations, WAF, mobile push platform applications, Chime and scheduler/workflow resources remain follow-up work. No unauthenticated placeholder WebSocket endpoint is exposed. Application/API access logs omit phone numbers, request bodies and query strings; SMS delivery-event logs are covered separately below. Cross-service private HTTP stays within the VPC security groups; service-to-service TLS is follow-up work.

## Application migration boundary

**Infrastructure creation does not migrate the .NET implementation to AWS APIs.** The current account code still needs MongoDB, the broker adapter still uses Service Bus, and OTP now uses AWS End User Messaging SMS with approved country-specific origination identities. The new DynamoDB/SNS/SQS resources are not consumed by current application code. DynamoDB repositories, transactional outbox/consumers, WebSocket ticket authorization, push and calling still need application implementations before the v2 platform is operational.

`enable_services=false` is the default. It creates the foundation without starting applications that cannot yet use DynamoDB. The API returns no healthy application response until containers are deployed. To run the current account slice temporarily on AWS:

1. Provide a reachable transaction-capable MongoDB replica set (not provisioned here). Allow connectivity from the private tasks/NAT addresses as appropriate.
2. Create a Secrets Manager secret outside Terraform with this JSON structure. Use independent random keys of at least 32 bytes and keep the phone-hash key stable:

   ```json
   {
     "mongoConnectionString": "mongodb+srv://...",
     "authSigningKey": "<independent random secret>",
     "authPhoneHashKey": "<independent stable random secret>"
   }
   ```

3. Set `account_secret_arn` to its ARN. Supply `account_secret_kms_key_arn` if the secret uses a customer-managed key. Only Identity and Device execution roles may retrieve it. Values are injected directly by ECS; Terraform manages no secret versions and does not read secret values into state.
4. Push all images with an immutable tag, then enable services with that tag. Production runs two tasks per host; staging runs one. Rollback is enabled on failed ECS deployments. Other hosts remain feature scaffolds, and the AWS deployment does not inject ACS credentials or enable development OTP. Verification start returns 503 until at least one approved short-code/pool ARN is configured; unsupported destination countries return 400.

Task roles have no general data-plane AWS access. Identity receives only scoped SMS send access when approved sender ARNs are configured; add DynamoDB, SNS/SQS, media and Chime permissions alongside their future adapters. ECS execution roles only pull images, write logs and fetch their permitted account secret.

## Bootstrap remote state

Use Terraform 1.10+ (CI pins 1.13.5), AWS CLI/SSO credentials, and an AWS administrator for the one-time bootstrap. No AWS keys go in env files or Terraform inputs. Verify the chosen region supports the target services; examples use `us-east-1` and must be adjusted before deployment if residency or SMS/calling requirements differ.

```sh
cp infra/bootstrap/terraform.tfvars.example infra/bootstrap/terraform.tfvars
# Edit both globally unique bucket names and the AWS region.
terraform -chdir=infra/bootstrap init
terraform -chdir=infra/bootstrap plan
terraform -chdir=infra/bootstrap apply
```

Bootstrap creates separate state buckets for `stg` and `prd`. Both block public access, require TLS, encrypt objects, enable versioning and prevent accidental Terraform destruction. Each bucket has its own environment/resource-group tags. Keep the bootstrap's local state secure and backed up, or move it to a separate approved remote backend after creation. Never commit state.

Create GitHub environments `stg` and `prd`, with production branch restrictions/reviewers. Configure AWS OIDC roles trusted only for the matching GitHub environment subject:

```text
repo:evotch/Cyllio:environment:stg
repo:evotch/Cyllio:environment:prd
```

`infra/bootstrap` can create those roles when `deployment_policy_arn` is supplied. It requires an administrator-reviewed managed deployment policy and does not attach AdministratorAccess. Set `github_repository` if this repository differs. If your AWS account already has the GitHub OIDC provider, pass `github_oidc_provider_arn` to reuse it. Each role receives state/lock access only to its matching environment bucket in addition to your supplied deployment policy. Separate AWS accounts are recommended for production isolation; bootstrap in each account in that case.

Your reviewed deployment policy must allow this stack's VPC/networking, ECS/ECR, ALB, API Gateway, Cloud Map/Route 53 private namespace, CloudWatch, Resource Groups/tagging, DynamoDB, KMS, S3, SNS/SQS and optional ElastiCache operations, including their required read/list operations. It must permit management of the `cyllio-<environment>-*` task/execution IAM roles and passing only those roles to ECS. Service-linked roles may need administrator creation on the first deployment. A deployment policy is an account-specific prerequisite, not generated as a wildcard administrator policy here.

## Plan and apply an environment

Use separate state buckets, state keys and initialization data for staging/production. Set each backend example to its matching bucket from the bootstrap `state_buckets` output. Copy the backend example and edit the bucket/region:

```sh
cp infra/aws/environments/stg.s3.tfbackend.example infra/aws/environments/stg.s3.tfbackend
TF_DATA_DIR="$PWD/.terraform-stg" terraform -chdir=infra/aws init -backend-config=environments/stg.s3.tfbackend
TF_DATA_DIR="$PWD/.terraform-stg" terraform -chdir=infra/aws plan -var-file=environments/stg.tfvars -out=stg.tfplan
TF_DATA_DIR="$PWD/.terraform-stg" terraform -chdir=infra/aws apply stg.tfplan
```

For production, use `prd` consistently in the backend filename, state key, data directory, variable file and plan filename. Private `*.tfvars`, backend files, state and saved plans are Git-ignored; the two checked-in environment variable files contain no secrets. Commit provider lock files. The S3 backend uses native S3 lockfiles (`use_lockfile=true`).

To deploy compatibility containers manually after the foundation and images exist:

```sh
TF_DATA_DIR="$PWD/.terraform-stg" terraform -chdir=infra/aws plan \
  -var-file=environments/stg.tfvars \
  -var='enable_services=true' \
  -var='image_tag=YOUR_COMMIT_SHA' \
  -var='account_secret_arn=YOUR_SECRET_ARN' \
  -out=stg.tfplan
TF_DATA_DIR="$PWD/.terraform-stg" terraform -chdir=infra/aws apply stg.tfplan
```

Keep supplying those service settings on later plans; otherwise Terraform plans to remove the optional services. The GitHub workflow rejects a foundation-only run when services are already enabled. Review plans before applying. Production deletion protection must be deliberately disabled before any planned teardown.

## GitHub Actions

Set these GitHub **environment variables** for each environment (ARNs are references, not secret values):

| Variable | Purpose |
| --- | --- |
| `AWS_REGION` | Deployment/state region |
| `AWS_ROLE_ARN` | Environment-specific OIDC deployment role |
| `TF_STATE_BUCKET` | Matching environment bucket from bootstrap `state_buckets` output |
| `ACCOUNT_SECRET_ARN` | Optional compatibility secret; required when deploying current containers |
| `ACCOUNT_SECRET_KMS_KEY_ARN` | Optional customer-managed key protecting that secret |

**Every push or merge to `main` runs Deploy AWS and automatically applies staging Terraform only after the full CI workflow succeeds: .NET build/formatting, OTP tests, Terraform validation/tests, container smoke tests and account integration. The legacy Service Bus integration job has been removed; AWS SNS/SQS application integration coverage is pending the AWS broker adapter.** CI is reused as a required job in Deploy AWS, so a failed or cancelled CI job prevents deployment of that commit. Pull requests run CI without deployment. Manual deployments also run full CI first. Push runs use the `stg` GitHub environment and `stg/terraform.tfstate`; production remains a manual deployment. The environment-specific bootstrap state bucket and OIDC deployment role must already exist.

On the first main push, application services remain disabled and the pipeline creates the foundation/ECR repositories. If staging services are already enabled, later main pushes keep them enabled and build/push the new commit images before applying Terraform; `ACCOUNT_SECRET_ARN` must remain configured.

You can also run **Deploy AWS** manually with `stg` or `prd`; manual runs default to **plan**. Apply with `deploy_services=false` to create the foundation/ECR repositories. After configuring the compatibility secret, apply again with `deploy_services=true` to build/push all images and start the containers. Building uses `linux/amd64` to match Fargate task architecture. The selected commit SHA is an immutable image tag; an existing tag is reused on retries. ECS must become stable and the HTTPS readiness check must pass. A services plan can be reviewed before image publication, but its apply run rebuilds the plan after publishing images.

A local commit does not run a deployment; pushing or merging it to `main` triggers the staging deployment. No data migration is performed. Existing MongoDB, Service Bus emulator, iPhone configuration and Azure resources remain unchanged. The Identity SMS adapter now uses AWS; old ACS env entries are no longer consumed by Compose.

## Validation

```sh
terraform fmt -check -recursive infra/aws infra/bootstrap
terraform -chdir=infra/aws init -backend=false
terraform -chdir=infra/aws validate
terraform -chdir=infra/aws test
terraform -chdir=infra/bootstrap init
terraform -chdir=infra/bootstrap validate
```

Mock-provider tests cover staging/production network topology, private compute/media, repository counts, data protection, queue/DLQ fanout and the compatibility-secret gate. They make no AWS API calls. They do not replace a real account-specific plan, regional availability checks or application integration tests. Deployment-selection tests run with `python3 -m unittest discover -s tests/pipeline -v`.

References: [AWS ECS private service integration](https://docs.aws.amazon.com/apigateway/latest/developerguide/http-api-develop-integrations-private.html), [Terraform S3 state/locking](https://developer.hashicorp.com/terraform/language/backend/s3).

## Local AWS verification

Compose uses the AWS SMS settings listed in `.env.example`. Leave them empty and keep `AUTH_ENABLE_DEVELOPMENT_OTP=true` to use development-only test codes. Setting `AWS_SMS_ENABLED=true`, or providing partial SMS settings, disables development code retrieval. Live sending requires standard AWS credentials available to the backend, region, configuration set and an approved country-specific origination ARN; ECS uses its task role automatically. For local live testing, run Identity with an authenticated AWS SDK credential chain. AWS credentials belong on the backend, never in the iPhone app. Old ACS settings are obsolete and do not send SMS through the new adapter.

## Environment resource groups and tags

Applying staging creates AWS Resource Group `cyllio-stg`; applying production in its separate state creates `cyllio-prd`. Each tag-based group selects supported resource types matching both `Project=cyllio` and the corresponding `Environment=stg` or `Environment=prd`. Groups are created in the configured AWS region/account. They organize resources by tags; they do not replace separate state, IAM policies or VPC boundaries.

The AWS provider supplies `Project`, `Environment`, `ManagedBy=Terraform` and `ResourceGroup=cyllio-<environment>` to every directly managed resource supporting tags. Per-service resources also receive `Service=<manifest service name>`. ECS services propagate their tags to launched tasks and enable ECS-managed tags. Resources such as IAM policy attachments, routes, subscriptions and bucket configuration objects that have no tag API remain associated with their tagged parent resource; they cannot carry independent tags or necessarily appear as group members. Existing externally supplied secrets/keys are not retagged by this stack.

The account-wide OIDC provider uses `Environment=shared` and `Component=terraform-bootstrap`. Each state bucket is tagged with its own `Environment=stg` or `Environment=prd` and matching `ResourceGroup`. Bootstrap deployment roles override the environment/resource-group tags for their own staging or production environment. Terraform outputs expose `resource_group_name`, `resource_group_arn` and `resource_tags`. The deployment policy must include the Resource Groups create/read/update/delete and tag operations needed to manage these groups.

## Upgrading an existing shared state bucket

Bootstrap now requires `state_bucket_names = { stg = "...", prd = "..." }` instead of `state_bucket_name`. If bootstrap has already created the old shared bucket, set the `stg` entry to that exact existing bucket name and choose a new, distinct `prd` name. The included Terraform `moved` blocks preserve the original bucket and its protection/configuration as the staging instance; they do not delete or copy state. Review the bootstrap plan before applying it.

Set GitHub `stg` → `TF_STATE_BUCKET` to the staging bucket and `prd` → `TF_STATE_BUCKET` to the production bucket. A new setup needs no state migration. If production application state already exists in the former shared bucket, migrate that environment's backend with `terraform init -migrate-state` before using the new production bucket in Actions. Do not start a production apply against an empty state when resources already exist. Keep the old bucket and state until migration is verified. The roles' inline state policies grant access to their own bucket; the administrator-supplied deployment policy should not independently grant cross-environment state-bucket access.

## Bootstrap without an existing deployment policy

Set `create_deployment_policies=true` to create a separate Terraform deployment policy for each environment instead of supplying `deployment_policy_arn`. Each generated policy enables the infrastructure service operations used by this stack in the configured region, scopes S3 access to `cyllio-<environment>-*` buckets, and limits IAM task-role management and passing to the matching environment's task/execution roles. It does not attach AdministratorAccess. Regional infrastructure permissions cover those services throughout the selected region; they are not a strict cross-environment IAM isolation boundary. Use separate accounts or a separately reviewed external policy for stronger isolation. An externally supplied policy takes precedence.

## GitHub OIDC immutable repository subjects

Newer GitHub repositories include immutable owner/repository IDs in the OIDC subject. For this repository, CloudTrail recorded `repo:evotch@15218305/Cyllio@1409423918:environment:stg`. Configure `github_oidc_repository="evotch@15218305/Cyllio@1409423918"` in bootstrap; Terraform appends the matching environment for each role. `github_repository` remains the normal `evotch/Cyllio` repository name. Legacy subjects are supported by leaving the override null. Never replace exact environment trust with a broad repository wildcard to work around a mismatch. Inspect only the subject/audience/role metadata, not the full signed token.

## AWS OTP SMS and dedicated short codes (US and Bulgaria)

Terraform creates a transactional SMS configuration set (`cyllio-stg-otp` / `cyllio-prd-otp`), delivery-event CloudWatch log group, an SMS service role permitted to write only those logs, and an Identity task send policy limited to the provided origination identities/configuration set. Delivery-event logs can contain destination phone numbers; retention follows `log_retention_days` and access should remain restricted. The SDK adapter does not log OTP codes, message bodies or provider exception text. Automatic SDK retries are disabled after ambiguous send failures, and message TTL never exceeds the OTP's remaining lifetime. Real OTP verification still happens in Identity/MongoDB; AWS transports the SMS.

Short codes are registered national sending numbers, usually five or six digits. Request separate dedicated short codes for the US and Bulgaria through AWS Support, including carrier registrations and any required opt-in evidence. Terraform cannot manufacture an approved short code by setting a five-digit sender string. It deliberately does not reserve a substitute paid number or silently send with an alphanumeric sender ID. See [AWS short-code requests](https://docs.aws.amazon.com/sms-voice/latest/userguide/phone-numbers-request-short-code.html) and the prepared [request checklist](sms-short-code-requests.md).

Once AWS has provisioned the short codes in the sending region, add their phone-number ARNs to the corresponding environment's Terraform inputs:

```hcl
sms_origination_identities = {
  US = "arn:aws:sms-voice:us-west-2:ACCOUNT_ID:phone-number/phone-US_ID"
  BG = "arn:aws:sms-voice:us-west-2:ACCOUNT_ID:phone-number/phone-BG_ID"
}
```

Terraform injects these ARNs, region and configuration-set name into Identity; only the Identity task role gets `sms-voice:SendTextMessage`. The backend parses numbers with libphonenumber: valid US numbers use the US identity, Bulgarian numbers use the BG identity, and unsupported countries (including Canadian +1 numbers) are rejected before creating a challenge or making an SMS call. No AWS access keys are stored in Terraform or ECS configuration. The bootstrap deployment policies include the SMS control-plane operations and narrowly targeted permission to pass the event-log role to `sms-voice.amazonaws.com`; apply bootstrap policy changes before deploying this module.

A foundation-only deployment creates configuration/logging but grants no send permission while the identity map is empty. SMS account production access, spending quotas, sandbox destination verification and short-code carrier approvals are account/telecom setup steps; Terraform does not bypass them. Tests use fake AWS clients/mock providers and never send or purchase SMS. Run `dotnet run --project tests/Cyllio.Otp.Tests` and `terraform -chdir=infra/aws test` to verify the adapter and wiring.
