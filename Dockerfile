FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG SERVICE_PROJECT=Cyllio.Api
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/ src/
RUN dotnet restore "src/${SERVICE_PROJECT}/${SERVICE_PROJECT}.csproj"
RUN dotnet publish "src/${SERVICE_PROJECT}/${SERVICE_PROJECT}.csproj" -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
ARG SERVICE_PROJECT=Cyllio.Api
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
ENV SERVICE_DLL=${SERVICE_PROJECT}.dll
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["sh", "-c", "exec dotnet \"$SERVICE_DLL\""]
