# Fixture for the Docker backend's build-spec integration test. The RUN bakes a marker
# the test reads back, proving the image was built from this context (not just pulled).
FROM alpine:3
RUN echo built-from-context > /marker
