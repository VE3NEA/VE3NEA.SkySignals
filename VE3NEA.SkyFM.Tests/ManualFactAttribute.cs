using System;
using Xunit;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>A <see cref="FactAttribute"/> for manual probes/harnesses that process multi-hundred-MB real
  /// captures (too slow for the normal suite): skipped by default, run by setting the environment variable
  /// <c>SKYFM_RUN_MANUAL=1</c> before <c>dotnet test</c> — no code edit needed. <paramref name="lastResult"/>
  /// documents the most recent run's findings and is shown as the skip reason.</summary>
  internal sealed class ManualFactAttribute : FactAttribute
  {
    public ManualFactAttribute(string lastResult)
    {
      if (Environment.GetEnvironmentVariable("SKYFM_RUN_MANUAL") != "1") Skip = lastResult;
    }
  }
}
