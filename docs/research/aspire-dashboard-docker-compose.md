# Wiring the Standalone Aspire Dashboard Container as an OTLP Sink in docker-compose

## TL;DR

The standalone Aspire Dashboard ships as a self-contained container image, `mcr.microsoft.com/dotnet/aspire-dashboard`, that has **no dependency on the Aspire AppHost**. It exposes three (really four) HTTP endpoints inside the container: `18888` (dashboard UI), `18889` (OTLP/gRPC), `18890` (OTLP/HTTP), and `18891` (MCP, not relevant here). By default the browser UI requires a one-time login token and the OTLP endpoint accepts telemetry **without any authentication at all** — there is no API key required unless you explicitly turn one on. For local dev, the whole thing (UI token + OTLP auth) can be switched off with a single env var, `DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true`. In compose, put the dashboard on its own service, point every other service's `OTEL_EXPORTER_OTLP_ENDPOINT` at `http://aspire-dashboard:18889` (the container-internal gRPC port, using the compose DNS name — NOT the host-mapped port), set `OTEL_EXPORTER_OTLP_PROTOCOL=grpc`, and each ASP.NET Core / worker service "just works" by calling `builder.Services.AddOpenTelemetry()....UseOtlpExporter()` — no code needs to manually read the `OTEL_EXPORTER_OTLP_*` variables, the SDK's `UseOtlpExporter()` helper reads them itself.

---

## 1. Container image name and tag

