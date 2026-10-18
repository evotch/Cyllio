# Cyllio AWS Terraform

See [deployment setup](../../docs/deployment.md) for bootstrap, environment inputs, OIDC and the application migration boundary. Start with foundation-only staging. This Terraform does not convert the existing MongoDB/ACS/Service Bus code into DynamoDB/AWS SMS/SNS consumers.

Use `terraform init -backend=false` and `terraform test` for credential-free validation; mocked tests do not provision AWS resources. Actual apply uses a separately bootstrapped S3 backend with environment-specific state keys.
