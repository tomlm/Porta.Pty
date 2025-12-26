# Porta.Pty Native Library

This directory contains the native PTY shim library that wraps `forkpty()` + `execvp()` in native C code.

## Why is this needed?

Starting with .NET 7, the runtime enables W^X (Write XOR Execute) memory protection by default. This conflicts with `fork()` when running managed code in the forked child process.

By performing the `forkpty()` + `execvp()` sequence entirely in native code, we avoid running any managed .NET code in the forked child, eliminating the W^X conflict.

## Building

You need to build this library on each target platform:

### Prerequisites

- CMake 3.10+
- C compiler (gcc, clang)
- On Linux: `libutil-dev` or equivalent (usually included with glibc)

### Build Steps

On each target platform (Linux x64, Linux ARM64, macOS x64, macOS ARM64):

```bash
chmod +x build.sh
./build.sh
```

The output will be placed in `output/runtimes/{rid}/native/` following the NuGet convention.

### Cross-compilation

For CI/CD, you can use Docker or cross-compilers:

```bash
# Linux x64 (on any Linux with Docker)
docker run --rm -v $(pwd):/src -w /src gcc:latest ./build.sh

# Linux ARM64 (using cross-compiler)
# Requires additional setup with arm64 toolchain
```

## Output Structure

After building on all platforms, you should have:

```
output/
??? runtimes/
    ??? linux-x64/
    ?   ??? native/
    ?       ??? libporta_pty.so
    ??? linux-arm64/
    ?   ??? native/
    ?       ??? libporta_pty.so
    ??? osx-x64/
    ?   ??? native/
    ?       ??? libporta_pty.dylib
    ??? osx-arm64/
        ??? native/
            ??? libporta_pty.dylib
```

These are automatically included in the NuGet package via the Porta.Pty.csproj configuration.

## API

The library exports these functions:

- `pty_spawn()` - Fork and spawn a process with a PTY
- `pty_resize()` - Resize the PTY window
- `pty_kill()` - Send a signal to the child process
- `pty_waitpid()` - Wait for the child process to exit
- `pty_close()` - Close the PTY master file descriptor
- `pty_get_errno()` - Get the last error code

See `porta_pty.c` for full documentation.
