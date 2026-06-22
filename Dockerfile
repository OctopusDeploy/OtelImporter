# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot AS build

WORKDIR /src
COPY src/ src/
RUN dotnet publish src/OtelImporter/OtelImporter.csproj \
      --configuration Release \
      --output /app

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled AS final

WORKDIR /app

# Copy only the executable -- not the .dbg symbols the publish also produces.
COPY --from=build /app/OtelImporter ./OtelImporter

# Chiseled images already default to a non-root user (UID 1654), so no USER line needed.
ENTRYPOINT ["/app/OtelImporter"]
