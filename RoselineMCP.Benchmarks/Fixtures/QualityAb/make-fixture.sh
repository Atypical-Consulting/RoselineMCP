#!/usr/bin/env bash
# Generates the throwaway multi-project fixture for the end-to-end quality A/B
# (docs/AGENT-BENCHMARK.md, "Planned: does the compile gate change quality?").
# Usage: make-fixture.sh <target-dir>   (must not exist). Never commit the output.
#
#   Core  <-  Consumer  <-  Tests        (arrow = "is referenced by")
#
# The agent is asked to change a public signature in Core; Consumer is never named in the prompt.
set -euo pipefail
dir="${1:?target dir}"
[ -e "$dir" ] && { echo "refusing: $dir exists" >&2; exit 1; }
mkdir -p "$dir/Core" "$dir/Consumer" "$dir/Tests"
cd "$dir"

cat > global.json <<'J'
{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }
J

proj() { # name, extra
cat > "$1/$1.csproj" <<P
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
$2
</Project>
P
}
proj Core ""
proj Consumer '  <ItemGroup><ProjectReference Include="..\Core\Core.csproj" /></ItemGroup>'
proj Tests '  <ItemGroup><ProjectReference Include="..\Consumer\Consumer.csproj" /></ItemGroup>'

cat > Core/OrderPricing.cs <<'C'
namespace Core;

public static class OrderPricing
{
    /// <summary>Total for a line: unit price times quantity.</summary>
    public static decimal ComputeTotal(decimal unitPrice, int quantity)
    {
        return unitPrice * quantity;
    }
}
C

cat > Consumer/InvoiceService.cs <<'C'
using Core;

namespace Consumer;

public sealed class InvoiceService
{
    public decimal LineTotal(decimal unitPrice, int quantity) =>
        OrderPricing.ComputeTotal(unitPrice, quantity);

    public decimal InvoiceTotal(IEnumerable<(decimal UnitPrice, int Quantity)> lines)
    {
        var sum = 0m;
        foreach (var (price, qty) in lines)
        {
            sum += OrderPricing.ComputeTotal(price, qty);
        }
        return sum;
    }
}
C

cat > Consumer/ReportBuilder.cs <<'C'
using Core;

namespace Consumer;

public static class ReportBuilder
{
    public static string Describe(decimal unitPrice, int quantity) =>
        $"{quantity} x {unitPrice} = {OrderPricing.ComputeTotal(unitPrice, quantity)}";
}
C

cat > Tests/InvoiceChecks.cs <<'C'
using Consumer;

namespace Tests;

public static class InvoiceChecks
{
    public static bool LineTotalIsPositive() => new InvoiceService().LineTotal(10m, 3) > 0m;
}
C

dotnet new sln -n Fixture --format sln >/dev/null
dotnet sln Fixture.sln add Core/Core.csproj Consumer/Consumer.csproj Tests/Tests.csproj >/dev/null
printf 'bin/\nobj/\n' > .gitignore
git init -q -b main && git add -A
git -c user.email=bench@example.invalid -c user.name=bench commit -q -m "fixture"
echo "fixture ready: $dir"
