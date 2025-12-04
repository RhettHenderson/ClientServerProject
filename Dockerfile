# ------------ Build stage ------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files and restore
COPY . .
RUN dotnet publish -c Release -o /app/publish

# ------------ Runtime stage ------------
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

# Use APP_PORT to configure the app (your code reads this)
ENV APP_PORT=11111

# Expose TCP and UDP on the configured port
EXPOSE 11111/tcp
EXPOSE 11111/udp

# Replace 'YourServerDllName.dll' with your actual dll name
ENTRYPOINT ["dotnet", "ServerApp.dll"]