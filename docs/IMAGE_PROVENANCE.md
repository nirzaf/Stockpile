# Image provenance and SBOM verification

The Docker workflow is configured to build `linux/amd64` and `linux/arm64` and
request BuildKit provenance and SBOM attestations. A successful published
workflow run records the full source commit and manifest digest in its summary.
A tag such as `latest` is only a pointer; use a verified digest for an audit or
deployment record.

The Docker and Release workflows currently configure this image name:

```text
ghcr.io/nirzaf/merconiq
```

The workflow configuration does not establish that a package or pullable
artifact currently exists. Verify package visibility and the exact candidate
digest before use. Repository identity and deployment status are tracked in
[`REPOSITORY_CUTOVER.md`](REPOSITORY_CUTOVER.md).

## Record a candidate

For an owner-approved published candidate, copy the exact values from the Docker
workflow summary:

```bash
IMAGE=ghcr.io/nirzaf/merconiq
SOURCE_SHA=<full-workflow-source-sha>
MANIFEST_DIGEST=sha256:<full-manifest-digest>
IMAGE_REF="$IMAGE@$MANIFEST_DIGEST"
```

Confirm that the repository, source commit, workflow run, and image are the same
candidate before using it. The digest is the immutable subject; the tag is not.

## Inspect the shipped manifest

These commands read registry metadata and do not build or publish anything:

```bash
docker buildx imagetools inspect "$IMAGE_REF"
docker buildx imagetools inspect --raw "$IMAGE_REF" > manifest.json
oras discover "$IMAGE_REF" --format json > referrers.json
```

The manifest inspection must show both configured platform subjects. A configured
platform is not runtime evidence: record a separate smoke result for each platform
or state that it was not validated.

## Inspect BuildKit attestations

For a published candidate, the workflow is configured to request BuildKit
provenance and SBOM attestations. Verify that records were actually published
for the candidate using registry tooling, then inspect their media types and
subject:

```bash
oras discover "$IMAGE_REF"
# For each returned attestation digest:
oras manifest fetch "$IMAGE@$ATTESTATION_DIGEST" --pretty
```

The attestation subject must identify the same manifest digest as `MANIFEST_DIGEST`.
If a referrer is missing, has a different subject, or is from an unexpected image,
reject the record and investigate the registry/build output.

These BuildKit records are useful build metadata, but this repository does not claim
that they provide a signed SLSA level, a reproducible-build guarantee, or an
independently authenticated GitHub/Sigstore identity. The workflow identity and
registry permissions remain trust assumptions.

## Deliberate mismatch check

The following must fail the candidate comparison even when the mutable tag resolves:

```bash
EXPECTED="$IMAGE@$MANIFEST_DIGEST"
SUBSTITUTED="$IMAGE@sha256:<different-digest>"
test "$EXPECTED" != "$SUBSTITUTED"
docker buildx imagetools inspect "$SUBSTITUTED"
```

Do not replace `<different-digest>` with a made-up value in an evidence record. For a
real verification run, use a digest returned by the registry and record the command,
tool version, workflow URL, source SHA, manifest digest, platform digests, attestation
subjects, and result. No published release or retained artifact is claimed by this
document until that owner-authorized run is recorded in `docs/RELEASE_CANDIDATE.md`.

Related controls:
[Docker workflow](https://github.com/nirzaf/merconiq/blob/master/.github/workflows/docker.yml),
[release-candidate evidence](RELEASE_CANDIDATE.md),
[project provenance](PROVENANCE.md).
