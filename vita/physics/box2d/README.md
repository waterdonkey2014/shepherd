# Box2D 2.3.1 (vendored, third-party)

This directory holds the core library of **Box2D 2.3.1** by Erin Catto, the physics engine the Shepherd physics core
(`libshepphys`, `vita/physics/`) is built on. It is third-party code under the **zlib licence**, not ours and not game code.

| | |
|---|---|
| upstream | https://github.com/erincatto/box2d, tag `v2.3.1` |
| source archive | https://codeload.github.com/erincatto/box2d/tar.gz/refs/tags/v2.3.1 |
| archive sha256 | `58ffc8475a8650aadc351345aef696937747b40501ab78d72c197c5ff5b3035c` (1,553,252 bytes) |
| downloaded | 2026-10-07, owner-approved (plan decision D1) |
| what is here | the archive's `box2d-2.3.1/Box2D/Box2D/**` (core library: Collision, Common, Dynamics, Rope, plus its own CMake files) as `Box2D/`, and `box2d-2.3.1/Box2D/License.txt` as `License.txt` |
| not vendored | Testbed, HelloWorld, glfw, glew, Build, Documentation, Contributions (unused) |
| licence | zlib, see [License.txt](License.txt); every source file also carries the notice in its header |

The files are byte-identical to the archive (CRLF line endings included; `.gitattributes` keeps git from touching them).
Check with:

```sh
tar -xzf box2d-v2.3.1.tar.gz && diff -r box2d-2.3.1/Box2D/Box2D vita/physics/box2d/Box2D
```

## Licence obligations (zlib)

1. Do not misrepresent the origin: this README and `License.txt` say where the code comes from.
2. **Altered source versions must be plainly marked as such.** Any file changed here gets a header comment saying so
   (`ALTERED for Shepherd: ...`) and a row in [PATCHES.md](PATCHES.md) with the reason.
3. The notice in `License.txt` and in every file header stays.

## How it is built

`vita/physics/CMakeLists.txt` compiles the 46 `.cpp` files directly (Box2D's own `CMakeLists.txt` is kept but not used),
together with our shim (`vita/physics/shim/`), into `libshepphys` (Mac dylib) and `libshepphys_vita.a` (Vita). Build-level
choices that change behaviour without changing any file here (for example our own `sinf`/`cosf`) are listed in PATCHES.md too.
