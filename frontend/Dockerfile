# Build the SPA
FROM node:22-slim AS build
WORKDIR /app
COPY package.json ./
RUN npm install
COPY . .
RUN npm run build

# Serve over HTTPS (self-signed cert generated at startup) + reverse-proxy /api and /health.
# Unprivileged nginx listens on 8080 (redirect) and 8443 (TLS); host maps 80->8080, 443->8443.
FROM nginxinc/nginx-unprivileged:1.27-alpine AS final
USER root
RUN apk add --no-cache openssl
COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY docker-entrypoint-tls.sh /docker-entrypoint-tls.sh
RUN chmod +x /docker-entrypoint-tls.sh
USER 101
EXPOSE 8080 8443
ENTRYPOINT ["/docker-entrypoint-tls.sh"]
