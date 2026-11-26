# Cazacabezas and Heroes server-plugin migration

Baseline `00e8dfb4` (`modding api start`). Paths are repository-relative. Commit `00e8dfb4` added client DLL distribution and nested settings. Cazacabezas gameplay now runs in the server package. The Heroes package owns active hero groups, selection and switching, follower party membership and movement, and teardown.

## Shared settings, distribution and authentication

- `Jondo.Unity.Server/ServerSettings.cs` (`Current`, `Load`, `Read`) reads `config/server_settings.json` for the running process. `Read` imports legacy flat fields when the corresponding nested object is absent or null. `Jondo.Unity.Server/UI/ServerSettingsView.cs` shows each modâ€™s gameplay controls when its DLL is available. Settings edits take effect after restart. DLL order and gameplay enablement are independent.
- The local, untracked `config/server_settings.json` uses legacy fields. It resolves to Cazacabezas disabled with a 15-minute window and 30-minute rest, and Heroes enabled with two followers and map reload off. `ClientMods` is absent, so the default order applies. These are checkout settings and may differ from a running serverâ€™s startup snapshot.
- `Cazacabezas/Cazacabezas.csproj` and `Heroes/Heroes.csproj` target `net6.0` and reference MelonLoader and game IL2CPP assemblies. `GuardarMod` copies builds to `mods/Cazacabezas.dll` and `mods/Heroes.dll`. `DesplegarMod` also copies to the installed clientâ€™s `Mods/` when present unless `SkipModDeploy=true`.
- `Jondo.Unity.Core/Paths.cs` (`ClientModsDirectory`) resolves `<root>/mods`. `ServerSettings.ClientMods` defaults to `JondoFix.dll`, `Cazacabezas.dll`, `Heroes.dll` in that order. `Jondo.Unity.Server/Network/ClientModCatalog.cs` (`ListAvailable`, `CreateManifest`, `OpenDownload`) discovers DLLs, emits configured available entries in order with name, byte size and SHA-256, skips missing files, excludes links, rejects invalid/duplicate names and enforces contract limits. `Jondo.Unity.Server/Network/ControlApi.cs` (`Mods`) and `Jondo.Unity.Server/Network/HaapiServer.cs` (`ServeClientModAsync`) serve unauthenticated `GET /api/mods` and `GET /api/mods/{name}`. Downloads require an exact configured name. `Jondo.Unity.Contract/ClientModManifest.cs` caps manifests at 128 entries and each DLL at 128 MiB. An empty order serves an empty manifest when no loaded packages supply client assets.
- `Jondo.Unity.Launcher/Setup/ClientModSync.cs` (`Prepare`, `Activate`) uses matching bundled DLLs or verifies downloads into a SHA-256 cache. `Jondo.Unity.Launcher/UI/LauncherPreferences.cs` (`TrustedModManifest`, `TrustModManifest`) stores approval per host for the manifest fingerprint. A changed previously approved manifest or an unrecognized DLL requires approval. `JondoModLoader/ServerModsPlugin.cs` (`OnPreModsLoaded`, `LoadApprovedManifest`) reads `JONDO_CLIENT_MOD_MANIFEST`, verifies cached bytes and loads in manifest order in MelonLoader. `Jondo.Unity.Launcher/LauncherService.cs` (`LaunchClient`, `InstallMod`, `ShippedModPath`) retains bundled install for compatible legacy launches after a 404 from a host with no saved approval. `Jondo.Unity.Mcp/GameClients.cs` still installs bundled copies directly.
- `Cazacabezas/CazacabezasMod.cs` (`CazacabezasMod`, F7) and `Heroes/HeroesMod.cs` (`HeroesMod`, Ctrl+Tab and Tab switching) POST JSON to `http://127.0.0.1:8888/api/`. Both read `JONDO_PLAYER_TOKEN` with `JONDO_CONTROL_TOKEN` as fallback. Active gameplay routes below go through `ControlApi.Responder` â†’ `ConSesion`, which resolves the bodyâ€™s `token` via `Jondo.Unity.Server/Network/ClientLaunchRegistry.cs` (`ResolveToken`, registry then DB `LauncherToken` and `GameToken`). Invalid tokens receive `401 sesion`. These gameplay routes accept the same requests regardless of HTTP verb. `ControlApi.InTheWorld` finds the authenticated accountâ€™s in-world character with an active selection.

