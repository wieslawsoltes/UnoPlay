#!/usr/bin/env bash
# Only the release job receives contents:write. Publish from a complete, verified draft.
set -euo pipefail

: "${TAG:?}" "${GITHUB_REPOSITORY:?}"
[[ "$TAG" == "v$(cat VERSION)" ]]
commit=$(git rev-parse HEAD)
api="repos/$GITHUB_REPOSITORY"
notes="releases/$TAG.md"
assets=(packages/UnoPlay-linux-x64.tar.gz packages/UnoPlay-osx-arm64.tar.gz packages/UnoPlay-win-x64.zip packages/SHA256SUMS)

# Resolve annotated tags to the underlying commit. Never move a previously cut tag.
remote_tag=$(git ls-remote --tags origin "refs/tags/$TAG")
if [[ -n $remote_tag ]]; then
  git fetch --no-tags origin "refs/tags/$TAG"
  [[ $(git rev-parse 'FETCH_HEAD^{commit}') == "$commit" ]] || {
    echo "$TAG already points at a different commit" >&2; exit 1;
  }
else
  gh api --method POST "$api/git/refs" -f ref="refs/tags/$TAG" -f sha="$commit" --silent
fi

# Listing also finds unpublished drafts. A permissions or API failure aborts the job.
release=$(gh api --paginate "$api/releases?per_page=100" --jq ".[] | select(.tag_name == \"$TAG\") | [.id, .draft] | @tsv")
if [[ -z $release ]]; then
  gh release create "$TAG" --draft --verify-tag --title "UnoPlay $TAG" --notes-file "$notes"
  id=$(gh release view "$TAG" --json databaseId --jq .databaseId)
  draft=true
else
  read -r id draft <<< "$release"
fi

if [[ $draft == true ]]; then
  gh release upload "$TAG" "${assets[@]}" --clobber
fi
inventory=$(gh api "$api/releases/$id/assets?per_page=100" --jq '.[] | [.name, .state, .digest] | @tsv')
for asset in "${assets[@]}"; do
  digest=$(awk -F '\t' -v n="${asset##*/}" '$1 == n && $2 == "uploaded" { print $3 }' <<< "$inventory")
  [[ $digest =~ ^sha256:[0-9a-f]{64}$ ]] || { echo "Missing verified asset: $asset" >&2; exit 1; }
  if [[ $draft == true ]]; then
    hash=$(sha256sum "$asset")
    [[ $digest == "sha256:${hash%% *}" ]] || { echo "Digest mismatch: $asset" >&2; exit 1; }
  fi
done
if [[ $draft == true ]]; then
  gh release edit "$TAG" --title "UnoPlay $TAG" --notes-file "$notes" --draft=false --latest
fi
gh release view "$TAG" --json url --jq .url
