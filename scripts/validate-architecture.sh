#!/usr/bin/env bash
set -euo pipefail

fail() {
  echo "Architecture validation failed: $1" >&2
  exit 1
}

core='src/Merconiq.Core/Merconiq.Core.csproj'
infrastructure='src/Merconiq.Infrastructure/Merconiq.Infrastructure.csproj'
web='src/Merconiq.Web/Merconiq.Web.csproj'
tests='tests/Merconiq.Tests/Merconiq.Tests.csproj'

for project in "$core" "$infrastructure" "$web" "$tests"; do
  [[ -f "$project" ]] || fail "missing project $project"
done

core_refs=$(grep -c '<ProjectReference' "$core" || true)
[[ "$core_refs" == '0' ]] || fail 'Core must not reference another project'

grep -Fq '..\Merconiq.Core\Merconiq.Core.csproj' "$infrastructure" \
  || fail 'Infrastructure must reference Core'
[[ $(grep -c '<ProjectReference' "$infrastructure") == '1' ]] \
  || fail 'Infrastructure must reference only Core'

grep -Fq '..\Merconiq.Core\Merconiq.Core.csproj' "$web" \
  || fail 'Web must reference Core'
grep -Fq '..\Merconiq.Infrastructure\Merconiq.Infrastructure.csproj' "$web" \
  || fail 'Web must reference Infrastructure'
[[ $(grep -c '<ProjectReference' "$web") == '2' ]] \
  || fail 'Web must reference only Core and Infrastructure'

grep -Fq '..\..\src\Merconiq.Core\Merconiq.Core.csproj' "$tests" \
  || fail 'Tests must reference Core through src'
grep -Fq '..\..\src\Merconiq.Infrastructure\Merconiq.Infrastructure.csproj' "$tests" \
  || fail 'Tests must reference Infrastructure through src'
grep -Fq '..\..\src\Merconiq.Web\Merconiq.Web.csproj' "$tests" \
  || fail 'Tests must reference Web through src'
[[ $(grep -c '<ProjectReference' "$tests") == '3' ]] \
  || fail 'Tests must reference the three production projects'

echo 'Architecture validation passed: Core <- Infrastructure <- Web; Tests reference production projects.'
