// ***************************************************************************************
// Original source code : Copyright 2020 Luis Carlos Farias.
// https://github.com/luizcarlosfaria/Oragon.Common.RingBuffer
// Current source code : The maintenance and evolution is maintained by the RingBufferPlus project 
// ***************************************************************************************

using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.Logging;

namespace RingBufferPlusBenchmarkSample
{
    internal class Program
    {
        internal static ILogger? logger;

        private static int delayseconds = 5;
        private static string rolerun = string.Empty;
        public static int Main(string[] args)
        {
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder
                    .SetMinimumLevel(LogLevel.Information)
                    .AddFilter("Microsoft", LogLevel.Warning)
                    .AddFilter("System", LogLevel.Warning)
                    .AddConsole();
            });
            logger = loggerFactory.CreateLogger<Program>();

            ParseCommand(args);

            switch (rolerun)
            {
                case "benchmark":
                    var summary = BenchmarkRunner.Run<BenchmarkProgram>();
                    break;
                case "publisher":
                    PublisherRoleProgram.Start(logger,delayseconds);
                    break;
                case "consumer":
                    ConsumerRoleProgram.Start(logger, delayseconds); 
                    break;
                default:
                    Console.WriteLine($"role {rolerun} not found");
                    return -1;
            }

            return 0;
        }


        static void ParseCommand(string[] args)
        {
            var root = new RootCommand("RingBuffer BenchmarkApp") 
            {
                new Option<string>("--role", "--role"),
                new Option<int>("--delay", "--delay")
            };
            var parseResult = new CommandLineBuilder(root)
            .Build()
            .Parse(args);

            if (parseResult.Errors.Count > 0)
            {
                foreach (var erro in parseResult.Errors)
                    Console.WriteLine(erro.Message);

                throw new InvalidOperationException();
            }
            Console.WriteLine(parseResult.Diagram());


            rolerun = parseResult.ValueForOption<string>("role")??string.Empty;

            delayseconds = parseResult.ValueForOption<int>("delay");

        }
    }
}
