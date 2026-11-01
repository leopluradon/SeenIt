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

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0 \
    SeenIt__KeyRing=/keys

# The key ring signs everyone's login tokens; keep it on a volume so redeploys don't sign people out.
RUN mkdir -p /keys && chown $APP_UID /keys

EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "SeenIt.Server.dll"]
