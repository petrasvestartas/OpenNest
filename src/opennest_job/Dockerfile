# OpenNest worker image: the static opennest_job binary on a distroless base (no shell, no package manager).
# Build from the REPOSITORY ROOT (the build needs src/nest_physics_cpp next to src/opennest_job):
#
#     docker build -f src/opennest_job/Dockerfile -t opennest-job:0.1.0 .
#
# Run one job (job.json in the current directory, result written next to it):
#
#     docker run --rm --cpus 4 --memory 4g --network none -v "$PWD:/work" opennest-job:0.1.0 \
#         /work/job.json /work/result.json --events /work/events.ndjson --max-wall 900
#
# The container stops on SIGTERM with the best-so-far layout written (`docker stop` sends SIGTERM).

FROM ubuntu:24.04 AS build
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends g++ cmake make \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY src/nest_physics_cpp/geometry          src/nest_physics_cpp/geometry
COPY src/nest_physics_cpp/solver            src/nest_physics_cpp/solver
COPY src/nest_physics_cpp/nest_physics_capi.cpp src/nest_physics_cpp/nest_physics_capi.h src/nest_physics_cpp/
COPY src/opennest_cpp/src/clipper2          src/opennest_cpp/src/clipper2
COPY src/opennest_job                       src/opennest_job
ARG OPENNEST_JOB_VERSION=0.1.0
RUN cmake -S src/opennest_job -B /build -DCMAKE_BUILD_TYPE=Release -DOPENNEST_JOB_STATIC=ON \
          -DOPENNEST_JOB_VERSION=${OPENNEST_JOB_VERSION} \
 && cmake --build /build -j"$(nproc)" \
 && strip /build/opennest_job \
 && /build/opennest_job --version \
 && /build/opennest_job src/opennest_job/examples/small.json /tmp/smoke.json --quiet

FROM gcr.io/distroless/static-debian12:nonroot
COPY --from=build /build/opennest_job /usr/local/bin/opennest_job
USER nonroot
ENTRYPOINT ["/usr/local/bin/opennest_job"]
