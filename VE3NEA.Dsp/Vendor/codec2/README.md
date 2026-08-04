# codec2 (x64, Windows)

`codec2.dll` is [drowe67/codec2](https://github.com/drowe67/codec2) at commit `310777b`
(version 1.2.0), built unmodified except for the build flags below. P/Invoked by
[`NativeCodec2`](../../NativeCodec2.cs); used by `VE3NEA.SkyTlm.Audio` to decode the HADES-SA
Codec2 700C voice downlink. LGPL-2.1 — see `COPYING`. `codec2.h` is kept beside it as the reference
for the bound signatures; nothing compiles against it.

## Build

**MSVC cannot build codec2** at any `/std:` level: the sources use C99 variable-length arrays
(`lpc.c`, `nlp.c`, `codec2.c`) and C99 `complex float` arithmetic (`filter.c`), neither of which the
Microsoft C compiler implements. Use the MSYS2 mingw-w64 toolchain:

```sh
c:/msys64/usr/bin/pacman.exe -S --needed mingw-w64-x86_64-gcc mingw-w64-x86_64-ninja
export PATH="/c/msys64/mingw64/bin:$PATH"

git clone --depth 1 https://github.com/drowe67/codec2.git
cmake -S codec2 -B codec2/build -G Ninja -DCMAKE_BUILD_TYPE=Release \
      -DBUILD_SHARED_LIBS=ON -DCMAKE_C_COMPILER=gcc \
      -DCMAKE_SHARED_LINKER_FLAGS="-static-libgcc -static"
cmake --build codec2/build -j 8

cp codec2/build/src/libcodec2.dll <here>/codec2.dll
```

`-static-libgcc -static` is not optional: without it the DLL imports `libgcc_s_seh-1.dll`, which is a
different exception model from the `libgcc_s_sjlj-1.dll` already in `Vendor/mingw`, so a second
runtime DLL would have to be vendored beside it. Statically linked it imports only `KERNEL32` and
`msvcrt` — check with `objdump -p codec2.dll | grep "DLL Name"`.

The same build produces `c2enc.exe` and `c2dec.exe`. Those are the oracle for
`NativeCodec2Tests`: the committed fixtures in `VE3NEA.SkyTlm.Tests/Data/Codec2/` are their output,
so the tests need no C toolchain at run time.
