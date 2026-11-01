#!/usr/bin/env bash
set -e

NM="VakifAtlas"
BB="src/BuildingBlocks"
SVC="src/Services"
GW="src/Gateway"

add_refs() {
  local tpath="$1"
  local tar="$2"
  local rpath="$3"
  shift 3
  local tar_proj="$tpath/$NM.$tar/$NM.$tar.csproj"
  local ref_projs=()
  for r in "$@"; do
    ref_projs+=("$rpath/$NM.$r/$NM.$r.csproj")
  done
  dotnet add "$tar_proj" reference "${ref_projs[@]}"
}

dotnet new sln -n $NM
dotnet new gitattributes
dotnet new gitignore
dotnet new buildprops
dotnet new packagesprops
dotnet new globaljson --sdk-version 10.0.401 --roll-forward latestFeature
dotnet new tool-manifest
dotnet tool install dotnet-ef

# BUILDING BLOCKS
bb="ServiceDefaults"; bb_path="$BB/$NM.$bb"; bb_proj="$bb_path/$NM.$bb.csproj";
dotnet new classlib -n "$NM.$bb" -o "$bb_path"
dotnet sln add "$bb_proj"
rm -f "$bb_path/Class1.cs" && touch "$bb_path/$bb.cs"
dotnet add "$bb_proj" package Asp.Versioning.Http
dotnet add "$bb_proj" package Asp.Versioning.Mvc.ApiExplorer
dotnet add "$bb_proj" package Asp.Versioning.OpenApi
dotnet add "$bb_proj" package Elastic.CommonSchema.Serilog
dotnet add "$bb_proj" package Microsoft.AspNetCore.Authentication.JwtBearer
dotnet add "$bb_proj" package Microsoft.AspNetCore.OpenApi
dotnet add "$bb_proj" package Npgsql.OpenTelemetry
dotnet add "$bb_proj" package OpenTelemetry.Exporter.OpenTelemetryProtocol
dotnet add "$bb_proj" package OpenTelemetry.Extensions.Hosting
dotnet add "$bb_proj" package OpenTelemetry.Instrumentation.AspNetCore
dotnet add "$bb_proj" package OpenTelemetry.Instrumentation.Http
dotnet add "$bb_proj" package OpenTelemetry.Instrumentation.Runtime
dotnet add "$bb_proj" package Scalar.AspNetCore
dotnet add "$bb_proj" package Serilog.AspNetCore
dotnet add "$bb_proj" package StackExchange.Redis
python3 -c '
import sys
p = sys.argv[1]
with open(p, "r", encoding="utf-8") as f:
    c = f.read()
if "Microsoft.AspNetCore.App" not in c:
    with open(p, "w", encoding="utf-8") as f:
        f.write(c.replace("</Project>", "  <ItemGroup>\n    <FrameworkReference Include=\"Microsoft.AspNetCore.App\" />\n  </ItemGroup>\n</Project>"))
' "$bb_proj"

bb="SharedKernel"; bb_path="$BB/$NM.$bb"; bb_proj="$bb_path/$NM.$bb.csproj";
dotnet new classlib -n "$NM.$bb" -o "$bb_path"
dotnet sln add "$bb_proj"
rm -f "$bb_path/Class1.cs" && touch "$bb_path/$bb.cs"

bb="Contracts"; bb_path="$BB/$NM.$bb"; bb_proj="$bb_path/$NM.$bb.csproj";
dotnet new classlib -n "$NM.$bb" -o "$bb_path"
dotnet sln add "$bb_proj"
rm -f "$bb_path/Class1.cs" && touch "$bb_path/$bb.cs"

bb="CatalogClient"; bb_path="$BB/$NM.$bb"; bb_proj="$bb_path/$NM.$bb.csproj";
dotnet new classlib -n "$NM.$bb" -o "$bb_path"
dotnet sln add "$bb_proj"
rm -f "$bb_path/Class1.cs" && touch "$bb_path/$bb.cs"
dotnet add "$bb_proj" package Microsoft.Extensions.Caching.Hybrid
dotnet add "$bb_proj" package Microsoft.Extensions.Caching.StackExchangeRedis
dotnet add "$bb_proj" package Microsoft.Extensions.Http.Resilience
python3 -c '
import sys
p = sys.argv[1]
with open(p, "r", encoding="utf-8") as f:
    c = f.read()
