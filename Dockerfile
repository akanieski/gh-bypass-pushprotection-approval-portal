FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json ./
COPY src/ApprovalPortal/ApprovalPortal.csproj src/ApprovalPortal/
RUN dotnet restore src/ApprovalPortal/ApprovalPortal.csproj
COPY src/ApprovalPortal/ src/ApprovalPortal/
RUN dotnet publish src/ApprovalPortal/ApprovalPortal.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
USER root
RUN mkdir -p /data && chown app:app /data
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 Portal__DataDirectory=/data
EXPOSE 8080
ENTRYPOINT ["dotnet", "ApprovalPortal.dll"]
