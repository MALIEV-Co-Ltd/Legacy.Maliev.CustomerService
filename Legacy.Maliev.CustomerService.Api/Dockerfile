# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ENV GITHUB_ACTIONS=false
COPY nuget.config .
COPY .dependencies/ .dependencies/
COPY Legacy.Maliev.CustomerService.Api/Legacy.Maliev.CustomerService.Api.csproj Legacy.Maliev.CustomerService.Api/
COPY Legacy.Maliev.CustomerService.Application/Legacy.Maliev.CustomerService.Application.csproj Legacy.Maliev.CustomerService.Application/
COPY Legacy.Maliev.CustomerService.Domain/Legacy.Maliev.CustomerService.Domain.csproj Legacy.Maliev.CustomerService.Domain/
COPY Legacy.Maliev.CustomerService.Data/Legacy.Maliev.CustomerService.Data.csproj Legacy.Maliev.CustomerService.Data/
RUN dotnet restore Legacy.Maliev.CustomerService.Api/Legacy.Maliev.CustomerService.Api.csproj \
    -p:MalievWorkspaceRoot=/src/.dependencies
COPY . .
RUN dotnet publish Legacy.Maliev.CustomerService.Api/Legacy.Maliev.CustomerService.Api.csproj \
    --configuration Release --no-restore --output /app /p:UseAppHost=false \
    -p:MalievWorkspaceRoot=/src/.dependencies

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app .
USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Legacy.Maliev.CustomerService.Api.dll"]
