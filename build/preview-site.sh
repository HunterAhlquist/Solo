#!/usr/bin/env bash
#
# Builds the documentation site the way GitHub Pages does and serves it at
# http://localhost:4000/Solo/ for a look before pushing.
#
# The build runs in a container holding the github-pages gem, the same gem the
# Pages workflow's Jekyll action runs, so no Ruby is needed on this machine.
# The image is built once and reused. Raise PAGES_GEM_VERSION when the action
# in .github/workflows/pages.yml moves to a newer gem.
#
# The site is built and served under /Solo on purpose. That is where a
# project's Pages site lives, and a link that forgets `relative_url` works at
# the root and breaks there.
#
# There is no watch mode. To see an edit, run this again with --build-only in
# a second terminal and reload the page; the server keeps running.
#
# Requires Docker and python3.
#
# Usage: build/preview-site.sh [--build-only]
#   --build-only   write site/_site and exit, without serving it
#   PORT=4001 build/preview-site.sh   serve on another port

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
image="solo-pages-preview"
gem_version="${PAGES_GEM_VERSION:-232}"
port="${PORT:-4000}"

"$root/build/stage-site-assets.sh"

if ! docker image inspect "$image:$gem_version" >/dev/null 2>&1; then
    echo "Building the $image:$gem_version image (first run only)..."
    docker build -t "$image:$gem_version" - <<DOCKERFILE
FROM ruby:3.3-slim
RUN apt-get update && apt-get install -y --no-install-recommends build-essential git \\
    && rm -rf /var/lib/apt/lists/*
RUN gem install github-pages -v $gem_version --no-document
DOCKERFILE
fi

# PAGES_ENV=dotcom and PAGES_REPO_NWO make the build work out the address a
# project site has on github.io, and so the /Solo base path, without asking
# GitHub's API. The workflow sets the address from the Pages settings instead.
# The destination is site/_site/Solo so that a plain file server on site/_site
# answers at /Solo/.
#
# The working directory matters: the plugin that turns a [link](page.md) into a
# site address works out paths from it, and gets none of them right from "/".
docker run --rm \
    -e JEKYLL_ENV=production \
    -e PAGES_ENV=dotcom \
    -e PAGES_REPO_NWO=HunterAhlquist/Solo \
    -v "$root/site:/site" \
    -w /site \
    "$image:$gem_version" \
    github-pages build --source /site --destination /site/_site/Solo
echo "Built site/_site/Solo."

if [ "${1:-}" = "--build-only" ]; then
    exit 0
fi

echo "Serving http://localhost:$port/Solo/ (Ctrl+C to stop)."
python3 -m http.server "$port" --directory "$root/site/_site"