## Cazacabezas

- **Settings** â€” `CazacabezasModSettings.Enabled=true`, `HuntWindowMinutes=15`, `RestMinutes=30`. Legacy aliases `HeadHunters`, `HeadHuntWindowMinutes`, `HeadHuntRestMinutes`. `HeadHunts.Window` and `Rest` enforce minima of 1 and 0 minutes respectively.
- **Client work** â€” `CazacabezasMod.OnUpdate` polls status every 2 seconds with its window or an active hunt, otherwise every 6 seconds, with error backoff. It adds an alignment-tab button and displays the hunt tracker. `OnInitializeMelon` leaves the UI idle without a player token.
- **Routes** â€” `Cazacabezas.Server/CazacabezasMod.cs` registers and owns `/api/headhunts/{status,board,take,abandon}`. The host authenticates each request before invoking the plugin. All four routes require the authenticated account's in-world character or return `409 world`. `take` reads the target character ID from `target`. With the package absent from `ServerMods`, all four paths return `404 ruta`.
- **Package and host** â€” `Cazacabezas.Server/HeadHunts.cs` owns eligibility, targets, holds, streaks, orders, currency, and payouts. `HuntResponses.cs` projects the status and board JSON, and `HuntTexts.cs` contains localized hunt notices. `Jondo.Unity.Contract/HeadHuntGameplay.cs` defines the typed boundary. `Jondo.Unity.Server/Mods/HuntWorld.cs` supplies selected characters, board data, presence, world coordinates and alignment. It applies honour and PvP changes through `Alignments`, refreshes the character sheet and look, removes the prey from the Koliseo queue, and sends existing game frames. The package registers gameplay hooks through `ServerModLoader`, which removes them on shutdown.
- **Development setup** — Run `dotnet build Cazacabezas.Server/Cazacabezas.Server.csproj -p:StageServerMod=true` to stage the manifest, server DLL and existing client DLL in `server-mods/cazacabezas/`. Pass `-p:CazacabezasPackageDirectory=<path>` to stage elsewhere. Add `"cazacabezas"` to `ServerMods` in `config/server_settings.json` and restart the rebuilt host. `Cazacabezas.Enabled` controls hunt rules. The manifest’s `clientAsset` supplies `Cazacabezas.dll` to the catalog. An entry in `ClientMods` places it in the configured order, and the catalog appends it when that entry is omitted. The test project stages the same three files in its own fixture.
- **Legacy client DLL** — The package asset takes precedence over an identically named standalone DLL in `mods/`. After package removal, the server can still advertise and serve a standalone `mods/Cazacabezas.dll` if `ClientMods` includes it, including through the default order. For a full uninstall, remove the package ID from `ServerMods`, move the package out of `server-mods/`, and move the standalone DLL out of the served `mods/` directory or explicitly omit it from `ClientMods`. Restart to apply these changes.
- **Storage and clock** â€” The package lazily loads/creates the existing tables through the host's SQLite connection to `DatabaseManager.WorldConnectionString` (`bases/world.db` by default). `HeadHunters` is keyed by `CharacterId` with `Chapeuvepes`, `Kills`, `Survivals`, `Failures`, `Streak`, `LockedUntilMs`. `HeadHuntOrders` is keyed by `Hunter` with `Target`, `TakenMs`, `OpenUntilMs`, `RestUntilMs`. Both participants are character IDs and timestamps use Unix milliseconds. Reload clears open state and keeps the later of the saved open/rest deadlines. `CazacabezasMod` owns the non-overlapping five-second tick. It starts when the host signals that gameplay services are online and waits for any running tick on shutdown. An unloaded package schedules no work. The tick honors `Cazacabezas.Enabled`. With the package loaded, that setting keeps its existing behavior for routes, persisted balances, and character cleanup.

