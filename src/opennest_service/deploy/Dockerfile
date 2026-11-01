# OpenNest service image: the Rust web service + the static C++ opennest_job worker + the web page.
# Build from the REPOSITORY ROOT:
#
#     docker build -f src/opennest_service/deploy/Dockerfile -t opennest-service:0.1.0 .
#
# Run behind the Caddy reverse proxy in docker-compose.yml (HTTPS), or alone for a test:
#
#     docker run --rm -p 8080:8080 -v opennest-data:/data opennest-service:0.1.0

# ---- C++ worker (static) ----
FROM debian:bookworm AS worker
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends g++ cmake make \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY src/nest_physics_cpp/geometry              src/nest_physics_cpp/geometry
COPY src/nest_physics_cpp/solver                src/nest_physics_cpp/solver
COPY src/nest_physics_cpp/nest_physics_capi.cpp src/nest_physics_cpp/nest_physics_capi.h src/nest_physics_cpp/
COPY src/opennest_cpp/src/clipper2              src/opennest_cpp/src/clipper2
COPY src/opennest_job                           src/opennest_job
RUN cmake -S src/opennest_job -B /build -DCMAKE_BUILD_TYPE=Release -DOPENNEST_JOB_STATIC=ON \
 && cmake --build /build -j"$(nproc)" \
 && strip /build/opennest_job \
 && /build/opennest_job src/opennest_job/examples/clearance.json /tmp/smoke.json --quiet

# ---- Rust service ----
FROM rust:1.96-bookworm AS service
WORKDIR /src
COPY src/opennest_service/Cargo.toml src/opennest_service/Cargo.lock ./
COPY src/opennest_service/src ./src
RUN cargo build --release --locked

# ---- runtime ----
FROM debian:bookworm-slim
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends ca-certificates tini \
 && rm -rf /var/lib/apt/lists/* \
 && useradd --system --uid 10001 --home /data opennest \
 && mkdir -p /data && chown opennest /data
COPY --from=worker  /build/opennest_job                     /usr/local/bin/opennest_job
COPY --from=service /src/target/release/opennest_service   /usr/local/bin/opennest_service
COPY src/opennest_service/web                               /srv/web
ENV OPENNEST_BIND=0.0.0.0:8080 \
    OPENNEST_DATA=/data \
    OPENNEST_JOB_BIN=/usr/local/bin/opennest_job \
    OPENNEST_WEB=/srv/web
USER opennest
VOLUME /data
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s CMD ["/bin/bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /healthz HTTP/1.0\\r\\n\\r\\n' >&3 && grep -q ok <&3"]
ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/opennest_service"]
