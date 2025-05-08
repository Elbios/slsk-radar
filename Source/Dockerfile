# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /src
RUN apt-get update && apt-get install -y clang lld libc-dev && rm -rf /var/lib/apt/lists/*
RUN apt-get update && apt-get install -y zlib1g-dev



# copy only the files that affect restore so the layer can be cached
COPY ["Spotify.Slsk.Integration.sln", "./"]
COPY ["Assemblies/Spotify.Slsk.Integration/*.csproj", "Assemblies/Spotify.Slsk.Integration/"]
COPY ["Assemblies/Spotify.Slsk.Integration.Cli/*.csproj", "Assemblies/Spotify.Slsk.Integration.Cli/"]

RUN dotnet restore Spotify.Slsk.Integration.sln

# now copy the rest of the source
COPY . .

# publish the CLI project as native AOT for musl
RUN dotnet publish \
    Assemblies/Spotify.Slsk.Integration.Cli/Spotify.Slsk.Integration.Cli.csproj \
    -c Release -r linux-musl-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -o /out

# export the artefact so you can grab it from the host
FROM scratch AS export
COPY --from=build /out /artifact