- **Full image reference:** `mcr.microsoft.com/dotnet/aspire-dashboard:13` (or a fully pinned patch tag such as `mcr.microsoft.com/dotnet/aspire-dashboard:13.5.0`, or `:latest`).
- Hosted on the **Microsoft Container Registry (MCR)**, `mcr.microsoft.com`, catalog page: [mcr.microsoft.com/artifact/mar/dotnet/aspire-dashboard/about](https://mcr.microsoft.com/artifact/mar/dotnet/aspire-dashboard/about).
- This is explicitly the **standalone** dashboard image — distinct from the Aspire AppHost NuGet/project tooling. Official docs describe it as: "the Aspire Dashboard is available as a [standalone Docker container](https://aspire.dev/dashboard/standalone/), which provides an OTLP endpoint that telemetry can be sent to... Using the dashboard in this way has no dependency on Aspire" — [Microsoft Learn: Example — Use OpenTelemetry with OTLP and the standalone Aspire Dashboard](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-otlp-example).
- As of Aug 2026, the project (and its GitHub org) has been rebranded from ".NET Aspire" / `dotnet/aspire` to plain "Aspire" / `microsoft/aspire`, and the current major version is **Aspire 13**, released alongside .NET 10 — [microsoft/aspire releases](https://github.com/microsoft/aspire/releases), [Aspire dashboard docs home](https://aspire.dev/dashboard/). The `dotnet/dotnet-docker` repo's image README lists concrete tags `13.5.0`, `13.5`, `13`, and `latest` for linux/amd64 and linux/arm64 — [dotnet/dotnet-docker README.aspire-dashboard.md](https://github.com/dotnet/dotnet-docker/blob/main/README.aspire-dashboard.md). For reproducible local-dev compose files, pin to the major tag (`:13`) or a full patch version rather than `:latest`.
- Note: the image also exists mirrored on Docker Hub as `microsoft/dotnet-aspire-dashboard`, but MCR is the canonical/first-party source — [Docker Hub microsoft/dotnet-aspire-dashboard](https://hub.docker.com/r/microsoft/dotnet-aspire-dashboard/).

## 2. Ports

The container listens on up to four HTTP endpoints, each configurable via its own env var. Defaults, confirmed against the actual `Aspire.Dashboard` source README in the `microsoft/aspire` repo ([src/Aspire.Dashboard/README.md](https://github.com/microsoft/aspire/blob/main/src/Aspire.Dashboard/README.md)) and the walkthrough in [Microsoft Learn's standalone OTLP example](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-otlp-example):

| Container-internal port | Purpose | Controlling env var |
|---|---|---|
| `18888` | **Dashboard UI** (browser frontend) | `ASPNETCORE_URLS` (default `http://localhost:18888`) |
| `18889` | **OTLP/gRPC** ingestion endpoint — this is the one services should target | `DOTNET_DASHBOARD_OTLP_ENDPOINT_URL` (default `http://localhost:18889`) |
| `18890` | **OTLP/HTTP** ingestion endpoint (Protobuf-over-HTTP) | `DOTNET_DASHBOARD_OTLP_HTTP_ENDPOINT_URL` (default `http://localhost:18890`) |
| `18891` | MCP endpoint (unrelated to telemetry, added post-rebrand) | `ASPIRE_DASHBOARD_MCP_ENDPOINT_URL` (default `http://localhost:18891`) |

**Inside-network vs host-mapped distinction — this is the detail that most guides get wrong:** the container's *internal* listening ports are always `18888`/`18889`/`18890` regardless of what you map them to on the host. The canonical Microsoft Learn `docker run` example deliberately maps *different* host ports to make the parallel with the "real" OTLP defaults obvious:

```bash
docker run --rm -it -p 18888:18888 -p 4317:18889 -p 4318:18890 -d --name aspire-dashboard \
    mcr.microsoft.com/dotnet/aspire-dashboard:latest
```
— i.e. host port `4317` → container port `18889` (gRPC), host port `4318` → container port `18890` (HTTP), host port `18888` → container port `18888` (UI). Source: [Microsoft Learn: Example — Use OpenTelemetry with OTLP and the standalone Aspire Dashboard](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-otlp-example) (step 7 shows the equivalent PowerShell form).

**For docker-compose, this host-remapping is irrelevant to service-to-collector traffic.** Other containers on the same compose network talk to the dashboard using the **compose service DNS name and the container's internal port** — e.g. `http://aspire-dashboard:18889` — never the host-mapped port. The host-port mapping (`ports:`) only matters for a human opening the dashboard UI at `http://localhost:18888` in a browser from outside the docker network. You do not need to publish the OTLP ports to the host at all unless you want to send telemetry from a process running directly on the host machine (outside compose).

## 3. Default authentication

Two independent auth surfaces, both documented on the authoritative Aspire security page [aspire.dev — Dashboard security considerations](https://aspire.dev/dashboard/security-considerations/) and cross-confirmed by [Microsoft Learn's OTLP example](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-otlp-example):

- **Browser UI:** secured by default with **browser-token authentication** (`Dashboard:Frontend:AuthMode=BrowserToken`, the default). On startup the container logs a URL containing a one-time token, e.g. `http://0.0.0.0:18888/login?t=123456780abcdef123456780` — you replace `0.0.0.0` with `localhost` and open it, or paste the token into the login screen. The token is regenerated every time the container restarts. Source: [Microsoft Learn: Example — Use OpenTelemetry with OTLP and the standalone Aspire Dashboard](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-otlp-example) (step 7 — "By default, the dashboard is secured with authentication that requires a token to log in. The token is displayed in the resulting output when running the container.").
- **OTLP ingestion endpoint:** the default `Dashboard:Otlp:AuthMode` is **`Unsecured`** — i.e., **no authentication is required by default** to POST telemetry into the dashboard's OTLP endpoint. Direct quote from [aspire.dev — Dashboard security considerations](https://aspire.dev/dashboard/security-considerations/): "The telemetry endpoint accepts incoming OTLP data without authentication... When the endpoint is unsecured, the dashboard is open to receiving telemetry from untrusted apps," with follow-on warnings about telemetry spoofing / resource exhaustion from unauthenticated sources.
- If you *do* choose to secure the OTLP endpoint, the mechanism is an API key sent as an `x-otlp-api-key` HTTP header, configured server-side via `Dashboard:Otlp:AuthMode=ApiKey` plus `Dashboard:Otlp:PrimaryApiKey=<key>` (and optionally `Dashboard:Otlp:SecondaryApiKey` for rotation). Source: same [aspire.dev security-considerations page](https://aspire.dev/dashboard/security-considerations/), which shows the equivalent CLI form `--Dashboard:Otlp:AuthMode=ApiKey --Dashboard:Otlp:PrimaryApiKey='{MY_APIKEY}'`, and the [microsoft/aspire Aspire.Dashboard README](https://github.com/microsoft/aspire/blob/main/src/Aspire.Dashboard/README.md), which documents the corresponding config keys `Dashboard:Otlp:AuthMode`, `Dashboard:Otlp:PrimaryApiKey`, `Dashboard:Otlp:SecondaryApiKey`. In docker-compose these are set as env vars using the ASP.NET Core double-underscore convention: `Dashboard__Otlp__AuthMode=ApiKey`, `Dashboard__Otlp__PrimaryApiKey=<key>`.

**Net effect for a fresh `docker run`/compose-up with zero configuration:** the dashboard UI is locked behind a token, but the OTLP endpoint is wide open — any container that can reach `aspire-dashboard:18889` on the compose network can push telemetry into it, no credentials needed.

## 4. Disabling/relaxing auth for local dev

The verified, source-confirmed shortcut environment variable is:

```
DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true
```

This single variable is documented as "a shortcut to configuring `Dashboard:Frontend:AuthMode` and `Dashboard:Otlp:AuthMode` to `Unsecured`" — i.e. it disables **both** the browser-token UI login **and** any OTLP auth requirement in one shot. Verified directly against:
- The primary source, [`microsoft/aspire` `src/Aspire.Dashboard/README.md`](https://github.com/microsoft/aspire/blob/main/src/Aspire.Dashboard/README.md), which lists `DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS` (using the `DOTNET_DASHBOARD_` prefix, same prefix family as `DOTNET_DASHBOARD_OTLP_ENDPOINT_URL` / `DOTNET_DASHBOARD_OTLP_HTTP_ENDPOINT_URL` / `DOTNET_DASHBOARD_CONFIG_FILE_PATH`).
- [`dotnet/dotnet-docker` `README.aspire-dashboard.md`](https://github.com/dotnet/dotnet-docker/blob/main/README.aspire-dashboard.md), whose example `docker run` command sets exactly `-e DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true`.
- A GitHub issue whose title itself uses the exact string: [dotnet/aspire#5490 — "Can't access dashboard resources when enabling DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS in 8.2.0"](https://github.com/dotnet/aspire/issues/5490).

**A documentation-naming caveat worth flagging explicitly (per the research brief's instruction to surface ambiguity rather than guess):** at least one fetch of the current [aspire.dev security-considerations page](https://aspire.dev/dashboard/security-considerations/) rendered the variable as `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS` instead. Cross-referencing multiple sources, the most consistent picture is that the post-rebrand project (now "Aspire" rather than ".NET Aspire", repo moved to `microsoft/aspire`) has started introducing **newer** features under an `ASPIRE_DASHBOARD_` prefix (confirmed examples: `ASPIRE_DASHBOARD_MCP_ENDPOINT_URL`, `ASPIRE_DASHBOARD_TELEMETRY_OPTOUT`), while **pre-existing** config keys — including the OTLP endpoint URLs and the unsecured-anonymous shortcut — retain their original `DOTNET_DASHBOARD_` prefix for backward compatibility, per the actual source README in the repo. Given the source-of-truth README (`microsoft/aspire/src/Aspire.Dashboard/README.md`) and the working `docker run` example in `dotnet/dotnet-docker` both consistently and unambiguously use `DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS`, that is the name to use. If it doesn't take effect against whatever exact image tag you pull, treat `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS` as a fallback to try (harmless to set both, since an unrecognized env var is simply ignored) and check the container's own `/README.md` or startup logs for the version you actually have.

**Security tradeoff:** setting this only makes sense for local/dev compose stacks. It removes the login-token wall from the dashboard UI (anyone who can reach port 18888 sees all resource/telemetry data, which can include sensitive request bodies, headers, exception details, connection strings in attributes, etc.) and removes all authentication from the OTLP ingestion endpoint (anyone/anything that can reach port 18889/18890 can inject arbitrary fake telemetry, or in principle exhaust dashboard memory since it stores telemetry in-process). Per [aspire.dev security-considerations](https://aspire.dev/dashboard/security-considerations/), this mode "should only be used during local development" and is explicitly called out as unsuitable "if you attempt to host the dashboard in other settings" (shared/staging/prod). Never expose an unsecured-anonymous dashboard container's ports beyond localhost/a private dev network.

## 5. docker-compose service definition — dashboard

```yaml
services:
  aspire-dashboard:
    image: mcr.microsoft.com/dotnet/aspire-dashboard:13
    container_name: aspire-dashboard
    environment:
      # Local-dev only: disables the UI login token AND all OTLP auth.
      - DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true
    ports:
      # Host-mapped UI port only — a human opens http://localhost:18888
      - "18888:18888"
      # Optional: expose OTLP ports to the host too, e.g. for telemetry
      # from processes running outside docker. Not needed for
      # service-to-service traffic inside the compose network.
      - "4317:18889"   # OTLP/gRPC
      - "4318:18890"   # OTLP/HTTP
    networks:
      - backend

networks:
  backend:
```

Other services reference it with `depends_on: [aspire-dashboard]` and talk to it over the compose network at `http://aspire-dashboard:18889` (gRPC, container-internal port — see §2).

## 6. Environment variables on the OTHER (producer) services

For each ASP.NET Core / worker service in the compose file (4+ of them, each with its own `OTEL_SERVICE_NAME`):

```yaml
services:
  order-service:
    build: ./src/OrderService
    environment:
      - OTEL_EXPORTER_OTLP_ENDPOINT=http://aspire-dashboard:18889
      - OTEL_EXPORTER_OTLP_PROTOCOL=grpc
      - OTEL_SERVICE_NAME=order-service
    depends_on:
      - aspire-dashboard
    networks:
      - backend
```

- `OTEL_EXPORTER_OTLP_ENDPOINT` — set to the **compose DNS service name** of the dashboard plus its **container-internal** gRPC port: `http://aspire-dashboard:18889`. (If you instead prefer OTLP/HTTP, use `http://aspire-dashboard:18890` and `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`; gRPC on 18889 is the port the official examples standardize on.)
- `OTEL_EXPORTER_OTLP_PROTOCOL` — `grpc` (matches port 18889) or `http/protobuf` (matches port 18890). These are standard OpenTelemetry SDK env vars, not Aspire-specific.
- `OTEL_SERVICE_NAME` — unique per service (`order-service`, `inventory-service`, `payments-service`, `notifications-worker`, etc.) — this is what distinguishes each "resource" in the dashboard's UI.
- **If the OTLP endpoint is NOT left unsecured** (i.e. you configured `Dashboard__Otlp__AuthMode=ApiKey` / `Dashboard__Otlp__PrimaryApiKey=<key>` on the dashboard service instead of using anonymous mode), each producer service must send that key as the `x-otlp-api-key` header on every OTLP request. With the standard OpenTelemetry .NET exporter this is done via the generic OTLP headers env var:
  ```
  OTEL_EXPORTER_OTLP_HEADERS=x-otlp-api-key=<the-same-key-as-Dashboard__Otlp__PrimaryApiKey>
  ```
  This is the standard OpenTelemetry SDK mechanism for attaching arbitrary headers to OTLP export requests (comma-separated `key=value` pairs), and the header name `x-otlp-api-key` itself is confirmed by [aspire.dev security-considerations](https://aspire.dev/dashboard/security-considerations/).

## 7. Minimal C# OpenTelemetry SDK wiring (.NET 10)

Confirmed from [Microsoft Learn's standalone OTLP walkthrough](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-otlp-example): **it "just works" from environment variables** — the code only needs to call `UseOtlpExporter()`, it does not need to manually read `OTEL_EXPORTER_OTLP_ENDPOINT` itself (the example even shows an optional manual check purely to decide *whether* to call `UseOtlpExporter()` at all, not to pass the value through by hand).

NuGet packages:
```xml
<ItemGroup>
  <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.9.0" />
  <PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.9.0" />
  <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.9.0" />
  <PackageReference Include="OpenTelemetry.Instrumentation.Http" Version="1.9.0" />
</ItemGroup>
```
(Use current versions at implementation time; these were current as of the cited doc.)

Program.cs (ASP.NET Core minimal API / generic host — identical pattern for a Worker Service using `HostApplicationBuilder`):

```csharp
var builder = WebApplication.CreateBuilder(args);

// Route ILogger output through OpenTelemetry too.
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
});

var otel = builder.Services.AddOpenTelemetry();

otel.ConfigureResource(r => r.AddService(
    serviceName: builder.Configuration["OTEL_SERVICE_NAME"] ?? builder.Environment.ApplicationName));

otel.WithMetrics(metrics =>
{
    metrics.AddAspNetCoreInstrumentation();
    metrics.AddHttpClientInstrumentation();
    metrics.AddRuntimeInstrumentation();
});

otel.WithTracing(tracing =>
{
    tracing.AddAspNetCoreInstrumentation();
    tracing.AddHttpClientInstrumentation();
});

// UseOtlpExporter() reads OTEL_EXPORTER_OTLP_ENDPOINT / _PROTOCOL / _HEADERS
// straight from environment variables (or IConfiguration) automatically —
// no manual wiring of the endpoint value is required.
otel.UseOtlpExporter();

var app = builder.Build();
app.MapGet("/", () => "ok");
app.Run();
```

`UseOtlpExporter()` (from `OpenTelemetry.Extensions.Hosting`) is the standard-library shortcut that registers the OTLP exporter for logs, metrics, and traces simultaneously and binds it to the standard `OTEL_EXPORTER_OTLP_*` env vars (`OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL`, `OTEL_EXPORTER_OTLP_HEADERS`) — this is generic OpenTelemetry .NET SDK behavior, not something specific to Aspire; the Aspire Dashboard is simply acting as a normal OTLP collector/sink on the other end. Source: [Microsoft Learn: Example — Use OpenTelemetry with OTLP and the standalone Aspire Dashboard](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-otlp-example) (steps 4–6), corroborated by the [OpenTelemetry .NET OTLP exporter configuration reference](https://github.com/open-telemetry/opentelemetry-dotnet/tree/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol#exporter-configuration) linked from that same doc.

If the OTLP endpoint is secured with an API key instead of anonymous mode, no code change is needed either — `OTEL_EXPORTER_OTLP_HEADERS=x-otlp-api-key=<key>` set as an env var is picked up by the same `UseOtlpExporter()` call.

---

## Complete example: docker-compose.yml + one dependent service

```yaml
services:
  aspire-dashboard:
    image: mcr.microsoft.com/dotnet/aspire-dashboard:13
    container_name: aspire-dashboard
    environment:
      - DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true   # local-dev only
    ports:
      - "18888:18888"   # dashboard UI -> http://localhost:18888
      - "4317:18889"    # optional: OTLP/gRPC reachable from the host too
      - "4318:18890"    # optional: OTLP/HTTP reachable from the host too
    networks:
      - backend

  order-service:
    build: ./src/OrderService
    environment:
      - OTEL_EXPORTER_OTLP_ENDPOINT=http://aspire-dashboard:18889
      - OTEL_EXPORTER_OTLP_PROTOCOL=grpc
      - OTEL_SERVICE_NAME=order-service
      # If NOT using anonymous mode, also set:
      # - OTEL_EXPORTER_OTLP_HEADERS=x-otlp-api-key=${DASHBOARD_OTLP_API_KEY}
    depends_on:
      - aspire-dashboard
    networks:
      - backend

  # inventory-service, payments-service, notifications-worker follow the
  # same pattern: same OTEL_EXPORTER_OTLP_ENDPOINT/PROTOCOL, unique
  # OTEL_SERVICE_NAME, depends_on: [aspire-dashboard].

networks:
  backend:
```

Matching C# wiring (Program.cs) — see §7 above for the full listing; the key three lines to remember are:

```csharp
var otel = builder.Services.AddOpenTelemetry();
otel.ConfigureResource(r => r.AddService(builder.Configuration["OTEL_SERVICE_NAME"]!));
otel.WithMetrics(m => m.AddAspNetCoreInstrumentation()).WithTracing(t => t.AddAspNetCoreInstrumentation());
otel.UseOtlpExporter(); // reads OTEL_EXPORTER_OTLP_ENDPOINT / _PROTOCOL / _HEADERS from env automatically
```

---

## Sources

- [aspire.dev — Dashboard security considerations](https://aspire.dev/dashboard/security-considerations/) (default OTLP-unauthenticated behavior; `x-otlp-api-key`; `Dashboard:Otlp:AuthMode`/`PrimaryApiKey`)
- [aspire.dev — Standalone Aspire Dashboard](https://aspire.dev/dashboard/standalone/) (docker run command, ports, image reference)
- [aspire.dev — Dashboard configuration reference](https://aspire.dev/dashboard/configuration/) (full env var list)
- [aspire.dev — Dashboard home](https://aspire.dev/dashboard/)
- [Microsoft Learn — Example: Use OpenTelemetry with OTLP and the standalone Aspire Dashboard](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-otlp-example) (full C# walkthrough, `UseOtlpExporter()`, docker run, browser-token login flow)
- [microsoft/aspire — src/Aspire.Dashboard/README.md](https://github.com/microsoft/aspire/blob/main/src/Aspire.Dashboard/README.md) (primary source for all env var names)
- [dotnet/dotnet-docker — README.aspire-dashboard.md](https://github.com/dotnet/dotnet-docker/blob/main/README.aspire-dashboard.md) (image tags, docker run example with `DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS`)
- [mcr.microsoft.com/artifact/mar/dotnet/aspire-dashboard/about](https://mcr.microsoft.com/artifact/mar/dotnet/aspire-dashboard/about) (MCR catalog page)
- [Docker Hub — microsoft/dotnet-aspire-dashboard](https://hub.docker.com/r/microsoft/dotnet-aspire-dashboard/) (mirror)
- [GitHub issue dotnet/aspire#5490](https://github.com/dotnet/aspire/issues/5490) (confirms exact string `DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS`)
- [microsoft/aspire releases](https://github.com/microsoft/aspire/releases) (confirms Aspire 13 as current major version, rebrand from dotnet/aspire)
- [OpenTelemetry .NET — OTLP exporter configuration reference](https://github.com/open-telemetry/opentelemetry-dotnet/tree/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol#exporter-configuration) (standard `OTEL_EXPORTER_OTLP_*` env var behavior)

### Unresolved ambiguity (flagged, not guessed)

The exact spelling of the "allow anonymous" env var showed minor inconsistency across sources: the primary GitHub source README and the `dotnet-docker` image README both consistently use `DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS`, which is what this document recommends. One fetch of the current aspire.dev security-considerations page rendered it as `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS`. Given the post-rebrand project appears to be migrating some (but not all) config keys from a `DOTNET_DASHBOARD_` to an `ASPIRE_DASHBOARD_` prefix, treat `DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS` as verified/primary and `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS` as a possible newer alias worth trying (or setting both) if the primary name doesn't take effect against whatever exact tag is pulled — confirm against the container's own bundled README/startup log for the specific version in use.
