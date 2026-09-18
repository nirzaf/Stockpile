#!/usr/bin/env bash
set -euo pipefail

test -s _site/index.html
test -s _site/USER_GUIDE.html
test -s _site/assets/css/style.css
grep -q 'USER_GUIDE.html' _site/index.html
grep -Fq 'href="/merconiq/assets/css/style.css?' _site/index.html
