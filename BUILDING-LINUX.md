# Building on Linux

This is the Linux head of Unigram, running on [Uno Platform](https://platform.uno/) with the
Skia/X11 backend. `Telegram/` holds the sources shared with the Windows head; `Telegram.Linux/`
is the Linux head itself, and `Telegram.Linux/Telegram.Linux.csproj` is the project you build.

A fresh clone does **not** build straight away, and that is deliberate rather than an oversight:
two required inputs are gitignored (one of them must be *yours*, not ours) and the native
libraries are built out of a separate repository. This document is the complete list. If you skip
a step, the build stops with a message that names the step — see *Prerequisite checks* at the end.

---

## 0. What you need installed

* **.NET 10 SDK** (`net10.0-desktop`). `dotnet --version` should report 10.x.
* A C++ toolchain for the native libraries: `cmake`, `ninja` or `make`, `clang++` or `g++`,
  `gperf`, `libssl-dev`, `zlib1g-dev`.
* At least ~8 GB of free RAM while compiling TDLib. It is the heaviest single step by a wide
  margin; everything else is comparatively small.

At runtime the client also loads `libmpv.so.2` and `libpulse` from your distribution. They are not
built here — install your distro's mpv and PulseAudio runtime packages.

---

## 1. Clone, with submodules

```sh
git clone --recursive <your-fork-url> Unigram
cd Unigram
```

Already cloned without `--recursive`?

```sh
git submodule update --init --recursive
```

This matters more than the usual boilerplate. Two different steps below read out of submodules,
and a partial checkout does not announce itself: you get a copy failure on `grammars.dat`, or —
worse — a scheme file that looks fine and silently generates the wrong API bindings. See step 3.

---

## 2. `Telegram/Constants.Secret.cs` — your own API credentials

Telegram requires every client build to use its own API credentials, so this file is gitignored
(`.gitignore`: `/Telegram/Constants.Secret.cs`) and is never committed. A template is committed in
its place.

```sh
cp Telegram/Constants.Secret.cs.template Telegram/Constants.Secret.cs
```

Then edit it and fill in `ApiId` and `ApiHash` with values from
<https://my.telegram.org/apps> (log in with the phone number that should own the application, then
*API development tools*). The other fields in the template are free-form and fine as they are.

`ApiId`/`ApiHash` are declared `public static readonly` in `Telegram/Constants.cs` and assigned by
this file's static constructor, which is why it is a hard requirement: leaving it out does not
degrade into an unconfigured build, it fails to compile.

**Do not commit your filled-in copy.** It is gitignored; keep it that way.

---

## 3. `Libraries/tdjson/td_api.tl` — the TDLib API scheme

The TD bindings under `Telegram/Td/Api/` are generated at build time from TDLib's API scheme,
which the csproj reads as an `AdditionalFiles` input. The scheme is gitignored, and comes out of
the `Libraries/tdlib` submodule:

```sh
mkdir -p Libraries/tdjson
cp Libraries/tdlib/td/generate/scheme/td_api.tl Libraries/tdjson/td_api.tl
```

### The scheme must match the submodule commit this repo records

This is the one step where being *almost* right is worse than being wrong, so it is worth
stating plainly.

If your `Libraries/tdlib` submodule is sitting at some commit **other** than the one this repo
records, that `cp` copies a perfectly valid scheme that is simply the wrong one. Codegen then
succeeds and produces bindings that do not match the source tree, and the build fails with dozens
of `CS0117` / `CS1739` / `CS1729` errors scattered across ordinary application files — none of
which mention `td_api.tl`, the submodule, or this page.

That is not a hypothetical. Building this tree against the scheme from tdlib `022d6020` yields
**58 such errors**; the scheme from the recorded commit yields **zero**. The difference is 37 API
entries the current sources use (community chats, welcome messages, ephemeral message content,
and others).

`git submodule update --init --recursive` in step 1 is what keeps you on the recorded commit. To
check by hand:

```sh
git ls-tree HEAD Libraries/tdlib                  # the commit this repo records
git -C Libraries/tdlib rev-parse HEAD             # the commit you actually have
md5sum Libraries/tdjson/td_api.tl Libraries/tdlib/td/generate/scheme/td_api.tl
```

