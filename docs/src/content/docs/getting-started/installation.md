---
title: Installation
description: Install ilrepl as a dotnet tool.
---

ilrepl needs the .NET 10 SDK or runtime. The front-end is a Native AOT executable; the part that
compiles and runs IL is a small framework-dependent host that ships inside the package and runs on
your `dotnet`.

## dotnet tool

```sh
dotnet tool install -g ilrepl
ilrepl
```

On the platforms with a native package (Windows, Linux, macOS on x64 and Arm64) the SDK installs the
Native AOT build. Everywhere else the portable build runs on the JIT.

## From source

```sh
git clone https://github.com/willibrandon/ilrepl
cd ilrepl
dotnet run --project src/IlRepl
```

`dotnet build` also publishes the host into `host/` beside the front-end, which is how the tool finds
it. To point at a different host, set `ILREPL_HOST_PATH`.

## Checking the install

```sh
ilrepl -e 'ldc.i4 6; ldc.i4 7; mul; ret'
```

prints

```
  ┊ [int32]
  ┊ [int32, int32] ◂ top
  ┊ [int32]
  = 42 : int32
```
