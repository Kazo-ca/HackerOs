# Kazo OS Server image (ADR 0191 §4): ghcr.io/kazo-ca/kazoos-server.
#
# Built by release.yml from the linux-x64 and linux-arm64 publications of build/publish-server.sh,
# unpacked as <context>/amd64/ and <context>/arm64/ (docker buildx sets TARGETARCH):
#
#   docker buildx build --platform linux/amd64,linux/arm64 -f Dockerfile <context>
#
# Run it on this computer only (the published port is bound to the loopback address):
#
#   docker run -d --name kazoos-server -p 127.0.0.1:5888:8080 -v kazoos-data:/data ghcr.io/kazo-ca/kazoos-server
#   docker exec kazoos-server ./Kazo.Os.Server pair admin
#
# Guide: https://github.com/Kazo-ca/Os/blob/main/doc/en/wasm/dev/installable-server.md
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0

ARG TARGETARCH
WORKDIR /app
COPY ${TARGETARCH}/ /app/

# Inside the container the server listens on every interface of the container only; what reaches
# it is decided by `docker run -p`. AllowedHosts still refuses any other Host header (DNS rebinding).
ENV KAZOOS_Kestrel__Endpoints__Http__Url=http://+:8080 \
    KAZOOS_AllowedHosts="localhost;127.0.0.1;[::1]" \
    KAZOOS_ConnectionStrings__KazoOsDb="Data Source=/data/kazoos.db" \
    KAZOOS_BlobStorage__Root=/data/blobs \
    KAZOOS_ServerBackup__Root=/data/backups \
    KAZOOS_KazoOs__Pairing__Root=/data/pairing

RUN mkdir -p /data && chown app:app /data && chmod +x /app/Kazo.Os.Server
USER app
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["/app/Kazo.Os.Server"]
