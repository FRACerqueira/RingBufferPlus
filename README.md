# ![RingBufferPlus Logo](https://raw.githubusercontent.com/FRACerqueira/RingBufferPlus/refs/heads/main/icon.png) Welcome to RingBufferPlus

## **The generic ring buffer with auto-scaler (elastic buffer).**

[![License](https://img.shields.io/badge/License-MIT-brightgreen.svg)](https://github.com/FRACerqueira/RingBufferPlus/blob/main/LICENSE)
[![Build](https://github.com/FRACerqueira/RingBufferPlus/workflows/Build/badge.svg)](https://github.com/FRACerqueira/RingBufferPlus/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)
[![Downloads](https://img.shields.io/nuget/dt/RingBufferPlus)](https://www.nuget.org/packages/RingBufferPlus/)

RingBufferPlus is a bounded, thread-safe pool of reusable instances of `T` — built once via a factory you supply, acquired and returned by callers — with an optional elastic capacity that scales up and down at runtime, manually or automatically, to keep resource usage matched to demand instead of provisioning for the worst case.

<!--
  This file is also packed as the NuGet package readme (see PackageReadmeFile in
  RingBufferPlus.csproj). Every link below is an absolute GitHub URL rather than a
  repo-relative path, deliberately, so it still resolves correctly when rendered from
  the package page on nuget.org, not just from github.com.
-->

### What's new in the latest version

Full version history has moved to [CHANGELOG.md](https://github.com/FRACerqueira/RingBufferPlus/blob/main/CHANGELOG.md).

**v4.0.1 (latest released version)** added .NET 10 support.

**v5.0.0 (in progress)** is a complete, coordinated product overhaul with sweeping breaking changes — see the [Action Plan](https://github.com/FRACerqueira/RingBufferPlus/blob/main/doc/action-plan.md), the [ADRs](https://github.com/FRACerqueira/RingBufferPlus/tree/main/doc/adr), and the CHANGELOG's "Breaking changes v5.0.0" section (the sole migration reference — no separate migration guide is planned) for full context. v4.x will no longer receive fixes once v5.0.0 ships.

## Installing

```
dotnet add package RingBufferPlus [--prerelease]
```

**_Note: `[--prerelease]` for pre-release versions._**

## Quickstart

```csharp
Random rnd = new();

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

- [Concepts](https://github.com/FRACerqueira/RingBufferPlus/blob/main/doc/guides/concepts.md) — the mental model: lifecycle, `Capacity`/`MinCapacity`/`MaxCapacity`, thread-safety, when *not* to use this library.
- [Fixed capacity](https://github.com/FRACerqueira/RingBufferPlus/blob/main/doc/guides/usage-fixed-capacity.md)
- [Elastic capacity with manual scale](https://github.com/FRACerqueira/RingBufferPlus/blob/main/doc/guides/usage-elastic-manual-scale.md)
- [Elastic capacity with autoscale on acquire fault](https://github.com/FRACerqueira/RingBufferPlus/blob/main/doc/guides/usage-elastic-autoscale.md)
- [Locking `SwitchToAsync` while scaling](https://github.com/FRACerqueira/RingBufferPlus/blob/main/doc/guides/usage-lock-when-scaling.md)
- [HeartBeat](https://github.com/FRACerqueira/RingBufferPlus/blob/main/doc/guides/usage-heartbeat.md)
- [Background logger](https://github.com/FRACerqueira/RingBufferPlus/blob/main/doc/guides/usage-background-logger.md)
- [RabbitMQ channel pooling](https://github.com/FRACerqueira/RingBufferPlus/blob/main/doc/guides/usage-rabbitmq.md)
- [ASP.NET Core / generic host dependency injection](https://github.com/FRACerqueira/RingBufferPlus/blob/main/doc/guides/usage-dependency-injection.md)
- [Architecture overview](https://github.com/FRACerqueira/RingBufferPlus/blob/main/doc/architecture/overview.md) — for contributors: components and where things live.

## Examples

For runnable samples, see the [Samples directory](https://github.com/FRACerqueira/RingBufferPlus/tree/main/samples):

- [RingBufferPlusBasicSample](https://github.com/FRACerqueira/RingBufferPlus/tree/main/samples/RingBufferPlusBasicSample) — fixed capacity with HeartBeat.
- [RingBufferPlusBasicManualScale](https://github.com/FRACerqueira/RingBufferPlus/tree/main/samples/RingBufferPlusBasicManualScale) — elastic capacity with manual scale.
- [RingBufferPlusApiSample](https://github.com/FRACerqueira/RingBufferPlus/tree/main/samples/RingBufferPlusApiSample) — elastic capacity with manual scale in an ASP.NET Core API.
- [RingBufferPlusBasicTriggerScale](https://github.com/FRACerqueira/RingBufferPlus/tree/main/samples/RingBufferPlusBasicTriggerScale) — elastic capacity with autoscale on acquire fault.
- [RingBufferPlusRabbitSample](https://github.com/FRACerqueira/RingBufferPlus/tree/main/samples/RingBufferPlusRabbitSample) — RabbitMQ channel pooling with autoscale.

## Documentation

API reference is available in the [Docs directory](https://github.com/FRACerqueira/RingBufferPlus/blob/main/src/docs/docindex.md).

## Code of Conduct

This project has adopted the code of conduct defined by the Contributor Covenant to clarify expected behavior in our community.
For more information see the [Code of Conduct](https://github.com/FRACerqueira/RingBufferPlus/blob/main/CODE_OF_CONDUCT.md).

## Contributing

See the [Contributing guide](https://github.com/FRACerqueira/RingBufferPlus/blob/main/CONTRIBUTING.md) for developer documentation.

## Credits

This work was inspired by [**Luiz Carlos Faria**](https://github.com/luizcarlosfaria/Oragon.Common.RingBuffer) project.

**API documentation generated by**

- [XmlDocMarkdown](https://github.com/ejball/XmlDocMarkdown), Copyright (c) 2024 [Ed Ball](https://github.com/ejball)
    - See an unrefined customization to contain header and other adjustments in project [XmlDocMarkdownGenerator](https://github.com/FRACerqueira/RingBufferPlus/tree/main/src/XmlDocMarkdownGenerator)

## License

Copyright 2022 @ Fernando Cerqueira

RingBufferPlus is licensed under the MIT license. See [LICENSE](https://github.com/FRACerqueira/RingBufferPlus/blob/main/LICENSE).
