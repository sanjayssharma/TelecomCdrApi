# Dockerfile for TelecomCdr.API ASP.NET Core Web API within PETechTest solution structure

# --- Stage 1: Build ---
# Use the official .NET SDK image for building the application.
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /source
# Set the working directory inside the container

# Copy the solution file from the build context root (PETechTest folder).
COPY PETechTest.sln .

# Copy project files. Paths are relative to the build context (PETechTest folder).
# Copy the Cdr.Api project file and its directory structure.
COPY TelecomCdr.API/TelecomCdr.API.csproj ./TelecomCdr.API/
# Copy dependent project files.
COPY TelecomCdr.Core/TelecomCdr.Core.csproj ./TelecomCdr.Core/
COPY TelecomCdr.Data/TelecomCdr.Data.csproj ./TelecomCdr.Data/
COPY TelecomCdr.Hangfire/TelecomCdr.Hangfire.csproj ./TelecomCdr.Hangfire/
COPY TelecomCdr.HangfireJobs/TelecomCdr.HangfireJobs.csproj ./TelecomCdr.HangfireJobs/
# Add COPY commands for any other referenced projects (e.g., test projects if needed for build steps, though unlikely).

# Restore NuGet packages for the entire solution.
RUN dotnet restore "PETechTest.sln"

# Copy the rest of the source code from the build context (PETechTest folder)
# into the container's /source directory.
COPY . .

# Build and publish the TelecomCdr.API project in Release configuration.
# The WORKDIR is still /source, so specify the project path relative to that.
RUN dotnet publish "./TelecomCdr.API/TelecomCdr.API.csproj" -c Release -o /app/publish --no-restore

# --- Stage 2: Runtime ---
# Use the official ASP.NET Core runtime image (matching the SDK base image).
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS final
WORKDIR /app
# Set the final working directory

# Copy the published output from the build stage (/app/publish) into the runtime container.
COPY --from=build /app/publish .

# Expose the ports the application listens on (match ASPNETCORE_URLS in docker-compose.yml).
EXPOSE 8080
EXPOSE 8081 # If using HTTPS

# Set the entry point for the container to run the application DLL.
# The DLL name typically matches the project/assembly name.
ENTRYPOINT ["dotnet", "Cdr.Api.dll"]

# Optional: Add health check instructions
# HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 CMD curl --fail http://localhost:8080/healthz || exit 1