| Gameplay operation | Hook and ordering |
| --- | --- |
| Alignment and deletion | `AlignmentHandler.PvpAsync` checks whether a hunt blocks folding wings before setting PvP. `SwitchAsync` invalidates orders after the alignment change and before refreshing the client. `CharacterDeletionHandler.DeleteAsync` removes orders and the hunter record after successful deletion. |
| Fight admission | `ChallengeDuelHandler.OfferAsync` and `AnswerAsync` ask the package about the pair of characters before proceeding. `FightHandler.AttackAsync` and the guarded branch of `FightJoin.JoinRequestAsync` still check the hunt hold before admission. `MonsterAggression.Find` skips automatic aggression against an actively hunted character. |
| Other admission | The typed activity hold remains in `DungeonHandler.AtTheDoorAsync`, `TryResumeAsync`, and `EnterByItemAsync`, `ExpeditionHandler.ReplyAsync`, `HouseHandler.GoToHouseAsync` and `EnterAsync`, `MerkasakoHandler.EnterFromOutsideAsync` and `AnswerInvitationAsync`, and `KoliseoHandler.EnrolPartyAsync` and `AnswerOfferAsync`. Existing jail checks still precede hunt holds where both apply. |
| Fight settlement | `FightHandler.EndFightAsync` calls `AlignmentWar.AnnounceAsync`. After the ordinary cities-war honour announcements, the hook passes the winning and losing characters and their fight levels to the package. The package updates streaks and settles open orders, then sends outcome notices. |
| Shop payment | `NpcHandler.SeguirLaConversacionAsync` debits chapeuvepÃ©s before giving the item and refunds the debit if delivery fails. With the package unloaded, these currency purchases follow the existing payment-refused dialogue. |
| Simulated hunters | `SimulatedPlayer.TakeAnOrderAsync` asks the package to choose an eligible online order using the bot's random selection. `HuntAsync` asks for the active target, then keeps movement and fight initiation in the host. |

Without the package loaded, hunt checks allow ordinary admissions and wing changes, and hunt notifications and settlement are skipped.

## Heroes

- **Settings** â€” `HeroesModSettings.Enabled=true`, `MaxHeroes=7`, `ReloadMapOnSwitch=false`. Legacy aliases `HeroMode`, `HeroesMax`, `HeroSwitchReloadsMap`. The package clamps the follower count to 1–7.
- **Client work** â€” `HeroesMod.OnInitializeMelon` installs client UI hooks when a token is available. `OnUpdate` polls the list every 3 seconds with a window or group panel, otherwise every 8 seconds, with auth-error backoff. The group panel uses server state for character controls, life and portraits.
- **Routes** — `Heroes.Server/HeroesMod.cs` registers `/api/heroes/{list,connect,disconnect,portrait,quests,autopass,loot,sheet,switch,create,name,preview}`. The host resolves the token before dispatch. The package handles `connect`, `disconnect`, and immediate or deferred `switch` with the authenticated account's in-world master. The remaining routes use `IHeroesControl` and `ControlApiHeroes.cs`. Portraits check account ownership. Name and preview need a token only. Creation uses the shared character creation path, then asks the package to connect the character.
- **Activation** — `Heroes.Enabled` and its legacy alias `HeroMode` select the package at startup. The default is still enabled with an empty `ServerMods` list. The server embeds the package manifest and assembly for installs that only update the apphost. A package with ID `heroes` in `server-mods/` takes precedence over that copy and loads once through the same lifecycle. When Heroes is disabled, startup skips the package even if `ServerMods` lists it and its routes return `404 ruta`. Package shutdown also unregisters the routes.
- **Development setup** — Run `dotnet build Heroes.Server/Heroes.Server.csproj -p:StageServerMod=true` to stage an external copy in `server-mods/heroes/`. Pass `-p:HeroesPackageDirectory=<path>` to stage elsewhere. Rebuild any installed external copy with this host so the package supplies the gameplay hooks. `mods/Heroes.dll` continues to ship through the existing standalone client catalog and `ClientMods` order. The server package declares no client asset so that opting out of that DLL continues to work independently of enabling server gameplay.
- **Storage and lifetime** — `Heroes.Server/HeroGroups.cs` owns active membership, group order, auto-pass, loot routing, quest-sharing preferences, and the account-keyed switch and regroup records. The package drains active groups on shutdown. The host still persists character state through `DatabaseManager.SaveCurrentCharacter` using the existing character IDs and tables. Dungeon keyring and progress, hub wins, and zaap discoveries retain their existing identifiers and storage. The client stores `Heroes.PanelX` and `Heroes.PanelY` in `UserData/MelonPreferences.cfg`.

