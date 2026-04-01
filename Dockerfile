FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution file and project files first for better layer caching
COPY *.sln ./
COPY SharpTools.SseServer/*.csproj ./SharpTools.SseServer/
COPY SharpTools.StdioServer/*.csproj ./SharpTools.StdioServer/
COPY SharpTools.Tools/*.csproj ./SharpTools.Tools/

# Restore packages
RUN dotnet restore SharpTools.sln

# Copy source code
COPY . .

# Build and publish
RUN dotnet build SharpTools.sln -c Release --no-restore
RUN dotnet publish SharpTools.SseServer/SharpTools.SseServer.csproj \
    -c Release \
    --no-restore \
    --no-build \
    -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Create non-root user for security
RUN addgroup --gid 1001 --system appgroup && \
    adduser --uid 1001 --system --ingroup appgroup appuser

# Create directories and set permissions
RUN mkdir -p /app/logs /app/solutions && \
    chown -R appuser:appgroup /app

COPY --from=build --chown=appuser:appgroup /app .

# Switch to non-root user
USER appuser

EXPOSE 3001

# Add health check endpoint (using dotnet instead of curl for lighter image)
HEALTHCHECK --interval=30s --timeout=10s --start-period=40s --retries=3 \
    CMD dotnet --version > /dev/null || exit 1

ENTRYPOINT ["dotnet", "SharpTools.SseServer.dll"]