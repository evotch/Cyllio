# Cyllio

.NET 10 messaging backend scaffolding with one shared solution and Azure Container Apps infrastructure. See [service boundaries](docs/services.md) for the ten specification services.

## Local development

```sh
dotnet restore Cyllio.sln
dotnet build Cyllio.sln -c Release
dotnet run --project src/Cyllio.Api --urls http://localhost:8080
```

Endpoints: `/`, `/health/live`, `/health/ready`. Readiness currently checks process health only; add dependency checks when databases or downstream services are introduced. No business endpoints or authentication are implemented yet.

```sh
docker build --build-arg SERVICE_PROJECT=Cyllio.Api -t cyllio-api:local .
docker run --rm -p 8080:8080 cyllio-api:local
```

## Azure deployment

See [deployment setup](docs/deployment.md) for Azure OIDC and GitHub configuration.

- `Cyllio.sln`: all service projects belong here.
- `src`: API entry host and ten independent service projects.
- `Dockerfile`: shared non-root service image build.
- `services.json`: shared manifest for CI and Azure deployment.
- `infra/platform.bicep`: registry, image-pull identity, role assignment, logs, Container Apps environment.
- `infra/apps.bicep`: all service deployments, ingress, probes and scaling.
- `infra/parameters`: dev and production platform parameters.
- `.github/workflows/ci.yml`: build, formatting, Bicep compilation, container smoke test.
- `.github/workflows/deploy.yml`: manually triggered Azure deployment for dev or prod.

To add a service, create its project under `src`, add it with `dotnet sln Cyllio.sln add ...`, add it to `services.json`, and build it with the shared Dockerfile.
