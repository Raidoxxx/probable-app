# Servidor do PCBoost (contas, servidores, chat, voz e quadros).
# Build a partir da raiz do monorepo:  docker build -f apps/server/Dockerfile -t pcboost-server .
# Rodar:  docker run -p 5080:5080 -v pcboost-data:/data pcboost-server
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY packages/shared/ packages/shared/
COPY apps/server/ apps/server/
COPY apps/web/ apps/web/
RUN dotnet publish apps/server/PCBoost.Server.csproj -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV PCBOOST_DATA=/data/pcboost-team.db
# Instalador do app: monte um arquivo em /app/downloads/PCBoost-Setup.exe ou defina PCBOOST_DOWNLOAD_URL.
VOLUME /data
EXPOSE 5080
ENTRYPOINT ["dotnet", "PCBoost.Server.dll"]