if "Microsoft.AspNetCore.App" not in c:
    with open(p, "w", encoding="utf-8") as f:
        f.write(c.replace("</Project>", "  <ItemGroup>\n    <FrameworkReference Include=\"Microsoft.AspNetCore.App\" />\n  </ItemGroup>\n</Project>"))
' "$bb_proj"

bb="Media"; bb_path="$BB/$NM.$bb"; bb_proj="$bb_path/$NM.$bb.csproj";
dotnet new classlib -n "$NM.$bb" -o "$bb_path"
dotnet sln add "$bb_proj"
rm -f "$bb_path/Class1.cs" && touch "$bb_path/$bb.cs"
dotnet add "$bb_proj" package AWSSDK.S3
dotnet add "$bb_proj" package Microsoft.EntityFrameworkCore.Relational
dotnet add "$bb_proj" package SkiaSharp
dotnet add "$bb_proj" package SkiaSharp.NativeAssets.Linux.NoDependencies
python3 -c '
import sys
p = sys.argv[1]
with open(p, "r", encoding="utf-8") as f:
    c = f.read()
if "Microsoft.AspNetCore.App" not in c:
    with open(p, "w", encoding="utf-8") as f:
        f.write(c.replace("</Project>", "  <ItemGroup>\n    <FrameworkReference Include=\"Microsoft.AspNetCore.App\" />\n  </ItemGroup>\n</Project>"))
' "$bb_proj"

bb="Messaging"; bb_path="$BB/$NM.$bb"; bb_proj="$bb_path/$NM.$bb.csproj";
dotnet new classlib -n "$NM.$bb" -o "$bb_path"
dotnet sln add "$bb_proj"
rm -f "$bb_path/Class1.cs" && touch "$bb_path/$bb.cs"
dotnet add "$bb_proj" package RabbitMQ.Client
dotnet add "$bb_proj" package Microsoft.EntityFrameworkCore.Relational
python3 -c '
import sys
p = sys.argv[1]
with open(p, "r", encoding="utf-8") as f:
    c = f.read()
if "Microsoft.AspNetCore.App" not in c:
    with open(p, "w", encoding="utf-8") as f:
        f.write(c.replace("</Project>", "  <ItemGroup>\n    <FrameworkReference Include=\"Microsoft.AspNetCore.App\" />\n  </ItemGroup>\n</Project>"))
' "$bb_proj"

bb="Persistence"; bb_path="$BB/$NM.$bb"; bb_proj="$bb_path/$NM.$bb.csproj";
dotnet new classlib -n "$NM.$bb" -o "$bb_path"
dotnet sln add "$bb_proj"
rm -f "$bb_path/Class1.cs" && touch "$bb_path/$bb.cs"
dotnet add "$bb_proj" package EFCore.NamingConventions
dotnet add "$bb_proj" package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add "$bb_proj" package Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore
python3 -c '
import sys
p = sys.argv[1]
with open(p, "r", encoding="utf-8") as f:
    c = f.read()
if "Microsoft.AspNetCore.App" not in c:
    with open(p, "w", encoding="utf-8") as f:
        f.write(c.replace("</Project>", "  <ItemGroup>\n    <FrameworkReference Include=\"Microsoft.AspNetCore.App\" />\n  </ItemGroup>\n</Project>"))
' "$bb_proj"

add_refs $BB "CatalogClient"    $BB "ServiceDefaults"
add_refs $BB "Media"            $BB "ServiceDefaults"
add_refs $BB "Messaging"        $BB "Contracts" "ServiceDefaults"
add_refs $BB "Persistence"      $BB "Messaging"
add_refs $BB "ServiceDefaults"  $BB "SharedKernel"

# API GATEWAY
gw="Gateway"; gw_path="$GW/$NM.$gw"; gw_proj="$gw_path/$NM.$gw.csproj";
dotnet new web -n "$NM.$gw" -o "$gw_path"
dotnet sln add "$gw_proj"
touch "$gw_path/"$gw"Routes.cs"
dotnet add "$gw_proj" package Yarp.ReverseProxy

add_refs $GW "Gateway"          $BB "ServiceDefaults"

