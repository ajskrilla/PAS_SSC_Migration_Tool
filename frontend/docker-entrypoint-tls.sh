#!/bin/sh
set -e
CERT_DIR=/tmp/certs
mkdir -p "$CERT_DIR"
if [ ! -f "$CERT_DIR/server.crt" ]; then
  echo "Generating self-signed TLS certificate (lab use)..."
  openssl req -x509 -nodes -newkey rsa:2048 -days 825 \
    -keyout "$CERT_DIR/server.key" -out "$CERT_DIR/server.crt" \
    -subj "/CN=pas-migration.local" \
    -addext "subjectAltName=DNS:localhost,IP:127.0.0.1" 2>/dev/null
  echo "Certificate generated at $CERT_DIR."
fi
exec nginx -g 'daemon off;'
