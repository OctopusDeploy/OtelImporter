FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled

WORKDIR /app

# Binary is pre-built by CI and copied in by the docker workflow before this image is built.
COPY OtelImporter ./OtelImporter

ENTRYPOINT ["/app/OtelImporter"]