# SERVICES
svc="Assistant"; svc_path="$SVC/$svc"; api="$NM.$svc.Api"; dom="$NM.$svc.Domain"
dotnet new web -n "$api" -o "$svc_path/$api"
dotnet new classlib -n "$dom" -o "$svc_path/$dom"
dotnet sln add "$svc_path/$api/$api.csproj" "$svc_path/$dom/$dom.csproj"
rm -f "$svc_path/$dom/Class1.cs"
mkdir -p "$svc_path/$api/Features" "$svc_path/$api/Infrastructure"
dotnet add "$svc_path/$api/$api.csproj" package Microsoft.EntityFrameworkCore.Design
add_refs $svc_path ""$svc".Api"      $svc_path ""$svc".Domain"
add_refs $svc_path ""$svc".Api"      $BB "CatalogClient"
add_refs $svc_path ""$svc".Api"      $BB "Persistence"
add_refs $svc_path ""$svc".Domain"   $BB "SharedKernel"

svc="Catalog"; svc_path="$SVC/$svc"; api="$NM.$svc.Api"; dom="$NM.$svc.Domain"
dotnet new web -n "$api" -o "$svc_path/$api"
dotnet new classlib -n "$dom" -o "$svc_path/$dom"
dotnet sln add "$svc_path/$api/$api.csproj" "$svc_path/$dom/$dom.csproj"
rm -f "$svc_path/$dom/Class1.cs"
mkdir -p "$svc_path/$api/Features" "$svc_path/$api/Infrastructure"
dotnet add "$svc_path/$api/$api.csproj" package Microsoft.EntityFrameworkCore.Design
dotnet add "$svc_path/$api/$api.csproj" package Microsoft.AspNetCore.OutputCaching.StackExchangeRedis
add_refs $svc_path ""$svc".Api"      $svc_path ""$svc".Domain"
add_refs $svc_path ""$svc".Api"      $BB "Media"
add_refs $svc_path ""$svc".Api"      $BB "Persistence"
add_refs $svc_path ""$svc".Domain"   $BB "SharedKernel"

svc="CivilProtection"; svc_path="$SVC/$svc"; api="$NM.$svc.Api"; dom="$NM.$svc.Domain"
dotnet new web -n "$api" -o "$svc_path/$api"
dotnet new classlib -n "$dom" -o "$svc_path/$dom"
dotnet sln add "$svc_path/$api/$api.csproj" "$svc_path/$dom/$dom.csproj"
rm -f "$svc_path/$dom/Class1.cs"
mkdir -p "$svc_path/$api/Features" "$svc_path/$api/Infrastructure"
dotnet add "$svc_path/$api/$api.csproj" package Microsoft.EntityFrameworkCore.Design
add_refs $svc_path ""$svc".Api"      $svc_path ""$svc".Domain"
add_refs $svc_path ""$svc".Api"      $BB "CatalogClient"
add_refs $svc_path ""$svc".Api"      $BB "Media"
add_refs $svc_path ""$svc".Api"      $BB "Persistence"
add_refs $svc_path ""$svc".Domain"   $BB "SharedKernel"

svc="Feedback"; svc_path="$SVC/$svc"; api="$NM.$svc.Api"; dom="$NM.$svc.Domain"
dotnet new web -n "$api" -o "$svc_path/$api"
dotnet new classlib -n "$dom" -o "$svc_path/$dom"
dotnet sln add "$svc_path/$api/$api.csproj" "$svc_path/$dom/$dom.csproj"
rm -f "$svc_path/$dom/Class1.cs"
mkdir -p "$svc_path/$api/Features" "$svc_path/$api/Infrastructure"
dotnet add "$svc_path/$api/$api.csproj" package Microsoft.EntityFrameworkCore.Design
add_refs $svc_path ""$svc".Api"      $svc_path ""$svc".Domain"
add_refs $svc_path ""$svc".Api"      $BB "Persistence"
add_refs $svc_path ""$svc".Domain"   $BB "SharedKernel"

svc="Gamification"; svc_path="$SVC/$svc"; api="$NM.$svc.Api"; dom="$NM.$svc.Domain"
dotnet new web -n "$api" -o "$svc_path/$api"
dotnet new classlib -n "$dom" -o "$svc_path/$dom"
dotnet sln add "$svc_path/$api/$api.csproj" "$svc_path/$dom/$dom.csproj"
rm -f "$svc_path/$dom/Class1.cs"
mkdir -p "$svc_path/$api/Features" "$svc_path/$api/Infrastructure"
dotnet add "$svc_path/$api/$api.csproj" package Microsoft.EntityFrameworkCore.Design
add_refs $svc_path ""$svc".Api"      $svc_path ""$svc".Domain"
add_refs $svc_path ""$svc".Api"      $BB "Persistence"
add_refs $svc_path ""$svc".Domain"   $BB "SharedKernel"

