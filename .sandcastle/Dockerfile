FROM node:22-bookworm-slim

# The agent sandbox image. Rebuild it (`pnpm sandcastle:build-image`) when:
#   - global.json moves to a new .NET feature band, or
#   - the client apps bump @playwright/test to a new minor (the OS deps below
#     are pinned to one version).
# Layer order: stable/expensive layers first; AGENT_UID/GID args last, so a
# different host UID only rebuilds the cheap user + agent-CLI layers.
#
# There is deliberately NO Docker daemon in here. The container-backed test
# suites therefore cannot run in a sandbox — that is why the host runs the
# post-merge gates itself, and why the healer phase (if enabled) runs on the
# host rather than in a sandbox.

# System dependencies (libicu72 is required by the .NET runtime on bookworm)
RUN apt-get update && apt-get install -y --no-install-recommends \
  git \
  curl \
  jq \
  ca-certificates \
  libicu72 \
  && rm -rf /var/lib/apt/lists/*

# GitHub CLI — the agents read and comment on issues through it.
RUN curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg \
  | dd of=/usr/share/keyrings/githubcli-archive-keyring.gpg \
  && echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" \
  | tee /etc/apt/sources.list.d/github-cli.list > /dev/null \
  && apt-get update && apt-get install -y --no-install-recommends gh \
  && rm -rf /var/lib/apt/lists/*

# .NET SDK — the 10.0 channel tracks the latest release of that major, which is
# the window global.json (10.0.100, rollForward: latestFeature) accepts.
# Installed system-wide as root so agents cannot corrupt it.
ENV DOTNET_ROOT=/usr/share/dotnet \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true \
    DOTNET_NOLOGO=true
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
  && bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet \
  && ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
  && rm /tmp/dotnet-install.sh

# pnpm for the repo root (the orchestrator's own scripts and lockfile). npm
# stays available because the client apps are npm workspaces with their own
# package-lock.json files — removing it would make them uninstallable.
# Corepack downloads the pinned pnpm while building the image, so agents never
# fetch a package manager at runtime.
ENV PNPM_HOME=/usr/local/share/pnpm
ENV COREPACK_HOME=/usr/local/share/corepack
ENV PATH="${PNPM_HOME}/bin:${PNPM_HOME}:${PATH}"
RUN mkdir -p "${PNPM_HOME}" "${COREPACK_HOME}" \
  && corepack enable pnpm \
  && corepack prepare pnpm@12.3.4 --activate \
  # pnpm ships a per-platform native binary that `corepack prepare` does NOT
  # fetch — only the first run downloads it (into ${COREPACK_HOME}). Warm it up
  # here, while network is still enabled, or every later pnpm call fails once
  # COREPACK_ENABLE_NETWORK=0 lands below.
  && pnpm --version \
  && chmod -R a+rX "${COREPACK_HOME}"
ENV COREPACK_DEFAULT_TO_LATEST=0 \
    COREPACK_ENABLE_NETWORK=0

# Playwright chromium OS dependencies only (needs root/apt, so baked here).
# Browser binaries are NOT baked — they land in the shared host cache mounted
# at /home/agent/.cache/ms-playwright, so a version bump needs no rebuild.
RUN npx --yes playwright@1.60.0 install-deps chromium \
  && rm -rf /var/lib/apt/lists/* /root/.npm /root/.cache

# Build-args for UID/GID alignment: `sandcastle docker build-image` defaults
# these to the host user's UID/GID so image-built files and bind-mounted files
# share an owner without a runtime chown.
ARG AGENT_UID=1000
ARG AGENT_GID=1000

# Rename the base image's "node" user to "agent" and align UID/GID.
RUN groupmod -o -g $AGENT_GID node && usermod -o -u $AGENT_UID -g $AGENT_GID -d /home/agent -m -l agent node

USER ${AGENT_UID}:${AGENT_GID}

# Install the Claude Code CLI — every phase runs on it.
RUN curl -fsSL https://claude.ai/install.sh | bash

ENV PATH="/home/agent/.local/bin:$PATH"

# Pre-create the shared-cache mount points AS THE AGENT USER. Without this,
# Docker creates missing parent dirs (e.g. /home/agent/.nuget) as root at
# container start, and NuGet then fails to write ~/.nuget/NuGet/NuGet.Config —
# every restore in the sandbox dies with a permission error.
RUN mkdir -p /home/agent/.nuget/packages /home/agent/.npm \
      /home/agent/.cache/ms-playwright /home/agent/.cache/pnpm \
      /home/agent/.pnpm-store /home/agent/.config/pnpm

# Make the package cache locations explicit so they survive a HOME redirection.
ENV NUGET_PACKAGES=/home/agent/.nuget/packages
ENV npm_config_cache=/home/agent/.npm

# Keep both pnpm's content-addressed store and its metadata cache on the shared
# mounts, using pnpm's native user-level configuration.
RUN printf "cacheDir: /home/agent/.cache/pnpm\nstoreDir: /home/agent/.pnpm-store\n" > /home/agent/.config/pnpm/config.yaml

WORKDIR /home/agent

# In worktree sandbox mode, Sandcastle bind-mounts the git worktree at
# /home/agent/workspace and overrides the working directory to it at container
# start, so /home/agent/workspace must be able to serve as the project root.
# Claude Code authenticates from CLAUDE_CODE_OAUTH_TOKEN / ANTHROPIC_API_KEY in
# the environment — no credential file is staged into the image.
ENTRYPOINT ["sleep", "infinity"]