The two hashes must be equal. The build also checks this for you and warns on a mismatch, but the
warning is easy to scroll past in a long log — if the errors above are what you are seeing, check
here first.

---

## 4. Native libraries

Four native libraries are built from the companion repository, **`unigram-linux`**, which must sit
as a **sibling of this repository's parent directory**. The csproj refers to them with paths like
`..\..\unigram-linux\native\...`, so the layout is:

```
some-parent/
  Unigram/            <- this repository
  unigram-linux/      <- the companion repository, with native/
```

Build them in this order (TDLib first — the others do not depend on it, but it is the long one and
you want it started):

| # | Library | Build with | Produces |
|---|---------|-----------|----------|
| 1 | **TDLib** (patched) | `native/tdlib/build-tdjson.sh` | `native/tdlib/out/libtdjson.so` (+ `.so.1.8.67`) |
| 2 | rlottie | `native/rlottie/build-rlottie.sh` | `native/rlottie/out/librlottie.so.0.2` |
| 3 | unigram-native | `native/unigram-native/build.sh` | `native/unigram-native/out/lib/libunigram-native.so` |
| 4 | unigram-calls | `native/calls/build-calls.sh` | `native/calls/out/lib/libunigram-calls.so` |

Build 2 before 3: `libunigram-native.so` is the C ABI over rlottie and links against it.

TDLib is **patched**, not stock. `native/tdlib/unigram-json-abi.patch` changes the JSON ABI
(`td_send` takes an explicit request id, `td_receive` returns client/request ids as out
parameters). `build-tdjson.sh` applies it to the `Libraries/tdlib` submodule idempotently and
builds from there — which is also why the scheme in step 3 and the native in this step come from
the same commit, and must stay that way.

### If you skip this step

The build still goes green. All four native copy items are `Condition="Exists(...)"`, so a missing
`.so` is skipped rather than failing the copy. The resulting binary then does not start, and the
first symptom is a `DllNotFoundException` for **`tdjson.dll`** — the Windows name the P/Invoke
declaration uses — which mentions neither `libtdjson.so` nor this document. The build prints a
warning when it skips `libtdjson.so`; that warning is the only notice you get.

---

## 5. Build

```sh
dotnet build Telegram.Linux/Telegram.Linux.csproj -c Debug -f net10.0-desktop
```

Output lands in `Telegram.Linux/bin/Debug/net10.0-desktop/`, with the native libraries copied in
beside `Unigram.dll`. Run `./Unigram` from that directory.

### `TDJSON_PATH` and the other escape hatches

If your `libtdjson.so` lives somewhere else — a system-wide install, a build outside the sibling
layout, a scratch directory — point at it instead of moving files around:

```sh
TDJSON_PATH=/path/to/libtdjson.so ./Unigram
```

`Telegram.Linux/Native/NativeLibraryResolver.cs` maps every native the client loads to its own
environment variable, and each is tried before the default search:

| Library | Variable | Files tried |
|---------|----------|-------------|
| TDLib | `TDJSON_PATH` | `libtdjson.so`, `libtdjson.so.1.8.67` |
| unigram-native | `UNIGRAM_NATIVE_PATH` | `libunigram-native.so` |
| unigram-calls | `UNIGRAM_CALLS_PATH` | `libunigram-calls.so` |
| mpv | `MPV_PATH` | `libmpv.so.2` |
| PulseAudio | `PULSE_SIMPLE_PATH`, `PULSE_PATH` | `libpulse-simple.so.0/.so`, `libpulse.so.0/.so` |

---

## Prerequisite checks

The project fails fast, before compiling, when a required input is missing, and each message names
the step on this page:

* `Telegram/Constants.Secret.cs` missing → step 2
* `Libraries/tdjson/td_api.tl` missing → step 3
* `Libraries/libprisma/libprisma/grammars.dat` missing → step 1 (submodules not checked out)

and it warns, without stopping, when:

* `Libraries/tdjson/td_api.tl` does not match the scheme in the `Libraries/tdlib` submodule → step 3
* `libtdjson.so` was not found and its copy was skipped → step 4

These exist because each of these failures used to surface as `CS2001`, `MSB3030`, or a wall of
unrelated compiler errors, none of which said what to do.
