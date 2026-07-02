# VE3NEA.SkySignals

Shared signal-processing libraries used by [SkyRoof](https://github.com/VE3NEA/SkyRoof)
and related amateur-radio tools.

## Projects (this branch)

| Project | Description |
|---|---|
| **VE3NEA.Dsp** | Native DSP primitives — liquid-dsp + FFTW + libfec P/Invoke wrappers, buffers, FFT, helpers. |
| **VE3NEA.SkyTlm** | Satellite telemetry: streaming demod pipeline (FSK/GFSK/GMSK/BPSK/MSK), deframers, and the telemetry field decoder. |

`VE3NEA.SkyTlm.Tests` and `VE3NEA.SkyTlm.Bench` are the test and profiling-bench projects.

## Build

- .NET 10, **x64 only** (VE3NEA.Dsp P/Invokes x64 native DLLs).
- The native libraries live in `VE3NEA.Dsp/Vendor` and flow to consumers via the project reference.

```
dotnet build VE3NEA.SkySignals.slnx -c Release
```