| Gameplay operation | Package ownership and remaining host dependencies |
| --- | --- |
| Session ownership and teardown | `HeroGroups` validates the master and checks ownership, online status, duplicate activation, fight state and capacity. `HeroesWorld` checks the account and loads a socketless `GameSession` through `CharacterSelectionHandler.LoadForHero`. Normal character selection still validates the account and server in the host and releases an active hero before loading it for the client. `SessionTeardown.LeaveAsync` tells the package the master left. The package drops the active group and keeps a three-minute regroup record. The host removes map and party presence, saves the character and notifies guilds and alliances through typed calls from the package. `SessionRegistry.IsOnline` reads the package's active membership. |
| Switching | The package serializes immediate switching on the real session, holds follower party updates and transfers group ordering and loot routing. The host saves and selects on the existing session and sends normal character, map, journal and shortcut state. `GameEntryService` reads pending switches for the two-minute deferred selection, filters the requested character through the host's account/server list, and notifies the package after entry for regroup. |
| Party and trade | The package decides which followers leave or join the master's party and suppresses follower churn during a switch. `PartyHandler` still applies changes to the authoritative `Parties` roster and sends the party frames. Invitations, kicks, promotion, leadership and dissolution still consult active membership through the host facade. `TradeHandler` still routes outside requests to the master and resolves the live session for trades with a follower. |
| Movement and presence | `WorldMoveHandler.ConfirmMovementAsync` and `SessionRegistry.AnunciarMudanzaAsync` notify the package after normal movement and map changes. The package positions and moves followers and asks the host to draw, walk or remove actors. Zaap discovery, map achievement updates, geometry, and packet delivery use host services. `MapInformationMessages.Actors` still projects the package's visible membership into host map snapshots. |
| Dungeon admission and payment | `DungeonHandler.AtTheDoorAsync`, `EnterByMatchAsync`, `EnterByItemAsync` call the partial in `Jondo.Unity.Server/Handlers/DungeonHeroes.cs` (`Following`, `WaysForHeroesAsync`, `PlanForHeroes`, `SpendForHeroesAsync`). Reserve the masterâ€™s payment, then plan for each out-of-fight follower. Use an eligible weekly keyring or own key, then masterâ€™s spares and other heroesâ€™ spares. A quest asking for a key prefers a physical key. A planning shortfall aborts before payment. Spending then runs sequentially and can fail after earlier debits. The door shares quest credit with key-paying heroes and new runs record progress per character. `TryResumeAsync` requires every follower to have the same saved room. `Jondo.Unity.Server/Handlers/MatchmakingHandler.cs` checks `CanEnterByMatch` before final entry. `ExpeditionHandler.ReplyAsync` and `Jondo.Unity.Server/Handlers/TravelItemHandler.cs` feed the match and item entry paths respectively. |
| Fight entry and turns | `FightHandler.InitiateFightFromMobCollision` â†’ `AddHeroes` in `Jondo.Unity.Server/Handlers/FightHeroes.cs`. The side cap, including solo quest fights, and each heroâ€™s fight state govern admission. `AddHeroes` builds fighters from their own sessions, removes their map actors and calls `PlayAsHero` with the master as controller. `PlayedAllyBar` uses `HeroBar`. `FightHandler.ConfirmAsync` consults `PassesOnHisOwn` to auto-pass the heroâ€™s turn. `Jondo.Unity.Server/Handlers/CommandHandler.cs` (`HeroesProbeAsync`, `.heros`/`.heroes`) and `Jondo.Unity.Server/Network/ControlApiTest.cs` also use played allies for probes. |
| Rewards and fight exit | `Jondo.Unity.Server/Handlers/FightRewards.cs` (`PlanRewards`) uses `SessionToPay` for socketless participants. Outside Dreams, `GatherHeroLoot` changes allocation before payout when the selected recipient is among winners. XP, kamas and personal quest drops stay with their earners. `FightHandler.EndFightAsync` calls `HeroesFightOver` to apply rewards, record achievement/challenge and dungeon progress, clear fight state and save. The later `HeroPresence.MasterMovedAsync` restores map presence. |
| Quest propagation | `Heroes.ShareQuestActionAsync` applies eligible actions to follower sessions after the masterâ€™s action and reports lag. Callers include `NpcHandler.StartQuestFromReplyAsync`/`CompleteQuestFromReplyAsync`, `DungeonHandler.AtTheDoorAsync`, `Jondo.Unity.Server/Handlers/ReadingItemHandler.cs` (`ShareReadingAsync`), `Jondo.Unity.Server/Handlers/QuestWatcher.cs` (`FightEndedAsync`) and `Jondo.Unity.Server/Managers/Quests.cs` (`OnTalkingToAsync`, `CloseSharedAsync`, `OnMapEnteredAsync`, `OnItemReadAsync`, `StartSharedAsync`). `Jondo.Unity.Server/Managers/HeroQuestItems.cs` (`PoolAsync`) is also a precondition/payment path for shared turn-ins from `NpcHandler` and `Quests`. |

