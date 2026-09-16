# Hex1b PTY helper

`hex1binterop.c` and `LICENSE` are copied from Hex1b commit
`39947cb9455dd39b8de6326c643baaf4e4324962`, the source of our pinned 0.165.0 package.
Only trailing whitespace has been removed.

That package ships glibc Linux libraries. Its Arm64 library requires the glibc loader,
which prevents the real PTY tests from starting on Alpine. The test project builds
this same helper against musl and copies it to the package's native asset location.
The application continues to use the Hex1b package without modification.

On Alpine, install `build-base` alongside the .NET SDK. Plain `dotnet test` builds
the helper automatically. Keep this source paired with the Hex1b package version.
