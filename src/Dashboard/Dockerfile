# Build context is `src/Dashboard` (unlike Api/Dockerfile and Management/Dockerfile, which build from `src/` to
# reach their shared Core project — Dashboard has no such cross-project dependency).
FROM node:24-alpine AS build
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY . .
RUN npm run build

FROM nginx:1.27-alpine AS runtime
COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx.conf.template /etc/nginx/templates/default.conf.template

# Overridden at deploy time to the real Management origin (e.g. its Container Apps HTTPS FQDN) — see
# nginx.conf.template and deploy/README.md.
ENV MANAGEMENT_UPSTREAM=http://management:8080
EXPOSE 8080
