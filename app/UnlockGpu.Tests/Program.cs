using UnlockGpu.Tests;

// Tiny test runner (no NuGet): runs every [Test] method found by reflection; exit code = number of failures.
//   dotnet run --project app\UnlockGpu.Tests              unit tests (FakeGpu only, never touches the GPU)
//   dotnet run --project app\UnlockGpu.Tests -- --smoke   read-only check against the real NVML (Info + one Sample)
//   dotnet run --project app\UnlockGpu.Tests -- <filter>  only tests whose name contains <filter>

//   dotnet run --project app\UnlockGpu.Tests -- --bench   read-only 5 s D3D11 benchmark on the real GPU (no clock/power changes)

if (args.Contains("--smoke")) return Smoke.Run();
if (args.Contains("--bench")) return Smoke.RunBenchmark(TimeSpan.FromSeconds(5));
string? filter = args.FirstOrDefault(a => !a.StartsWith('-'));
return TestRunner.Run(typeof(Program).Assembly, filter);
