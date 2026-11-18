# Changelog

## Unreleased

- The lighting player that a switch runs from its memory takes its port layout from the switch's hardware profile, and EE now checks both the player and its renderer before reusing a running player. The first connection after the update replaces a running player once and keeps its saved scene. Switch playback is still available only on the USW-Pro-XG-8-PoE.

## 0.6.0

This release removes API routes, saves schedules and port names in a format that 0.5.1 cannot read, keeps the fan mode for each switch in a new place, and moves the build to Python 3.14, Node.js 24.18.0 and .NET 10.

- LAN access lets phones and other computers on your network open EE at the Windows computer's address, without Tailscale. It is off by default; turn it on under LAN access in Settings → Remote. Each device pairs once with a code shown on the Windows computer and stays paired until you remove it or turn LAN access off. A browser keeps its access key for one year and then needs a new code. LAN access uses port 4280 by default and IPv4 only, and accepts only private network addresses. Its traffic is plain HTTP and is not encrypted, and Settings says so. Switch and UniFi Network passwords never cross it. An uninstall for one user deletes the LAN access setting and the paired devices.
- Tailscale is optional. Private access stays off until you turn it on, and EE runs no Tailscale command while it is off. The one exception is a route that EE added earlier and has not yet seen removed, including one from a setting turned off in 0.5.1: EE reads `tailscale serve status` at start, refresh and quit until a start or refresh sees the route gone.
- As in 0.5.1, the first connection to a switch that has no saved SSH key, whether you sign in, connect with the saved login or EE connects at startup, signs in with the username and password and saves the switch's key. No SSH key is needed on the switch. Keys saved by earlier versions are used as they are.
- Settings → Connection shows the SSH key fingerprint and key type of the connected switch.
- When a switch presents a key that differs from the saved one, EE refuses the connection before it sends the username or password, and the Connect page shows the saved and the new fingerprints. The new key can be accepted only in EE on the computer that runs it, which also clears the lighting player record of the previous switch. Lighting, schedules and port names saved for the previous key stay on disk but are not used with the new key. `eectl probe --accept-host-key <fingerprint>` accepts a changed key from the command line.
- Devices on Tailscale and paired LAN devices can use only the requests listed for remote use: lighting, effects, automation, profiles, schedules, port names, fans, status, reconnecting to the saved switch and Handoff to UniFi. Logging in to a new switch, forgetting the saved login, UniFi Network setup, the Tailscale and LAN access settings and accepting a changed SSH key work only on the computer that runs EE. Clients on those devices leave out the controls they cannot use. Update macOS and Linux clients that connect by address.
- Changing the Tailscale or LAN access settings or closing the web session from a browser needs a key that EE creates at each start. Open the browser tab with Open in browser in the tray menu or in Settings → Connection. Tabs opened another way show these settings read-only. A source run prints a one-use administration link.
- EE removes its own Tailscale Serve route when it stops and adds it again when it starts with private access on. Uninstalling EE removes the route when EE was installed for you only. After uninstalling an installation for all users, run `tailscale serve status` and remove EE's port with `tailscale serve --https=<port> off`.
- Fans saves the fan mode for each switch, and a switch that EE has not controlled before starts in Switch control. The mode chosen in 0.5.1 moves to the first USW-Pro-XG-8-PoE that EE connects to after the update.
- Handoff to UniFi also returns the fans of a USW-Pro-XG-8-PoE to the switch's own controller, and EE restores the switch's saved fan mode when you connect again. Uninstalling EE does not connect to the switch, so choose Handoff to UniFi for each switch before you uninstall.
- EE's fan control program on the switch changed. On the first switch that EE connects to after the update, the first check restarts the fan control that 0.5.1 started there once, with the same mode. On other switches, EE stops fan control that 0.5.1 started, on this or another computer, and the switch's own controller runs the fans until you choose a mode.
- When the fan mode saved for a switch cannot be read, Fans says so, and EE does not change fan control that is running on the switch until you choose a mode. Fan control that EE on another computer started with this or a later release also keeps running until you choose a mode. A fan setting that takes effect but cannot be saved is reported instead of failing with a server error.
- When EE's fan control on the switch stops, EE starts it again. If that fails, EE tries again every 60 seconds and shows that the fans are back on switch control.
- When the saved lighting for a switch cannot be read, EE renames the file with `.corrupt-` and the time and offers to start a new lighting setup. If the switch plays that lighting from its memory, the login stops with a message and the file is kept. Saved lighting from a newer EE version is refused with a message to update EE. The desktop app and the browser show these messages instead of a general connection error.
- Schedules, port names and saved SSH host keys are saved with a format version, and EE refuses files saved by a newer version instead of overwriting them. A login refused because the saved SSH host keys cannot be read or come from a newer version says so. EE 0.5.1 cannot read the schedules and port names saved by this version.
- When the EE service stops on its own, the desktop window says so and offers to start it again. The browser workspace's gateway stops as well, instead of answering every request with an error.
- Settings → About in the desktop app shows the app and service versions and the service's disclaimer. The browser workspace shows the same disclaimer.
- The desktop app no longer has Close web session in Settings, so a client connected by address cannot close the workspace of the computer it connects to. `--url` and `EE_WORKSPACE_URL` accept only the addresses that the workspace address dialog accepts.
- The desktop app keeps `desktop.log` in its data folder with the errors it could not show and why its service failed to start or stopped. The loading spinner no longer leaves a failed task in that log at every start.
- `service-diagnostics.log` also records EE's fixed error codes and log message templates, still without hosts, names or passwords.
- The local gateway and the engine refuse API requests (403 `forbidden`) and WebSocket connections that a browser marks as coming from another website, and request bodies over 1 MiB (413 `too_large`).
- API changes: the WebSocket accepts the token only in the `Authorization` header. `cors_origins = ["*"]` requires a token of at least 32 characters. `POST /api/presets/{name}/apply` waits for SFP output and can return 409 `output_unconfirmed` or 503 `offline_scene_unconfirmed`. `POST /api/rules/test` refuses the rules that the save routes refuse. Login fields have length limits (422 `invalid_credentials`), zones are limited to 64 and rules to 256. `GET /api/session` adds `ssh_host_key`, `ssh_host_key_type` and `fan_handoff`. `GET /api/fans` adds `settings_error` and `hold`, and `PUT /api/fans` can return 500 `fan_settings_save_failed`. `GET /api/private-access` adds `admin_required` and `local`. New routes: `POST /api/session/host-key/replace`, `POST /api/workspace/admin-link` and the LAN access routes under `/api/lan-access`.
- Removed API routes: `POST /api/session/forget-host-key` (use `POST /api/session/host-key/replace` or `eectl forget-host`), and, because no client used them, `POST /api/session/switch-playback`, `POST /api/session/switch-playback/check`, `GET` and `PUT /api/session/switch-playback/effects`, `GET /api/controller/diagnostics` and `POST /api/presets/action`, and the `can_save_to_switch` session field.
- `etherlighting-evolved serve` serves the API only. Its page at `/` links to the API documentation at `/docs`. The old standalone browser front end, the 0.4 desktop host and the separate WPF client are removed from the repository.
- Updating EE replaces the whole application folder, so no file from an earlier version stays behind. The app keeps no data in that folder.
- The lighting engine is split into smaller parts for the command rate, the SFP indicators and port identification. Lighting, its timing and the API behave as before.
- The Windows package bundles Python 3.14 and Node.js 24.18.0, and the desktop apps use .NET 10. The macOS client needs macOS 12 or later.
- Releases are built by GitHub Actions from a version tag and published on GitHub with `SHA256SUMS` and build provenance attestations. The Windows installer has no Authenticode signature yet, and the macOS app has an ad hoc signature but no Developer ID signature or notarization.