### Retiring the Heroes control bridge

| Bridge operations | Migration task that can remove them |
| --- | --- |
| `create`, `name`, `preview`, `portrait` | Move creation and appearance responses into the package after the ownership cutover. Replace the handlers with narrow character creation, name and portrait services, preserving the shared character-creation rules and the portrait ownership check. |
| `quests` | Move the route and lag reporting with quest propagation. Quest-sharing preferences live in the package. |
| `autopass`, `loot` | Move the routes with fight turn control and reward settlement. Both continue to use the preferences already owned by the package. |
| `list`, `sheet` | Move the client read models after ownership and presence, quest sharing, and fight control and rewards have landed. Use the shared character sheet and look services for their projections. |

Dungeon, combat, trade, map snapshots and quest gameplay read the same active groups through the host facade over `IHeroGroups`. These paths use the live host sessions for their existing payments, rewards, actor data and quest state. The quest lag cache and propagation logic still run in the host.

Remove `IHeroesControl`, its operation enum and `ControlApi.HandleHeroes` when the last operation has moved.

## Runtime loading and implementation order

- `Jondo.Unity.Server/Jondo.Unity.Server.csproj` targets `net10.0` with `SelfContained=false`, `PublishSingleFile=true` and `IncludeNativeLibrariesForSelfExtract=true`. The RID defaults to `NETCoreSdkRuntimeIdentifier`. Checked-in server publish paths specify neither trimming nor Native AOT. The framework-dependent apphost requires .NET 10 on the host. `DeployServer` copies only the apphost to the root unless `SkipRootDeploy=true`.
- The host loads enabled external packages from `server-mods/` at startup. Release archives and the Docker image stage the apphost and client assets, so Cazacabezas needs to be installed separately. The apphost carries the default Heroes package.
- Cazacabezas gameplay, state and scheduling have moved to the server package. Heroes ownership, teardown and follower party and movement policy now run in the package. Dungeon planning/payment, fight control and reward settlement still run in the host. Keep client routes and saved character IDs stable across the cutover.

## Cazacabezas package verification

The Windows .NET 10 smoke test used the built server's normal `--headless` entry point and a fresh installation. Direct checks against the running process covered the activation log, catalog name/size/SHA-256, download bytes, all four authenticated hunt routes, restart persistence, and removal. The package served the existing 42,496-byte DLL with SHA-256 `75438a80015c75280d4766a979e7dfc8f85bb0f8d29457bbc0a18906f3291801`.

The existing `/api/test` harness supplied two headless clients for the live gameplay checks. Both connected through the game sockets. The hunt clock opened the order, the hunter sent the ordinary aggression frame, and the prey surrendered through its own socket. The production fight settlement removed the order and paid four chapeuvepés. A second open order survived a graceful process restart as a resting order, alongside the balance and kill count. With the package removed, all four routes returned `404 ruta` and an expired order stayed untouched through two five-second tick intervals. The configured standalone DLL still served in that run. Moving it out of `mods/` and restarting with the same `ClientMods` list produced an empty catalog and a `404` download.

### Repeat the Windows smoke test

Run in PowerShell 7 from the repo root with the .NET 10 SDK and Python 3. Use an unused set of server ports (8888, 15881, 5555, 5556 and 6337) and a Windows session that can open loopback `HttpListener` endpoints. The setup uses the shipped world archive and a fresh auth database.

