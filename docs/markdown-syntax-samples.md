# Markdown syntax-highlighting samples

A visual test bench for the Markdown preview's fenced-code highlighter
(`Perch.Core/Data/CodeHighlight.cs`). Open this file in the Markdown viewer (right-click a
session → **Markdown files…**, then find it via **Search all project files…**) and skim the
preview pane to check each language's colours in both the light and dark preview themes.

Each block deliberately exercises the token classes the highlighter knows: **comments**,
**strings**, **numbers**, **keywords**, **type/builtin names**, **function calls**, and
(for shells) **variables**. If a colour looks wrong or a token is miscategorised, this is the
place to reproduce it.

> Tip: toggle **Preview: Light / Dark** in the header to check both palettes. Plain text uses a
> neutral colour; unknown languages (bottom) should render as one flat block, exactly as before.

---

## Shell (bash / sh / zsh)

```bash
#!/usr/bin/env bash
# build, test, and report
set -euo pipefail
export REPO="${HOME}/src/perch"

for f in "$REPO"/*.md; do
  lines=$(wc -l < "$f")   # count lines
  if [ "$lines" -gt 100 ]; then
    echo "big file: $f ($lines lines)"
  fi
done

dotnet test "$REPO/tests" && echo "passed" || exit 1
```

## PowerShell

```powershell
# Sum the sizes of every .md under the current tree
<#
  Block comment: this is a throwaway helper.
#>
function Get-MarkdownSize {
    param([string]$Path = ".")

    $total = 0
    foreach ($file in Get-ChildItem -Path $Path -Filter *.md -Recurse) {
        $total += $file.Length
    }
    Write-Host "Total: $total bytes"
}
```

## Python

```python
from dataclasses import dataclass

@dataclass
class Session:
    """A single Claude Code session."""
    id: str
    tokens: int = 0

    def label(self) -> str:
        # f-strings and numbers
        pct = self.tokens / 200_000 * 100
        return f"{self.id}: {pct:.1f}% of 0x30D40"

if __name__ == "__main__":
    print(Session("abc", tokens=12345).label())
```

## JavaScript

```javascript
// Debounce a function by `ms` milliseconds
const debounce = (fn, ms = 150) => {
  let timer = null;
  return (...args) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), ms);
  };
};

const log = debounce((msg) => console.log(`[${Date.now()}] ${msg}`), 250);
```

## TypeScript

```typescript
interface Session {
  id: string;
  tokens: number;
  status: "running" | "idle";
}

function summarise(sessions: readonly Session[]): number {
  // reduce over a typed array
  return sessions.reduce((sum, s) => sum + s.tokens, 0);
}

const total: number = summarise([{ id: "a", tokens: 42, status: "idle" }]);
```

## C#

```csharp
using System.Linq;

namespace Perch.Sample;

public sealed record Session(string Id, int Tokens)
{
    // Percentage of the 200k budget, clamped to [0, 100].
    public double Percent => Math.Clamp(Tokens / 200_000d * 100, 0, 100);

    public static int Sum(IEnumerable<Session> all) =>
        all.Sum(s => s.Tokens);   // 0xCAFE, 1_000, etc.
}
```

## C

```c
#include <stdio.h>

/* Return the number of set bits in x. */
int popcount(unsigned int x) {
    int count = 0;
    while (x != 0u) {
        count += x & 1;
        x >>= 1;
    }
    return count;   // e.g. popcount(0xFF) == 8
}
```

## C++

```cpp
#include <vector>
#include <string>

template <typename T>
class Stack {
public:
    void push(const T& value) { data_.push_back(value); }
    bool empty() const noexcept { return data_.empty(); }
private:
    std::vector<T> data_;   // backing store
};
```

## Go

```go
package main

import "fmt"

// Sum returns the total of every value in xs.
func Sum(xs []int) int {
    total := 0
    for _, x := range xs {
        total += x
    }
    return total
}

func main() {
    fmt.Printf("sum = %d\n", Sum([]int{1, 2, 3, 0x10}))
}
```

## Rust

