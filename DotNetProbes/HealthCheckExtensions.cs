using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System;

namespace DotNetProbes
{
    public static class HealthCheckExtensions
    {
        private static readonly string LivenessRoute = "/health/live";
        private static readonly string ReadinessRoute = "/health/ready";

        public static void UseHealthCheckDefaults(this IApplicationBuilder @this)
        {
            static Func<HealthCheckRegistration, bool> BuildHealthCheckTagFilter(string tag) =>
                x => x.Tags.Contains(tag);

            static HealthCheckOptions BuildHealthCheckOptions(string tagToFilter) =>
                new HealthCheckOptions { Predicate = BuildHealthCheckTagFilter(tagToFilter) };

            @this.UseHealthChecks(LivenessRoute, BuildHealthCheckOptions(HealthCheckTag.Live.ToString()));
            @this.UseHealthChecks(ReadinessRoute, BuildHealthCheckOptions(HealthCheckTag.Ready.ToString()));
        }
    }
}