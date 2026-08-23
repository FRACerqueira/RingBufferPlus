# ![RingBufferPlus Logo](https://raw.githubusercontent.com/FRACerqueira/RingBufferPlus/refs/heads/main/icon.png) Welcome to RingBufferPlus

## **The generic ring buffer with auto-scaler (elastic buffer).**

[![License](https://img.shields.io/badge/License-MIT-brightgreen.svg)](LICENSE)
[![Build](https://github.com/FRACerqueira/RingBufferPlus/workflows/Build/badge.svg)](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)
[![Downloads](https://img.shields.io/nuget/dt/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)

RingBufferPlus is a bounded, thread-safe pool of reusable instances of `T` — built once via a factory you supply, acquired and returned by callers — with an optional elastic capacity that scales up and down at runtime, manually or automatically, to keep resource usage matched to demand instead of provisioning for the worst case.

<!--
  Links below are repo-relative, not absolute GitHub URLs, so they keep working when
  this file is viewed from any branch/tag instead of only from main. This file is also
  packed as the NuGet package readme (see PackageReadmeFile in RingBufferPlus.csproj) -
  nuget.org resolves these relative links itself using RepositoryUrl/RepositoryType
  (set in the csproj), so no absolute URL is needed for that case either.
-->

### What's new in the latest version

Full version history has moved to [CHANGELOG.md](CHANGELOG.md).

**v5.0.0 (latest released version)** is a complete, coordinated product overhaul with sweeping breaking changes, plus one additive feature carried forward without a breaking change: native OpenTelemetry-compatible observability (metrics and tracing, see [ADR008](doc/adr/ADR008V01-native-observability-via-open-telemetry-compatible-metrics-and-tracing.md)) — see the [ADRs](doc/adr/indexadrs.md) and the CHANGELOG's "Breaking changes v5.0.0" section (the sole migration reference — no separate migration guide is provided) for full context. v4.x no longer receives fixes now that v5.0.0 has shipped.

## Installing

```
dotnet add package RingBufferPlus [--prerelease]
```

**_Note: `[--prerelease]` for pre-release versions._**

## Quickstart

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

For elastic (scaling) buffers, dependency injection, RabbitMQ channel pooling, and every other builder option, see the guides below.

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

Generated per-type/per-member reference: [src/docs/docindex.md](./src/docs/docindex.md).

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
