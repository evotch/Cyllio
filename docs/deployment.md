# Azure and GitHub setup

## One-time configuration

Create GitHub environments named `dev` and `prod`. Configure required reviewers and restrict production deployment branches to `main` in GitHub environment settings. Use a separate Azure resource group and deployment identity for each environment.

Set these GitHub environment variables:

| Variable | Value |
| --- | --- |
| `AZURE_CLIENT_ID` | Deployment app registration client ID |
| `AZURE_TENANT_ID` | Entra tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Azure subscription ID |
| `AZURE_RESOURCE_GROUP` | e.g. `rg-cyllio-dev` or `rg-cyllio-prod` |
| `AZURE_LOCATION` | e.g. `eastus` |

Also set these GitHub **environment secrets** for the account APIs:

| Secret | Value |
| --- | --- |
| `MONGO_CONNECTION_STRING` | TLS connection URI for a reachable MongoDB replica set supporting transactions; provision it separately. |
| `AUTH_SIGNING_KEY` | Independent random signing secret of at least 32 bytes. |
| `AUTH_PHONE_HASH_KEY` | Independent stable random phone-lookup secret of at least 32 bytes. |

The pipeline validates these before creating Azure resources and passes them through a protected temporary parameter file into Container App secrets. API routes use the account services' internal HTTPS addresses. The development OTP provider is disabled in Azure; a production SMS/voice adapter still needs implementation before phone verification can run there. The local MongoDB container is not deployed by Bicep.

Use Azure CLI as an Azure administrator to create the group and deployment identity (replace placeholders):

```sh
az login
az account set --subscription <subscription-id>
az group create --name rg-cyllio-dev --location eastus
az ad app create --display-name cyllio-github-dev
# Use appId from the preceding output:
az ad sp create --id <app-client-id>
```

Create a federated credential JSON file for the environment:

```json
{
  "name": "github-dev",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:evotch/Cyllio:environment:dev",
  "description": "Cyllio dev deployments",
  "audiences": ["api://AzureADTokenExchange"]
}
```

```sh
az ad app federated-credential create --id <app-client-id> --parameters credential.json
az role assignment create --assignee-object-id <service-principal-object-id> --assignee-principal-type ServicePrincipal --role Contributor --scope /subscriptions/<subscription-id>/resourceGroups/rg-cyllio-dev
az role assignment create --assignee-object-id <service-principal-object-id> --assignee-principal-type ServicePrincipal --role "Role Based Access Control Administrator" --scope /subscriptions/<subscription-id>/resourceGroups/rg-cyllio-dev
```

The role-management permission is needed because Bicep grants the image-pull identity `AcrPull`. The deployment identity also needs `AcrPush` on the registry before the image push; an administrator can grant it after the first platform deployment, or grant `AcrPush` at the resource-group scope upfront:

```sh
az role assignment create --assignee-object-id <service-principal-object-id> --assignee-principal-type ServicePrincipal --role AcrPush --scope /subscriptions/<subscription-id>/resourceGroups/rg-cyllio-dev
```

Repeat for production with its own identity, group, and `repo:evotch/Cyllio:environment:prod` subject. No Azure client secrets are stored in GitHub. Subscription administrators may need to register `Microsoft.App`, `Microsoft.ContainerRegistry`, `Microsoft.ManagedIdentity`, and `Microsoft.OperationalInsights` resource providers before first deployment.

## Run deployment

Push the files to GitHub, let CI pass, then run **Deploy Azure** from Actions and select the environment. The deployment uses the selected ref's commit SHA as its image tag. Platform resources are deployed before building the image so the first deployment has an existing registry. All service apps are created only after their images are pushed. Azure RBAC propagation can briefly delay a first deployment; rerun after propagation if image pull or push is denied.

The workflow prints the public API and realtime host URLs to its run summary. Deployment creates billable Azure resources. Dev scales to zero; production keeps at least one replica per service. API and realtime ingress are public; other services use internal ingress. Add authentication before exposing protected business endpoints. This starter does not provision databases, messaging, private networking, custom domains, or application secrets.

## Preview and rollback

With the resource group already created:

```sh
az deployment group what-if --resource-group rg-cyllio-dev --template-file infra/platform.bicep --parameters infra/parameters/dev.bicepparam
```

To roll back, run the deployment workflow against a previous known-good commit that contains this workflow. This also reapplies that commit's infrastructure, so review infrastructure differences first. Single-revision mode transfers traffic once the new revision is healthy.

## References

- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)
- [Container Apps managed identity image pulls](https://learn.microsoft.com/en-us/azure/container-apps/managed-identity-image-pull)
- [Azure Login with OIDC](https://github.com/Azure/login#login-with-openid-connect-oidc-recommended)
