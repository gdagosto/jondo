# Server mod contract

Place each package in its own directory under `<emulator root>/server-mods/`. The following manifest belongs at `server-mods/example/manifest.json`.

```json
{
  "id": "example.greeting",
  "version": "1.0.0",
  "serverApiVersion": 1,
  "serverAssembly": "server/Example.Greeting.dll",
  "clientAsset": "client/Example.Greeting.Client.dll"
}
```

`id` identifies the mod across package renames and upgrades. It starts with an ASCII letter or digit and can also contain dots, underscores and hyphens. ID comparison ignores case. An enabled ID must identify exactly one package. `version` is a nonempty release string. `serverApiVersion` is an integer and must equal `ServerModApi.Version`.

`serverAssembly` and `clientAsset` are package-relative file paths. Each declared file must exist. Use `/` for nested folders. The loader rejects absolute paths, `.` and `..` components, and links or junctions in package paths. Omit `clientAsset` or set it to `null` for a server-only mod.

After a package initializes successfully, its declared client asset contributes one entry to `GET /api/mods` if its filename meets the client DLL naming rules. Download URLs use the exact filename from that manifest. The server looks up the approved file by name. Server assemblies, private dependencies, manifests, and other package paths are private.

Configured standalone DLLs in `mods/` keep their load order. A package DLL with the same name, ignoring case, takes the standalone entry's slot. Other package DLLs follow in `ServerMods` order. If multiple packages declare the same filename, the first enabled package wins. If the winning file becomes unavailable, its entry is omitted until it returns.

Existing standalone `Cazacabezas.dll` and `Heroes.dll` still load when listed in `ClientMods` and no active package supplies that filename. When switching to a package, remove the old filename from `ClientMods` if the replacement uses a different filename or if disabling the package should stop serving the old DLL. For manifest-based launches, the launcher sets aside older copies from the game's `Mods/` folder so the game loads the versions selected by the manifest.

## Enable packages

Add the manifest IDs to `ServerMods` in `config/server_settings.json`, alongside the other server settings.
The Heroes package is selected by `Heroes.Enabled` (default `true`). The server loads its bundled
copy when there is no installed package with ID `heroes`.

```json
{
  "ServerMods": ["example.greeting"]
}
```

The server uses the listed order and starts each ID once. An omitted, null, or empty list enables zero packages. Restart the desktop or headless server to apply settings, installs, or updates. If an enabled package is missing, ambiguous, invalid, or fails initialization, startup fails with its ID and the reason.

## Lifecycle

Target .NET 10 and reference `Jondo.Unity.Contract`. The server assembly must export exactly one concrete `Jondo.Unity.ServerMods.IServerMod` implementation with a public parameterless constructor. The server calls `Initialize(IServerModHost host)` once, after loading the database and world services and before opening network listeners or signaling readiness. `host.Log` labels messages with the package and mod ID.

Use `host.RegisterCleanup(Action)` immediately after acquiring a resource so its cleanup runs even if a later step in initialization fails. On shutdown or startup rollback, the server calls `Shutdown()` on successfully initialized mods in reverse startup order, while their host services are still available. Each mod's registered callbacks run in reverse registration order after its shutdown hook. If initialization throws, the server runs that mod's registered callbacks before stopping the previously initialized mods. The server logs shutdown and callback errors and continues cleanup.

The server supplies its own `Jondo.Unity.Contract` assembly, including in single-file builds. Leave the contract DLL out of distributed packages. Each package has its own assembly load context. Put private managed DLLs beside the server assembly, or include the assembly's `.deps.json` and its referenced files within the package. Mods run with the server process's permissions.

The loader reads managed DLLs into memory so Windows can release the package files immediately. Loaded plugin assemblies have an empty `Assembly.Location`.

## Authenticated control routes

Call `host.RegisterAuthenticatedControlRoute(path, handler)` in `Initialize` to register an exact, case-sensitive path under `/api/`. Built-in routes take priority. Duplicate plugin registrations for the same path fail. The host removes a package's registrations on shutdown or failed initialization.

Before invoking the handler, the host resolves the JSON body's `token` using the same account registry as built-in control routes. Missing or invalid tokens return `401` with `{"error":"sesion"}`. The handler receives the resolved `AccountId`, HTTP method, and original body, and returns a status code and JSON body. The host applies no additional HTTP method restriction to registered paths.
