#!/bin/bash
for f in *.cs; do
  echo "=== $f ==="
  grep -n "^\s*public\|^\s*private" "$f" | grep -E "Update|Send|Recv|WaitSnd|PeekSize|Check|Flush|Input" | head -20
  echo
done
