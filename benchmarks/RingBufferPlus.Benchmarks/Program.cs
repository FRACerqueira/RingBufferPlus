// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using System.Reflection;
using BenchmarkDotNet.Running;
using RingBufferPlus.Benchmarks;

// Not a BenchmarkDotNet run - a decision-quality simulation (see AutoScaleAlgorithmComparison
// class remarks). Handled before the switcher so it does not appear in the interactive menu.
if (args.Contains("--algo-comparison"))
{
    AutoScaleAlgorithmComparison.Run();
    return;
}

BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
