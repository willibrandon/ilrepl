# Hex1b native terminal helper

`hex1binterop.c` and `LICENSE` are copied from Hex1b commit
`d87cce18e6eaa1260305ce4a5278c14d7e79a95d`, the source of the 0.168.0 package we reference.
Only trailing whitespace has been removed.

That package ships glibc Linux libraries. Its Arm64 library requires the glibc loader,
which prevents both the terminal application and real PTY tests from starting on Alpine.
The shared build target compiles the same helper against musl for the frontend and tests.
It replaces the package's native asset in build and publish output, including tool packages.
Published distributions retain the helper's MIT license in `licenses/Hex1b.LICENSE`.
Other operating systems and glibc builds retain the package's native libraries.

Build musl targets inside the matching Alpine .NET SDK image with `build-base` installed.
Plain `dotnet test`, frontend publishing, and tool packing build the helper automatically.
Keep this source paired with the Hex1b package version. The managed library calls the helper by name, so a newer package
can need functions an older copy lacks. `NativeHelperTests` compares the two on every platform and names what is missing.
To refresh the copy, take `src/Hex1b/native/hex1binterop.c` and `LICENSE` from the tag of the new version, strip trailing
whitespace, and update the commit above. Compile it with the flags that version's `src/Hex1b/native/Makefile` uses.
