FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY AwsRagChat.Api/AwsRagChat.Api.csproj AwsRagChat.Api/
COPY AwsRagChat.Application/AwsRagChat.Application.csproj AwsRagChat.Application/
COPY AwsRagChat.Domain/AwsRagChat.Domain.csproj AwsRagChat.Domain/
COPY AwsRagChat.Infrastructure/AwsRagChat.Infrastructure.csproj AwsRagChat.Infrastructure/

RUN dotnet restore AwsRagChat.Api/AwsRagChat.Api.csproj

COPY AwsRagChat.Api/ AwsRagChat.Api/
COPY AwsRagChat.Application/ AwsRagChat.Application/
COPY AwsRagChat.Domain/ AwsRagChat.Domain/
COPY AwsRagChat.Infrastructure/ AwsRagChat.Infrastructure/

RUN dotnet publish AwsRagChat.Api/AwsRagChat.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "AwsRagChat.Api.dll"]