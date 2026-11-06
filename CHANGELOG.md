# Changelog

## Unreleased

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
