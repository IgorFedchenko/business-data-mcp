FROM mcr.microsoft.com/dotnet/sdk:8.0.426 AS build
WORKDIR /source
COPY . .
RUN dotnet restore BusinessData.sln --locked-mode --disable-parallel
RUN dotnet publish src/BusinessData.Server/BusinessData.Server.csproj -c Release --no-restore -o /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
USER root
RUN mkdir /data && chown $APP_UID:$APP_UID /data
USER $APP_UID
ENV MCP_DATABASE=/data/business.db MCP_ALLOW_WRITES=false ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
VOLUME /data
ENTRYPOINT ["dotnet", "BusinessData.Server.dll", "--transport", "http"]
