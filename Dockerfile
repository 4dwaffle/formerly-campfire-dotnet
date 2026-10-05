FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
RUN apt-get update && apt-get install -y --no-install-recommends build-essential zlib1g-dev libvips-dev pkg-config && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY NuGet.Config global.json ./
COPY src/Campfire/Campfire.csproj src/Campfire/
RUN dotnet restore src/Campfire/Campfire.csproj
COPY src/Campfire/ src/Campfire/
RUN dotnet publish src/Campfire/Campfire.csproj -c Release --no-restore -o /app
RUN cc -O2 -Wall -Werror=incompatible-pointer-types -o /app/vips-safe src/Campfire/Features/Storage/native/vips-safe.c $(pkg-config --cflags --libs vips)
RUN cc -O3 -Wall -Wextra -Werror -shared -fPIC -o /app/libcampfire_compression.so src/Campfire/Features/WebSupport/native/fragment-deflate.c -lz

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
USER root
RUN apt-get update && apt-get install -y --no-install-recommends libvips-tools ffmpeg file && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
COPY hooks/ /hooks/
RUN sed -i 's/\r$//' /hooks/pre-backup /hooks/post-restore && chmod +x /hooks/pre-backup /hooks/post-restore
RUN mkdir -p /rails/storage/db /rails/storage/files && chown -R app:app /rails/storage
USER app
ENV ASPNETCORE_URLS=http://+:3000 ASPNETCORE_ENVIRONMENT=Production CAMPFIRE_STORAGE=/rails/storage
ENV CAMPFIRE_VIPS_COMMAND=/app/vips-safe VIPS_BLOCK_UNTRUSTED=1
EXPOSE 3000
ENTRYPOINT ["dotnet", "Campfire.dll"]
