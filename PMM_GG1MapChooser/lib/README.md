# lib

`0Harmony.dll` — [Lib.Harmony](https://github.com/pardeike/Harmony) 2.4.2, `lib/net10.0` (MIT license).

It is kept in the repository so the plugin builds without the NuGet package. The build copies it to
`bin/.../harmony/0Harmony.dll`. On the server it must stay in `plugins/PMM_GG1MapChooser/harmony/`:
the plugin loads it into the default (non-collectible) load context itself.
