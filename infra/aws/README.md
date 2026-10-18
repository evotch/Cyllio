# Cyllio AWS Terraform

See [deployment setup](../../docs/deployment.md) for bootstrap, environment inputs, OIDC and the application migration boundary. Start with foundation-only staging. AWS SMS configuration and the Identity adapter are implemented; approved country-specific short-code ARNs must be supplied. MongoDB persistence and the existing Service Bus adapter still require separate DynamoDB/SNS migrations.

Use `terraform init -backend=false` and `terraform test` for credential-free validation; mocked tests do not provision AWS resources. Actual apply uses a separately bootstrapped S3 backend with environment-specific state keys.