## 0.5.1

- Fans controls the two fans of a USW-Pro-XG-8-PoE. Choose Switch control to leave the fans to the switch, one of four curves (Low, Medium, High and Max) that set the fan speed from the CPU temperature, Manual for one fixed speed on both fans, or Thermal target to run the fans as slowly as possible while keeping the CPU at a chosen temperature. The page shows the CPU, SFP and port chip temperatures and both fan speeds.
- In every fan mode the fans speed up when the CPU passes 85 °C, the SFP sensor passes 65 °C or a port chip passes 100 °C. If EE's fan control stops or a sensor cannot be read, the switch's own fan controller takes over again.
- Fan control runs on the switch and continues while EE is closed. A switch restart returns the fans to the switch's own control; EE restores the chosen mode when it connects again.
- A fan change takes effect within about a second and goes straight to the new mode's speed. Thermal target starts at the speed estimated to hold the target instead of running the fans up to full speed first. Choosing Manual starts at the fans' current speed.
- In a rack window such as 1280 × 400, Fans fills the screen with large readings and controls, including − and + buttons beside the slider.
- Rack-sized windows show no tooltips. A tap streamed as a mouse click leaves the pointer where it tapped, which kept a tooltip on screen.
- In narrow windows the title bar leaves out the app name so every page button fits.

