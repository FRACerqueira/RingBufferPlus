# ![RingBufferPlus Logo](https://raw.githubusercontent.com/FRACerqueira/RingBufferPlus/refs/heads/main/icon.png) RingBufferPlus

## **Stop provisioning for worst case. Pool it, scale it, let it breathe.**

[![License](https://img.shields.io/badge/License-MIT-brightgreen.svg)](LICENSE)
[![Build](https://github.com/FRACerqueira/RingBufferPlus/workflows/Build/badge.svg)](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)
[![Downloads](https://img.shields.io/nuget/dt/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)

RingBufferPlus is a bounded, thread-safe pool for any expensive-to-create resource — database connections, RabbitMQ channels, HTTP clients, whatever your `Factory` builds. You get one back with `AcquireAsync`, you return it by disposing it, and the pool takes care of keeping enough of them around without you having to guess a number up front.

<!--
  Links below are repo-relative, not absolute GitHub URLs, so they keep working when
  this file is viewed from any branch/tag instead of only from main. This file is also
  packed as the NuGet package readme (see PackageReadmeFile in RingBufferPlus.csproj) -
  nuget.org resolves these relative links itself using RepositoryUrl/RepositoryType
  (set in the csproj), so no absolute URL is needed for that case either.
-->

## Why RingBufferPlus

- **Elastic capacity, on by default.** An elastic pool grows the instant callers are genuinely waiting, and shrinks predictively as demand trends down — no cron job, no manual tuning required to get useful behavior.
- **Three signals, one owner.** A floor guard, a backlog-reactive signal, and a predictive Monitor all watch the pool; a single-consumer engine arbitrates between them so capacity never races itself.
- **Manual override when you need it.** `SwitchToAsync` pins the pool to a capacity for a set duration — the automatic signals keep running underneath, they just take a back seat.
- **Native observability, zero extra dependency.** Metrics and traces via the .NET-shipped `Meter`/`ActivitySource` — plug in any OpenTelemetry exporter, nothing extra to install.
- **Health checks built in.** `HeartBeat` inspects a live item on a schedule and replaces it automatically if it's gone bad — no separate watchdog to write.
- **Async all the way down.** `IAsyncDisposable` throughout; no sync-over-async traps.
- **.NET 8, 9, and 10** — one package, three target frameworks.

### What's new in the latest version

Full version history has moved to [CHANGELOG.md](CHANGELOG.md).

**v6.0.0 (latest released version)** is a complete, coordinated product overhaul with sweeping breaking changes to the concurrency model, autoscale algorithm, and public fluent API surface — see the [ADRs](doc/adr/indexadrs.md) and the CHANGELOG's "Breaking changes" section for v6.0.0 (the sole migration reference — no separate migration guide is provided). v5.x no longer receives fixes now that v6.0.0 has shipped.

Every v6.0.0 change went through a 12-round adversarial pre-release audit before shipping — see the [audit report](doc/audits/v6.0.0-pre-release-audit.md) for the method and what it found.

## Installing

```
dotnet add package RingBufferPlus [--prerelease]
```

**_Note: `[--prerelease]` for pre-release versions._**

## Quickstart

A fixed pool, for when you know your capacity up front:

```csharp
Random rnd = new();
CancellationToken cancellation = default;

var rb = await RingBuffer<int>.New("MyBuffer")
    .Factory((_) => Task.FromResult(rnd.Next(1, 10)))
    .FixedCapacity(3)
    .BuildWarmupAsync(cancellation);

await using (var buffer = await rb.AcquireAsync(cancellation))
{
    if (buffer.Successful)
    {
        Console.WriteLine($"value: {buffer.Current}, elapsed: {buffer.ElapsedTime}");
    }
}

await rb.DisposeAsync();
```

An elastic pool, for when demand varies and you'd rather not guess:

```csharp
var rb = await RingBuffer<int>.New("MyBuffer")
    .Factory((_) => Task.FromResult(rnd.Next(1, 10)))
    .ElasticCapacity(minCapacity: 2, maxCapacity: 10, target: 4)
    .BuildWarmupAsync(cancellation);

// No further setup needed - the floor guard, backlog-reactive signal, and
// predictive Monitor are already watching this pool.
```

For dependency injection, RabbitMQ channel pooling, and every other builder option, see the guides below.

## Guides

- [Concepts](doc/guides/concepts.md) — the mental model: lifecycle, `Capacity`/`MinCapacity`/`MaxCapacity`, thread-safety, when *not* to use this library.
- [Fixed capacity](doc/guides/usage-fixed-capacity.md)
- [Pinning capacity manually](doc/guides/usage-elastic-manual-scale.md)
- [Elastic autoscale](doc/guides/usage-elastic-autoscale.md)
- [Locking `SwitchToAsync` while scaling](doc/guides/usage-lock-when-scaling.md)
- [HeartBeat](doc/guides/usage-heartbeat.md)
- [RabbitMQ channel pooling](doc/guides/usage-rabbitmq.md)
- [ASP.NET Core / generic host dependency injection](doc/guides/usage-dependency-injection.md)
- [Observability (metrics and tracing)](doc/guides/usage-observability.md)

## Examples

For runnable samples, see the [Samples directory](./samples):

- [RingBufferPlusBasicSample](./samples/RingBufferPlusBasicSample) — fixed capacity with HeartBeat.
- [RingBufferPlusBasicManualScale](./samples/RingBufferPlusBasicManualScale) — elastic capacity, pinning capacity manually.
- [RingBufferPlusApiSample](./samples/RingBufferPlusApiSample) — elastic capacity, pinning capacity manually, in an ASP.NET Core API.
- [RingBufferPlusBasicTriggerScale](./samples/RingBufferPlusBasicTriggerScale) — elastic capacity with autoscale.
- [RingBufferPlusRabbitSample](./samples/RingBufferPlusRabbitSample) — RabbitMQ channel pooling with autoscale.

## API Reference

Generated per-type/per-member reference: [doc/api/docindex.md](./doc/api/docindex.md).

## Architecture Decision Records (ADR)

RingBufferPlus documents its significant architectural and design decisions as
**Architecture Decision Records (ADR)**, following the
[AdrPlus](https://github.com/FRACerqueira/AdrPlus) convention. Each record
captures the context, the decision, the alternatives considered, and the
consequences — so the reasoning behind the library's design stays traceable over
time.

👉 See the **[ADR index](doc/adr/indexadrs.md)** for the full list of decisions.

## Code of Conduct

This project has adopted the code of conduct defined by the Contributor Covenant to clarify expected behavior in our community.
For more information see the [Code of Conduct](CODE_OF_CONDUCT.md).

## Contributing

See the [Contributing guide](CONTRIBUTING.md) for developer documentation, and the [Architecture overview](doc/architecture/overview.md) for a map of the main components before you dive into the source.

## Security

To report a (suspected) security vulnerability, see [SECURITY.md](SECURITY.md).

## Credits

This work was inspired by [**Luiz Carlos Faria**](https://github.com/luizcarlosfaria/Oragon.Common.RingBuffer) project.

**API documentation generated by**

- [XmlDocMarkdown](https://github.com/ejball/XmlDocMarkdown), Copyright (c) 2024 [Ed Ball](https://github.com/ejball)
    - See an unrefined customization to contain header and other adjustments in project [XmlDocMarkdownGenerator](./src/XmlDocMarkdownGenerator)

## License

Copyright 2022 @ Fernando Cerqueira

RingBufferPlus is licensed under the MIT license. See [LICENSE](LICENSE).