```powershell
$stage = (New-Item -ItemType Directory (Join-Path (Resolve-Path .) "host-artifacts/cazacabezas-$([guid]::NewGuid().ToString('N'))") -Force).FullName
dotnet build Jondo.Unity.Tests/Jondo.Unity.Tests.csproj -m:1 -nr:false -p:StageServerMod=true "-p:CazacabezasPackageDirectory=$stage/server-mods/cazacabezas"
if ($LASTEXITCODE) { throw 'Build failed' }
Copy-Item .jondo-root $stage
Copy-Item datos,content -Destination $stage -Recurse
New-Item -ItemType Directory -Path "$stage/config","$stage/mods" | Out-Null
$server = (Resolve-Path 'Jondo.Unity.Server/bin/Debug/net10.0/win-x64/Jondo Server.dll').Path
$env:JONDO_EMULATOR_ROOT = $stage
$env:JONDO_PUBLIC_BIND = '0'
$env:JONDO_TEST_KEY = [guid]::NewGuid().ToString('N')
$dll = 'Cazacabezas.dll'

function Start-Run($label, $packages, $clientMods) {
    @{ ServerMods = $packages; ClientMods = $clientMods; Cazacabezas = @{ Enabled = $true } } |
        ConvertTo-Json -Depth 4 | Set-Content "$stage/config/server_settings.json"
    $script:serverLog = "$stage/$label.log"
    $script:process = Start-Process dotnet -ArgumentList ('"{0}" --headless' -f $server) -WorkingDirectory $stage `
        -RedirectStandardOutput $serverLog -RedirectStandardError "$stage/$label.err" -WindowStyle Hidden -PassThru
    $deadline = (Get-Date).AddMinutes(2)
    while (!(Select-String -Quiet -SimpleMatch 'ALL EMULATION SERVICES ONLINE AND READY!' $serverLog)) {
        if ($process.HasExited -or (Get-Date) -gt $deadline) { throw "Server startup failed. See $serverLog" }
        Start-Sleep -Milliseconds 250
    }
    Select-String -SimpleMatch '[Server mods] cazacabezas (cazacabezas) initialized' $serverLog
}
function Stop-Run {
    $signal = [Threading.EventWaitHandle]::OpenExisting('Local\JondoEmuServidor.Stop')
    try { [void]$signal.Set() } finally { $signal.Dispose() }
    if (!$process.WaitForExit(20000) -or $process.ExitCode) { throw "Server shutdown failed. See $serverLog" }
}
function Post($route, $body, $expected = 200) {
    $reply = Invoke-RestMethod "http://127.0.0.1:8888/api/$route" -Method Post -ContentType 'application/json' `
        -Body (ConvertTo-Json -InputObject $body -Depth 6 -Compress) -SkipHttpErrorCheck -StatusCodeVariable code
    if ($code -ne $expected) { throw "$route returned $code. Expected $expected" }
    $reply
}
function Probe($route, $slot = 0, $body = @{}) {
    Post "test/$route" ($body + @{ key = $env:JONDO_TEST_KEY; slot = $slot })
}
function Hunt($route, $body = @{}, $expected = 200) {
    Post "headhunts/$route" ($body + @{ token = $token }) $expected
}
function Check-Catalog {
    $catalog = (Invoke-RestMethod 'http://127.0.0.1:8888/api/mods').mods
    $download = "$stage/download.dll"
    Invoke-WebRequest "http://127.0.0.1:8888/api/mods/$dll" -OutFile $download
    $hash = (Get-FileHash "mods/$dll").Hash.ToLowerInvariant()
    if ($catalog.Count -ne 1 -or $catalog[0].name -cne $dll -or $catalog[0].sha256 -cne $hash -or
        $catalog[0].size -ne (Get-Item "mods/$dll").Length -or (Get-FileHash $download).Hash.ToLowerInvariant() -cne $hash) {
        throw 'Catalog or download mismatch'
    }
}
function Wait-HuntOpen {
    for ($attempt = 0; $attempt -lt 25; $attempt++) {
        $order = (Hunt status).order
        if ($order.open) { return $order }
        Start-Sleep -Seconds 1
    }
    throw 'Hunt did not open'
}
```

Start with the package alone, walk through the authenticated operations, and have the prey surrender. The status after surrender should show `kills: 1`, `chapeuvepes` equal to the quoted bounty, and `order: null`.

```powershell
Start-Run installed @('cazacabezas') @($dll)
Check-Catalog
$hunter = (Probe create 0 @{ name = 'Cazador-Smoke'; breed = 8; level = 100 }).id
$prey = (Probe create 1 @{ name = 'Presa-Smoke'; breed = 8; level = 110 }).id
$token = (Probe token).token
$routes = 'status','board','take','abandon'
foreach ($route in $routes) {
    Post "headhunts/$route" @{ token = 'invalid' } 401
    Hunt $route @{} 409
}
Probe headless
Probe align 0 @{ side = 3; pvp = 1 }
Probe headless 1
Probe align 1 @{ side = 4; pvp = 1 }
Probe goto 0 @{ map = 101715467; cell = 300 }
Start-Sleep -Seconds 2
Probe goto 1 @{ map = 101715467; cell = 314 }
Start-Sleep -Seconds 2
Hunt status
(Hunt board).prey | Where-Object id -eq $prey
Hunt take @{ target = $prey }
Hunt take @{ target = $prey } 409
$bounty = (Wait-HuntOpen).bounty
Probe 'headless/send' 0 @{ op = 'hph'; fields = @{ '1' = $prey } } | Out-Null
$deadline = (Get-Date).AddSeconds(25)
while ((Probe fight).state -ne 'Ongoing') {
    if ((Get-Date) -gt $deadline) { throw 'Aggression did not start' }
    Start-Sleep -Milliseconds 250
}
Probe 'headless/send' 1 @{ op = 'kme' } | Out-Null
$deadline = (Get-Date).AddSeconds(25)
do {
    $settled = Hunt status
    if ((Get-Date) -gt $deadline) { throw 'Hunt did not settle' }
    Start-Sleep -Milliseconds 250
} until ($settled.kills -eq 1 -and $null -eq $settled.order)
if ($settled.chapeuvepes -ne $bounty) { throw 'Bounty mismatch' }
Hunt take @{ target = $prey }
Wait-HuntOpen
Stop-Run
```

On restart, check that the same token resolves from the auth database. After reconnecting the hunter, expect the saved kill and balance, the same target, `open: false`, and a positive `restSeconds`. Use the other slot to check abandoning an order and its subsequent `409 locked` response.

```powershell
Start-Run restarted @('cazacabezas') @()
Check-Catalog
Hunt status @{} 409
Probe headless
$restored = Hunt status
if ($restored.kills -ne 1 -or $restored.chapeuvepes -ne $bounty -or $restored.order.target -ne $prey -or
    $restored.order.open -or $restored.order.restSeconds -le 0) { throw 'Persisted hunt state mismatch' }
Probe headless 1
$hunterToken = $token
$token = (Probe token 1).token
Hunt take @{ target = $hunter }
Hunt abandon
Hunt status
Hunt take @{ target = $hunter } 409
$token = $hunterToken
Stop-Run
```

Remove the package and age the test order so an active hunt clock would delete it even with both players offline. With the legacy DLL present, expect the same download bytes, `404 ruta` on the gameplay routes and identical SQLite rows before and after waiting. After moving the legacy DLL out of `mods/`, expect an empty manifest and a `404` download as well.

```powershell
Move-Item "$stage/server-mods/cazacabezas" "$stage/uninstalled-cazacabezas"
Copy-Item "mods/$dll" "$stage/mods/$dll"
$db = "$stage/bases/world.db"
@'
import sqlite3, sys, time
with sqlite3.connect(sys.argv[1]) as db:
    db.execute('UPDATE HeadHuntOrders SET TakenMs=?, OpenUntilMs=0, RestUntilMs=0 WHERE Hunter=?',
               (int((time.time()-4*86400)*1000), int(sys.argv[2])))
'@ | python - $db $hunter
function Read-Order {
    python -c 'import sqlite3,sys; print(sqlite3.connect(sys.argv[1]).execute("SELECT * FROM HeadHuntOrders WHERE Hunter=?",(int(sys.argv[2]),)).fetchone())' $db $hunter
}
$due = Read-Order
if ($due -eq 'None') { throw 'Missing test order' }
Start-Run legacy-only @() @($dll)
Check-Catalog
foreach ($route in $routes) { Hunt $route @{} 404 }
Start-Sleep -Seconds 11
if ((Read-Order) -ne $due) { throw 'Hunt clock changed the saved order' }
Stop-Run
Move-Item "$stage/mods/$dll" "$stage/legacy-$dll"
Start-Run removed @() @($dll)
(Invoke-RestMethod 'http://127.0.0.1:8888/api/mods') | ConvertTo-Json
(Invoke-WebRequest "http://127.0.0.1:8888/api/mods/$dll" -SkipHttpErrorCheck).StatusCode
foreach ($route in $routes) { Hunt $route @{} 404 }
Stop-Run
```

All six `HeadHuntTests` also passed in the in-process harness. Those tests use fixture sessions and the cities-war callback to cover holds, timeout, win and loss payouts, currency trades, legacy SQLite rows, and disposal. To repeat, use a scratch data root containing `.jondo-root`, `datos/`, `content/`, and `bases/world.db` extracted from `datos/world.zip`. Point `JONDO_EMULATOR_ROOT` at that root and reuse the compiled test assembly.

```powershell
dotnet test Jondo.Unity.Tests/Jondo.Unity.Tests.csproj --no-build --filter FullyQualifiedName~HeadHuntTests
```

**Remaining manual check** — Launch a supported Dofus client through the launcher against the packaged installation. Confirm MelonLoader loads the downloaded DLL, F7 and the alignment-tab button open the hunt panel, and the tracker follows a live order through settlement. Repeat against the fully removed installation with any old client-local `Mods/Cazacabezas.dll` moved aside. This smoke test used a framework-dependent build. Published single-file and Docker startup remain outside this verification.

## Existing test entry points

Run from the repository root with the .NET 10 SDK. Gameplay fixtures use `bases/world.db` from `datos/world.zip` and create test characters there.

```sh
dotnet test Jondo.Unity.Tests/Jondo.Unity.Tests.csproj --filter "FullyQualifiedName~HeadHuntTests|FullyQualifiedName~World.HeroesTests|FullyQualifiedName~HeroesControlTests|FullyQualifiedName~DungeonHeroesTests|FullyQualifiedName~HeroProbeTests|FullyQualifiedName~HeroAchievementsTests|FullyQualifiedName~ZaapDiscoveryTests|FullyQualifiedName~ClientModSyncTests|FullyQualifiedName~ModInstallTests"
dotnet test Jondo.Unity.Server.Tests/Jondo.Unity.Server.Tests.csproj --filter "FullyQualifiedName~ServerModLoaderTests|FullyQualifiedName~ClientModCatalogTests|FullyQualifiedName~ServerSettingsWindowTests|FullyQualifiedName~HeroSwitchSyncTests|FullyQualifiedName~AchievementStateTests|FullyQualifiedName~GuildRosterTests"
```

- `Jondo.Unity.Tests/World/HeadHuntTests.cs` covers eligibility/opening, holds/rest, wins/losses, currency trades, and authenticated HTTP behavior with the staged package loaded and unloaded. The integration test takes an order through HTTP, waits for the scheduled tick, settles the hunt through the cities-war fight callback, reloads a saved order from the original table format, and verifies that a pending order stays closed after the package shuts down. `Jondo.Unity.Tests/World/HeroesTests.cs` covers ownership, switch/regroup, party, presence, quests, trade, control and solo fights with the bundled package. `Jondo.Unity.Tests/World/HeroesControlTests.cs` covers external package activation, authenticated routes, master disconnect and repeated cleanup, regroup, unload, and normal entry and teardown with Heroes absent. `Jondo.Unity.Tests/World/DungeonHeroesTests.cs` covers the key planner and shortfalls. `Jondo.Unity.Tests/Combat/HeroProbeTests.cs`, `Jondo.Unity.Tests/Sessions/HeroAchievementsTests.cs` and `Jondo.Unity.Tests/Movement/ZaapDiscoveryTests.cs` cover fight control/bar, achievement ownership and zaap discovery.
- `Jondo.Unity.Tests/Launcher/ClientModSyncTests.cs` and `Jondo.Unity.Tests/Launcher/ModInstallTests.cs` cover approval, cache reuse, corrupt downloads and bundled install. `Jondo.Unity.Server.Tests/ClientModCatalogTests.cs` and `Jondo.Unity.Server.Tests/ServerSettingsWindowTests.cs` cover catalog order, filename checks, settings migration and restart behavior. `Jondo.Unity.Server.Tests/HeroSwitchSyncTests.cs`, `Jondo.Unity.Server.Tests/AchievementStateTests.cs` and `Jondo.Unity.Server.Tests/GuildRosterTests.cs` cover switch state, hero achievement delivery and socketless online status. `RunTestsBeforePublish` in the server project runs general, server and protocol suites by default, with narrower filters for `JondoProtocolTarget=3.8.1.2`.

## Unverified behavior

Live MelonLoader/IL2CPP behavior across client versions and hosts, recovery from a live server crash, and recovery after a partial Heroes key debit or interrupted handover are unverified end to end.
