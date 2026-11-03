# Build the API server (which also serves the Blazor WebAssembly site), then ship only the output.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/SeenIt.Contracts/SeenIt.Contracts.csproj src/SeenIt.Contracts/
COPY src/SeenIt.UI/SeenIt.UI.csproj src/SeenIt.UI/
COPY src/SeenIt.Web/SeenIt.Web.csproj src/SeenIt.Web/
COPY src/SeenIt.Server/SeenIt.Server.csproj src/SeenIt.Server/
RUN dotnet restore src/SeenIt.Server/SeenIt.Server.csproj

COPY src/ src/
RUN dotnet publish src/SeenIt.Server/SeenIt.Server.csproj -c Release -o /app --no-restore
# Which NuGet packages have newer versions, for the admin page (best effort: never fails the build).
RUN for p in Server Web UI Contracts; do \
      dotnet list src/SeenIt.$p/SeenIt.$p.csproj package --outdated --format json > /app/outdated-$p.json 2>/dev/null || echo '{}' > /app/outdated-$p.json; \
    done

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Which build this is, shown on the admin page (scripts/deploy.sh fills these in).
ARG SEENIT_VERSION=dev
ARG SEENIT_COMMIT=dev
ARG SEENIT_BUILT_AT=
ARG SEENIT_SUBJECT_B64=
ENV SEENIT_VERSION=$SEENIT_VERSION \
    SEENIT_COMMIT=$SEENIT_COMMIT \
    SEENIT_BUILT_AT=$SEENIT_BUILT_AT \
    SEENIT_SUBJECT_B64=$SEENIT_SUBJECT_B64

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0 \
    SeenIt__KeyRing=/keys

# The key ring signs everyone's login tokens; keep it on a volume so redeploys don't sign people out.
RUN mkdir -p /keys && chown $APP_UID /keys

EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "SeenIt.Server.dll"]
