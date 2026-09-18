# Hex1b native terminal helper

`hex1binterop.c` and `LICENSE` are copied from Hex1b commit
`6eea363f96a54f7c4029af806969094c6929b72a`, the source of our pinned 0.167.0 package.
Only trailing whitespace has been removed.

That package ships glibc Linux libraries. Its Arm64 library requires the glibc loader,
which prevents both the terminal application and real PTY tests from starting on Alpine.
The shared build target compiles the same helper against musl for the frontend and tests.
It replaces the package's native asset in build and publish output, including tool packages.
Published distributions retain the helper's MIT license in `licenses/Hex1b.LICENSE`.
Other operating systems and glibc builds retain the package's native libraries.

Build musl targets inside the matching Alpine .NET SDK image with `build-base` installed.
Plain `dotnet test`, frontend publishing, and tool packing build the helper automatically.
Keep this source paired with the Hex1b package version.
