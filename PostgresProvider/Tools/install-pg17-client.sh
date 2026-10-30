#!/bin/sh
set -eu

# Run at image build time as root. PostgreSQL's signed repository supplies the
# matching client major instead of Ubuntu's moving default PostgreSQL version.
. /etc/os-release
if [ "$ID" != ubuntu ] || [ "$VERSION_CODENAME" != resolute ]; then
    echo 'This image recipe is pinned to Ubuntu 26.04 (resolute).' >&2
    exit 1
fi

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y --no-install-recommends ca-certificates curl
install -d /usr/share/postgresql-common/pgdg
curl --fail --silent --show-error --location \
    https://www.postgresql.org/media/keys/ACCC4CF8.asc \
    --output /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc
cat > /etc/apt/sources.list.d/pgdg.sources <<'SOURCES'
Types: deb
URIs: https://apt.postgresql.org/pub/repos/apt
Suites: resolute-pgdg
Components: main
Signed-By: /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc
SOURCES
apt-get update -qq
apt-get install -y --no-install-recommends postgresql-client-17
/usr/lib/postgresql/17/bin/pg_dump --version
/usr/lib/postgresql/17/bin/pg_restore --version
rm -rf /var/lib/apt/lists/*