## 0.5.0

- The desktop window uses less processor time while lighting moves: the page background and the port sockets are drawn once and reused, port colors are painted 20 times a second, and the uplink chart redraws only when it has moved a pixel.
- While a lighting change is applying, the Lighting page turns off Discard, Undo, the toolbar and the selection buttons, so a change cannot be dropped or reverted part way through.
- The desktop Automation and Profiles pages match the browser workspace: the editor column is left aligned, condition rows fill the row, the port picker shows how many ports are selected, and Move up, Move down and Delete rule sit on the right.
- Port controls follows lighting changes made by a schedule, a rule or another device, and a setting that no longer matches the current lighting shows an error instead of closing EE. Previewing saved effect settings keeps fixed colors on the selected ports.
- When Quit cannot confirm that the switch lighting was returned, EE stays open and says so; choose Quit again to close anyway. If the EE service does not start, the window offers Try again.
- EE closes its SSH session when a switch status shell cannot open, and reconnects when status reads stay blocked, so no shells are left on the switch and effects do not run on stale status.
- Updating EE keeps the Start with Windows setting and removes files of the earlier desktop program. The installer includes the full license text for every desktop component.
- In a rack window such as 1280 × 400, the desktop Lighting page fits without scrolling. The switch name shares a line with the selection buttons, uplink bandwidth is one line, the map legend moves to the bottom bar, and Port controls uses compact rows with tabs for effects that have many settings.
- Fixed a desktop color picker crash when closing with a color field focused. Done stays visible while the color controls scroll.
- Settings can save a UniFi OS API key when connecting. EE remembers the controller and sign-in method across restarts and updates.
- Workspace owners can approve individual tagged Tailscale devices. Linux clients can use the private workspace address when local DNS cannot resolve it.
- The uplink graph scrolls the last minute of download and upload traffic using each existing switch counter reading. A delayed measurement is labeled “Last reading.”
- Choose Quit in the Windows tray to close EE. EE can recover the lighting connection during shutdown so the switch can resume its saved scene.
- The Windows app opens Lighting, Effects, Automation, Profiles, Settings and Help in a native desktop window. The installer includes the workspace service and lighting engine.
- macOS and Linux desktop clients can connect to an existing EE workspace. The client remembers the workspace address for the next launch.
- The native port map follows the switch's reported colors and traffic. Minimize the window or hide it in the notification area to pause the live display. Opening it again refreshes the readings.

## 0.4.28

- EE uses far less processor and graphics time. A window in the notification area or minimized no longer renders or receives the live stream. With steady lighting the port map stops repainting; with moving lighting it repaints 30 times a second instead of at the display rate. Status text and readings are rewritten only a few times a second, and the ambient glow no longer uses a blur pass.

## 0.4.27

- Open EE from its system tray icon. The workspace stays off the Windows taskbar while open. Minimize or close the window to return it to the tray.

## 0.4.26

- Choose who can open the private workspace in Settings → Mobile & remote access: only your Tailscale account, or any Tailscale account that can reach this computer. The default stays your account only. Devices without an account identity, such as tagged servers, stay blocked.