svc="Identity"; svc_path="$SVC/$svc"; api="$NM.$svc.Api"; dom="$NM.$svc.Domain"
dotnet new web -n "$api" -o "$svc_path/$api"
dotnet new classlib -n "$dom" -o "$svc_path/$dom"
dotnet sln add "$svc_path/$api/$api.csproj" "$svc_path/$dom/$dom.csproj"
rm -f "$svc_path/$dom/Class1.cs"
mkdir -p "$svc_path/$api/Features" "$svc_path/$api/Infrastructure"
dotnet add "$svc_path/$api/$api.csproj" package Microsoft.EntityFrameworkCore.Design
dotnet add "$svc_path/$api/$api.csproj" package Konscious.Security.Cryptography.Argon2
dotnet add "$svc_path/$api/$api.csproj" package MailKit
dotnet add "$svc_path/$api/$api.csproj" package System.Security.Cryptography.Pkcs
add_refs $svc_path ""$svc".Api"      $svc_path ""$svc".Domain"
add_refs $svc_path ""$svc".Api"      $BB "Persistence"
add_refs $svc_path ""$svc".Domain"   $BB "SharedKernel"

svc="Media"; svc_path="$SVC/$svc"; api="$NM.$svc.Api"; dom="$NM.$svc.Domain"
dotnet new web -n "$api" -o "$svc_path/$api"
dotnet new classlib -n "$dom" -o "$svc_path/$dom"
dotnet sln add "$svc_path/$api/$api.csproj" "$svc_path/$dom/$dom.csproj"
rm -f "$svc_path/$dom/Class1.cs"
mkdir -p "$svc_path/$api/Features" "$svc_path/$api/Infrastructure"
dotnet add "$svc_path/$api/$api.csproj" package Microsoft.EntityFrameworkCore.Design
add_refs $svc_path ""$svc".Api"      $svc_path ""$svc".Domain"
add_refs $svc_path ""$svc".Api"      $BB "Media"
add_refs $svc_path ""$svc".Api"      $BB "Persistence"
add_refs $svc_path ""$svc".Domain"   $BB "SharedKernel"

svc="Memories"; svc_path="$SVC/$svc"; api="$NM.$svc.Api"; dom="$NM.$svc.Domain"
dotnet new web -n "$api" -o "$svc_path/$api"
dotnet new classlib -n "$dom" -o "$svc_path/$dom"
dotnet sln add "$svc_path/$api/$api.csproj" "$svc_path/$dom/$dom.csproj"
rm -f "$svc_path/$dom/Class1.cs"
mkdir -p "$svc_path/$api/Features" "$svc_path/$api/Infrastructure"
dotnet add "$svc_path/$api/$api.csproj" package Microsoft.EntityFrameworkCore.Design
add_refs $svc_path ""$svc".Api"      $svc_path ""$svc".Domain"
add_refs $svc_path ""$svc".Api"      $BB "CatalogClient"
add_refs $svc_path ""$svc".Api"      $BB "Media"
add_refs $svc_path ""$svc".Api"      $BB "Persistence"
add_refs $svc_path ""$svc".Domain"   $BB "SharedKernel"

svc="Routes"; svc_path="$SVC/$svc"; api="$NM.$svc.Api"; dom="$NM.$svc.Domain"
dotnet new web -n "$api" -o "$svc_path/$api"
dotnet new classlib -n "$dom" -o "$svc_path/$dom"
dotnet sln add "$svc_path/$api/$api.csproj" "$svc_path/$dom/$dom.csproj"
rm -f "$svc_path/$dom/Class1.cs"
mkdir -p "$svc_path/$api/Features" "$svc_path/$api/Infrastructure"
dotnet add "$svc_path/$api/$api.csproj" package Microsoft.EntityFrameworkCore.Design
add_refs $svc_path ""$svc".Api"      $svc_path ""$svc".Domain"
add_refs $svc_path ""$svc".Api"      $BB "CatalogClient"
add_refs $svc_path ""$svc".Api"      $BB "Persistence"
add_refs $svc_path ""$svc".Domain"   $BB "SharedKernel"
