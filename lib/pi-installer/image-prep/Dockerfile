# hermod-pi image-prep container
#
# Builds a cloud-init-injected Ubuntu Server 24.04 arm64 Raspberry Pi image.
# Uses mtools (mcopy) to write into the FAT32 system-boot partition directly
# from the image file — zero kernel-privilege requirements, runs cleanly under
# rootless podman with no flags at all.

FROM python:3.11-slim-bookworm

ARG DEBIAN_FRONTEND=noninteractive

# Runtime deps:
#   xz-utils    -> decompress / recompress .img.xz
#   mtools      -> mcopy into FAT32 image (replaces losetup/mount)
#   parted      -> read partition offsets from image
#   util-linux  -> sfdisk (alt partition info), dd
#   dosfstools  -> FAT32 tooling (fsck if we need to repair after write)
#   ca-certificates, curl -> fetching upstream image + SHA256SUMS
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        xz-utils \
        mtools \
        parted \
        util-linux \
        dosfstools \
        ca-certificates \
        curl \
 && rm -rf /var/lib/apt/lists/*

# Python deps: jinja2 for templates, PyYAML for config parse + YAML emit,
# jsonschema to reject unknown config fields strictly.
RUN pip install --no-cache-dir \
        jinja2==3.1.4 \
        PyYAML==6.0.2 \
        jsonschema==4.23.0

WORKDIR /app

COPY prep.py /app/prep.py
COPY templates/ /app/templates/

# Cache dir for downloaded upstream images; caller may volume-mount this
# to persist across builds.
RUN mkdir -p /cache /output && chmod 0700 /cache /output

ENV HERMOD_PI_CACHE=/cache \
    HERMOD_PI_OUTPUT=/output \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

ENTRYPOINT ["python3", "/app/prep.py"]
CMD ["--config", "/config.yaml", "--pubkey", "/pubkey.pub", "--output-dir", "/output"]