```rust
/// A bounded counter that saturates at `max`.
struct Counter {
    value: u32,
    max: u32,
}

impl Counter {
    fn bump(&mut self) -> Option<u32> {
        match self.value < self.max {
            true => { self.value += 1; Some(self.value) }
            false => None,
        }
    }
}
```

## Java

```java
import java.util.List;

public final class Sessions {
    // Total tokens across all sessions.
    public static long total(List<Integer> tokens) {
        long sum = 0L;
        for (int t : tokens) {
            sum += t;
        }
        return sum;   // 0xFF, 1_000, 3.14 all count
    }
}
```

## Kotlin

```kotlin
data class Session(val id: String, val tokens: Int = 0)

fun summarise(sessions: List<Session>): Int {
    // sumOf over a typed list
    return sessions.sumOf { it.tokens }
}

fun main() {
    val total = summarise(listOf(Session("a", 42), Session("b", 0x10)))
    println("total = $total")
}
```

## Swift

```swift
struct Session {
    let id: String
    var tokens: Int = 0
}

func summarise(_ sessions: [Session]) -> Int {
    // reduce over the array
    return sessions.reduce(0) { $0 + $1.tokens }
}

let total = summarise([Session(id: "a", tokens: 42)])  // 0x2A
```

## PHP

```php
<?php
// Sum the tokens across every session.
function summarise(array $sessions): int {
    $total = 0;
    foreach ($sessions as $s) {
        $total += $s['tokens'];   # both // and # are comments
    }
    return $total;
}

echo summarise([['tokens' => 42], ['tokens' => 0x10]]);
```

## Ruby

```ruby
# Sum the tokens across every session.
def summarise(sessions)
  sessions.reduce(0) { |sum, s| sum + s[:tokens] }
end

class Session
  attr_reader :id, :tokens
  def initialize(id, tokens = 0)
    @id, @tokens = id, tokens
  end
end

puts summarise([{ tokens: 42 }, { tokens: 0x10 }])
```

## SQL

```sql
-- Top projects by token spend this month
SELECT p.name, SUM(s.tokens) AS total
FROM sessions AS s
JOIN projects AS p ON p.id = s.project_id
WHERE s.started_at >= '2026-08-01'
GROUP BY p.name
HAVING SUM(s.tokens) > 100000
ORDER BY total DESC
LIMIT 10;
```

## JSON

```json
{
  "session": "abc123",
  "tokens": 12345,
  "active": true,
  "model": null,
  "tags": ["docs", "markdown"],
  "usage": { "input": 1000, "output": 2500 }
}
```

## YAML

```yaml
# A sample settings file
version: 1
enabled: true
retries: 3
window:
  width: 1040
  height: 720
themes:
  - midnight
  - paper
notes: "quotes are optional but colourful"
```

## TOML / INI

```toml
# App configuration
title = "Perch"
version = "0.3.37"

[window]
width = 1040
height = 720
dark = true
```

## CSS

```css
/* Overlay card */
.overlay-card {
  background: #1e1f29;
  border-radius: 12px;
  padding: 8px 12px;
  opacity: 0.95 !important;
}
```

## HTML / XML

```html
<!-- A small fragment -->
<section class="overlay" data-state="running">
  <h1>Perch</h1>
  <p>Monitoring <strong>3</strong> sessions.</p>
</section>
```

## Dockerfile

```dockerfile
# Build the app head
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Perch.App -c Release -o /out

ENV PERCH_ENV=production
EXPOSE 8080
ENTRYPOINT ["/out/perch"]
```

## Makefile

```makefile
# Common developer tasks
REPO := $(HOME)/src/perch

.PHONY: test build
build:
	dotnet build $(REPO)/perch.slnx

test: build
	dotnet test $(REPO)/tests   # run the suite
```

---

## Fallbacks (should render as plain, uncoloured blocks)

An unknown language tag:

```brainfuck
++++++++[>++++[>++>+++>+++>+<<<<-]>+>+>->>+[<]<-]>>.>---.
```

No language tag at all:

```
just some text
with 123 numbers and // slashes that are not comments
```

And inline code stays a chip, not a highlighted block: `dotnet test`, `Palette.Fixed`, `0xFF`.