## 0.4.25

- Private access through Tailscale now comes back by itself after the computer restarts. When EE starts before Tailscale is connected, it checks again on a schedule and reopens the saved private address; no Tailscale setting is changed.

## 0.4.24

- Open the regular Lighting workspace with the saved switch login. Applying a supported effect also saves it for when the desktop goes offline.

## 0.4.23

- Change a port group's effect and settings while the switch keeps playing its saved scene.

## 0.4.22

- Save lighting to a Pro XG 8 PoE from Settings → Connection. Activity Glow follows port traffic on the switch, including while EE is closed. Choose Handoff to UniFi to stop playback.

## 0.4.21

- Rename Ethernet and SFP ports in Port details, or use UniFi port names automatically. Manual names stay saved when switching modes and appear across the map, search, details and SFP source selector.

## 0.4.20

- A lighting session that ended without a confirmed shutdown (power loss, a forced restart, a stopped process) no longer locks the switch out of EE. The next login takes over the retained ownership record, returns the switch lighting to its defaults with acknowledged commands, and clears the record; a record left by a different switch at the same address is dropped without any command. When Windows signs out or shuts down, the desktop app asks Windows to wait while it returns the lighting first.
- Choose from nine dark themes and three light themes in Settings, including Silver and Midnight. Each device remembers its selection.
- Find ports by number or device name, inspect live traffic and link changes, and identify a socket for ten seconds.
- Save named effect settings and reusable port selections. Review recent lighting setups and restore one from history.
- Create weekly schedules and test rule conditions and priority before applying.
- Profile renaming preserves scheduled references. Saved changes use revision checks, and scheduled occurrences survive restarts.
- Review concurrent lighting edits before applying them. Port previews reflect updated group membership and SFP brightness.
- Health shows reported PoE budget, measured draw, available headroom, and the largest consumers.
- Settings shows the last session outcome. Desktop exit and parent-loss cleanup preserve unresolved recovery status.
- Regular effects can run on selected SFP lights, on their own or alongside Ethernet ports. Return to SFP lighting restores the saved indicator settings.
- Activity Glow, Traffic Trails, and Live Traffic can include measured uplink traffic.

## 0.4.19

- Switch health displays the three temperature channels reported by the Pro XG 8 PoE.
- Self-hosted controller sign-in uses the same strict login option as UniFi Network. EE renews an expired controller session when UniFi reports it in a JSON response.
- Controller service errors in Settings identify the failed operation and show the HTTP status when available.

## 0.4.18

- Hide Port controls to expand the map. Reopen the panel to continue editing the same selection.
- The touchscreen color editor keeps the color and hue controls open until Done. Apply changes sends the preview to the switch.
- Uplink bandwidth shows Download first, followed by Upload.

## 0.4.17

- The rack port inspector uses the full workspace height. The map key and brightness control share a row with the link legend.
- Uplink rates stay in Mbps across gigabit speeds. The rack display shows the last minute of measured traffic and the highest five-second reading for each direction.

## 0.4.16

- The Lighting faceplate shows uplink bandwidth next to the switch name. The eight Ethernet ports and two SFP ports share a row on desktop and rack displays.
- Default brightness stays within reach while editing. The map key includes the full link-speed legend and brightness help.
- The window header and workspace toolbar use less vertical space.

## 0.4.15

Changes since 0.4.12:

- Compact rack and phone layouts retain port selection, groups, profiles, and Apply controls.
- The port inspector scrolls independently under the pointer, without a second visible scrollbar.
- Color editing uses a shared touch picker in Lighting and Automation.
- Drafts retain edits across port selections and can be saved as preview profiles before applying.
- Apply feedback identifies matching automation rules and links to their controls.
- Settings groups connection controls consistently and clarifies saved-login preferences.
- Validation, cancel navigation, profile Undo, and synchronized SFP previews preserve editing context.
- Repository verification records source inputs, test results, and packaged simulator checks. Windows CI produces installers for verified main revisions.
