# syntax=docker/dockerfile:1.7
# Inno Setup compiler under Wine: builds the Raskladushka installer on Linux.
#
# Build the image (context is this folder only):
#   docker build -t raskladushka-iscc installer/docker
# Build the installer from the repository root:
#   docker run --rm -v "$PWD:/src" raskladushka-iscc \
#     /DAppVersion=0.2.0 /DPublishDir=..\artifacts\publish /DRuntimeDir=..\artifacts\vcrt \
#     installer\Raskladushka.iss
# The result lands in artifacts/installer and is owned by the owner of the repository folder.

FROM debian:bookworm-20261005-slim

# Inno Setup 6 is a 32-bit program, so Wine needs its i386 half as well.
# Debian's innoextract 1.9 cannot read installers of Inno Setup 6.4 and newer,
# so the official installer is checked by SHA-256 and run silently instead.
ARG INNO_VERSION=6.7.3
ARG INNO_TAG=is-6_7_3
ARG INNO_SHA256=9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732

ENV DEBIAN_FRONTEND=noninteractive \
    WINEPREFIX=/opt/wine \
    WINEARCH=win64 \
    WINEDEBUG=-all \
    WINEDLLOVERRIDES="mscoree,mshtml="

RUN dpkg --add-architecture i386 \
 && apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl wine wine32 wine64 xauth xvfb \
 && rm -rf /var/lib/apt/lists/*

RUN curl -fsSL -o /tmp/innosetup.exe \
      "https://github.com/jrsoftware/issrc/releases/download/${INNO_TAG}/innosetup-${INNO_VERSION}.exe" \
 && echo "${INNO_SHA256}  /tmp/innosetup.exe" | sha256sum -c - \
 && xvfb-run -a wine /tmp/innosetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- \
      /CURRENTUSER /PORTABLE=1 /DIR='C:\InnoSetup' \
 && wineserver -w \
 && rm /tmp/innosetup.exe \
 && test -f "$WINEPREFIX/drive_c/InnoSetup/ISCC.exe" \
 && test -f "$WINEPREFIX/drive_c/InnoSetup/Languages/Russian.isl"

COPY --chmod=755 <<'EOF' /usr/local/bin/iscc
#!/bin/sh
# Runs ISCC from /src and hands the output back to the owner of the mounted repository.
set -u
cd /src || exit 2
wine 'C:\InnoSetup\ISCC.exe' '/OZ:\src\artifacts\installer' "$@"
status=$?
wineserver -w
owner="$(stat -c '%u:%g' /src)"
[ -d /src/artifacts ] && chown "$owner" /src/artifacts
[ -d /src/artifacts/installer ] && chown -R "$owner" /src/artifacts/installer
exit "$status"
EOF

WORKDIR /src
ENTRYPOINT ["/usr/local/bin/iscc"]
