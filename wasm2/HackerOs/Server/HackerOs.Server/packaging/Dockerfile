# HackerOS Server image (ADR 0191 §4): ghcr.io/kazo-ca/hackeros-server.
#
# Built by release.yml from the linux-x64 and linux-arm64 publications of build/publish-server.sh,
# unpacked as <context>/amd64/ and <context>/arm64/ (docker buildx sets TARGETARCH):
#
#   docker buildx build --platform linux/amd64,linux/arm64 -f Dockerfile <context>
#
# Run it on this computer only (the published port is bound to the loopback address):
#
#   docker run -d --name hackeros-server -p 127.0.0.1:5888:8080 -v hackeros-data:/data ghcr.io/kazo-ca/hackeros-server
#   docker exec hackeros-server ./HackerOs.Server pair admin
#
# Guide: https://github.com/Kazo-ca/HackerOs/blob/main/doc/en/wasm/dev/installable-server.md
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0

ARG TARGETARCH
WORKDIR /app
COPY ${TARGETARCH}/ /app/

# Inside the container the server listens on every interface of the container only; what reaches
# it is decided by `docker run -p`. AllowedHosts still refuses any other Host header (DNS rebinding).
ENV HACKEROS_Kestrel__Endpoints__Http__Url=http://+:8080 \
    HACKEROS_AllowedHosts="localhost;127.0.0.1;[::1]" \
    HACKEROS_ConnectionStrings__HackerOsDb="Data Source=/data/hackeros.db" \
    HACKEROS_BlobStorage__Root=/data/blobs \
    HACKEROS_ServerBackup__Root=/data/backups \
    HACKEROS_HackerOs__Pairing__Root=/data/pairing

RUN mkdir -p /data && chown app:app /data && chmod +x /app/HackerOs.Server
USER app
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["/app/HackerOs.Server"]
