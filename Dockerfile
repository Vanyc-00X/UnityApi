# Сборка и запуск для Render.com (и других Docker-хостов).
# В Render укажите Root Directory: Backend/Mango3D.Api
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Mango3D.Api.csproj .
RUN dotnet restore Mango3D.Api.csproj
COPY . .
RUN dotnet publish Mango3D.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS run
WORKDIR /app
COPY --from=build /app/publish .
# Render передаёт переменную PORT — слушаем её.
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet Mango3D.Api.dll --urls http://0.0.0.0:${PORT:-8080}"]
